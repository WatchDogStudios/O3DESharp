/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Runtime.InteropServices;

namespace O3DE.Interop
{
    /// <summary>
    /// Version and shape of the frozen C++ <-> managed ABI.
    ///
    /// The editor (CoreCLR + Coral) and the shipping desktop build (NativeAOT)
    /// are two artifacts built from one codebase. They differ only in HOW the
    /// two structs below are exchanged - Coral uploads NativeImports by name
    /// and the host resolves exports through CoralNativeThunkHost; the
    /// NativeAOT image hands both across in one exported call. The struct
    /// SHAPES are identical in every build, which is what makes one C#
    /// codebase and one C++ integration serve both.
    ///
    /// The C++ mirror is Code/Source/Scripting/HostAbi.h. Editor/Tests/
    /// test_host_abi_contract.py fails the build if the two drift.
    /// </summary>
    public static class HostAbi
    {
        /// <summary>
        /// Bumped whenever a field is added, removed or reordered in either
        /// struct. Host, editor build and shipping build must agree: a host
        /// that reads a version it does not recognise must refuse to run
        /// rather than reinterpret pointers.
        /// </summary>
        public const uint Version = 1;
    }

    /// <summary>
    /// Function pointers C++ exposes to managed code. v1 mirrors
    /// O3DE.InternalCalls (InternalCalls.cs) field-for-field, in declaration
    /// order - that ordering IS the ABI.
    ///
    /// O3DE.Reflection.ReflectionInternalCalls is registered separately by
    /// GenericDispatcher and is deliberately NOT part of v1. Adding it is an
    /// ABI v2 change, which is exactly what the Version field exists for.
    ///
    /// Fields are IntPtr rather than delegate* unmanaged<...> so this
    /// file stays free of Coral.Managed.Interop types (NativeString, Bool32)
    /// and can be described in plain C++ as void*. The typed signatures live
    /// on the call sites, not in the transport struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeImports
    {
        /// <summary>Must equal <see cref="HostAbi.Version"/>. 0 means "not populated".</summary>
        public uint Version;

        // ============================================================
        // Logging
        // ============================================================
        public IntPtr Log_Info;
        public IntPtr Log_Warning;
        public IntPtr Log_Error;

        // ============================================================
        // Entity
        // ============================================================
        public IntPtr Entity_IsValid;
        public IntPtr Entity_GetName;
        public IntPtr Entity_SetName;
        public IntPtr Entity_IsActive;
        public IntPtr Entity_Activate;
        public IntPtr Entity_Deactivate;
        public IntPtr Entity_Destroy;
        public IntPtr Entity_FindByName;
        public IntPtr Entity_GetChildCount;
        public IntPtr Entity_GetChildAtIndex;
        public IntPtr Entity_GetChildren;

        // ============================================================
        // Transform
        // ============================================================
        public IntPtr Transform_GetWorldPosition;
        public IntPtr Transform_SetWorldPosition;
        public IntPtr Transform_GetLocalPosition;
        public IntPtr Transform_SetLocalPosition;
        public IntPtr Transform_GetWorldRotation;
        public IntPtr Transform_SetWorldRotation;
        public IntPtr Transform_GetWorldRotationEuler;
        public IntPtr Transform_SetWorldRotationEuler;
        public IntPtr Transform_GetLocalScale;
        public IntPtr Transform_SetLocalScale;
        public IntPtr Transform_GetLocalUniformScale;
        public IntPtr Transform_SetLocalUniformScale;
        public IntPtr Transform_GetForward;
        public IntPtr Transform_GetRight;
        public IntPtr Transform_GetUp;
        public IntPtr Transform_GetParentId;
        public IntPtr Transform_SetParent;

        // ============================================================
        // Input
        // ============================================================
        public IntPtr Input_IsKeyDown;
        public IntPtr Input_IsKeyPressed;
        public IntPtr Input_IsKeyReleased;
        public IntPtr Input_IsMouseButtonDown;
        public IntPtr Input_IsMouseButtonPressed;
        public IntPtr Input_IsMouseButtonReleased;
        public IntPtr Input_GetMousePosition;
        public IntPtr Input_GetMouseDelta;
        public IntPtr Input_GetAxis;

        // ============================================================
        // Time
        // ============================================================
        public IntPtr Time_GetDeltaTime;
        public IntPtr Time_GetTotalTime;
        public IntPtr Time_GetTimeScale;
        public IntPtr Time_SetTimeScale;
        public IntPtr Time_GetFrameCount;

        // ============================================================
        // Physics
        // ============================================================
        public IntPtr Physics_Raycast;

        // ============================================================
        // Component
        // ============================================================
        public IntPtr Component_HasComponent;
    }

    /// <summary>
    /// Function pointers managed code exposes to C++. Every field is an
    /// [UnmanagedCallersOnly] static emitted by HostExportsGenerator into
    /// ManagedExports.g.cs.
    ///
    /// Signatures (frozen - the generator and the C++ host both hard-code them):
    ///   CreateInstance    delegate* unmanaged<byte*, int>
    ///   InvokeLifecycle   delegate* unmanaged<int, int, float, int>
    ///   DispatchEBusEvent delegate* unmanaged<long, byte*, byte*, byte*, int, int>
    ///   DestroyInstance   delegate* unmanaged<int, void>
    ///   HotReloadSwap     delegate* unmanaged<int>
    ///
    /// Strings are UTF-8 and results are written into a caller-supplied
    /// buffer (snprintf-style: the return value is the number of bytes the
    /// result needs, so a short buffer is a retry rather than a truncation).
    /// That deliberately keeps allocation ownership from crossing the seam,
    /// so no Free export is needed and the struct stays at five fields.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ManagedExports
    {
        /// <summary>Must equal <see cref="HostAbi.Version"/>. 0 means "not populated".</summary>
        public uint Version;

        public IntPtr CreateInstance;
        public IntPtr InvokeLifecycle;
        public IntPtr DispatchEBusEvent;
        public IntPtr DestroyInstance;

        /// <summary>
        /// Editor-only. Shipping AOT thunks return 0 and the host reports
        /// SupportsHotReload() == false; there is no ALC to swap.
        /// </summary>
        public IntPtr HotReloadSwap;
    }
}
