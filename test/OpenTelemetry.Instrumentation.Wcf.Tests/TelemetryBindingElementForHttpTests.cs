// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using OpenTelemetry.Instrumentation.Wcf.Implementation;
using OpenTelemetry.Instrumentation.Wcf.Tests.Tools;
using OpenTelemetry.Trace;
using Xunit;

namespace OpenTelemetry.Instrumentation.Wcf.Tests;

[Collection("WCF")]
public class TelemetryBindingElementForHttpTests : IDisposable
{
    private readonly Uri serviceBaseUri;
    private readonly HttpListener listener;

    public TelemetryBindingElementForHttpTests()
    {
        var retryCount = WcfTestHelpers.MaxRetries;
        HttpListener? createdListener = null;
        while (retryCount > 0)
        {
            try
            {
                this.serviceBaseUri = WcfTestHelpers.GetRandomBaseUri(Uri.UriSchemeHttp);

                createdListener = new HttpListener();
                createdListener.Prefixes.Add(this.serviceBaseUri.OriginalString);
                createdListener.Start();
                break;
            }
            catch
            {
                createdListener?.Close();
                createdListener = null;
                retryCount--;
            }
        }

        if (createdListener == null || this.serviceBaseUri == null)
        {
            throw new InvalidOperationException("HttpListener could not be started.");
        }

        this.listener = createdListener;
        var initializationHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
        try
        {
            Listener();

            initializationHandle.WaitOne();
        }
        finally
        {
            initializationHandle.Dispose();
#pragma warning disable IDE0059 // Unnecessary assignment of a value
            initializationHandle = null;
#pragma warning restore IDE0059 // Unnecessary assignment of a value
        }

        async void Listener()
        {
            while (true)
            {
                try
                {
                    var ctxTask = this.listener.GetContextAsync();

                    initializationHandle?.Set();

                    var ctx = await ctxTask.ConfigureAwait(false);

                    using var reader = new StreamReader(ctx.Request.InputStream);

                    var request = reader.ReadToEnd();

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/xml; charset=utf-8";

                    using (var writer = new StreamWriter(ctx.Response.OutputStream))
                    {
                        if (request.Contains("ExecuteWithEmptyActionName"))
                        {
                            writer.Write(@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><ExecuteWithEmptyActionNameResponse xmlns=""http://opentelemetry.io/""><ExecuteResult xmlns:a=""http://schemas.datacontract.org/2004/07/OpenTelemetry.Instrumentation.Wcf.Tests"" xmlns:i=""http://www.w3.org/2001/XMLSchema-instance""><a:Payload>RSP: Hello Open Telemetry!</a:Payload></ExecuteResult></ExecuteWithEmptyActionNameResponse></s:Body></s:Envelope>");
                        }
                        else if (request.Contains("ExecuteSynchronous"))
                        {
                            writer.Write(@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><ExecuteSynchronousResponse xmlns=""http://opentelemetry.io/""><ExecuteResult xmlns:a=""http://schemas.datacontract.org/2004/07/OpenTelemetry.Instrumentation.Wcf.Tests"" xmlns:i=""http://www.w3.org/2001/XMLSchema-instance""><a:Payload>RSP: Hello Open Telemetry!</a:Payload></ExecuteResult></ExecuteSynchronousResponse></s:Body></s:Envelope>");
                        }
                        else
                        {
                            writer.Write(@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><ExecuteResponse xmlns=""http://opentelemetry.io/""><ExecuteResult xmlns:a=""http://schemas.datacontract.org/2004/07/OpenTelemetry.Instrumentation.Wcf.Tests"" xmlns:i=""http://www.w3.org/2001/XMLSchema-instance""><a:Payload>RSP: Hello Open Telemetry!</a:Payload></ExecuteResult></ExecuteResponse></s:Body></s:Envelope>");
                        }
                    }

                    ctx.Response.Close();
                }
                catch (Exception ex)
                {
                    if (ex is ObjectDisposedException
                        || (ex is HttpListenerException httpEx && httpEx.ErrorCode == 995))
                    {
                        // Listener was closed before we got into GetContextAsync or
                        // Listener was closed while we were in GetContextAsync.
                        break;
                    }

                    throw;
                }
            }
        }
    }

    public void Dispose()
    {
        if (this.listener != null)
        {
            (this.listener as IDisposable).Dispose();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(true, false, false)]
    [InlineData(false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, true, true, true, true)]
    [InlineData(true, false, true, true, true, true, true)]
    public async Task OutgoingRequestInstrumentationTest(
        bool instrument,
        bool filter = false,
        bool suppressDownstreamInstrumentation = true,
        bool includeVersion = false,
        bool enrich = false,
        bool enrichmentException = false,
        bool emptyOrNullAction = false)
    {
        List<Activity> stoppedActivities = [];

        var builder = Sdk.CreateTracerProviderBuilder()
            .AddInMemoryExporter(stoppedActivities);

        if (instrument)
        {
            builder
                .AddWcfInstrumentation(options =>
                {
                    if (enrich)
                    {
                        options.Enrich = enrichmentException
                            ? (_, _, _) => throw new Exception("Error while enriching activity")
                            : (activity, eventName, _) =>
                            {
                                switch (eventName)
                                {
                                    case WcfEnrichEventNames.BeforeSendRequest:
                                        activity.SetTag("client.beforesendrequest", WcfEnrichEventNames.BeforeSendRequest);
                                        break;
                                    case WcfEnrichEventNames.AfterReceiveReply:
                                        activity.SetTag("client.afterreceivereply", WcfEnrichEventNames.AfterReceiveReply);
                                        break;
                                    default:
                                        break;
                                }
                            };
                    }

                    options.OutgoingRequestFilter = _ => !filter;
                    options.SuppressDownstreamInstrumentation = suppressDownstreamInstrumentation;
                    options.SetSoapMessageVersion = includeVersion;
                })
                .AddDownstreamInstrumentation();
        }

        var tracerProvider = builder.Build();

        var client = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            client.Endpoint.EndpointBehaviors.Add(new DownstreamInstrumentationEndpointBehavior());
            client.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());
            var req = new ServiceRequest(payload: "Hello Open Telemetry!");

            if (emptyOrNullAction)
            {
                await client.ExecuteWithEmptyActionNameAsync(req);
            }
            else
            {
                await client.ExecuteAsync(req);
            }
        }
        finally
        {
            client.AbortOrClose();
            tracerProvider?.Shutdown();
            tracerProvider?.Dispose();

            WcfInstrumentationActivitySource.Options = null;
        }

        if (instrument)
        {
            if (!suppressDownstreamInstrumentation)
            {
                WcfTestHelpers.AssertDownstreamInstrumentationActivities(stoppedActivities, filter);
            }
            else
            {
                if (!filter)
                {
                    Assert.NotEmpty(stoppedActivities);
                    var activity = Assert.Single(stoppedActivities);

                    WcfTestHelpers.AssertOutgoingRequestActivity(
                        activity,
                        this.serviceBaseUri,
                        emptyOrNullAction,
                        includeVersion,
                        "Soap11 (http://schemas.xmlsoap.org/soap/envelope/) AddressingNone (http://schemas.microsoft.com/ws/2005/05/addressing/none)",
                        "http",
                        enrich,
                        enrichmentException);
                }
                else
                {
                    Assert.Empty(stoppedActivities);
                }
            }
        }
        else
        {
            Assert.Empty(stoppedActivities);
        }
    }

    [Fact]
    public async Task ActivitiesHaveCorrectParentTest()
    {
        var testSource = new ActivitySource("TestSource");

        List<Activity> stoppedActivities = [];
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("TestSource")
            .AddInMemoryExporter(stoppedActivities)
            .AddWcfInstrumentation()
            .Build();

        var client = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            client.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());

            using var parentActivity = testSource.StartActivity("ParentActivity");
            client.ExecuteSynchronous(new ServiceRequest(payload: "Hello Open Telemetry!"));
            client.ExecuteSynchronous(new ServiceRequest(payload: "Hello Open Telemetry!"));
            var firstAsyncCall = client.ExecuteAsync(new ServiceRequest(payload: "Hello Open Telemetry!"));
            await client.ExecuteAsync(new ServiceRequest(payload: "Hello Open Telemetry!"));
            await firstAsyncCall;
        }
        finally
        {
            client.AbortOrClose();
            tracerProvider?.Shutdown();
            tracerProvider?.Dispose();
            testSource.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        WcfTestHelpers.AssertActivitiesHaveCorrectParentage(stoppedActivities);
    }

    [Fact]
    public async Task ErrorsAreHandledProperlyTest()
    {
        var testSource = new ActivitySource("TestSource");

        List<Activity> stoppedActivities = [];
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("TestSource")
            .AddInMemoryExporter(stoppedActivities)
            .AddWcfInstrumentation()
            .Build();

        var client = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        var clientBadUrl = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri("http://localhost:1/Service")));
        try
        {
            client.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());
            clientBadUrl.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());

            using var parentActivity = testSource.StartActivity("ParentActivity");
            Assert.ThrowsAny<Exception>(client.ErrorSynchronous);
            await Assert.ThrowsAnyAsync<Exception>(client.ErrorAsync);
            Assert.ThrowsAny<Exception>(() => clientBadUrl.ExecuteSynchronous(new ServiceRequest(payload: "Hello Open Telemetry!")));
            await Assert.ThrowsAnyAsync<Exception>(() => clientBadUrl.ExecuteAsync(new ServiceRequest(payload: "Hello Open Telemetry!")));
        }
        finally
        {
            client.AbortOrClose();
            clientBadUrl.AbortOrClose();
            tracerProvider?.Shutdown();
            tracerProvider?.Dispose();
            testSource.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        Assert.Equal(5, stoppedActivities.Count);
        WcfTestHelpers.AssertActivitiesHaveCorrectParentage(stoppedActivities);
    }

    [Fact]
    public void OutgoingRequestProvidesStableSamplingAttributesAtActivityCreation()
    {
        using var scope = WcfTestHelpers.UseRpcSemanticConvention(RpcSemanticConventionMode.Stable);

        Dictionary<string, object?>? creationTags = null;

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == WcfInstrumentationActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                creationTags = options.Tags?.ToDictionary(tag => tag.Key, tag => tag.Value);
                return ActivitySamplingResult.AllDataAndRecorded;
            },
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation()
            .Build();

        var action = WcfTestHelpers.GetContractQualifiedMethod("Execute");

        using var request = Message.CreateMessage(MessageVersion.Soap11, action);
        request.Properties[TelemetryContextMessageProperty.Name] = new TelemetryContextMessageProperty(
            new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [action] = new ActionMetadata(contractName: WcfTestHelpers.ServiceContractName, operationName: "Execute"),
            });

        var state = ClientChannelInstrumentation.BeforeSendRequest(
            request,
            new Uri(this.serviceBaseUri, "/Service"));

        using var reply = Message.CreateMessage(MessageVersion.Soap11, action: WcfTestHelpers.GetContractQualifiedMethod("ExecuteResponse"));
        ClientChannelInstrumentation.AfterRequestCompleted(reply, state);

        tracerProvider.Shutdown();
        tracerProvider.Dispose();
        WcfInstrumentationActivitySource.Options = null;

        Assert.NotNull(creationTags);
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, creationTags![SemanticConventions.AttributeRpcSystemName]);
        Assert.Equal(action, creationTags[SemanticConventions.AttributeRpcMethod]);
        Assert.Equal(this.serviceBaseUri.Host, creationTags[SemanticConventions.AttributeServerAddress]);
        Assert.Equal(this.serviceBaseUri.Port, creationTags[SemanticConventions.AttributeServerPort]);
    }

    [Fact]
    public void OutgoingRequestProvidesLegacyAndStableSamplingAttributesAtActivityCreationWhenDupIsOptedIn()
    {
        using var scope = WcfTestHelpers.UseRpcSemanticConvention(RpcSemanticConventionMode.Duplicate);

        List<KeyValuePair<string, object?>>? creationTags = null;

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == WcfInstrumentationActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                creationTags = options.Tags?.ToList();
                return ActivitySamplingResult.AllDataAndRecorded;
            },
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation()
            .Build();

        var action = WcfTestHelpers.GetContractQualifiedMethod("Execute");

        using var request = Message.CreateMessage(MessageVersion.Soap11, action);
        request.Properties[TelemetryContextMessageProperty.Name] = new TelemetryContextMessageProperty(
            new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [action] = new ActionMetadata(contractName: WcfTestHelpers.ServiceContractName, operationName: "Execute"),
            });

        var state = ClientChannelInstrumentation.BeforeSendRequest(
            request,
            new Uri(this.serviceBaseUri, "/Service"));

        using var reply = Message.CreateMessage(MessageVersion.Soap11, action: WcfTestHelpers.GetContractQualifiedMethod("ExecuteResponse"));
        ClientChannelInstrumentation.AfterRequestCompleted(reply, state);

        tracerProvider.Shutdown();
        tracerProvider.Dispose();
        WcfInstrumentationActivitySource.Options = null;

        Assert.NotNull(creationTags);
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcSystem && Equals(tag.Value, WcfInstrumentationConstants.WcfSystemValue));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcService && Equals(tag.Value, WcfTestHelpers.ServiceContractName));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcMethod && Equals(tag.Value, "Execute"));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeNetPeerName && Equals(tag.Value, this.serviceBaseUri.Host));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeNetPeerPort && Equals(tag.Value, this.serviceBaseUri.Port));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcSystemName && Equals(tag.Value, WcfInstrumentationConstants.WcfSystemValue));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcMethod && Equals(tag.Value, action));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeServerAddress && Equals(tag.Value, this.serviceBaseUri.Host));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeServerPort && Equals(tag.Value, this.serviceBaseUri.Port));
    }

    [Fact]
    public void OutgoingRequestSetsStableErrorTypeForTransportExceptions()
    {
        using var scope = WcfTestHelpers.UseRpcSemanticConvention(RpcSemanticConventionMode.Stable);

        List<Activity> stoppedActivities = [];

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == WcfInstrumentationActivitySource.ActivitySourceName,
            ActivityStopped = stoppedActivities.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation()
            .Build();

        var client = new ServiceClient(
            new BasicHttpBinding(BasicHttpSecurityMode.None),
            new EndpointAddress(new Uri("http://localhost:1/Service")));

        string? expectedErrorType = null;

        try
        {
            client.Endpoint.EndpointBehaviors.Add(new TelemetryEndpointBehavior());

            var exception = Assert.ThrowsAny<Exception>(() => client.ExecuteSynchronous(new ServiceRequest(payload: "Hello Open Telemetry!")));
            expectedErrorType = exception.GetType().FullName ?? exception.GetType().Name;
        }
        finally
        {
            client.AbortOrClose();
            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(expectedErrorType, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeErrorType));
    }

    [Fact]
    public void OutgoingRequestUsesOperationOnlyRpcMethodWhenActionMetadataContractIsUnavailable()
    {
        List<Activity> stoppedActivities = [];

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            ActivityStopped = stoppedActivities.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation()
            .Build();

        using var request = Message.CreateMessage(MessageVersion.Soap11, action: string.Empty);
        request.Properties[TelemetryContextMessageProperty.Name] = new TelemetryContextMessageProperty(
            new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = new ActionMetadata(contractName: null, operationName: "ExecuteWithEmptyActionName"),
            });

        var state = ClientChannelInstrumentation.BeforeSendRequest(
            request,
            new Uri(this.serviceBaseUri, "/Service"));

        using var reply = Message.CreateMessage(MessageVersion.Soap11, action: WcfTestHelpers.GetContractQualifiedMethod("ExecuteWithEmptyActionNameResponse"));
        ClientChannelInstrumentation.AfterRequestCompleted(reply, state);

        tracerProvider.Shutdown();
        tracerProvider.Dispose();
        WcfInstrumentationActivitySource.Options = null;

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(WcfInstrumentationActivitySource.OutgoingRequestActivityName, activity.DisplayName);
        Assert.Equal("ExecuteWithEmptyActionName", activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
    }

    [Theory]
    [InlineData("https://example.com/OrderService/SubmitOrder")]
    [InlineData("urn:example:orders#SubmitOrder")]
    public void OutgoingRequestUsesOtherRpcMethodAndOriginalMethodWhenActionMappingIsUnavailable(string action)
    {
        List<Activity> stoppedActivities = [];

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            ActivityStopped = stoppedActivities.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation()
            .Build();

        using var request = Message.CreateMessage(MessageVersion.Soap11, action);

        var state = ClientChannelInstrumentation.BeforeSendRequest(
            request,
            new Uri(this.serviceBaseUri, "/Service"));

        using var reply = Message.CreateMessage(MessageVersion.Soap11, action: $"{action}Response");
        ClientChannelInstrumentation.AfterRequestCompleted(reply, state);

        tracerProvider.Shutdown();
        tracerProvider.Dispose();
        WcfInstrumentationActivitySource.Options = null;

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(action, activity.DisplayName);
        Assert.Equal(action, activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
        Assert.DoesNotContain(activity.TagObjects, t => t.Key == SemanticConventions.AttributeRpcMethodOriginal);
    }

    [Fact]
    public void OutgoingRequestUsesStableRpcMethodWhenStableRpcConventionsAreOptedIn()
    {
        using var scope = WcfTestHelpers.UseRpcSemanticConvention(RpcSemanticConventionMode.Stable);

        List<Activity> stoppedActivities = [];

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            ActivityStopped = stoppedActivities.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation()
            .Build();

        using var request = Message.CreateMessage(MessageVersion.Soap11, action: string.Empty);
        request.Properties[TelemetryContextMessageProperty.Name] = new TelemetryContextMessageProperty(
            new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = new ActionMetadata(contractName: null, operationName: "ExecuteWithEmptyActionName"),
            });

        var state = ClientChannelInstrumentation.BeforeSendRequest(
            request,
            new Uri(this.serviceBaseUri, "/Service"));

        using var reply = Message.CreateMessage(MessageVersion.Soap11, action: WcfTestHelpers.GetContractQualifiedMethod("ExecuteWithEmptyActionNameResponse"));
        ClientChannelInstrumentation.AfterRequestCompleted(reply, state);

        tracerProvider.Shutdown();
        tracerProvider.Dispose();
        WcfInstrumentationActivitySource.Options = null;

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal("ExecuteWithEmptyActionName", activity.DisplayName);
        Assert.Equal("ExecuteWithEmptyActionName", activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
        Assert.DoesNotContain(activity.TagObjects, t => t.Key == SemanticConventions.AttributeRpcService);
    }

    [Theory]
    [InlineData("https://example.com/OrderService/SubmitOrder")]
    [InlineData("urn:example:orders#SubmitOrder")]
    public void OutgoingRequestUsesOtherRpcMethodAndOriginalMethodWhenStableRpcConventionsAreOptedIn(string action)
    {
        using var scope = WcfTestHelpers.UseRpcSemanticConvention(RpcSemanticConventionMode.Stable);

        List<Activity> stoppedActivities = [];

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            ActivityStopped = stoppedActivities.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation()
            .Build();

        using var request = Message.CreateMessage(MessageVersion.Soap11, action);

        var state = ClientChannelInstrumentation.BeforeSendRequest(
            request,
            new Uri(this.serviceBaseUri, "/Service"));

        using var reply = Message.CreateMessage(MessageVersion.Soap11, action: $"{action}Response");
        ClientChannelInstrumentation.AfterRequestCompleted(reply, state);

        tracerProvider.Shutdown();
        tracerProvider.Dispose();
        WcfInstrumentationActivitySource.Options = null;

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, activity.DisplayName);
        Assert.Equal(SemanticConventions.AttributeRpcMethodOther, activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
        Assert.Equal(action, activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethodOriginal).Value);
    }

    [Fact]
    public void OutgoingRequestEmitsLegacyAndStableRpcConventionsWhenDupIsOptedIn()
    {
        using var scope = WcfTestHelpers.UseRpcSemanticConvention(RpcSemanticConventionMode.Duplicate);

        List<Activity> stoppedActivities = [];

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            ActivityStopped = stoppedActivities.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation()
            .Build();

        var action = WcfTestHelpers.GetContractQualifiedMethod("Execute");

        using var request = Message.CreateMessage(MessageVersion.Soap11, action);
        request.Properties[TelemetryContextMessageProperty.Name] = new TelemetryContextMessageProperty(
            new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [action] = new ActionMetadata(contractName: WcfTestHelpers.ServiceContractName, operationName: "Execute"),
            });

        var state = ClientChannelInstrumentation.BeforeSendRequest(
            request,
            new Uri(this.serviceBaseUri, "/Service"));

        using var reply = Message.CreateMessage(MessageVersion.Soap11, action: WcfTestHelpers.GetContractQualifiedMethod("ExecuteResponse"));
        ClientChannelInstrumentation.AfterRequestCompleted(reply, state);

        tracerProvider.Shutdown();
        tracerProvider.Dispose();
        WcfInstrumentationActivitySource.Options = null;

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(action, activity.DisplayName);
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcSystem));
        Assert.Equal(WcfTestHelpers.ServiceContractName, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcService));
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcSystemName));
        Assert.Equal(action, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcMethod));
        Assert.Equal(this.serviceBaseUri.Host, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeNetPeerName));
        Assert.Equal(this.serviceBaseUri.Port, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeNetPeerPort));
        Assert.Equal(this.serviceBaseUri.Host, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeServerAddress));
        Assert.Equal(this.serviceBaseUri.Port, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeServerPort));
    }
}
