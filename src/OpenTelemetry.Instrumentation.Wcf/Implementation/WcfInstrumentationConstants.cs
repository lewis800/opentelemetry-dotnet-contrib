// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.Wcf;

internal static class WcfInstrumentationConstants
{
    public const string ErrorTypeOther = "_OTHER";
    public const string AttributeSoapMessageVersion = "soap.message_version";
    public const string AttributeSoapReplyAction = "soap.reply_action";
    public const string AttributeSoapVia = "soap.via";
    public const string AttributeWcfChannelScheme = "wcf.channel.scheme";
    public const string AttributeWcfChannelPath = "wcf.channel.path";

    public const string WcfSystemValue = "dotnet_wcf";

    public static string GetRpcMethod(ActionMetadata actionMetadata) =>
        string.IsNullOrEmpty(actionMetadata.ContractName)
            ? actionMetadata.OperationName
            : $"{actionMetadata.ContractName}/{actionMetadata.OperationName}";

    public static string GetSpanName(string rpcMethod) =>
        string.IsNullOrEmpty(rpcMethod) || rpcMethod == SemanticConventions.AttributeRpcMethodOther
            ? WcfSystemValue
            : rpcMethod;

    public static string GetLegacyRpcMethod(ActionMetadata? actionMetadata, string action) =>
        actionMetadata?.OperationName ?? action;

    public static string? GetLegacyRpcService(ActionMetadata? actionMetadata) => actionMetadata?.ContractName;

    public static IEnumerable<KeyValuePair<string, object?>> GetRpcCreationTags(
        bool emitOldAttributes,
        bool emitNewAttributes,
        string legacyRpcMethod,
        string rpcMethod,
        string? legacyRpcService,
        Uri? serverAddress,
        (string Name, string Port) legacyServerAddressAttributes)
    {
        List<KeyValuePair<string, object?>> tags = [];

        if (emitOldAttributes)
        {
            tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeRpcSystem, WcfSystemValue));

            if (legacyRpcService != null)
            {
                tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeRpcService, legacyRpcService));
            }

            tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeRpcMethod, legacyRpcMethod));

            if (serverAddress != null)
            {
                tags.Add(new KeyValuePair<string, object?>(legacyServerAddressAttributes.Name, serverAddress.Host));
                tags.Add(new KeyValuePair<string, object?>(legacyServerAddressAttributes.Port, serverAddress.Port));
            }
        }

        if (emitNewAttributes)
        {
            tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeRpcSystemName, WcfSystemValue));
            tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeRpcMethod, rpcMethod));

            if (serverAddress != null)
            {
                tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeServerAddress, serverAddress.Host));
                tags.Add(new KeyValuePair<string, object?>(SemanticConventions.AttributeServerPort, serverAddress.Port));
            }
        }

        return tags;
    }

    public static string GetErrorType(Exception? exception = null)
        => exception?.GetType().FullName ?? exception?.GetType().Name ?? ErrorTypeOther;

    public static (string RpcMethod, string? RpcMethodOriginal) ResolveRpcMethod(ActionMetadata? actionMetadata, string action)
    {
        if (actionMetadata != null)
        {
            return (GetRpcMethod(actionMetadata), null);
        }

        return (SemanticConventions.AttributeRpcMethodOther, action);
    }
}
