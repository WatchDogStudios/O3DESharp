/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include <Components/O3DESharpComponentController.h>

#include <AzCore/Asset/AssetManager.h>
#include <AzCore/Asset/AssetManagerBus.h>
#include <AzCore/Asset/AssetSerializer.h>
#include <AzCore/Serialization/SerializeContext.h>

#include <AzFramework/Entity/EntityContextBus.h>
#include <AzFramework/Entity/EntityContext.h>
#include <AzFramework/Scene/Scene.h>
#include <AzFramework/Scene/SceneSystemInterface.h>

#include <AzCore/RTTI/BehaviorContext.h>

#include <Atom/RPI.Public/Scene.h>

namespace O3DESharp
{
    void O3DESharpComponentConfig::Reflect(AZ::ReflectContext* context)
    {
        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            // Guard against double-reflection
            if (serializeContext->FindClassData(azrtti_typeid<O3DESharpComponentConfig>()))
            {
                return;
            }

            serializeContext->Class<O3DESharpComponentConfig>()
                ;
        }
    }

    void O3DESharpComponentController::Reflect(AZ::ReflectContext* context)
    {
        O3DESharpComponentConfig::Reflect(context);

        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            // Guard against double-reflection
            if (serializeContext->FindClassData(azrtti_typeid<O3DESharpComponentController>()))
            {
                return;
            }

            serializeContext->Class<O3DESharpComponentController>()
                ->Version(0)
                ->Field("Configuration", &O3DESharpComponentController::m_configuration);

            if (AZ::EditContext* editContext = serializeContext->GetEditContext())
            {
                editContext->Class<O3DESharpComponentController>(
                    "O3DESharpComponentController", "")
                    ->ClassElement(AZ::Edit::ClassElements::EditorData, "")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                    ->DataElement(AZ::Edit::UIHandlers::Default, &O3DESharpComponentController::m_configuration, "Configuration", "")
                        ->Attribute(AZ::Edit::Attributes::Visibility, AZ::Edit::PropertyVisibility::ShowChildrenOnly)
                    ;
            }
        }
    }

    void O3DESharpComponentController::GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent)
    {
        dependent.push_back(AZ_CRC_CE("TransformService"));
    }

    void O3DESharpComponentController::GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided)
    {
        provided.push_back(AZ_CRC_CE("O3DESharpService"));
    }

    void O3DESharpComponentController::GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible)
    {
        incompatible.push_back(AZ_CRC_CE("O3DESharpService"));
    }

    void O3DESharpComponentController::GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required)
    {
        required.push_back(AZ_CRC_CE("TransformService"));
    }

    O3DESharpComponentController::O3DESharpComponentController(const O3DESharpComponentConfig& config)
        : m_configuration(config)
    {
    }

    void O3DESharpComponentController::Activate(AZ::EntityId entityId)
    {
        m_entityId = entityId;

        AZ::TransformNotificationBus::Handler::BusConnect(m_entityId);

        m_featureProcessor = AZ::RPI::Scene::GetFeatureProcessorForEntity<O3DESharpFeatureProcessorInterface>(entityId);
        AZ_Assert(m_featureProcessor, "O3DESharpComponentController was unable to find a O3DESharpFeatureProcessor on the EntityContext provided.");

    }

    void O3DESharpComponentController::Deactivate()
    {
        AZ::TransformNotificationBus::Handler::BusDisconnect();
    }

    void O3DESharpComponentController::SetConfiguration(const O3DESharpComponentConfig& config)
    {
        m_configuration = config;
    }

    const O3DESharpComponentConfig& O3DESharpComponentController::GetConfiguration() const
    {
        return m_configuration;
    }

    void O3DESharpComponentController::OnTransformChanged([[maybe_unused]] const AZ::Transform& local, [[maybe_unused]] const AZ::Transform& world)
    {
        if (!m_featureProcessor)
        {
            return;
        }
    }
}
