/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/Asset/AssetCommon.h>
#include <AzCore/Component/Component.h>
#include <AzCore/Component/TransformBus.h>

#include <O3DESharp/O3DESharpFeatureProcessorInterface.h>

namespace O3DESharp
{
    class O3DESharpComponentConfig final
        : public AZ::ComponentConfig
    {
    public:
        AZ_RTTI(O3DESharpComponentConfig, "{2E75AD7E-CDE9-4730-8E87-79A5367A6D88}", ComponentConfig);
        AZ_CLASS_ALLOCATOR(O3DESharpComponentConfig, AZ::SystemAllocator);
        static void Reflect(AZ::ReflectContext* context);

        O3DESharpComponentConfig() = default;

        AZ::u64 m_entityId{ AZ::EntityId::InvalidEntityId };
    };

    class O3DESharpComponentController final
        : public AZ::Data::AssetBus::MultiHandler
        , private AZ::TransformNotificationBus::Handler
    {
    public:
        friend class EditorO3DESharpComponent;

        AZ_RTTI(O3DESharpComponentController, "{6CD54DAF-3002-47A1-BB88-E8E88BC4E5B0}");
        AZ_CLASS_ALLOCATOR(O3DESharpComponentController, AZ::SystemAllocator);

        static void Reflect(AZ::ReflectContext* context);
        static void GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent);
        static void GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided);
        static void GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible);
        static void GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required);

        O3DESharpComponentController() = default;
        O3DESharpComponentController(const O3DESharpComponentConfig& config);

        void Activate(AZ::EntityId entityId);
        void Deactivate();
        void SetConfiguration(const O3DESharpComponentConfig& config);
        const O3DESharpComponentConfig& GetConfiguration() const;

    private:

        AZ_DISABLE_COPY(O3DESharpComponentController);

        // TransformNotificationBus overrides
        void OnTransformChanged(const AZ::Transform& local, const AZ::Transform& world) override;

        // handle for this probe in the feature processor
        O3DESharpHandle m_handle;

        O3DESharpFeatureProcessorInterface* m_featureProcessor = nullptr;
        AZ::TransformInterface* m_transformInterface = nullptr;
        AZ::EntityId m_entityId;
        
        O3DESharpComponentConfig m_configuration;

    };
}
