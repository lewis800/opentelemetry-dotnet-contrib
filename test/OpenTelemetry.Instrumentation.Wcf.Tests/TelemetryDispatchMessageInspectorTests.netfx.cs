// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NETFRAMEWORK
using System.Diagnostics;
using System.ServiceModel;
using System.ServiceModel.Channels;
using OpenTelemetry.Instrumentation.Wcf.Implementation;
using OpenTelemetry.Trace;
using Xunit;
using Xunit.Abstractions;

namespace OpenTelemetry.Instrumentation.Wcf.Tests;

[Collection("WCF")]
public class TelemetryDispatchMessageInspectorTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly Uri serviceBaseUri;
    private readonly ServiceHost serviceHost;

    public TelemetryDispatchMessageInspectorTests(ITestOutputHelper outputHelper)
    {
        this.output = outputHelper;

        var random = new Random();
        var retryCount = 5;
        ServiceHost? createdHost = null;
        while (retryCount > 0)
        {
            try
            {
                this.serviceBaseUri = new Uri($"net.tcp://localhost:{random.Next(2000, 5000)}/");
                createdHost = new ServiceHost(new Service(), this.serviceBaseUri);
                var endpoint = createdHost.AddServiceEndpoint(
                    typeof(IServiceContract),
                    new NetTcpBinding(),
                    "/Service");
                endpoint.Behaviors.Add(new TelemetryEndpointBehavior());
                createdHost.Open();
                break;
            }
            catch (Exception ex)
            {
                this.output.WriteLine(ex.ToString());
                if (createdHost?.State == CommunicationState.Faulted)
                {
                    createdHost.Abort();
                }
                else
                {
                    createdHost?.Close();
                }

                createdHost = null;
                retryCount--;
            }
        }

        if (createdHost == null || this.serviceBaseUri == null)
        {
            throw new InvalidOperationException("ServiceHost could not be started.");
        }

        this.serviceHost = createdHost;
    }

    public void Dispose() => this.serviceHost?.Close();

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, true, true, true, true)]
    public async Task IncomingRequestInstrumentationTest(
        bool instrument,
        bool filter = false,
        bool includeVersion = false,
        bool enrich = false,
        bool enrichmentException = false,
        bool emptyOrNullAction = false)
    {
        List<Activity> stoppedActivities = [];

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            ActivityStopped = stoppedActivities.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        TracerProvider? tracerProvider = null;
        if (instrument)
        {
            tracerProvider = Sdk.CreateTracerProviderBuilder()
                .AddWcfInstrumentation(options =>
                {
                    if (enrich)
                    {
                        options.Enrich = enrichmentException
                            ? (_, _, _) => throw new Exception("Failure whilst enriching activity")
                            : (activity, eventName, _) =>
                            {
                                switch (eventName)
                                {
                                    case WcfEnrichEventNames.AfterReceiveRequest:
                                        activity.SetTag("server.afterreceiverequest", WcfEnrichEventNames.AfterReceiveRequest);
                                        break;
                                    case WcfEnrichEventNames.BeforeSendReply:
                                        activity.SetTag("server.beforesendreply", WcfEnrichEventNames.BeforeSendReply);
                                        break;
                                    default:
                                        break;
                                }
                            };
                    }

                    options.IncomingRequestFilter = _ => !filter;
                    options.SetSoapMessageVersion = includeVersion;
                })
                .Build();
        }

        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            if (emptyOrNullAction)
            {
                await client.ExecuteWithEmptyActionNameAsync(
                    new ServiceRequest(
                        payload: "Hello Open Telemetry!"));
            }
            else
            {
                await client.ExecuteAsync(
                    new ServiceRequest(
                        payload: "Hello Open Telemetry!"));
            }
        }
        finally
        {
            client.AbortOrClose();
            tracerProvider?.Shutdown();
            tracerProvider?.Dispose();

            WcfInstrumentationActivitySource.Options = null;
        }

        if (instrument && !filter)
        {
            Assert.NotEmpty(stoppedActivities);
            var activity = Assert.Single(stoppedActivities);

            if (emptyOrNullAction)
            {
                Assert.Equal(WcfInstrumentationActivitySource.IncomingRequestActivityName, activity.DisplayName);
                Assert.Equal("ExecuteWithEmptyActionName", activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("ExecuteWithEmptyActionNameResponse"), activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.AttributeSoapReplyAction).Value);
            }
            else
            {
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("Execute"), activity.DisplayName);
                Assert.Equal("Execute", activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("ExecuteResponse"), activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.AttributeSoapReplyAction).Value);
            }

            if (includeVersion)
            {
                Assert.Equal("Soap12 (http://www.w3.org/2003/05/soap-envelope) Addressing10 (http://www.w3.org/2005/08/addressing)", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.AttributeSoapMessageVersion).Value);
            }

            if (enrich && !enrichmentException)
            {
                Assert.Equal(WcfEnrichEventNames.AfterReceiveRequest, activity.TagObjects.Single(t => t.Key == "server.afterreceiverequest").Value);
                Assert.Equal(WcfEnrichEventNames.BeforeSendReply, activity.TagObjects.Single(t => t.Key == "server.beforesendreply").Value);
            }

            WcfTestHelpers.AssertIncomingRequestActivityCommon(activity, this.serviceBaseUri);
        }
        else
        {
            Assert.Empty(stoppedActivities);
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task RecordExceptionTest(
        bool recordException,
        bool triggerException,
        bool runAsync)
    {
        List<Activity> stoppedActivities = [];
        List<Activity> startedActivities = [];

        List<Exception> recordedExceptions = [];
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            ActivityStarted = startedActivities.Add,
            ActivityStopped = stoppedActivities.Add,
        };

        activityListener.ExceptionRecorder += (activity, ex, ref tags) =>
        {
            recordedExceptions.Add(ex);
        };

        ActivitySource.AddActivityListener(activityListener);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddWcfInstrumentation(options =>
            {
                options.RecordException = recordException;
            })
            .Build();

        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            if (triggerException)
            {
                if (runAsync)
                {
                    await client.ErrorAsync();
                }
                else
                {
                    client.ErrorSynchronous();
                }
            }
            else
            {
                if (runAsync)
                {
                    await client.ExecuteAsync(
                        new ServiceRequest(
                            payload: "Hello Open Telemetry!"));
                }
                else
                {
                    client.ExecuteSynchronous(
                        new ServiceRequest(
                            payload: "Hello Open Telemetry!"));
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            startedActivities[0].AddTag(nameof(recordException), recordException);
            startedActivities[0].AddTag(nameof(triggerException), triggerException);
            startedActivities[0].AddTag(nameof(runAsync), runAsync);

            client.AbortOrClose();
            tracerProvider?.Shutdown();
            tracerProvider?.Dispose();

            WcfInstrumentationActivitySource.Options = null;
        }

        Assert.NotEmpty(stoppedActivities);
        var activity = Assert.Single(stoppedActivities);

        if (recordException && triggerException)
        {
            Assert.All(recordedExceptions, e => Assert.IsType<Exception>(e));
        }
        else
        {
            Assert.Empty(recordedExceptions);
        }
    }

    [Fact]
    public void IncomingRequestProvidesStableSamplingAttributesAtActivityCreation()
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

        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            client.ExecuteSynchronous(new ServiceRequest(payload: "Hello Open Telemetry!"));
        }
        finally
        {
            client.AbortOrClose();
            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        Assert.NotNull(creationTags);
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, creationTags![SemanticConventions.AttributeRpcSystemName]);
        Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("ExecuteSynchronous"), creationTags[SemanticConventions.AttributeRpcMethod]);
        Assert.Equal(this.serviceBaseUri.Host, creationTags[SemanticConventions.AttributeServerAddress]);
        Assert.Equal(this.serviceBaseUri.Port, creationTags[SemanticConventions.AttributeServerPort]);
    }

    [Fact]
    public void IncomingRequestProvidesLegacyAndStableSamplingAttributesAtActivityCreationWhenDupIsOptedIn()
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

        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            client.ExecuteSynchronous(new ServiceRequest(payload: "Hello Open Telemetry!"));
        }
        finally
        {
            client.AbortOrClose();
            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        Assert.NotNull(creationTags);
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcSystem && Equals(tag.Value, WcfInstrumentationConstants.WcfSystemValue));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcService && Equals(tag.Value, WcfTestHelpers.ServiceContractName));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcMethod && Equals(tag.Value, "ExecuteSynchronous"));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeNetHostName && Equals(tag.Value, this.serviceBaseUri.Host));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeNetHostPort && Equals(tag.Value, this.serviceBaseUri.Port));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcSystemName && Equals(tag.Value, WcfInstrumentationConstants.WcfSystemValue));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeRpcMethod && Equals(tag.Value, WcfTestHelpers.GetContractQualifiedMethod("ExecuteSynchronous")));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeServerAddress && Equals(tag.Value, this.serviceBaseUri.Host));
        Assert.Contains(creationTags!, tag => tag.Key == SemanticConventions.AttributeServerPort && Equals(tag.Value, this.serviceBaseUri.Port));
    }

    [Fact]
    public void IncomingRequestSetsStableErrorTypeWhenServerThrowsAndRecordExceptionIsDisabled()
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
            .AddWcfInstrumentation(options => options.RecordException = false)
            .Build();

        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));

        try
        {
            Assert.ThrowsAny<Exception>(client.ErrorSynchronous);
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
        Assert.Equal(typeof(Exception).FullName, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeErrorType));
    }

    [Fact]
    public void IncomingRequestUsesOperationOnlyRpcMethodWhenActionMetadataContractIsUnavailable()
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

        var inspector = new TelemetryDispatchMessageInspector(new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new ActionMetadata(contractName: null, operationName: "ExecuteWithEmptyActionName"),
        });

        var request = Message.CreateMessage(MessageVersion.Default, action: string.Empty);
        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            var correlationState = inspector.AfterReceiveRequest(ref request, client.InnerChannel, new InstanceContext(new Service()));
            var reply = Message.CreateMessage(MessageVersion.Default, action: WcfTestHelpers.GetContractQualifiedMethod("ExecuteWithEmptyActionNameResponse"));
            inspector.BeforeSendReply(ref reply, correlationState);
            reply.Close();
        }
        finally
        {
            request.Close();

            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(WcfInstrumentationActivitySource.IncomingRequestActivityName, activity.DisplayName);
        Assert.Equal("ExecuteWithEmptyActionName", activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
    }

    [Theory]
    [InlineData("https://example.com/OrderService/SubmitOrder")]
    [InlineData("urn:example:orders#SubmitOrder")]
    public void IncomingRequestUsesOtherRpcMethodAndOriginalMethodWhenActionMappingIsUnavailable(string action)
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

        var inspector = new TelemetryDispatchMessageInspector(new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase));

        var request = Message.CreateMessage(MessageVersion.Default, action);
        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            var correlationState = inspector.AfterReceiveRequest(ref request, client.InnerChannel, new InstanceContext(new Service()));
            var reply = Message.CreateMessage(MessageVersion.Default, action: $"{action}Response");
            inspector.BeforeSendReply(ref reply, correlationState);
            reply.Close();
        }
        finally
        {
            request.Close();

            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(action, activity.DisplayName);
        Assert.Equal(action, activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
        Assert.DoesNotContain(activity.TagObjects, t => t.Key == SemanticConventions.AttributeRpcMethodOriginal);
    }

    [Fact]
    public void IncomingRequestUsesStableRpcMethodWhenStableRpcConventionsAreOptedIn()
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

        var inspector = new TelemetryDispatchMessageInspector(new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = new ActionMetadata(contractName: null, operationName: "ExecuteWithEmptyActionName"),
        });

        var request = Message.CreateMessage(MessageVersion.Default, action: string.Empty);
        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            var correlationState = inspector.AfterReceiveRequest(ref request, client.InnerChannel, new InstanceContext(new Service()));
            var reply = Message.CreateMessage(MessageVersion.Default, action: WcfTestHelpers.GetContractQualifiedMethod("ExecuteWithEmptyActionNameResponse"));
            inspector.BeforeSendReply(ref reply, correlationState);
            reply.Close();
        }
        finally
        {
            request.Close();

            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal("ExecuteWithEmptyActionName", activity.DisplayName);
        Assert.Equal("ExecuteWithEmptyActionName", activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
    }

    [Theory]
    [InlineData("https://example.com/OrderService/SubmitOrder")]
    [InlineData("urn:example:orders#SubmitOrder")]
    public void IncomingRequestUsesOtherRpcMethodAndOriginalMethodWhenStableRpcConventionsAreOptedIn(string action)
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

        var inspector = new TelemetryDispatchMessageInspector(new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase));

        var request = Message.CreateMessage(MessageVersion.Default, action);
        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            var correlationState = inspector.AfterReceiveRequest(ref request, client.InnerChannel, new InstanceContext(new Service()));
            var reply = Message.CreateMessage(MessageVersion.Default, action: $"{action}Response");
            inspector.BeforeSendReply(ref reply, correlationState);
            reply.Close();
        }
        finally
        {
            request.Close();

            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, activity.DisplayName);
        Assert.Equal(SemanticConventions.AttributeRpcMethodOther, activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
        Assert.Equal(action, activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethodOriginal).Value);
    }

    [Fact]
    public void IncomingRequestEmitsLegacyAndStableRpcConventionsWhenDupIsOptedIn()
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
        var inspector = new TelemetryDispatchMessageInspector(new Dictionary<string, ActionMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            [action] = new ActionMetadata(contractName: WcfTestHelpers.ServiceContractName, operationName: "Execute"),
        });

        var request = Message.CreateMessage(MessageVersion.Default, action);
        var client = new ServiceClient(
            new NetTcpBinding(),
            new EndpointAddress(new Uri(this.serviceBaseUri, "/Service")));
        try
        {
            var correlationState = inspector.AfterReceiveRequest(ref request, client.InnerChannel, new InstanceContext(new Service()));
            var reply = Message.CreateMessage(MessageVersion.Default, action: WcfTestHelpers.GetContractQualifiedMethod("ExecuteResponse"));
            inspector.BeforeSendReply(ref reply, correlationState);
            reply.Close();
        }
        finally
        {
            request.Close();

            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider.Shutdown();
            tracerProvider.Dispose();
            WcfInstrumentationActivitySource.Options = null;
        }

        var activity = Assert.Single(stoppedActivities);
        Assert.Equal(action, activity.DisplayName);
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcSystem));
        Assert.Equal(WcfTestHelpers.ServiceContractName, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcService));
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcSystemName));
        Assert.Equal(action, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcMethod));
        Assert.Equal(WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeNetHostName), WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeServerAddress));
        Assert.Equal(WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeNetHostPort), WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeServerPort));
    }
}

#endif
