/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <AzCore/Math/Vector3.h>
#include <AzCore/Math/Quaternion.h>
#include <AzCore/Math/Transform.h>
#include <AzCore/Component/EntityId.h>
#include <AzFramework/Input/Channels/InputChannel.h>

#include <Coral/String.hpp>

namespace Coral
{
    class ManagedAssembly;
}

namespace O3DESharp
{
    class CoralHostManager;

    /**
     * Interop structures for passing data between C++ and C#
     * These must match the layout of corresponding C# structs
     */

    // Matches O3DE.Vector3 in C#
    struct InteropVector3
    {
        float x;
        float y;
        float z;

        InteropVector3() : x(0), y(0), z(0) {}
        InteropVector3(float inX, float inY, float inZ) : x(inX), y(inY), z(inZ) {}
        InteropVector3(const AZ::Vector3& vec) : x(vec.GetX()), y(vec.GetY()), z(vec.GetZ()) {}

        AZ::Vector3 ToAZ() const { return AZ::Vector3(x, y, z); }
    };

    // Matches O3DE.Quaternion in C#
    struct InteropQuaternion
    {
        float x;
        float y;
        float z;
        float w;

        InteropQuaternion() : x(0), y(0), z(0), w(1) {}
        InteropQuaternion(float inX, float inY, float inZ, float inW) : x(inX), y(inY), z(inZ), w(inW) {}
        InteropQuaternion(const AZ::Quaternion& quat)
            : x(quat.GetX()), y(quat.GetY()), z(quat.GetZ()), w(quat.GetW()) {}

        AZ::Quaternion ToAZ() const { return AZ::Quaternion(x, y, z, w); }
    };

    /**
     * ScriptBindings - Static class that registers all C++ functions exposed to C#
     *
     * These are "internal calls" in .NET terminology - native functions that can be
     * called from managed code.
     *
     * The functions registered here correspond to function pointer fields in InternalCalls.cs.
     */
    class ScriptBindings
    {
    public:
        /**
         * Register all internal calls with the given assembly
         * @param assembly The core API assembly (O3DE.Core.dll)
         */
        static void RegisterAll(Coral::ManagedAssembly* assembly);

    private:
        // ============================================================
        // Input Helpers
        // ============================================================

        /// Maps a C# KeyCode integer to an O3DE InputChannelId
        static const AzFramework::InputChannelId& KeyCodeToChannelId(int keyCode);

        /// Maps a C# mouse button integer to an O3DE InputChannelId
        static const AzFramework::InputChannelId& MouseButtonToChannelId(int button);

        // ============================================================
        // Logging Functions
        // ============================================================

        static void Log_Info(Coral::String message);
        static void Log_Warning(Coral::String message);
        static void Log_Error(Coral::String message);

        // ============================================================
        // Entity Functions
        // ============================================================

        static bool Entity_IsValid(AZ::u64 entityId);
        static Coral::String Entity_GetName(AZ::u64 entityId);
        static void Entity_SetName(AZ::u64 entityId, Coral::String name);
        static bool Entity_IsActive(AZ::u64 entityId);
        static void Entity_Activate(AZ::u64 entityId);
        static void Entity_Deactivate(AZ::u64 entityId);
        static void Entity_Destroy(AZ::u64 entityId);
        static AZ::u64 Entity_FindByName(Coral::String name);
        static int Entity_GetChildCount(AZ::u64 entityId);
        static AZ::u64 Entity_GetChildAtIndex(AZ::u64 entityId, int index);

        /// Fetches the children of entityId in a single EBus broadcast and copies up
        /// to bufferCapacity child EntityIds into the caller-owned outBuffer. Returns
        /// the number of IDs written (min(actual child count, bufferCapacity)). The
        /// caller (Entity.cs) is expected to size outBuffer using Entity_GetChildCount
        /// first; if the hierarchy changed between the two calls, extra children are
        /// simply not written this call (no truncation error - caller may re-check
        /// ChildCount and retry, matching the existing GetChildCount/GetChildAtIndex
        /// contract which had the same benign race).
        static int Entity_GetChildren(AZ::u64 entityId, AZ::u64* outBuffer, int bufferCapacity);

        // ============================================================
        // Transform Functions
        // ============================================================

        static InteropVector3 Transform_GetWorldPosition(AZ::u64 entityId);
        static void Transform_SetWorldPosition(AZ::u64 entityId, InteropVector3 position);
        static InteropVector3 Transform_GetLocalPosition(AZ::u64 entityId);
        static void Transform_SetLocalPosition(AZ::u64 entityId, InteropVector3 position);

        static InteropQuaternion Transform_GetWorldRotation(AZ::u64 entityId);
        static void Transform_SetWorldRotation(AZ::u64 entityId, InteropQuaternion rotation);
        static InteropVector3 Transform_GetWorldRotationEuler(AZ::u64 entityId);
        static void Transform_SetWorldRotationEuler(AZ::u64 entityId, InteropVector3 eulerDegrees);

        static InteropVector3 Transform_GetLocalScale(AZ::u64 entityId);
        static void Transform_SetLocalScale(AZ::u64 entityId, InteropVector3 scale);
        static float Transform_GetLocalUniformScale(AZ::u64 entityId);
        static void Transform_SetLocalUniformScale(AZ::u64 entityId, float scale);

        static InteropVector3 Transform_GetForward(AZ::u64 entityId);
        static InteropVector3 Transform_GetRight(AZ::u64 entityId);
        static InteropVector3 Transform_GetUp(AZ::u64 entityId);

        static AZ::u64 Transform_GetParentId(AZ::u64 entityId);
        static void Transform_SetParent(AZ::u64 entityId, AZ::u64 parentId);

        // ============================================================
        // Input Functions
        // ============================================================

        static bool Input_IsKeyDown(int keyCode);
        static bool Input_IsKeyPressed(int keyCode);
        static bool Input_IsKeyReleased(int keyCode);
        static bool Input_IsMouseButtonDown(int button);
        static bool Input_IsMouseButtonPressed(int button);
        static bool Input_IsMouseButtonReleased(int button);
        static InteropVector3 Input_GetMousePosition();
        static InteropVector3 Input_GetMouseDelta();
        static float Input_GetAxis(Coral::String axisName);

        // ============================================================
        // Time Functions
        // ============================================================

        static float Time_GetDeltaTime();
        static float Time_GetTotalTime();
        static float Time_GetTimeScale();
        static void Time_SetTimeScale(float scale);
        static AZ::u64 Time_GetFrameCount();

        // ============================================================
        // Physics Functions
        // ============================================================

        struct RaycastHit
        {
            bool hit;
            InteropVector3 point;
            InteropVector3 normal;
            float distance;
            AZ::u64 entityId;
        };

        static RaycastHit Physics_Raycast(InteropVector3 origin, InteropVector3 direction, float maxDistance);

        // ============================================================
        // Component Functions
        // ============================================================

        static bool Component_HasComponent(AZ::u64 entityId, Coral::String componentTypeName);
    };

} // namespace O3DESharp
