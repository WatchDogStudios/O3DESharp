//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Reflection;
using O3DE;
using O3DE.Reflection;

namespace O3DE.Core.Tests;

/// <summary>
/// Regression tests for NativeReflection's argument serialization. Covers the
/// fix for a bug where an unrecognized argument type (e.g. Vector2) silently
/// fell through to arg.ToString(), producing a display string the native
/// marshaler could not consume, instead of failing immediately with a clear
/// error naming the offending type.
///
/// SerializeArgumentToObject is private, so these tests invoke it via
/// reflection rather than widening its visibility just for tests.
/// </summary>
public class NativeReflectionSerializationTests
{
    private static object? InvokeSerializeArgumentToObject(object? arg)
    {
        MethodInfo method = typeof(NativeReflection).GetMethod(
            "SerializeArgumentToObject",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "NativeReflection.SerializeArgumentToObject not found - has it been renamed?");

        try
        {
            return method.Invoke(null, new object?[] { arg });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Unwrap so callers can assert on the real exception type/message
            // instead of TargetInvocationException.
            throw ex.InnerException;
        }
    }

    [Fact]
    public void UnsupportedType_ThrowsNotSupportedException_NamingTheType()
    {
        var unsupported = new Vector2(1.0f, 2.0f);

        Action act = () => InvokeSerializeArgumentToObject(unsupported);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Vector2*")
            .WithMessage("*SerializeArgumentToObject*");
    }

    [Fact]
    public void UnsupportedType_DoesNotFallBackToToString()
    {
        var unsupported = new Vector2(1.0f, 2.0f);

        Action act = () => InvokeSerializeArgumentToObject(unsupported);

        // Before the fix, this returned unsupported.ToString() (a display
        // string) instead of throwing - guard against regressing back to that.
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Null_ReturnsNull()
    {
        object? result = InvokeSerializeArgumentToObject(null);

        result.Should().BeNull();
    }

    [Fact]
    public void Vector3_SerializesToThreeElementFloatArray()
    {
        var v = new Vector3(1.0f, 2.0f, 3.0f);

        object? result = InvokeSerializeArgumentToObject(v);

        result.Should().BeOfType<float[]>();
        ((float[])result!).Should().Equal(1.0f, 2.0f, 3.0f);
    }

    [Fact]
    public void Quaternion_SerializesToFourElementFloatArray()
    {
        var q = new Quaternion(1.0f, 2.0f, 3.0f, 4.0f);

        object? result = InvokeSerializeArgumentToObject(q);

        result.Should().BeOfType<float[]>();
        ((float[])result!).Should().Equal(1.0f, 2.0f, 3.0f, 4.0f);
    }

    [Fact]
    public void Bool_PassesThroughUnchanged()
    {
        object? result = InvokeSerializeArgumentToObject(true);

        result.Should().Be(true);
    }

    [Fact]
    public void Int_PassesThroughUnchanged()
    {
        object? result = InvokeSerializeArgumentToObject(42);

        result.Should().Be(42);
    }

    [Fact]
    public void String_PassesThroughUnchanged()
    {
        object? result = InvokeSerializeArgumentToObject("hello");

        result.Should().Be("hello");
    }
}
