// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.ServiceModel.Channels;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Internal;
using OpenTelemetry.Trace;
using static OpenTelemetry.Internal.RpcSemanticConventionHelper;

namespace OpenTelemetry.Instrumentation.Wcf.Implementation;

internal static class ClientChannelInstrumentation
{
    public static RequestTelemetryState BeforeSendRequest(Message request, Uri? remoteChannelAddress)
    {
        if (!ShouldInstrumentRequest(request))
        {
            return new RequestTelemetryState { SuppressionScope = SuppressDownstreamInstrumentation() };
        }

        var options = WcfInstrumentationActivitySource.Options!;
        var action = string.Empty;
        if (!string.IsNullOrEmpty(request.Headers.Action))
        {
            action = request.Headers.Action;
        }

        var actionMetadata = GetActionMetadata(request, action);
        var legacyRpcMethod = WcfInstrumentationConstants.GetLegacyRpcMethod(actionMetadata, action);
        var legacyDisplayName = string.IsNullOrEmpty(action) ? null : action;
        var legacyRpcService = WcfInstrumentationConstants.GetLegacyRpcService(actionMetadata);
        var (rpcMethod, rpcMethodOriginal) = WcfInstrumentationConstants.ResolveRpcMethod(actionMetadata, action);
        var displayName = GetConventionValue(options.EmitNewAttributes, legacyDisplayName, WcfInstrumentationConstants.GetSpanName(rpcMethod));
        var remoteAddressUri = request.Headers.To ?? remoteChannelAddress;

        var activity = WcfInstrumentationActivitySource.ActivitySource.StartActivity(
            WcfInstrumentationActivitySource.OutgoingRequestActivityName,
            ActivityKind.Client,
            default(ActivityContext),
            WcfInstrumentationConstants.GetRpcCreationTags(
                options.EmitOldAttributes,
                options.EmitNewAttributes,
                legacyRpcMethod,
                rpcMethod,
                legacyRpcService,
                remoteAddressUri,
                (SemanticConventions.AttributeNetPeerName, SemanticConventions.AttributeNetPeerPort)));
        var suppressionScope = SuppressDownstreamInstrumentation();

        if (activity != null)
        {
            if (displayName != null)
            {
                activity.DisplayName = displayName;
            }

            Propagators.DefaultTextMapPropagator.Inject(
                new PropagationContext(activity.Context, Baggage.Current),
                request,
                WcfInstrumentationActivitySource.MessageHeaderValueSetter);

            if (activity.IsAllDataRequested)
            {
                SetTag(activity, options.EmitOldAttributes, options.EmitNewAttributes, (SemanticConventions.AttributeRpcSystem, SemanticConventions.AttributeRpcSystemName), WcfInstrumentationConstants.WcfSystemValue);
                SetTag(activity, options.EmitOldAttributes, options.EmitNewAttributes, (SemanticConventions.AttributeRpcService, null), (legacyRpcService, null));

                // `rpc.method` changed meaning between the old and new RPC conventions.
                // When both are requested, the stable value is set last and wins.
                SetTag(activity, options.EmitOldAttributes, options.EmitNewAttributes, (SemanticConventions.AttributeRpcMethod, SemanticConventions.AttributeRpcMethod), (legacyRpcMethod, rpcMethod));
                SetTag(activity, options.EmitOldAttributes, options.EmitNewAttributes, (null, SemanticConventions.AttributeRpcMethodOriginal), (null, rpcMethodOriginal));

                if (options.SetSoapMessageVersion)
                {
                    activity.SetTag(WcfInstrumentationConstants.AttributeSoapMessageVersion, request.Version.ToString());
                }

                if (remoteAddressUri != null)
                {
                    SetTag(activity, options.EmitOldAttributes, options.EmitNewAttributes, (SemanticConventions.AttributeNetPeerName, SemanticConventions.AttributeServerAddress), remoteAddressUri.Host);
                    SetTag(activity, options.EmitOldAttributes, options.EmitNewAttributes, (SemanticConventions.AttributeNetPeerPort, SemanticConventions.AttributeServerPort), remoteAddressUri.Port);

                    activity.SetTag(WcfInstrumentationConstants.AttributeWcfChannelScheme, remoteAddressUri.Scheme);
                    activity.SetTag(WcfInstrumentationConstants.AttributeWcfChannelPath, remoteAddressUri.LocalPath);
                }

                if (request.Properties.Via != null)
                {
                    activity.SetTag(WcfInstrumentationConstants.AttributeSoapVia, request.Properties.Via.ToString());
                }

                try
                {
                    options.Enrich?.Invoke(activity, WcfEnrichEventNames.BeforeSendRequest, request);
                }
                catch (Exception ex)
                {
                    WcfInstrumentationEventSource.Log.EnrichmentException(ex);
                }
            }
        }

        return new RequestTelemetryState
        {
            SuppressionScope = suppressionScope,
            Activity = activity,
        };
    }

    public static void AfterRequestCompleted(Message? reply, RequestTelemetryState? state, Exception? exception = null)
    {
        Guard.ThrowIfNull(state);
        state.SuppressionScope?.Dispose();
        if (state.Activity is Activity activity)
        {
            var options = WcfInstrumentationActivitySource.Options!;
            if (activity.IsAllDataRequested)
            {
                if (reply == null || reply.IsFault)
                {
                    activity.SetStatus(ActivityStatusCode.Error);

                    if (options.EmitNewAttributes)
                    {
                        activity.SetTag(SemanticConventions.AttributeErrorType, WcfInstrumentationConstants.GetErrorType(exception));
                    }

                    if (options.RecordException && exception != null)
                    {
                        activity.AddException(exception);
                    }
                }

                if (reply != null)
                {
                    activity.SetTag(WcfInstrumentationConstants.AttributeSoapReplyAction, reply.Headers.Action);
                    try
                    {
                        options.Enrich?.Invoke(activity, WcfEnrichEventNames.AfterReceiveReply, reply);
                    }
                    catch (Exception ex)
                    {
                        WcfInstrumentationEventSource.Log.EnrichmentException(ex);
                    }
                }
            }

            activity.Stop();
        }
    }

    private static IDisposable? SuppressDownstreamInstrumentation() =>
        WcfInstrumentationActivitySource.Options?.SuppressDownstreamInstrumentation ?? false
            ? SuppressInstrumentationScope.Begin()
            : null;

    private static ActionMetadata? GetActionMetadata(Message request, string action)
    {
        if (request.Properties.TryGetValue(TelemetryContextMessageProperty.Name, out var telemetryContextProperty))
        {
            var actionMappings = (telemetryContextProperty as TelemetryContextMessageProperty)?.ActionMappings;
            if (actionMappings != null && actionMappings.TryGetValue(action, out var metadata))
            {
                return metadata;
            }
        }

        return null;
    }

    private static bool ShouldInstrumentRequest(Message request)
    {
        try
        {
            if (WcfInstrumentationActivitySource.Options == null || WcfInstrumentationActivitySource.Options.OutgoingRequestFilter?.Invoke(request) == false)
            {
                WcfInstrumentationEventSource.Log.RequestIsFilteredOut();
                return false;
            }
        }
        catch (Exception ex)
        {
            WcfInstrumentationEventSource.Log.RequestFilterException(ex);
            return false;
        }

        return true;
    }
}
