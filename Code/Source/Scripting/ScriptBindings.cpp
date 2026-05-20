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
#include <AzFramework/Input/Channels/InputChannel.h>
#include <AzFramework/Input/Buses/Requests/InputChannelRequestBus.h>
#include <AzFramework/Input/Buses/Requests/InputSystemCursorRequestBus.h>
#include <AzFramework/Entity/GameEntityContextBus.h>
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
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_Destroy", reinterpret_cast<void*>(&Entity_Destroy));
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_FindByName", reinterpret_cast<void*>(&Entity_FindByName));
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_GetChildCount", reinterpret_cast<void*>(&Entity_GetChildCount));
        assembly->AddInternalCall("O3DE.InternalCalls", "Entity_GetChildAtIndex", reinterpret_cast<void*>(&Entity_GetChildAtIndex));

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
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_IsMouseButtonPressed", reinterpret_cast<void*>(&Input_IsMouseButtonPressed));
        assembly->AddInternalCall("O3DE.InternalCalls", "Input_IsMouseButtonReleased", reinterpret_cast<void*>(&Input_IsMouseButtonReleased));
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

    void ScriptBindings::Entity_Destroy(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AzFramework::GameEntityContextRequestBus::Broadcast(
            &AzFramework::GameEntityContextRequests::DestroyGameEntity, id);
    }

    AZ::u64 ScriptBindings::Entity_FindByName(Coral::String name)
    {
        std::string searchName(name);

        AZ::EntityId foundId;
        AZ::ComponentApplicationBus::Broadcast(
            [&searchName, &foundId](AZ::ComponentApplicationRequests* requests)
            {
                requests->EnumerateEntities(
                    [&searchName, &foundId](AZ::Entity* entity) -> bool
                    {
                        if (entity && entity->GetName() == searchName.c_str())
                        {
                            foundId = entity->GetId();
                            return false; // stop enumeration
                        }
                        return true; // continue
                    });
            });

        return static_cast<AZ::u64>(foundId);
    }

    int ScriptBindings::Entity_GetChildCount(AZ::u64 entityId)
    {
        AZ::EntityId id(entityId);
        AZStd::vector<AZ::EntityId> children;
        AZ::TransformBus::EventResult(children, id, &AZ::TransformBus::Events::GetChildren);
        return static_cast<int>(children.size());
    }

    AZ::u64 ScriptBindings::Entity_GetChildAtIndex(AZ::u64 entityId, int index)
    {
        AZ::EntityId id(entityId);
        AZStd::vector<AZ::EntityId> children;
        AZ::TransformBus::EventResult(children, id, &AZ::TransformBus::Events::GetChildren);

        if (index >= 0 && index < static_cast<int>(children.size()))
        {
            return static_cast<AZ::u64>(children[index]);
        }
        return static_cast<AZ::u64>(AZ::EntityId::InvalidEntityId);
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
        // O3DE's AZ::Transform stores uniform scale only. If a script passes a
        // non-uniform Vector3 we'd silently lose two components, so warn loudly
        // and apply the X component as the uniform scale. Use a tiny epsilon
        // because exact float equality across managed -> native marshalling is
        // unreliable.
        constexpr float kScaleEpsilon = 1e-4f;
        if (AZStd::abs(scale.x - scale.y) > kScaleEpsilon ||
            AZStd::abs(scale.x - scale.z) > kScaleEpsilon)
        {
            AZ_Warning(
                "O3DESharp",
                false,
                "Transform.LocalScale assigned non-uniform (%.4f, %.4f, %.4f); O3DE only "
                "supports uniform scale on a Transform. Applying X=%.4f and discarding Y/Z. "
                "Use Transform.UniformScale for an unambiguous API.",
                scale.x, scale.y, scale.z, scale.x);
        }

        AZ::TransformBus::Event(id, &AZ::TransformBus::Events::SetLocalUniformScale, scale.x);
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

    // Helper to query the state of an input channel by its ID
    static const AzFramework::InputChannel* FindInputChannel(const AzFramework::InputChannelId& channelId)
    {
        const AzFramework::InputChannel* channel = nullptr;
        AzFramework::InputChannelRequestBus::EventResult(
            channel,
            channelId,
            &AzFramework::InputChannelRequests::GetInputChannel);
        return channel;
    }

    const AzFramework::InputChannelId& ScriptBindings::KeyCodeToChannelId(int keyCode)
    {
        // KeyCode enum values (must match C# KeyCode enum):
        // Letters: A=0..Z=25, Digits: 0=26..9=35
        // F-keys: F1=36..F12=47
        // Arrows: Up=48,Down=49,Left=50,Right=51
        // Modifiers: LeftShift=52,RightShift=53,LeftCtrl=54,RightCtrl=55,LeftAlt=56,RightAlt=57
        // Special: Space=58,Enter=59,Escape=60,Tab=61,Backspace=62,Delete=63,Insert=64
        //          Home=65,End=66,PageUp=67,PageDown=68
        //          CapsLock=69,NumLock=70,ScrollLock=71
        //          PrintScreen=72,Pause=73
        //          Minus=74,Equals=75,LeftBracket=76,RightBracket=77
        //          Semicolon=78,Apostrophe=79,Comma=80,Period=81,Slash=82,Backslash=83
        //          Grave=84 (tilde/backtick)

        using Key = AzFramework::InputDeviceKeyboard::Key;

        // Letters A-Z (0-25)
        static const AzFramework::InputChannelId* letterKeys[] = {
            &Key::AlphanumericA, &Key::AlphanumericB, &Key::AlphanumericC, &Key::AlphanumericD,
            &Key::AlphanumericE, &Key::AlphanumericF, &Key::AlphanumericG, &Key::AlphanumericH,
            &Key::AlphanumericI, &Key::AlphanumericJ, &Key::AlphanumericK, &Key::AlphanumericL,
            &Key::AlphanumericM, &Key::AlphanumericN, &Key::AlphanumericO, &Key::AlphanumericP,
            &Key::AlphanumericQ, &Key::AlphanumericR, &Key::AlphanumericS, &Key::AlphanumericT,
            &Key::AlphanumericU, &Key::AlphanumericV, &Key::AlphanumericW, &Key::AlphanumericX,
            &Key::AlphanumericY, &Key::AlphanumericZ
        };

        // Digits 0-9 (26-35)
        static const AzFramework::InputChannelId* digitKeys[] = {
            &Key::Alphanumeric0, &Key::Alphanumeric1, &Key::Alphanumeric2, &Key::Alphanumeric3,
            &Key::Alphanumeric4, &Key::Alphanumeric5, &Key::Alphanumeric6, &Key::Alphanumeric7,
            &Key::Alphanumeric8, &Key::Alphanumeric9
        };

        // F-keys F1-F12 (36-47)
        static const AzFramework::InputChannelId* fKeys[] = {
            &Key::Function01, &Key::Function02, &Key::Function03, &Key::Function04,
            &Key::Function05, &Key::Function06, &Key::Function07, &Key::Function08,
            &Key::Function09, &Key::Function10, &Key::Function11, &Key::Function12
        };

        if (keyCode >= 0 && keyCode <= 25)
        {
            return *letterKeys[keyCode];
        }
        if (keyCode >= 26 && keyCode <= 35)
        {
            return *digitKeys[keyCode - 26];
        }
        if (keyCode >= 36 && keyCode <= 47)
        {
            return *fKeys[keyCode - 36];
        }

        switch (keyCode)
        {
        case 48: return Key::NavigationArrowUp;
        case 49: return Key::NavigationArrowDown;
        case 50: return Key::NavigationArrowLeft;
        case 51: return Key::NavigationArrowRight;
        case 52: return Key::ModifierShiftL;
        case 53: return Key::ModifierShiftR;
        case 54: return Key::ModifierCtrlL;
        case 55: return Key::ModifierCtrlR;
        case 56: return Key::ModifierAltL;
        case 57: return Key::ModifierAltR;
        case 58: return Key::EditSpace;
        case 59: return Key::EditEnter;
        case 60: return Key::Escape;
        case 61: return Key::EditTab;
        case 62: return Key::EditBackspace;
        case 63: return Key::NavigationDelete;
        case 64: return Key::NavigationInsert;
        case 65: return Key::NavigationHome;
        case 66: return Key::NavigationEnd;
        case 67: return Key::NavigationPageUp;
        case 68: return Key::NavigationPageDown;
        case 69: return Key::EditCapsLock;
        case 70: return Key::NumLock;
        case 71: return Key::WindowsSystemScrollLock;
        case 72: return Key::WindowsSystemPrint;
        case 73: return Key::WindowsSystemPause;
        case 74: return Key::PunctuationHyphen;
        case 75: return Key::PunctuationEquals;
        case 76: return Key::PunctuationBracketL;
        case 77: return Key::PunctuationBracketR;
        case 78: return Key::PunctuationSemicolon;
        case 79: return Key::PunctuationApostrophe;
        case 80: return Key::PunctuationComma;
        case 81: return Key::PunctuationPeriod;
        case 82: return Key::PunctuationSlash;
        case 83: return Key::PunctuationBackslash;
        case 84: return Key::PunctuationTilde;
        default:
            // Return space as a safe fallback for unknown key codes
            return Key::EditSpace;
        }
    }

    const AzFramework::InputChannelId& ScriptBindings::MouseButtonToChannelId(int button)
    {
        // MouseButton enum: Left=0, Right=1, Middle=2
        using Button = AzFramework::InputDeviceMouse::Button;

        switch (button)
        {
        case 0: return Button::Left;
        case 1: return Button::Right;
        case 2: return Button::Middle;
        default: return Button::Left;
        }
    }

    bool ScriptBindings::Input_IsKeyDown(int keyCode)
    {
        const auto& channelId = KeyCodeToChannelId(keyCode);
        const auto* channel = FindInputChannel(channelId);
        if (channel)
        {
            return channel->IsActive();
        }
        return false;
    }

    bool ScriptBindings::Input_IsKeyPressed(int keyCode)
    {
        const auto& channelId = KeyCodeToChannelId(keyCode);
        const auto* channel = FindInputChannel(channelId);
        if (channel)
        {
            return channel->GetState() == AzFramework::InputChannel::State::Began;
        }
        return false;
    }

    bool ScriptBindings::Input_IsKeyReleased(int keyCode)
    {
        const auto& channelId = KeyCodeToChannelId(keyCode);
        const auto* channel = FindInputChannel(channelId);
        if (channel)
        {
            return channel->GetState() == AzFramework::InputChannel::State::Ended;
        }
        return false;
    }

    bool ScriptBindings::Input_IsMouseButtonDown(int button)
    {
        const auto& channelId = MouseButtonToChannelId(button);
        const auto* channel = FindInputChannel(channelId);
        if (channel)
        {
            return channel->IsActive();
        }
        return false;
    }

    bool ScriptBindings::Input_IsMouseButtonPressed(int button)
    {
        const auto& channelId = MouseButtonToChannelId(button);
        const auto* channel = FindInputChannel(channelId);
        if (channel)
        {
            return channel->GetState() == AzFramework::InputChannel::State::Began;
        }
        return false;
    }

    bool ScriptBindings::Input_IsMouseButtonReleased(int button)
    {
        const auto& channelId = MouseButtonToChannelId(button);
        const auto* channel = FindInputChannel(channelId);
        if (channel)
        {
            return channel->GetState() == AzFramework::InputChannel::State::Ended;
        }
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
        float deltaX = 0.0f;
        float deltaY = 0.0f;

        const auto* channelX = FindInputChannel(AzFramework::InputDeviceMouse::Movement::X);
        if (channelX && channelX->IsActive())
        {
            deltaX = channelX->GetValue();
        }

        const auto* channelY = FindInputChannel(AzFramework::InputDeviceMouse::Movement::Y);
        if (channelY && channelY->IsActive())
        {
            deltaY = channelY->GetValue();
        }

        return InteropVector3(deltaX, deltaY, 0.0f);
    }

    float ScriptBindings::Input_GetAxis(Coral::String axisName)
    {
        std::string name(axisName);

        // Map common axis names to O3DE input channels
        // "Horizontal" = A/D or Left/Right arrows
        // "Vertical" = W/S or Up/Down arrows
        // "MouseX" / "MouseY" = mouse movement
        if (name == "Horizontal")
        {
            float value = 0.0f;
            const auto* right = FindInputChannel(AzFramework::InputDeviceKeyboard::Key::AlphanumericD);
            const auto* left = FindInputChannel(AzFramework::InputDeviceKeyboard::Key::AlphanumericA);
            if (right && right->IsActive()) value += right->GetValue();
            if (left && left->IsActive()) value -= left->GetValue();

            // Also check arrow keys
            const auto* arrowRight = FindInputChannel(AzFramework::InputDeviceKeyboard::Key::NavigationArrowRight);
            const auto* arrowLeft = FindInputChannel(AzFramework::InputDeviceKeyboard::Key::NavigationArrowLeft);
            if (arrowRight && arrowRight->IsActive()) value += arrowRight->GetValue();
            if (arrowLeft && arrowLeft->IsActive()) value -= arrowLeft->GetValue();

            return AZ::GetClamp(value, -1.0f, 1.0f);
        }
        else if (name == "Vertical")
        {
            float value = 0.0f;
            const auto* forward = FindInputChannel(AzFramework::InputDeviceKeyboard::Key::AlphanumericW);
            const auto* back = FindInputChannel(AzFramework::InputDeviceKeyboard::Key::AlphanumericS);
            if (forward && forward->IsActive()) value += forward->GetValue();
            if (back && back->IsActive()) value -= back->GetValue();

            // Also check arrow keys
            const auto* arrowUp = FindInputChannel(AzFramework::InputDeviceKeyboard::Key::NavigationArrowUp);
            const auto* arrowDown = FindInputChannel(AzFramework::InputDeviceKeyboard::Key::NavigationArrowDown);
            if (arrowUp && arrowUp->IsActive()) value += arrowUp->GetValue();
            if (arrowDown && arrowDown->IsActive()) value -= arrowDown->GetValue();

            return AZ::GetClamp(value, -1.0f, 1.0f);
        }
        else if (name == "MouseX")
        {
            const auto* channel = FindInputChannel(AzFramework::InputDeviceMouse::Movement::X);
            return (channel && channel->IsActive()) ? channel->GetValue() : 0.0f;
        }
        else if (name == "MouseY")
        {
            const auto* channel = FindInputChannel(AzFramework::InputDeviceMouse::Movement::Y);
            return (channel && channel->IsActive()) ? channel->GetValue() : 0.0f;
        }

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
        if (auto* timeSystem = AZ::Interface<AZ::ITime>::Get())
        {
            return static_cast<AZ::u64>(timeSystem->GetElapsedTimeMs());
        }
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
