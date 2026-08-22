/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>

namespace O3DESharp
{
    //! Result of host initialization. Lives here (not in CoralHostManager.h)
    //! so that IManagedHost.h - the seam every backend, including the
    //! Coral-free NativeAotHost, implements - can see this type without
    //! transitively pulling in CoralHostManager.h's Coral/HostInstance.hpp
    //! etc. CoralHostManager.h includes this header for the same enum rather
    //! than redeclaring it.
    enum class CoralHostStatus
    {
        Success,
        NotInitialized,
        CoralManagedNotFound,
        CoralInitError,
        DotNetNotFound,
        AssemblyLoadFailed,
        AlreadyInitialized
    };
} // namespace O3DESharp

namespace O3DESharp::Abi
{
    //! Version of the frozen C++ <-> managed ABI. MUST equal
    //! O3DE.Interop.HostAbi.Version in Assets/Scripts/O3DE.Core/Interop/HostAbi.cs.
    //! Editor/Tests/test_host_abi_contract.py fails if the two drift.
    //!
    //! Bump only when a field is added, removed or reordered in either struct
    //! below. A host that reads an unrecognised version must refuse to run
    //! rather than reinterpret pointers.
    inline constexpr AZ::u32 HostAbiVersion = 1;

    //! Function pointers C++ exposes to managed code.
    //!
    //! v1 mirrors O3DE.InternalCalls (Assets/Scripts/O3DE.Core/InternalCalls.cs)
    //! field-for-field, in declaration order. That ordering IS the ABI: insert a
    //! field here without inserting it there and every pointer after it is
    //! reinterpreted, with no diagnostic from either compiler.
    //!
    //! O3DE.Reflection.ReflectionInternalCalls is registered separately by
    //! GenericDispatcher and is deliberately NOT part of v1.
    //!
    //! Under Coral (editor) this struct is populated by
    //! ScriptBindings::MakeNativeImports and is descriptive - the actual
    //! transport is still assembly->AddInternalCall / UploadInternalCalls, and
    //! nothing about that path changes. Under NativeAOT it is the sole
    //! transport, handed to the managed side in one exported call.
    struct NativeImports
    {
        //! Must equal HostAbiVersion. 0 means "not populated".
        AZ::u32 version;

        // ============================================================
        // Logging
        // ============================================================
        void* Log_Info;
        void* Log_Warning;
        void* Log_Error;

        // ============================================================
        // Entity
        // ============================================================
        void* Entity_IsValid;
        void* Entity_GetName;
        void* Entity_SetName;
        void* Entity_IsActive;
        void* Entity_Activate;
        void* Entity_Deactivate;
        void* Entity_Destroy;
        void* Entity_FindByName;
        void* Entity_GetChildCount;
        void* Entity_GetChildAtIndex;
        void* Entity_GetChildren;

        // ============================================================
        // Transform
        // ============================================================
        void* Transform_GetWorldPosition;
        void* Transform_SetWorldPosition;
        void* Transform_GetLocalPosition;
        void* Transform_SetLocalPosition;
        void* Transform_GetWorldRotation;
        void* Transform_SetWorldRotation;
        void* Transform_GetWorldRotationEuler;
        void* Transform_SetWorldRotationEuler;
        void* Transform_GetLocalScale;
        void* Transform_SetLocalScale;
        void* Transform_GetLocalUniformScale;
        void* Transform_SetLocalUniformScale;
        void* Transform_GetForward;
        void* Transform_GetRight;
        void* Transform_GetUp;
        void* Transform_GetParentId;
        void* Transform_SetParent;

        // ============================================================
        // Input
        // ============================================================
        void* Input_IsKeyDown;
        void* Input_IsKeyPressed;
        void* Input_IsKeyReleased;
        void* Input_IsMouseButtonDown;
        void* Input_IsMouseButtonPressed;
        void* Input_IsMouseButtonReleased;
        void* Input_GetMousePosition;
        void* Input_GetMouseDelta;
        void* Input_GetAxis;

        // ============================================================
        // Time
        // ============================================================
        void* Time_GetDeltaTime;
        void* Time_GetTotalTime;
        void* Time_GetTimeScale;
        void* Time_SetTimeScale;
        void* Time_GetFrameCount;

        // ============================================================
        // Physics
        // ============================================================
        void* Physics_Raycast;

        // ============================================================
        // Component
        // ============================================================
        void* Component_HasComponent;
    };

    //! Function pointers managed code exposes to C++. Frozen signatures:
    //!
    //!   CreateInstance    int  (*)(const char* utf8TypeName)
    //!   InvokeLifecycle   int  (*)(int handle, int lifecycleId, float arg)
    //!   DispatchEBusEvent int  (*)(AZ::s64 token, const char* utf8EventName,
    //!                             const char* utf8ArgsJson,
    //!                             char* outBuffer, int outCapacity)
    //!   DestroyInstance   void (*)(int handle)
    //!   HotReloadSwap     int  (*)()
    //!
    //! Strings are UTF-8; DispatchEBusEvent writes into a caller-supplied
    //! buffer and returns the number of bytes the result needs (snprintf
    //! semantics), or -1 on error. No allocation ownership crosses the seam,
    //! so there is no Free export and the struct stays at five fields.
    struct ManagedExports
    {
        //! Must equal HostAbiVersion. 0 means "not populated".
        AZ::u32 version;

        void* CreateInstance;
        void* InvokeLifecycle;
        void* DispatchEBusEvent;
        void* DestroyInstance;

        //! Editor-only. The shipping NativeAOT thunk returns 0 and
        //! NativeAotHost::SupportsHotReload() reports false - there is no
        //! AssemblyLoadContext to swap.
        void* HotReloadSwap;
    };

    // The C# side asserts the same two identities in
    // HostAbiLayoutTests.NativeImports_IsBlittableAndPointerSized. A uint
    // followed by pointers pads up to pointer alignment on both 32- and
    // 64-bit, so (1 + N) * sizeof(void*) holds on both.
    static_assert(
        sizeof(NativeImports) == (1 + 47) * sizeof(void*),
        "NativeImports layout drifted from O3DE.Interop.NativeImports - see HostAbi.cs");
    static_assert(
        sizeof(ManagedExports) == (1 + 5) * sizeof(void*),
        "ManagedExports layout drifted from O3DE.Interop.ManagedExports - see HostAbi.cs");
} // namespace O3DESharp::Abi
