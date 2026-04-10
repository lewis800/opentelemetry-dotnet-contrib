// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace OpenTelemetry.Internal;

/// <summary>
/// Helper class for RPC semantic conventions.
/// </summary>
internal static class RpcSemanticConventionHelper
{
    internal const string SemanticConventionOptInKeyName = "OTEL_SEMCONV_STABILITY_OPT_IN";
    internal static readonly char[] Separator = [',', ' '];

    [Flags]
    internal enum RpcSemanticConvention
    {
        /// <summary>
        /// Instructs an instrumentation library to emit the old experimental RPC attributes.
        /// </summary>
        Old = 0x1,

        /// <summary>
        /// Instructs an instrumentation library to emit the new RPC attributes.
        /// </summary>
        New = 0x2,

        /// <summary>
        /// Instructs an instrumentation library to emit both the old and new attributes.
        /// </summary>
        Dupe = Old | New,
    }

    public static RpcSemanticConvention GetSemanticConventionOptIn(IConfiguration configuration)
    {
        if (TryGetConfiguredValues(configuration, out var values))
        {
            if (values.Contains("rpc/dup"))
            {
                return RpcSemanticConvention.Dupe;
            }
            else if (values.Contains("rpc"))
            {
                return RpcSemanticConvention.New;
            }
        }

        return RpcSemanticConvention.Old;
    }

    public static void SetTag(
        Activity activity,
        bool emitOldAttributes,
        bool emitNewAttributes,
        (string? Old, string? New) attributes,
        object? value)
        => SetTag(activity, emitOldAttributes, emitNewAttributes, attributes, (value, value));

    public static void SetTag(
        Activity activity,
        bool emitOldAttributes,
        bool emitNewAttributes,
        (string? Old, string? New) attributes,
        (object? Old, object? New) values)
    {
        if (emitOldAttributes && attributes.Old != null)
        {
            activity.SetTag(attributes.Old, values.Old);
        }

        if (emitNewAttributes && attributes.New != null)
        {
            activity.SetTag(attributes.New, values.New);
        }
    }

    public static T? GetConventionValue<T>(bool emitNewAttributes, T? oldValue, T? newValue)
        => emitNewAttributes ? newValue : oldValue;

    private static bool TryGetConfiguredValues(IConfiguration configuration, [NotNullWhen(true)] out HashSet<string>? values)
    {
        try
        {
            var stringValue = configuration[SemanticConventionOptInKeyName];

            if (string.IsNullOrWhiteSpace(stringValue))
            {
                values = null;
                return false;
            }

            var stringValues = stringValue!.Split(separator: Separator, options: StringSplitOptions.RemoveEmptyEntries);
            values = new HashSet<string>(stringValues, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch
        {
            values = null;
            return false;
        }
    }
}
