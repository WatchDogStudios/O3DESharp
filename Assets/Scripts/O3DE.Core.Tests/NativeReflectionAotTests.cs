//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Collections.Generic;
using System.Text.Json;
using O3DE.Reflection;

namespace O3DE.Core.Tests;

/// <summary>
/// NativeReflection's argument serializer is the managed side's only
/// reflection-based JsonSerializer use, and under NativeAOT reflection-based
/// serialization needs runtime code generation that is not there - it throws
/// at the first EBus broadcast rather than failing to build.
///
/// Moving to a source-generated JsonSerializerContext fixes that, but the wire
/// format is a contract with the C++ marshaler
/// (O3DESharp::Marshaling::JsonValueToBehaviorParameter), so these pin the
/// exact output for every type SerializeArgumentToObject can produce. If the
/// context resolver ever misses a type, serialization changes shape or throws -
/// both caught here.
/// </summary>
public class NativeReflectionAotTests
{
    [Fact]
    public void Context_ResolvesEveryTypeTheArgumentSerializerCanProduce()
    {
        // The closed set from SerializeArgumentToObject. A type missing from
        // the context throws NotSupportedException under AOT at the first call.
        var closedSet = new[]
        {
            typeof(bool), typeof(int), typeof(long), typeof(ulong),
            typeof(float), typeof(double), typeof(string),
            typeof(float[]), typeof(List<object?>),
        };

        foreach (var type in closedSet)
        {
            NativeReflectionJsonContext.Default.GetTypeInfo(type)
                .Should().NotBeNull($"{type.Name} is producible by SerializeArgumentToObject");
        }
    }

    [Theory]
    [InlineData(true, "[true]")]
    [InlineData(42, "[42]")]
    [InlineData("hello", "[\"hello\"]")]
    public void SerializeArguments_PrimitiveWireFormatIsUnchanged(object arg, string expected)
    {
        NativeReflection.SerializeArgumentsForTest(new[] { arg }).Should().Be(expected);
    }

    [Fact]
    public void SerializeArguments_MathTypesStayAsNumberArrays()
    {
        // The C++ marshaler maps the 3- vs 4-element array shape to
        // Vector3 / Quaternion. Changing this breaks every EBus call with a
        // math argument, silently, at runtime.
        NativeReflection.SerializeArgumentsForTest(new object[] { new Vector3(1f, 2f, 3f) })
            .Should().Be("[[1,2,3]]");
        NativeReflection.SerializeArgumentsForTest(new object[] { new Quaternion(0f, 0f, 0f, 1f) })
            .Should().Be("[[0,0,0,1]]");
    }

    [Fact]
    public void SerializeArguments_MixedArgumentsKeepTheirOrder()
    {
        NativeReflection.SerializeArgumentsForTest(new object[] { 1, "two", 3.5, true })
            .Should().Be("[1,\"two\",3.5,true]");
    }

    [Fact]
    public void SerializeArguments_EmptyIsAnEmptyArray()
    {
        NativeReflection.SerializeArgumentsForTest(System.Array.Empty<object>()).Should().Be("[]");
    }

    [Fact]
    public void SerializeArguments_UnsupportedType_StillThrowsLoudly()
    {
        // The pre-existing NotSupportedException must survive the AOT change:
        // a silently stringified argument is worse than a hard failure, because
        // the C++ marshaler cannot consume a display string.
        var act = () => NativeReflection.SerializeArgumentsForTest(new object[] { new object() });
        act.Should().Throw<System.NotSupportedException>();
    }
}
