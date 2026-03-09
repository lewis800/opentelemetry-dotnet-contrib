// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace OpenTelemetry.Instrumentation.Wcf.Tests;

internal static class WcfTestHelpers
{
    public const string ServiceContractName = "http://opentelemetry.io/Service";

    public static object? GetTagValue(Activity activity, string tagName)
        => activity.TagObjects.FirstOrDefault(tag => tag.Key == tagName).Value;

    public static string GetContractQualifiedMethod(string operationName)
        => $"{ServiceContractName}/{operationName}";
}
