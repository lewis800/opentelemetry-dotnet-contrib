// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry.Trace;
using Xunit;

namespace OpenTelemetry.Instrumentation.Wcf.Tests;

/// <summary>
/// Helper class with static methods for common WCF instrumentation test assertions.
/// </summary>
internal static class WcfTestHelpers
{
    public const string ServiceContractName = "http://opentelemetry.io/Service";

    internal const int MaxRetries = 5;

    private static readonly Random Random =
#if NET
        Random.Shared;
#else
        new();
#endif

    public static object? GetTagValue(Activity activity, string tagName)
        => activity.TagObjects.FirstOrDefault(tag => tag.Key == tagName).Value;

    public static string GetContractQualifiedMethod(string operationName)
        => $"{ServiceContractName}/{operationName}";

    public static Uri GetRandomBaseUri(string scheme)
    {
        const int MinPort = 2000;
        const int MaxPort = 5000;

        var port = Random.Next(MinPort, MaxPort);

        return new UriBuilder()
        {
            Scheme = scheme,
            Host = "localhost",
            Port = port,
            Path = "/",
        }.Uri;
    }

    /// <summary>
    /// Asserts common activity properties for outgoing request instrumentation tests.
    /// </summary>
    /// <param name="activity">The activity to validate.</param>
    /// <param name="serviceBaseUri">The service base URI.</param>
    /// <param name="emptyOrNullAction">Whether the action name is empty or null.</param>
    /// <param name="includeVersion">Whether to validate SOAP message version.</param>
    /// <param name="includeVersionExpected">The expected version string when includeVersion is true (binding-specific).</param>
    /// <param name="schemeTag">The expected scheme tag value (e.g., "http" or "net.tcp").</param>
    /// <param name="enrich">Whether enrichment tags should be present.</param>
    /// <param name="enrichmentException">Whether enrichment threw an exception.</param>
    public static void AssertOutgoingRequestActivity(
        Activity activity,
        Uri serviceBaseUri,
        bool emptyOrNullAction,
        bool includeVersion,
        string? includeVersionExpected,
        string schemeTag,
        bool enrich,
        bool enrichmentException)
    {
        Assert.NotNull(activity);

        var expectedMethod = emptyOrNullAction
            ? GetContractQualifiedMethod("ExecuteWithEmptyActionName")
            : GetContractQualifiedMethod("Execute");

        Assert.Equal(expectedMethod, activity.DisplayName);
        Assert.Equal(expectedMethod, GetTagValue(activity, SemanticConventions.AttributeRpcMethod));

        Assert.Equal(WcfInstrumentationActivitySource.OutgoingRequestActivityName, activity.OperationName);
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, GetTagValue(activity, SemanticConventions.AttributeRpcSystemName));
        Assert.DoesNotContain(activity.TagObjects, t => t.Key == SemanticConventions.AttributeRpcService);
        Assert.Equal(serviceBaseUri.Host, GetTagValue(activity, SemanticConventions.AttributeServerAddress));
        Assert.Equal(serviceBaseUri.Port, GetTagValue(activity, SemanticConventions.AttributeServerPort));
        Assert.Equal(schemeTag, GetTagValue(activity, WcfInstrumentationConstants.AttributeWcfChannelScheme));
        Assert.Equal("/Service", GetTagValue(activity, WcfInstrumentationConstants.AttributeWcfChannelPath));

        if (includeVersion)
        {
            if (includeVersionExpected != null)
            {
                // HTTP binding: exact match
                Assert.Equal(includeVersionExpected, GetTagValue(activity, WcfInstrumentationConstants.AttributeSoapMessageVersion));
            }
            else
            {
                // TCP binding: regex match
                var value = GetTagValue(activity, WcfInstrumentationConstants.AttributeSoapMessageVersion)!.ToString();
                Assert.Matches("""Soap.* \(http.*\) Addressing.* \(http.*\)""", value);
            }
        }

        if (enrich && !enrichmentException)
        {
            Assert.Equal(WcfEnrichEventNames.BeforeSendRequest, activity.TagObjects.Single(t => t.Key == "client.beforesendrequest").Value);
            Assert.Equal(WcfEnrichEventNames.AfterReceiveReply, activity.TagObjects.Single(t => t.Key == "client.afterreceivereply").Value);
        }
    }

#if NETFRAMEWORK
    public static void AssertIncomingRequestActivityCommon(Activity activity, Uri serviceBaseUri)
    {
        Assert.NotNull(activity);

        Assert.Equal(WcfInstrumentationActivitySource.IncomingRequestActivityName, activity.OperationName);
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, GetTagValue(activity, SemanticConventions.AttributeRpcSystemName));
        Assert.DoesNotContain(activity.TagObjects, t => t.Key == SemanticConventions.AttributeRpcService);
        Assert.Equal(serviceBaseUri.Host, GetTagValue(activity, SemanticConventions.AttributeServerAddress));
        Assert.Equal(serviceBaseUri.Port, GetTagValue(activity, SemanticConventions.AttributeServerPort));
        Assert.Equal("net.tcp", GetTagValue(activity, WcfInstrumentationConstants.AttributeWcfChannelScheme));
        Assert.Equal("/Service", GetTagValue(activity, WcfInstrumentationConstants.AttributeWcfChannelPath));
    }
#endif

    public static void AssertDownstreamInstrumentationActivities(IList<Activity> stoppedActivities, bool filter)
    {
        Assert.NotEmpty(stoppedActivities);
        if (filter)
        {
            var activity = Assert.Single(stoppedActivities);
            Assert.Equal("DownstreamInstrumentation", activity.OperationName);
        }
        else
        {
            Assert.Equal(2, stoppedActivities.Count);
            Assert.NotNull(stoppedActivities.SingleOrDefault(a => a.OperationName == "DownstreamInstrumentation"));
            Assert.NotNull(stoppedActivities.SingleOrDefault(a => a.OperationName == WcfInstrumentationActivitySource.OutgoingRequestActivityName));
        }
    }

    /// <summary>
    /// Asserts that all activities have the same trace ID and that children have the parent as their parent.
    /// </summary>
    /// <param name="activities">The activities to validate.</param>
    public static void AssertActivitiesHaveCorrectParentage(IList<Activity> activities)
    {
        Assert.NotEmpty(activities);
        Assert.All(activities, activity => Assert.Equal(activities[0].TraceId, activity.TraceId));
        var parent = activities.Single(activity => activity.Parent == null);
        Assert.All(
            activities.Where(activity => activity != parent),
            activity => Assert.Equal(parent.SpanId, activity.ParentSpanId));
    }
}
