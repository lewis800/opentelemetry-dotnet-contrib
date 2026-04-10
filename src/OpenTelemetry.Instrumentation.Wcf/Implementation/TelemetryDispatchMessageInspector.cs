// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NETFRAMEWORK
using System.Diagnostics;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Dispatcher;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Internal;
using OpenTelemetry.Trace;
using static OpenTelemetry.Internal.RpcSemanticConventionHelper;

namespace OpenTelemetry.Instrumentation.Wcf.Implementation;

/// <summary>
/// An <see cref="IDispatchMessageInspector"/> implementation which adds telemetry to incoming requests.
/// </summary>
internal class TelemetryDispatchMessageInspector : IDispatchMessageInspector
{
    private readonly IDictionary<string, ActionMetadata> actionMappings;

    internal TelemetryDispatchMessageInspector(IDictionary<string, ActionMetadata> actionMappings)
    {
        Guard.ThrowIfNull(actionMappings);

        this.actionMappings = actionMappings;
    }

    /// <inheritdoc/>
    public object? AfterReceiveRequest(ref Message request, IClientChannel channel, InstanceContext instanceContext)
    {
        Guard.ThrowIfNull(request);
        Guard.ThrowIfNull(channel);

        try
        {
            if (WcfInstrumentationActivitySource.Options == null || WcfInstrumentationActivitySource.Options.IncomingRequestFilter?.Invoke(request) == false)
            {
                WcfInstrumentationEventSource.Log.RequestIsFilteredOut();
                return null;
            }
        }
        catch (Exception ex)
        {
            WcfInstrumentationEventSource.Log.RequestFilterException(ex);
            return null;
        }

        var textMapPropagator = Propagators.DefaultTextMapPropagator;
        var ctx = textMapPropagator.Extract(default, request, WcfInstrumentationActivitySource.MessageHeaderValuesGetter);

        var options = WcfInstrumentationActivitySource.Options!;
        var action = !string.IsNullOrEmpty(request.Headers.Action)
            ? request.Headers.Action
            : string.Empty;
        this.actionMappings.TryGetValue(action, out var actionMetadata);
        var legacyRpcMethod = WcfInstrumentationConstants.GetLegacyRpcMethod(actionMetadata, action);
        var legacyDisplayName = string.IsNullOrEmpty(action) ? null : action;
        var legacyRpcService = WcfInstrumentationConstants.GetLegacyRpcService(actionMetadata);
        var (rpcMethod, rpcMethodOriginal) = WcfInstrumentationConstants.ResolveRpcMethod(actionMetadata, action);
        var displayName = GetConventionValue(options.EmitNewAttributes, legacyDisplayName, WcfInstrumentationConstants.GetSpanName(rpcMethod));
        var localAddressUri = channel.LocalAddress?.Uri;

        var activity = WcfInstrumentationActivitySource.ActivitySource.StartActivity(
            WcfInstrumentationActivitySource.IncomingRequestActivityName,
            ActivityKind.Server,
            ctx.ActivityContext,
            WcfInstrumentationConstants.GetRpcCreationTags(
                options.EmitOldAttributes,
                options.EmitNewAttributes,
                legacyRpcMethod,
                rpcMethod,
                legacyRpcService,
                localAddressUri,
                (SemanticConventions.AttributeNetHostName, SemanticConventions.AttributeNetHostPort)));

        if (activity != null)
        {
            if (displayName != null)
            {
                activity.DisplayName = displayName;
            }

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

                if (localAddressUri != null)
                {
                    SetTag(activity, options.EmitOldAttributes, options.EmitNewAttributes, (SemanticConventions.AttributeNetHostName, SemanticConventions.AttributeServerAddress), localAddressUri.Host);
                    SetTag(activity, options.EmitOldAttributes, options.EmitNewAttributes, (SemanticConventions.AttributeNetHostPort, SemanticConventions.AttributeServerPort), localAddressUri.Port);

                    activity.SetTag(WcfInstrumentationConstants.AttributeWcfChannelScheme, localAddressUri.Scheme);
                    activity.SetTag(WcfInstrumentationConstants.AttributeWcfChannelPath, localAddressUri.LocalPath);
                }

                try
                {
                    options.Enrich?.Invoke(activity, WcfEnrichEventNames.AfterReceiveRequest, request);
                }
                catch (Exception ex)
                {
                    WcfInstrumentationEventSource.Log.EnrichmentException(ex);
                }
            }

            if (options.RecordException || options.EmitNewAttributes)
            {
                OperationContext.Current?.Extensions.Add(new WcfOperationContext(activity));
            }

            if (textMapPropagator is not TraceContextPropagator)
            {
                Baggage.Current = ctx.Baggage;
            }
        }

        return activity;
    }

    /// <inheritdoc/>
    public void BeforeSendReply(ref Message reply, object? correlationState)
    {
        if (correlationState is Activity activity)
        {
            var options = WcfInstrumentationActivitySource.Options!;
            if (activity.IsAllDataRequested && reply != null)
            {
                if (reply.IsFault)
                {
                    activity.SetStatus(ActivityStatusCode.Error);

                    if (options.EmitNewAttributes && activity.GetTagItem(SemanticConventions.AttributeErrorType) == null)
                    {
                        activity.SetTag(SemanticConventions.AttributeErrorType, WcfInstrumentationConstants.GetErrorType());
                    }
                }

                activity.SetTag(WcfInstrumentationConstants.AttributeSoapReplyAction, reply.Headers.Action);
                try
                {
                    options.Enrich?.Invoke(activity, WcfEnrichEventNames.BeforeSendReply, reply);
                }
                catch (Exception ex)
                {
                    WcfInstrumentationEventSource.Log.EnrichmentException(ex);
                }
            }

            activity.Stop();

            if (Propagators.DefaultTextMapPropagator is not TraceContextPropagator)
            {
                Baggage.Current = default;
            }
        }
    }
}

#endif
