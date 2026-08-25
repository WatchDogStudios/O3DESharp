/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

// Coral is the only thing that ever populates O3DE.InternalCalls' function
// pointers today (AddInternalCall + UploadInternalCalls). A NativeAOT image
// has no Coral, so without this file every InternalCalls field stays null
// forever and the first Debug.Log/Entity/Transform/etc. call is a jump
// through a null unmanaged function pointer - an access violation, not a
// catchable exception.
//
// This is the NativeAOT-only counterpart to what AddInternalCall does under
// Coral: it takes the NativeImports struct HostExportsGenerator already
// receives and cast-assigns each pointer into InternalCalls' matching field.
// Whole-file guarded, same reasoning as the file that guards itself out of
// NativeAOT builds instead: this one only exists for NativeAOT, so
// referencing it from Coral-mode code would fail to compile - the call site
// in the generated O3DESharp_GetManagedExports is wrapped in the same #if.
#if O3DE_HOST_NATIVEAOT

using Coral.Managed.Interop;

namespace O3DE.Interop
{
    /// <summary>
    /// Wires a received <see cref="NativeImports"/> table into
    /// <see cref="InternalCalls"/>'s unmanaged function-pointer fields.
    ///
    /// Field-for-field with <c>InternalCalls.cs</c> - both are cross-checked
    /// against the same source (<c>Editor/Tests/test_host_abi_contract.py</c>
    /// pins the field NAMES; the source-text test alongside this file pins
    /// that each field's cast TYPE here matches its real declared type in
    /// InternalCalls.cs, so a signature drift there is caught here too).
    /// </summary>
    internal static unsafe class NativeImportsWiring
    {
        internal static void Apply(in NativeImports imports)
        {
            InternalCalls.Log_Info = (delegate* unmanaged<NativeString, void>)(void*)imports.Log_Info;
            InternalCalls.Log_Warning = (delegate* unmanaged<NativeString, void>)(void*)imports.Log_Warning;
            InternalCalls.Log_Error = (delegate* unmanaged<NativeString, void>)(void*)imports.Log_Error;

            InternalCalls.Entity_IsValid = (delegate* unmanaged<ulong, Bool32>)(void*)imports.Entity_IsValid;
            InternalCalls.Entity_GetName = (delegate* unmanaged<ulong, NativeString>)(void*)imports.Entity_GetName;
            InternalCalls.Entity_SetName = (delegate* unmanaged<ulong, NativeString, void>)(void*)imports.Entity_SetName;
            InternalCalls.Entity_IsActive = (delegate* unmanaged<ulong, Bool32>)(void*)imports.Entity_IsActive;
            InternalCalls.Entity_Activate = (delegate* unmanaged<ulong, void>)(void*)imports.Entity_Activate;
            InternalCalls.Entity_Deactivate = (delegate* unmanaged<ulong, void>)(void*)imports.Entity_Deactivate;
            InternalCalls.Entity_Destroy = (delegate* unmanaged<ulong, void>)(void*)imports.Entity_Destroy;
            InternalCalls.Entity_FindByName = (delegate* unmanaged<NativeString, ulong>)(void*)imports.Entity_FindByName;
            InternalCalls.Entity_GetChildCount = (delegate* unmanaged<ulong, int>)(void*)imports.Entity_GetChildCount;
            InternalCalls.Entity_GetChildAtIndex = (delegate* unmanaged<ulong, int, ulong>)(void*)imports.Entity_GetChildAtIndex;
            InternalCalls.Entity_GetChildren = (delegate* unmanaged<ulong, ulong*, int, int>)(void*)imports.Entity_GetChildren;

            InternalCalls.Transform_GetWorldPosition = (delegate* unmanaged<ulong, Vector3>)(void*)imports.Transform_GetWorldPosition;
            InternalCalls.Transform_SetWorldPosition = (delegate* unmanaged<ulong, Vector3, void>)(void*)imports.Transform_SetWorldPosition;
            InternalCalls.Transform_GetLocalPosition = (delegate* unmanaged<ulong, Vector3>)(void*)imports.Transform_GetLocalPosition;
            InternalCalls.Transform_SetLocalPosition = (delegate* unmanaged<ulong, Vector3, void>)(void*)imports.Transform_SetLocalPosition;
            InternalCalls.Transform_GetWorldRotation = (delegate* unmanaged<ulong, Quaternion>)(void*)imports.Transform_GetWorldRotation;
            InternalCalls.Transform_SetWorldRotation = (delegate* unmanaged<ulong, Quaternion, void>)(void*)imports.Transform_SetWorldRotation;
            InternalCalls.Transform_GetWorldRotationEuler = (delegate* unmanaged<ulong, Vector3>)(void*)imports.Transform_GetWorldRotationEuler;
            InternalCalls.Transform_SetWorldRotationEuler = (delegate* unmanaged<ulong, Vector3, void>)(void*)imports.Transform_SetWorldRotationEuler;
            InternalCalls.Transform_GetLocalScale = (delegate* unmanaged<ulong, Vector3>)(void*)imports.Transform_GetLocalScale;
            InternalCalls.Transform_SetLocalScale = (delegate* unmanaged<ulong, Vector3, void>)(void*)imports.Transform_SetLocalScale;
            InternalCalls.Transform_GetLocalUniformScale = (delegate* unmanaged<ulong, float>)(void*)imports.Transform_GetLocalUniformScale;
            InternalCalls.Transform_SetLocalUniformScale = (delegate* unmanaged<ulong, float, void>)(void*)imports.Transform_SetLocalUniformScale;
            InternalCalls.Transform_GetForward = (delegate* unmanaged<ulong, Vector3>)(void*)imports.Transform_GetForward;
            InternalCalls.Transform_GetRight = (delegate* unmanaged<ulong, Vector3>)(void*)imports.Transform_GetRight;
            InternalCalls.Transform_GetUp = (delegate* unmanaged<ulong, Vector3>)(void*)imports.Transform_GetUp;
            InternalCalls.Transform_GetParentId = (delegate* unmanaged<ulong, ulong>)(void*)imports.Transform_GetParentId;
            InternalCalls.Transform_SetParent = (delegate* unmanaged<ulong, ulong, void>)(void*)imports.Transform_SetParent;

            InternalCalls.Input_IsKeyDown = (delegate* unmanaged<int, Bool32>)(void*)imports.Input_IsKeyDown;
            InternalCalls.Input_IsKeyPressed = (delegate* unmanaged<int, Bool32>)(void*)imports.Input_IsKeyPressed;
            InternalCalls.Input_IsKeyReleased = (delegate* unmanaged<int, Bool32>)(void*)imports.Input_IsKeyReleased;
            InternalCalls.Input_IsMouseButtonDown = (delegate* unmanaged<int, Bool32>)(void*)imports.Input_IsMouseButtonDown;
            InternalCalls.Input_IsMouseButtonPressed = (delegate* unmanaged<int, Bool32>)(void*)imports.Input_IsMouseButtonPressed;
            InternalCalls.Input_IsMouseButtonReleased = (delegate* unmanaged<int, Bool32>)(void*)imports.Input_IsMouseButtonReleased;
            InternalCalls.Input_GetMousePosition = (delegate* unmanaged<Vector3>)(void*)imports.Input_GetMousePosition;
            InternalCalls.Input_GetMouseDelta = (delegate* unmanaged<Vector3>)(void*)imports.Input_GetMouseDelta;
            InternalCalls.Input_GetAxis = (delegate* unmanaged<NativeString, float>)(void*)imports.Input_GetAxis;

            InternalCalls.Time_GetDeltaTime = (delegate* unmanaged<float>)(void*)imports.Time_GetDeltaTime;
            InternalCalls.Time_GetTotalTime = (delegate* unmanaged<float>)(void*)imports.Time_GetTotalTime;
            InternalCalls.Time_GetTimeScale = (delegate* unmanaged<float>)(void*)imports.Time_GetTimeScale;
            InternalCalls.Time_SetTimeScale = (delegate* unmanaged<float, void>)(void*)imports.Time_SetTimeScale;
            InternalCalls.Time_GetFrameCount = (delegate* unmanaged<ulong>)(void*)imports.Time_GetFrameCount;

            InternalCalls.Physics_Raycast = (delegate* unmanaged<Vector3, Vector3, float, RaycastHit>)(void*)imports.Physics_Raycast;

            InternalCalls.Component_HasComponent = (delegate* unmanaged<ulong, NativeString, Bool32>)(void*)imports.Component_HasComponent;
        }
    }
}

#endif // O3DE_HOST_NATIVEAOT
