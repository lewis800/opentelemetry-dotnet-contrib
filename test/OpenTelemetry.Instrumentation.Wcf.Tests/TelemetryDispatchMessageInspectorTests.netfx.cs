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

    public void Dispose()
    {
        this.serviceHost?.Close();
    }

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
            ShouldListenTo = activitySource => true,
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
            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider?.Shutdown();
            tracerProvider?.Dispose();

            WcfInstrumentationActivitySource.Options = null;
        }

        if (instrument && !filter)
        {
            Assert.NotEmpty(stoppedActivities);
            Assert.Single(stoppedActivities);

            var activity = stoppedActivities[0];

            if (emptyOrNullAction)
            {
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("ExecuteWithEmptyActionName"), activity.DisplayName);
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("ExecuteWithEmptyActionName"), activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("ExecuteWithEmptyActionNameResponse"), activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.SoapReplyActionTag).Value);
            }
            else
            {
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("Execute"), activity.DisplayName);
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("Execute"), activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
                Assert.Equal(WcfTestHelpers.GetContractQualifiedMethod("ExecuteResponse"), activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.SoapReplyActionTag).Value);
            }

            Assert.Equal(WcfInstrumentationActivitySource.IncomingRequestActivityName, activity.OperationName);
            Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeRpcSystemName));
            Assert.DoesNotContain(activity.TagObjects, t => t.Key == SemanticConventions.AttributeRpcService);
            Assert.Equal(this.serviceBaseUri.Host, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeServerAddress));
            Assert.Equal(this.serviceBaseUri.Port, WcfTestHelpers.GetTagValue(activity, SemanticConventions.AttributeServerPort));
            Assert.Equal("net.tcp", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.WcfChannelSchemeTag).Value);
            Assert.Equal("/Service", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.WcfChannelPathTag).Value);

            if (includeVersion)
            {
                Assert.Equal("Soap12 (http://www.w3.org/2003/05/soap-envelope) Addressing10 (http://www.w3.org/2005/08/addressing)", activity.TagObjects.FirstOrDefault(t => t.Key == WcfInstrumentationConstants.SoapMessageVersionTag).Value);
            }

            if (enrich && !enrichmentException)
            {
                Assert.Equal(WcfEnrichEventNames.AfterReceiveRequest, activity.TagObjects.Single(t => t.Key == "server.afterreceiverequest").Value);
                Assert.Equal(WcfEnrichEventNames.BeforeSendReply, activity.TagObjects.Single(t => t.Key == "server.beforesendreply").Value);
            }
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
            ShouldListenTo = activitySource => true,
            ActivityStarted = startedActivities.Add,
            ActivityStopped = stoppedActivities.Add,
        };

        activityListener.ExceptionRecorder += (Activity activity, Exception ex, ref TagList tags) =>
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

            if (client.State == CommunicationState.Faulted)
            {
                client.Abort();
            }
            else
            {
                client.Close();
            }

            tracerProvider?.Shutdown();
            tracerProvider?.Dispose();

            WcfInstrumentationActivitySource.Options = null;
        }

        Assert.NotEmpty(stoppedActivities);
        var activity = Assert.Single(stoppedActivities);

        if (recordException && triggerException)
        {
            Assert.Collection(recordedExceptions, e => Assert.IsType<Exception>(e));
        }
        else
        {
            Assert.Empty(recordedExceptions);
        }
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
        Assert.Equal("ExecuteWithEmptyActionName", activity.DisplayName);
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
        Assert.Equal(WcfInstrumentationConstants.WcfSystemValue, activity.DisplayName);
        Assert.Equal(SemanticConventions.AttributeRpcMethodOther, activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethod).Value);
        Assert.Equal(action, activity.TagObjects.FirstOrDefault(t => t.Key == SemanticConventions.AttributeRpcMethodOriginal).Value);
    }
}

#endif
