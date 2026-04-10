// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;
using Xunit;
using static OpenTelemetry.Internal.RpcSemanticConventionHelper;

namespace OpenTelemetry.Internal.Tests;

public class RpcSemanticConventionHelperTests
{
    public static IEnumerable<object[]> TestCases =>
    [
        [null!, nameof(RpcSemanticConvention.Old)],
        [string.Empty, nameof(RpcSemanticConvention.Old)],
        [" ", nameof(RpcSemanticConvention.Old)],
        ["junk", nameof(RpcSemanticConvention.Old)],
        ["none", nameof(RpcSemanticConvention.Old)],
        ["NONE", nameof(RpcSemanticConvention.Old)],
        ["rpc", nameof(RpcSemanticConvention.New)],
        ["RPC", nameof(RpcSemanticConvention.New)],
        ["rpc/dup", nameof(RpcSemanticConvention.Dupe)],
        ["RPC/DUP", nameof(RpcSemanticConvention.Dupe)],
        ["junk,,junk", nameof(RpcSemanticConvention.Old)],
        ["junk,JUNK", nameof(RpcSemanticConvention.Old)],
        ["junk1,junk2", nameof(RpcSemanticConvention.Old)],
        ["junk,rpc", nameof(RpcSemanticConvention.New)],
        ["junk,rpc , rpc ,junk", nameof(RpcSemanticConvention.New)],
        ["junk,rpc/dup", nameof(RpcSemanticConvention.Dupe)],
        ["junk, rpc/dup ", nameof(RpcSemanticConvention.Dupe)],
        ["rpc/dup,rpc", nameof(RpcSemanticConvention.Dupe)],
        ["rpc,rpc/dup", nameof(RpcSemanticConvention.Dupe)],
    ];

    [Fact]
    public void VerifyFlags()
    {
        var testValue = RpcSemanticConvention.Dupe;
        Assert.True(testValue.HasFlag(RpcSemanticConvention.Old));
        Assert.True(testValue.HasFlag(RpcSemanticConvention.New));

        testValue = RpcSemanticConvention.Old;
        Assert.True(testValue.HasFlag(RpcSemanticConvention.Old));
        Assert.False(testValue.HasFlag(RpcSemanticConvention.New));

        testValue = RpcSemanticConvention.New;
        Assert.False(testValue.HasFlag(RpcSemanticConvention.Old));
        Assert.True(testValue.HasFlag(RpcSemanticConvention.New));
    }

    [Theory]
    [MemberData(nameof(TestCases))]
    public void VerifyGetSemanticConventionOptIn_UsingEnvironmentVariable(string input, string expectedValue)
    {
        using (EnvironmentVariableScope.Create(SemanticConventionOptInKeyName, input))
        {
#if NET
            var expected = Enum.Parse<RpcSemanticConvention>(expectedValue);
#else
            var expected = Enum.Parse(typeof(RpcSemanticConvention), expectedValue);
#endif
            Assert.Equal(expected, GetSemanticConventionOptIn(new ConfigurationBuilder().AddEnvironmentVariables().Build()));
        }
    }

    [Theory]
    [MemberData(nameof(TestCases))]
    public void VerifyGetSemanticConventionOptIn_UsingIConfiguration(string input, string expectedValue)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SemanticConventionOptInKeyName] = input })
            .Build();

#if NET
        var expected = Enum.Parse<RpcSemanticConvention>(expectedValue);
#else
        var expected = Enum.Parse(typeof(RpcSemanticConvention), expectedValue);
#endif
        Assert.Equal(expected, GetSemanticConventionOptIn(configuration));
    }
}
