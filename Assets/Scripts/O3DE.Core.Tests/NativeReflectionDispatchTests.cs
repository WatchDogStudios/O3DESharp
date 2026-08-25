//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Reflection;
using System.Text.Json;
using O3DE;
using O3DE.Reflection;

namespace O3DE.Core.Tests;

/// <summary>
/// Coverage for the NativeReflection dispatch methods that used to be
/// unconditional NotImplementedException stubs: InvokeStaticMethod,
/// InvokeInstanceMethod, InvokeGlobalMethod, GetProperty/SetProperty, and
/// GetGlobalProperty/SetGlobalProperty. The native ReflectionInternalCalls
/// function pointers are populated by Coral only inside a running host, so
/// they are null here - these tests exercise only what's reachable without
/// a host: the null/invalid-instance guards on the NativeObject overloads,
/// and the SetProperty/SetGlobalProperty wire format.
///
/// Wire format, confirmed by reading GenericDispatcher::SetProperty /
/// SetGlobalProperty in Code/Source/Scripting/Reflection/GenericDispatcher.cpp:
/// both take valueJson as a BARE JSON value, not a single-element array.
/// SetProperty parses it straight into the setter's one value parameter;
/// SetGlobalProperty wraps it in "[%s]" itself before dispatching. Sending
/// an already-wrapped array from C# (e.g. by reusing SerializeArguments)
/// would silently send the wrong shape and fail to marshal on the native
/// side - that's the regression these SerializeValue tests guard against.
///
/// Anything that reaches the unsafe `ReflectionInternalCalls.Reflection_*`
/// function pointer call is NOT covered here: those fields are null outside
/// a Coral host, so invoking them would access-violate the process rather
/// than throw a catchable .NET exception. That surface needs an integration
/// test running inside the real O3DE + Coral host, not a plain unit test.
/// </summary>
public class NativeReflectionDispatchTests
{
    private static NativeObject InvalidInstance(string typeName = "SomeClass")
    {
        // Handle 0 => NativeObject.IsValid is false.
        return new NativeObject(typeName, 0);
    }

    private static object? InvokeSerializeValue(object value)
    {
        MethodInfo method = typeof(NativeReflection).GetMethod(
            "SerializeValue",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "NativeReflection.SerializeValue not found - has it been renamed?");

        try
        {
            return method.Invoke(null, new object?[] { value });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    // --- InvokeInstanceMethod guards ---

    [Fact]
    public void InvokeInstanceMethod_NullInstance_ThrowsArgumentNullException()
    {
        Action act = () => NativeReflection.InvokeInstanceMethod(null!, "Foo");

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("instance");
    }

    [Fact]
    public void InvokeInstanceMethod_InvalidInstance_ThrowsInvalidOperationException()
    {
        NativeObject instance = InvalidInstance();

        Action act = () => NativeReflection.InvokeInstanceMethod(instance, "Foo");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Foo*")
            .WithMessage("*invalid*");
    }

    // --- GetProperty<T> guards ---

    [Fact]
    public void GetProperty_NullInstance_ThrowsArgumentNullException()
    {
        Action act = () => NativeReflection.GetProperty<int>(null!, "Bar");

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("instance");
    }

    [Fact]
    public void GetProperty_InvalidInstance_ThrowsInvalidOperationException()
    {
        NativeObject instance = InvalidInstance();

        Action act = () => NativeReflection.GetProperty<int>(instance, "Bar");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Bar*")
            .WithMessage("*invalid*");
    }

    // --- SetProperty guards ---

    [Fact]
    public void SetProperty_NullInstance_ThrowsArgumentNullException()
    {
        Action act = () => NativeReflection.SetProperty(null!, "Bar", 1);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("instance");
    }

    [Fact]
    public void SetProperty_InvalidInstance_ThrowsInvalidOperationException()
    {
        NativeObject instance = InvalidInstance();

        Action act = () => NativeReflection.SetProperty(instance, "Bar", 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Bar*")
            .WithMessage("*invalid*");
    }

    // --- NativeObject convenience overloads route through the same guards ---

    [Fact]
    public void NativeObject_InvokeMethod_OnInvalidHandle_Throws()
    {
        NativeObject instance = InvalidInstance();

        // NativeObject.InvokeMethod calls ThrowIfDisposed() first, which
        // already rejects a Handle==0 instance before NativeReflection's
        // own guard would ever run - confirms the two guard layers agree.
        Action act = () => instance.InvokeMethod("Foo");

        act.Should().Throw<InvalidOperationException>();
    }

    // --- SetProperty / SetGlobalProperty wire format: bare value, never an array ---

    [Fact]
    public void SerializeValue_Int_IsBareNumber_NotWrappedInArray()
    {
        object? result = InvokeSerializeValue(42);

        result.Should().Be(JsonSerializer.Serialize(42));
        result.Should().NotBe("[42]");
    }

    [Fact]
    public void SerializeValue_String_IsBareJsonString_NotWrappedInArray()
    {
        object? result = InvokeSerializeValue("hello");

        result.Should().Be(JsonSerializer.Serialize("hello"));
        result.Should().NotBe("[\"hello\"]");
    }

    [Fact]
    public void SerializeValue_Bool_IsBareJsonBool_NotWrappedInArray()
    {
        object? result = InvokeSerializeValue(true);

        result.Should().Be(JsonSerializer.Serialize(true));
        result.Should().NotBe("[true]");
    }

    [Fact]
    public void SerializeValue_Vector3_IsSingleArray_NotDoublyWrapped()
    {
        object? result = InvokeSerializeValue(new Vector3(1.0f, 2.0f, 3.0f));

        // Vector3 itself serializes to a 3-element array (the math-type wire
        // shape). It must not be wrapped in a second, outer array - that's
        // what would happen if SetProperty mistakenly used SerializeArguments
        // (array-of-args) instead of SerializeValue (bare value).
        string expected = JsonSerializer.Serialize(new float[] { 1.0f, 2.0f, 3.0f });
        result.Should().Be(expected);
        ((string)result!).Should().NotStartWith("[[");
    }
}
