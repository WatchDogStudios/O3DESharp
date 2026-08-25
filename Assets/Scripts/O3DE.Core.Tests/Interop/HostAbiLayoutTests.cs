//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Runtime.InteropServices;
using O3DE.Interop;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// NativeImports / ManagedExports are the whole C++ <-> managed boundary.
/// Three artifacts must agree on their layout: this C# declaration, the C++
/// mirror in Code/Source/Scripting/HostAbi.h, and whatever the shipping
/// NativeAOT image was built from. A field inserted on one side and not the
/// other silently reinterprets every pointer after it - which is memory
/// corruption, not a compile error. These pin the managed side; the
/// cross-language field-order check is Editor/Tests/test_host_abi_contract.py.
/// </summary>
public class HostAbiLayoutTests
{
    // 47 = the number of delegate* unmanaged fields on O3DE.InternalCalls
    // (InternalCalls.cs:30-106). ReflectionInternalCalls is registered
    // separately by GenericDispatcher and is deliberately NOT part of ABI v1.
    private const int NativeImportCount = 47;
    private const int ManagedExportCount = 5;

    [Fact]
    public void Version_IsOne()
    {
        HostAbi.Version.Should().Be(1u,
            "the version field is what lets ABI v2 add ReflectionInternalCalls without silent misreads");
    }

    [Fact]
    public void NativeImports_IsBlittableAndPointerSized()
    {
        // uint Version is followed by pointers, so it is padded up to
        // pointer alignment: total == (1 + N) * IntPtr.Size on both 32- and
        // 64-bit. Stating it that way keeps the assertion arch-agnostic.
        Marshal.SizeOf<NativeImports>().Should().Be((1 + NativeImportCount) * IntPtr.Size);
    }

    [Fact]
    public void ManagedExports_IsBlittableAndPointerSized()
    {
        Marshal.SizeOf<ManagedExports>().Should().Be((1 + ManagedExportCount) * IntPtr.Size);
    }

    [Fact]
    public void NativeImports_FirstAndLastFieldsAreAtTheExpectedOffsets()
    {
        // Pins both ends of the struct: Log_Info is the first pointer after
        // the version word, Component_HasComponent is the last one.
        Marshal.OffsetOf<NativeImports>(nameof(NativeImports.Log_Info))
            .Should().Be(new IntPtr(IntPtr.Size));
        Marshal.OffsetOf<NativeImports>(nameof(NativeImports.Component_HasComponent))
            .Should().Be(new IntPtr(NativeImportCount * IntPtr.Size));
    }

    [Fact]
    public void ManagedExports_FieldsAreInTheFrozenOrder()
    {
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.CreateInstance))
            .Should().Be(new IntPtr(1 * IntPtr.Size));
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.InvokeLifecycle))
            .Should().Be(new IntPtr(2 * IntPtr.Size));
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.DispatchEBusEvent))
            .Should().Be(new IntPtr(3 * IntPtr.Size));
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.DestroyInstance))
            .Should().Be(new IntPtr(4 * IntPtr.Size));
        Marshal.OffsetOf<ManagedExports>(nameof(ManagedExports.HotReloadSwap))
            .Should().Be(new IntPtr(5 * IntPtr.Size));
    }

    [Fact]
    public void DefaultConstructedStructs_CarryNoVersion()
    {
        // A zero-initialised struct must NOT look like a valid v1 struct -
        // the host checks Version before trusting any pointer in it.
        default(NativeImports).Version.Should().Be(0u);
        default(ManagedExports).Version.Should().Be(0u);
    }
}
