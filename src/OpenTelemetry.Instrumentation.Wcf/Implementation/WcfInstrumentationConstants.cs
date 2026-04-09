// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.Wcf;

internal static class WcfInstrumentationConstants
{
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

    public static (string RpcMethod, string? RpcMethodOriginal) ResolveRpcMethod(ActionMetadata? actionMetadata, string action)
    {
        if (actionMetadata != null)
        {
            return (GetRpcMethod(actionMetadata), null);
        }

        return (SemanticConventions.AttributeRpcMethodOther, action);
    }
}
