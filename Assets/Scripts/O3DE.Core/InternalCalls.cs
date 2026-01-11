/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Runtime.InteropServices;
using Coral.Managed.Interop;

namespace O3DE
{
    /// <summary>
    /// Internal calls to native O3DE C++ functions.
    /// These are static function pointer fields that Coral binds to native code at runtime.
    ///
    /// DO NOT call these directly - use the wrapper classes (Debug, Entity, Transform, etc.)
    /// </summary>
    internal static unsafe class InternalCalls
    {
        // Suppress CS0649: Field is never assigned (Coral assigns these at runtime)
#pragma warning disable 0649

        // ============================================================
        // Logging Functions
        // ============================================================

        internal static delegate* unmanaged<NativeString, void> Log_Info;
        internal static delegate* unmanaged<NativeString, void> Log_Warning;
        internal static delegate* unmanaged<NativeString, void> Log_Error;

        // ============================================================
        // Entity Functions
        // ============================================================

        internal static delegate* unmanaged<ulong, Bool32> Entity_IsValid;
        internal static delegate* unmanaged<ulong, NativeString> Entity_GetName;
        internal static delegate* unmanaged<ulong, NativeString, void> Entity_SetName;
        internal static delegate* unmanaged<ulong, Bool32> Entity_IsActive;
        internal static delegate* unmanaged<ulong, void> Entity_Activate;
        internal static delegate* unmanaged<ulong, void> Entity_Deactivate;

        // ============================================================
        // Transform Functions
        // ============================================================

        internal static delegate* unmanaged<ulong, Vector3> Transform_GetWorldPosition;
        internal static delegate* unmanaged<ulong, Vector3, void> Transform_SetWorldPosition;
        internal static delegate* unmanaged<ulong, Vector3> Transform_GetLocalPosition;
        internal static delegate* unmanaged<ulong, Vector3, void> Transform_SetLocalPosition;
        internal static delegate* unmanaged<ulong, Quaternion> Transform_GetWorldRotation;
        internal static delegate* unmanaged<ulong, Quaternion, void> Transform_SetWorldRotation;
        internal static delegate* unmanaged<ulong, Vector3> Transform_GetWorldRotationEuler;
        internal static delegate* unmanaged<ulong, Vector3, void> Transform_SetWorldRotationEuler;
        internal static delegate* unmanaged<ulong, Vector3> Transform_GetLocalScale;
        internal static delegate* unmanaged<ulong, Vector3, void> Transform_SetLocalScale;
        internal static delegate* unmanaged<ulong, float> Transform_GetLocalUniformScale;
        internal static delegate* unmanaged<ulong, float, void> Transform_SetLocalUniformScale;
        internal static delegate* unmanaged<ulong, Vector3> Transform_GetForward;
        internal static delegate* unmanaged<ulong, Vector3> Transform_GetRight;
        internal static delegate* unmanaged<ulong, Vector3> Transform_GetUp;
        internal static delegate* unmanaged<ulong, ulong> Transform_GetParentId;
        internal static delegate* unmanaged<ulong, ulong, void> Transform_SetParent;

        // ============================================================
        // Input Functions
        // ============================================================

        internal static delegate* unmanaged<int, Bool32> Input_IsKeyDown;
        internal static delegate* unmanaged<int, Bool32> Input_IsKeyPressed;
        internal static delegate* unmanaged<int, Bool32> Input_IsKeyReleased;
        internal static delegate* unmanaged<int, Bool32> Input_IsMouseButtonDown;
        internal static delegate* unmanaged<Vector3> Input_GetMousePosition;
        internal static delegate* unmanaged<Vector3> Input_GetMouseDelta;
        internal static delegate* unmanaged<NativeString, float> Input_GetAxis;

        // ============================================================
        // Time Functions
        // ============================================================

        internal static delegate* unmanaged<float> Time_GetDeltaTime;
        internal static delegate* unmanaged<float> Time_GetTotalTime;
        internal static delegate* unmanaged<float> Time_GetTimeScale;
        internal static delegate* unmanaged<float, void> Time_SetTimeScale;
        internal static delegate* unmanaged<ulong> Time_GetFrameCount;

        // ============================================================
        // Physics Functions
        // ============================================================

        internal static delegate* unmanaged<Vector3, Vector3, float, RaycastHit> Physics_Raycast;

        // ============================================================
        // Component Functions
        // ============================================================

        internal static delegate* unmanaged<ulong, NativeString, Bool32> Component_HasComponent;

#pragma warning restore 0649
    }

    /// <summary>
    /// Raycast hit result structure.
    /// Must match the layout of InteropRaycastHit in C++.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RaycastHit
    {
        /// <summary>
        /// Whether the raycast hit anything
        /// </summary>
        public bool Hit;

        /// <summary>
        /// The point where the ray hit
        /// </summary>
        public Vector3 Point;

        /// <summary>
        /// The normal of the surface at the hit point
        /// </summary>
        public Vector3 Normal;

        /// <summary>
        /// The distance from the ray origin to the hit point
        /// </summary>
        public float Distance;

        /// <summary>
        /// The entity ID of the object that was hit
        /// </summary>
        public ulong EntityId;

        /// <summary>
        /// Get the entity that was hit
        /// </summary>
        public Entity? GetEntity()
        {
            if (!Hit || EntityId == 0)
                return null;
            return new Entity(EntityId);
        }
    }
}
