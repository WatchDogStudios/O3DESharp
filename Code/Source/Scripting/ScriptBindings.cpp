/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "ScriptBindings.h"
#include "CoralHostManager.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/Component/Entity.h>
#include <AzCore/Component/ComponentApplicationBus.h>
#include <AzCore/Component/TransformBus.h>
#include <AzCore/Time/ITime.h>
#include <AzFramework/Input/Devices/Keyboard/InputDeviceKeyboard.h>
#include <AzFramework/Input/Devices/Mouse/InputDeviceMouse.h>
#include <AzFramework/Input/Buses/Requests/InputSystemCursorRequestBus.h>
#include <AzFramework/Physics/PhysicsSystem.h>
#include <AzFramework/Physics/Common/PhysicsSceneQueries.h>

#include <Coral/Assembly.hpp>

namespace O3DESharp
{
    void ScriptBindings::RegisterAll(Coral::ManagedAssembly* assembly)
    {
        if (assembly == nullptr)
        {
            AZLOG_ERROR("ScriptBindings::RegisterAll - Assembly is null");
            return;
        }

        // Debug: Log the assembly we're registering internal calls to
        AZLOG_INFO("ScriptBindings: Registering internal calls to assembly '%s' (ID: %d)", 
            assembly->GetName().data(), assembly->GetAssemblyID());

        // ============================================================
        // Logging Functions - O3DE.InternalCalls
        // ============================================================
        assembly->AddInternalCall("O3DE.InternalCalls", "Log_Info", reinterpret_cast<void*>(&Log_Info));
        assembly->AddInternalCall("O3DE.InternalCalls", "Log_Warning", reinterpret_cast<void*>(&Log_Warning));
        assembly->AddInternalCall("O3DE.InternalCalls", "Log_Error", reinterpret_cast<void*>(&Log_Error));

        // ============================================================
        // Entity Functions - O3DE.InternalCalls
        // ============================================================
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_IsValid", reinterpret_cast<void*>(&Entity_IsValid));
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_GetName", reinterpret_cast<void*>(&Entity_GetName));
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_SetName", reinterpret_cast<void*>(&Entity_SetName));
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_IsActive", reinterpret_cast<void*>(&Entity_IsActive));
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_Activate", reinterpret_cast<void*>(&Entity_Activate));
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_Deactivate", reinterpret_cast<void*>(&Entity_Deactivate));

        // ============================================================
        // Transform Functions - O3DE.InternalCalls
        // ============================================================
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetWorldPosition", reinterpret_cast<void*>(&Transform_GetWorldPosition));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_SetWorldPosition", reinterpret_cast<void*>(&Transform_SetWorldPosition));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetLocalPosition", reinterpret_cast<void*>(&Transform_GetLocalPosition));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_SetLocalPosition", reinterpret_cast<void*>(&Transform_SetLocalPosition));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetWorldRotation", reinterpret_cast<void*>(&Transform_GetWorldRotation));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_SetWorldRotation", reinterpret_cast<void*>(&Transform_SetWorldRotation));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetWorldRotationEuler", reinterpret_cast<void*>(&Transform_GetWorldRotationEuler));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_SetWorldRotationEuler", reinterpret_cast<void*>(&Transform_SetWorldRotationEuler));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetLocalScale", reinterpret_cast<void*>(&Transform_GetLocalScale));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_SetLocalScale", reinterpret_cast<void*>(&Transform_SetLocalScale));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetLocalUniformScale", reinterpret_cast<void*>(&Transform_GetLocalUniformScale));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_SetLocalUniformScale", reinterpret_cast<void*>(&Transform_SetLocalUniformScale));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetForward", reinterpret_cast<void*>(&Transform_GetForward));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetRight", reinterpret_cast<void*>(&Transform_GetRight));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetUp", reinterpret_cast<void*>(&Transform_GetUp));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_GetParentId", reinterpret_cast<void*>(&Transform_GetParentId));
        assembly->AddInternalCall("O3DE.InternalCalls", "Transform_SetParent", reinterpret_cast<void*>(&Transform_SetParent));

        // ============================================================
        // Input Functions - O3DE.InternalCalls
        // ============================================================
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_IsKeyDown", reinterpret_cast<void*>(&Input_IsKeyDown));
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_IsKeyPressed", reinterpret_cast<void*>(&Input_IsKeyPressed));
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_IsKeyReleased", reinterpret_cast<void*>(&Input_IsKeyReleased));
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_IsMouseButtonDown", reinterpret_cast<void*>(&Input_IsMouseButtonDown));
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_GetMousePosition", reinterpret_cast<void*>(&Input_GetMousePosition));
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_GetMouseDelta", reinterpret_cast<void*>(&Input_GetMouseDelta));
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_GetAxis", reinterpret_cast<void*>(&Input_GetAxis));

        // ============================================================
        // Time Functions - O3DE.InternalCalls
        // ============================================================
        assembly->AddInternalCall("O3DE.InternalCalls", "Time_GetDeltaTime", reinterpret_cast<void*>(&Time_GetDeltaTime));
        assembly->AddInternalCall("O3DE.InternalCalls", "Time_GetTotalTime", reinterpret_cast<void*>(&Time_GetTotalTime));
        assembly->AddInternalCall("O3DE.InternalCalls", "Time_GetTimeScale", reinterpret_cast<void*>(&Time_GetTimeScale));
        assembly->AddInternalCall("O3DE.InternalCalls", "Time_SetTimeScale", reinterpret_cast<void*>(&Time_SetTimeScale));
        assembly->AddInternalCall("O3DE.InternalCalls", "Time_GetFrameCount", reinterpret_cast<void*>(&Time_GetFrameCount));

        // ============================================================
        // Physics Functions - O3DE.InternalCalls
        // ============================================================
        assembly->AddInternalCall("O3DE.InternalCalls", "Physics_Raycast", reinterpret_cast<void*>(&Physics_Raycast));

        // ============================================================
        // Component Functions - O3DE.InternalCalls
        // ============================================================
        assembly->AddInternalCall("O3DE.InternalCalls", "Component_HasComponent", reinterpret_cast<void*>(&Component_HasComponent));

        // Upload all registered internal calls to the .NET runtime
        assembly->UploadInternalCalls();

        AZLOG_INFO("ScriptBindings: Internal calls registered successfully");
    }

    // ============================================================
    // Logging Implementation
    // ============================================================

    void ScriptBindings::Log_Info(Coral::String message)
    {
        std::string msg(message);
        AZLOG_INFO("[C#] %s", msg.c_str());
    }

    void ScriptBindings::Log_Warning(Coral::String message)
    {
        std::string msg(message);
        AZLOG_WARN("[C#] %s", msg.c_str());
    }

    void ScriptBindings::Log_Error(Coral::String message)
    {
        std::string msg(message);
        AZLOG_ERROR("[C#] %s", msg.c_str());
    }

    // ============================================================
    // Entity Implementation
    // ============================================================

    bool ScriptBindings::Entity_IsValid(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Entity* entity = nullptr;
        AZ::ComponentApplicationBus::BroadcastResult(entity, &AZ::ComponentApplicationRequests::FindEntity, id);
        return entity != nullptr;
    }

    Coral::String ScriptBindings::Entity_GetName(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Entity* entity = nullptr;
        AZ::ComponentApplicationBus::BroadcastResult(entity, &AZ::ComponentApplicationRequests::FindEntity, id);
        
        if (entity)
        {
            return Coral::String::New(entity->GetName().c_str());
        }
        return Coral::String::New("");
    }

    void ScriptBindings::Entity_SetName(AZ::u64 entityId, Coral::String name)
    {
        AZ::EntityId id(entityId);
        AZ::Entity* entity = nullptr;
        AZ::ComponentApplicationBus::BroadcastResult(entity, &AZ::ComponentApplicationRequests::FindEntity, id);
        
        if (entity)
        {
            std::string nameStr(name);
            entity->SetName(nameStr.c_str());
        }
    }

    bool ScriptBindings::Entity_IsActive(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Entity* entity = nullptr;
        AZ::ComponentApplicationBus::BroadcastResult(entity, &AZ::ComponentApplicationRequests::FindEntity, id);
        
        if (entity)
        {
            return entity->GetState() == AZ::Entity::State::Active;
        }
        return false;
    }

    void ScriptBindings::Entity_Activate(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Entity* entity = nullptr;
        AZ::ComponentApplicationBus::BroadcastResult(entity, &AZ::ComponentApplicationRequests::FindEntity, id);
        
        if (entity && entity->GetState() != AZ::Entity::State::Active)
        {
            entity->Activate();
        }
    }

    void ScriptBindings::Entity_Deactivate(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Entity* entity = nullptr;
        AZ::ComponentApplicationBus::BroadcastResult(entity, &AZ::ComponentApplicationRequests::FindEntity, id);
        
        if (entity && entity->GetState() == AZ::Entity::State::Active)
        {
            entity->Deactivate();
        }
    }

    // ============================================================
    // Transform Implementation
    // ============================================================

    InteropVector3 ScriptBindings::Transform_GetWorldPosition(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Vector3 position = AZ::Vector3::CreateZero();
        AZ::TransformBus::EventResult(position, id, &AZ::TransformBus::Events::GetWorldTranslation);
        return InteropVector3(position);
    }

    void ScriptBindings::Transform_SetWorldPosition(AZ::u64 entityId, InteropVector3 position)
    {
        AZ::EntityId id(entityId);
        AZ::TransformBus::Event(id, &AZ::TransformBus::Events::SetWorldTranslation, position.ToAZ());
    }

    InteropVector3 ScriptBindings::Transform_GetLocalPosition(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Vector3 position = AZ::Vector3::CreateZero();
        AZ::TransformBus::EventResult(position, id, &AZ::TransformBus::Events::GetLocalTranslation);
        return InteropVector3(position);
    }

    void ScriptBindings::Transform_SetLocalPosition(AZ::u64 entityId, InteropVector3 position)
    {
        AZ::EntityId id(entityId);
        AZ::TransformBus::Event(id, &AZ::TransformBus::Events::SetLocalTranslation, position.ToAZ());
    }

    InteropQuaternion ScriptBindings::Transform_GetWorldRotation(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Quaternion rotation = AZ::Quaternion::CreateIdentity();
        AZ::TransformBus::EventResult(rotation, id, &AZ::TransformBus::Events::GetWorldRotationQuaternion);
        return InteropQuaternion(rotation);
    }

    void ScriptBindings::Transform_SetWorldRotation(AZ::u64 entityId, InteropQuaternion rotation)
    {
        AZ::EntityId id(entityId);
        AZ::TransformBus::Event(id, &AZ::TransformBus::Events::SetWorldRotationQuaternion, rotation.ToAZ());
    }

    InteropVector3 ScriptBindings::Transform_GetWorldRotationEuler(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Vector3 eulerRadians = AZ::Vector3::CreateZero();
        AZ::TransformBus::EventResult(eulerRadians, id, &AZ::TransformBus::Events::GetWorldRotation);
        // Convert to degrees
        AZ::Vector3 eulerDegrees = AZ::Vector3RadToDeg(eulerRadians);
        return InteropVector3(eulerDegrees);
    }

    void ScriptBindings::Transform_SetWorldRotationEuler(AZ::u64 entityId, InteropVector3 eulerDegrees)
    {
        AZ::EntityId id(entityId);
        // Convert from degrees to radians
        AZ::Vector3 eulerRadians = AZ::Vector3DegToRad(eulerDegrees.ToAZ());
        AZ::TransformBus::Event(id, &AZ::TransformBus::Events::SetWorldRotation, eulerRadians);
    }

    InteropVector3 ScriptBindings::Transform_GetLocalScale(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        float uniformScale = 1.0f;
        AZ::TransformBus::EventResult(uniformScale, id, &AZ::TransformBus::Events::GetLocalUniformScale);
        return InteropVector3(uniformScale, uniformScale, uniformScale);
    }

    void ScriptBindings::Transform_SetLocalScale(AZ::u64 entityId, InteropVector3 scale)
    {
        AZ::EntityId id(entityId);
        // O3DE uses uniform scale, so we'll use the average or x component
        float uniformScale = scale.x;
        AZ::TransformBus::Event(id, &AZ::TransformBus::Events::SetLocalUniformScale, uniformScale);
    }

    float ScriptBindings::Transform_GetLocalUniformScale(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        float scale = 1.0f;
        AZ::TransformBus::EventResult(scale, id, &AZ::TransformBus::Events::GetLocalUniformScale);
        return scale;
    }

    void ScriptBindings::Transform_SetLocalUniformScale(AZ::u64 entityId, float scale)
    {
        AZ::EntityId id(entityId);
        AZ::TransformBus::Event(id, &AZ::TransformBus::Events::SetLocalUniformScale, scale);
    }

    InteropVector3 ScriptBindings::Transform_GetForward(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Transform worldTm = AZ::Transform::CreateIdentity();
        AZ::TransformBus::EventResult(worldTm, id, &AZ::TransformBus::Events::GetWorldTM);
        // In O3DE, forward is typically +Y
        AZ::Vector3 forward = worldTm.GetBasisY();
        return InteropVector3(forward);
    }

    InteropVector3 ScriptBindings::Transform_GetRight(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Transform worldTm = AZ::Transform::CreateIdentity();
        AZ::TransformBus::EventResult(worldTm, id, &AZ::TransformBus::Events::GetWorldTM);
        // In O3DE, right is typically +X
        AZ::Vector3 right = worldTm.GetBasisX();
        return InteropVector3(right);
    }

    InteropVector3 ScriptBindings::Transform_GetUp(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::Transform worldTm = AZ::Transform::CreateIdentity();
        AZ::TransformBus::EventResult(worldTm, id, &AZ::TransformBus::Events::GetWorldTM);
        // In O3DE, up is typically +Z
        AZ::Vector3 up = worldTm.GetBasisZ();
        return InteropVector3(up);
    }

    AZ::u64 ScriptBindings::Transform_GetParentId(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZ::EntityId parentId;
        AZ::TransformBus::EventResult(parentId, id, &AZ::TransformBus::Events::GetParentId);
        return static_cast<AZ::u64>(parentId);
    }

    void ScriptBindings::Transform_SetParent(AZ::u64 entityId, AZ::u64 parentId)
    {
        AZ::EntityId id(entityId);
        AZ::EntityId parent(parentId);
        AZ::TransformBus::Event(id, &AZ::TransformBus::Events::SetParent, parent);
    }

    // ============================================================
    // Input Implementation
    // ============================================================

    bool ScriptBindings::Input_IsKeyDown([[maybe_unused]] int keyCode)
    {
        // TODO: Implement using AzFramework input system
        // This requires tracking key states across frames
        // For now, return false as a placeholder
        return false;
    }

    bool ScriptBindings::Input_IsKeyPressed([[maybe_unused]] int keyCode)
    {
        // TODO: Implement - key just pressed this frame
        return false;
    }

    bool ScriptBindings::Input_IsKeyReleased([[maybe_unused]] int keyCode)
    {
        // TODO: Implement - key just released this frame
        return false;
    }

    bool ScriptBindings::Input_IsMouseButtonDown([[maybe_unused]] int button)
    {
        // TODO: Implement mouse button tracking
        return false;
    }

    InteropVector3 ScriptBindings::Input_GetMousePosition()
    {
        AZ::Vector2 mousePos = AZ::Vector2::CreateZero();
        AzFramework::InputSystemCursorRequestBus::EventResult(
            mousePos,
            AzFramework::InputDeviceMouse::Id,
            &AzFramework::InputSystemCursorRequests::GetSystemCursorPositionNormalized);
        return InteropVector3(mousePos.GetX(), mousePos.GetY(), 0.0f);
    }

    InteropVector3 ScriptBindings::Input_GetMouseDelta()
    {
        // TODO: Implement mouse delta tracking
        return InteropVector3(0.0f, 0.0f, 0.0f);
    }

    float ScriptBindings::Input_GetAxis([[maybe_unused]] Coral::String axisName)
    {
        // TODO: Implement axis input mapping
        return 0.0f;
    }

    // ============================================================
    // Time Implementation
    // ============================================================

    float ScriptBindings::Time_GetDeltaTime()
    {
        if (auto* timeSystem = AZ::Interface<AZ::ITime>::Get())
        {
            return aznumeric_cast<float>(timeSystem->GetSimulationTickDeltaTimeUs()) / 1000000.0f;
        }
        return 0.016f; // Default to ~60fps
    }

    float ScriptBindings::Time_GetTotalTime()
    {
        if (auto* timeSystem = AZ::Interface<AZ::ITime>::Get())
        {
            return aznumeric_cast<float>(timeSystem->GetElapsedTimeUs()) / 1000000.0f;
        }
        return 0.0f;
    }

    float ScriptBindings::Time_GetTimeScale()
    {
        if (auto* timeSystem = AZ::Interface<AZ::ITime>::Get())
        {
            return timeSystem->GetSimulationTickScale();
        }
        return 1.0f;
    }

    void ScriptBindings::Time_SetTimeScale(float scale)
    {
        if (auto* timeSystem = AZ::Interface<AZ::ITime>::Get())
        {
            timeSystem->SetSimulationTickScale(scale);
        }
    }

    AZ::u64 ScriptBindings::Time_GetFrameCount()
    {
        // TODO: Track frame count or get from appropriate system
        return 0;
    }

    // ============================================================
    // Physics Implementation
    // ============================================================

    ScriptBindings::RaycastHit ScriptBindings::Physics_Raycast(InteropVector3 origin, InteropVector3 direction, float maxDistance)
    {
        RaycastHit result;
        result.hit = false;
        result.point = InteropVector3();
        result.normal = InteropVector3(0, 0, 1);
        result.distance = 0.0f;
        result.entityId = static_cast<AZ::u64>(AZ::EntityId::InvalidEntityId);

        auto* physicsSystem = AZ::Interface<AzPhysics::SystemInterface>::Get();
        if (!physicsSystem)
        {
            return result;
        }

        // Get the default scene
        AzPhysics::SceneHandle sceneHandle = physicsSystem->GetSceneHandle(AzPhysics::DefaultPhysicsSceneName);
        if (sceneHandle == AzPhysics::InvalidSceneHandle)
        {
            return result;
        }

        auto* scene = physicsSystem->GetScene(sceneHandle);
        if (!scene)
        {
            return result;
        }

        // Setup raycast request
        AzPhysics::RayCastRequest request;
        request.m_start = origin.ToAZ();
        request.m_direction = direction.ToAZ().GetNormalized();
        request.m_distance = maxDistance;

        // Perform the raycast
        AzPhysics::SceneQueryHits hits = scene->QueryScene(&request);

        if (!hits.m_hits.empty())
        {
            const auto& hit = hits.m_hits[0];
            result.hit = true;
            result.point = InteropVector3(hit.m_position);
            result.normal = InteropVector3(hit.m_normal);
            result.distance = hit.m_distance;
            result.entityId = static_cast<AZ::u64>(hit.m_entityId);
        }

        return result;
    }

    // ============================================================
    // Component Implementation
    // ============================================================

    bool ScriptBindings::Component_HasComponent(AZ::u64 entityId, Coral::String componentTypeName)
    {
        AZ::EntityId id(entityId);
        AZ::Entity* entity = nullptr;
        AZ::ComponentApplicationBus::BroadcastResult(entity, &AZ::ComponentApplicationRequests::FindEntity, id);
        
        if (!entity)
        {
            return false;
        }

        std::string typeName(componentTypeName);
        
        // Try to find component by type name
        // This is a simplified implementation - in practice you'd want to use
        // the type registry to look up the TypeId from the name
        for (auto* component : entity->GetComponents())
        {
            if (component)
            {
                const char* currentComponentTypeName = component->RTTI_GetTypeName();
                if (currentComponentTypeName && typeName == currentComponentTypeName)
                {
                    return true;
                }
            }
        }
        
        return false;
    }

} // namespace O3DESharp