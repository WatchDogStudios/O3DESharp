/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CSharpScriptComponent.h"
#include "CoralHostManager.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/Serialization/SerializeContext.h>
#include <AzCore/Serialization/EditContext.h>
#include <AzCore/RTTI/BehaviorContext.h>
#include <AzCore/Component/Entity.h>

namespace O3DESharp
{
    // ============================================================
    // CSharpScriptComponentConfig
    // ============================================================

    void CSharpScriptComponentConfig::Reflect(AZ::ReflectContext* context)
    {
        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            // Guard against double-reflection (can happen when both runtime and editor modules load)
            if (serializeContext->FindClassData(azrtti_typeid<CSharpScriptComponentConfig>()))
            {
                return;
            }

            serializeContext->Class<CSharpScriptComponentConfig, AZ::ComponentConfig>()
                ->Version(1)
                ->Field("ScriptClassName", &CSharpScriptComponentConfig::m_scriptClassName)
                ->Field("AssemblyPath", &CSharpScriptComponentConfig::m_assemblyPath)
                ;

            if (AZ::EditContext* editContext = serializeContext->GetEditContext())
            {
                editContext->Class<CSharpScriptComponentConfig>("C# Script Configuration", "Configuration for a C# script component")
                    ->ClassElement(AZ::Edit::ClassElements::EditorData, "")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                    ->DataElement(AZ::Edit::UIHandlers::Default, &CSharpScriptComponentConfig::m_scriptClassName, 
                        "Script Class", "The fully qualified C# class name (e.g., MyGame.PlayerController)")
                        ->Attribute(AZ::Edit::Attributes::ChangeNotify, AZ::Edit::PropertyRefreshLevels::EntireTree)
                    ->DataElement(AZ::Edit::UIHandlers::Default, &CSharpScriptComponentConfig::m_assemblyPath, 
                        "Assembly Path", "Optional: Path to the assembly containing the script (leave empty for default)")
                    ;
            }
        }
    }

    // ============================================================
    // CSharpScriptComponent
    // ============================================================

    void CSharpScriptComponent::Reflect(AZ::ReflectContext* context)
    {
        CSharpScriptComponentConfig::Reflect(context);

        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            serializeContext->Class<CSharpScriptComponent, AZ::Component>()
                ->Version(1)
                ->Field("Configuration", &CSharpScriptComponent::m_config)
                ;

            if (AZ::EditContext* editContext = serializeContext->GetEditContext())
            {
                editContext->Class<CSharpScriptComponent>("C# Script", "Attaches a C# script to this entity")
                    ->ClassElement(AZ::Edit::ClassElements::EditorData, "")
                        ->Attribute(AZ::Edit::Attributes::Category, "Scripting")
                        ->Attribute(AZ::Edit::Attributes::Icon, "Icons/Components/Script.svg")
                        ->Attribute(AZ::Edit::Attributes::ViewportIcon, "Icons/Components/Viewport/Script.svg")
                        ->Attribute(AZ::Edit::Attributes::AppearsInAddComponentMenu, AZ_CRC_CE("Game"))
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                        ->Attribute(AZ::Edit::Attributes::HelpPageURL, "")
                    ->DataElement(AZ::Edit::UIHandlers::Default, &CSharpScriptComponent::m_config, "Configuration", "")
                        ->Attribute(AZ::Edit::Attributes::Visibility, AZ::Edit::PropertyVisibility::ShowChildrenOnly)
                    ;
            }
        }

        if (auto* behaviorContext = azrtti_cast<AZ::BehaviorContext*>(context))
        {
            behaviorContext->Class<CSharpScriptComponent>("CSharpScriptComponent")
                ->Attribute(AZ::Script::Attributes::Module, "scripting")
                ->Attribute(AZ::Script::Attributes::Scope, AZ::Script::Attributes::ScopeFlags::Common)
                ->Method("IsScriptValid", &CSharpScriptComponent::IsScriptValid)
                ->Method("ReloadScript", &CSharpScriptComponent::ReloadScript)
                ;
        }
    }

    void CSharpScriptComponent::GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided)
    {
        provided.push_back(AZ_CRC_CE("CSharpScriptService"));
    }

    void CSharpScriptComponent::GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible)
    {
        // Multiple C# scripts can be on the same entity, so no incompatibilities
        AZ_UNUSED(incompatible);
    }

    void CSharpScriptComponent::GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required)
    {
        required.push_back(AZ_CRC_CE("TransformService"));
    }

    void CSharpScriptComponent::GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent)
    {
        dependent.push_back(AZ_CRC_CE("O3DESharpSystemService"));
    }

    CSharpScriptComponent::CSharpScriptComponent(const CSharpScriptComponentConfig& config)
        : m_config(config)
    {
    }

    CSharpScriptComponent::~CSharpScriptComponent()
    {
        DestroyScriptInstance();
    }

    void CSharpScriptComponent::SetConfiguration(const CSharpScriptComponentConfig& config)
    {
        m_config = config;
    }

    const CSharpScriptComponentConfig& CSharpScriptComponent::GetConfiguration() const
    {
        return m_config;
    }

    bool CSharpScriptComponent::IsScriptValid() const
    {
        return m_scriptInstance.IsValid() && m_scriptInitialized;
    }

    void CSharpScriptComponent::ReloadScript()
    {
        AZLOG_INFO("CSharpScriptComponent: Reloading script '%s' on entity '%s'",
            m_config.m_scriptClassName.c_str(),
            GetEntity() ? GetEntity()->GetName().c_str() : "Unknown");

        // Destroy current instance
        DestroyScriptInstance();

        // Create new instance
        if (CreateScriptInstance())
        {
            // Call OnCreate on the new instance
            if (m_scriptInstance.IsValid())
            {
                SetEntityIdOnScript();
                m_scriptInstance.InvokeMethod("OnCreate");
            }
        }
    }

    void CSharpScriptComponent::Init()
    {
        // Initialization before activation
    }

    void CSharpScriptComponent::Activate()
    {
        if (m_isActivating)
        {
            return;
        }
        m_isActivating = true;

        AZLOG_INFO("CSharpScriptComponent: Activating script '%s' on entity '%s'",
            m_config.m_scriptClassName.c_str(),
            GetEntity() ? GetEntity()->GetName().c_str() : "Unknown");

        // Create the managed script instance
        if (CreateScriptInstance())
        {
            // Pass entity ID to the script
            SetEntityIdOnScript();

            // Call OnCreate on the managed instance
            if (m_scriptInstance.IsValid())
            {
                m_scriptInstance.InvokeMethod("OnCreate");
            }
        }

        // Connect to tick bus to call OnUpdate
        AZ::TickBus::Handler::BusConnect();

        // Connect to transform notifications
        AZ::TransformNotificationBus::Handler::BusConnect(GetEntityId());

        m_isActivating = false;
    }

    void CSharpScriptComponent::Deactivate()
    {
        AZLOG_INFO("CSharpScriptComponent: Deactivating script '%s' on entity '%s'",
            m_config.m_scriptClassName.c_str(),
            GetEntity() ? GetEntity()->GetName().c_str() : "Unknown");

        // Disconnect from buses
        AZ::TransformNotificationBus::Handler::BusDisconnect();
        AZ::TickBus::Handler::BusDisconnect();

        // Call OnDestroy before destroying the instance
        if (m_scriptInstance.IsValid())
        {
            m_scriptInstance.InvokeMethod("OnDestroy");
        }

        // Destroy the managed instance
        DestroyScriptInstance();
    }

    void CSharpScriptComponent::OnTick(float deltaTime, [[maybe_unused]] AZ::ScriptTimePoint time)
    {
        if (m_scriptInstance.IsValid() && m_scriptInitialized)
        {
            // Call OnUpdate on the managed instance
            m_scriptInstance.InvokeMethod("OnUpdate", deltaTime);
        }
    }

    void CSharpScriptComponent::OnTransformChanged(
        [[maybe_unused]] const AZ::Transform& local, 
        [[maybe_unused]] const AZ::Transform& world)
    {
        // Optionally notify the script of transform changes
        // This could be used to call OnTransformChanged on the C# side
        if (m_scriptInstance.IsValid() && m_scriptInitialized)
        {
            // The script can query the transform via the Transform API
            // We don't pass the transform data directly to avoid complex marshalling
            // Scripts that need to react to transform changes can override OnTransformChanged
            m_scriptInstance.InvokeMethod("OnTransformChanged");
        }
    }

    bool CSharpScriptComponent::CreateScriptInstance()
    {
        if (m_config.m_scriptClassName.empty())
        {
            AZLOG_WARN("CSharpScriptComponent: No script class name specified");
            return false;
        }

        // Get the Coral host manager
        ICoralHostManager* hostManager = CoralHostManagerInterface::Get();
        if (!hostManager || !hostManager->IsInitialized())
        {
            AZLOG_ERROR("CSharpScriptComponent: Coral host not initialized");
            return false;
        }

        // Try to find the type in the user assembly first, then core assembly
        Coral::Type* scriptType = hostManager->GetUserType(m_config.m_scriptClassName);
        if (!scriptType)
        {
            scriptType = hostManager->GetCoreType(m_config.m_scriptClassName);
        }

        if (!scriptType)
        {
            AZLOG_ERROR("CSharpScriptComponent: Script class not found: '%s'",
                m_config.m_scriptClassName.c_str());
            return false;
        }

        m_scriptType = scriptType;

        // Create an instance of the script class
        m_scriptInstance = hostManager->CreateInstance(*m_scriptType);

        if (!m_scriptInstance.IsValid())
        {
            AZLOG_ERROR("CSharpScriptComponent: Failed to create instance of script class: '%s'",
                m_config.m_scriptClassName.c_str());
            m_scriptType = nullptr;
            return false;
        }

        m_scriptInitialized = true;

        AZLOG_INFO("CSharpScriptComponent: Successfully created script instance: '%s'",
            m_config.m_scriptClassName.c_str());

        return true;
    }

    void CSharpScriptComponent::DestroyScriptInstance()
    {
        if (m_scriptInstance.IsValid())
        {
            m_scriptInstance.Destroy();
        }

        m_scriptType = nullptr;
        m_scriptInitialized = false;
    }

    void CSharpScriptComponent::SetEntityIdOnScript()
    {
        if (!m_scriptInstance.IsValid())
        {
            return;
        }

        // Set the EntityId field on the script base class
        // The C# ScriptComponent base class has an EntityId property
        AZ::u64 entityId = static_cast<AZ::u64>(GetEntityId());
        
        try
        {
            // The C# ScriptComponent class has a field "m_entityId" that stores the native entity ID
            m_scriptInstance.SetFieldValue("m_entityId", entityId);
        }
        catch (...)
        {
            AZLOG_WARN("CSharpScriptComponent: Could not set entity ID on script. "
                "Make sure the script inherits from O3DE.ScriptComponent");
        }
    }

} // namespace O3DESharp