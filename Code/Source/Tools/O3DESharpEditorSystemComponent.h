/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzToolsFramework/API/ToolsApplicationAPI.h>
#include <AzToolsFramework/ActionManager/ActionManagerRegistrationNotificationBus.h>

#include <Clients/O3DESharpSystemComponent.h>

namespace O3DESharp
{
    /// System component for O3DESharp editor
    /// 
    /// This component handles editor-specific functionality for C# scripting:
    /// - Registers menu items in the Tools menu for C# project management
    /// - Provides actions for creating projects, scripts, and building
    class O3DESharpEditorSystemComponent
        : public O3DESharpSystemComponent
        , protected AzToolsFramework::EditorEvents::Bus::Handler
        , protected AzToolsFramework::ActionManagerRegistrationNotificationBus::Handler
    {
        using BaseSystemComponent = O3DESharpSystemComponent;
    public:
        AZ_COMPONENT_DECL(O3DESharpEditorSystemComponent);

        static void Reflect(AZ::ReflectContext* context);

        O3DESharpEditorSystemComponent();
        ~O3DESharpEditorSystemComponent();

    private:
        static void GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided);
        static void GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible);
        static void GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required);
        static void GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent);

        // AZ::Component
        void Activate() override;
        void Deactivate() override;

        // ActionManagerRegistrationNotificationBus
        void OnActionRegistrationHook() override;
        void OnMenuBindingHook() override;

        // Helper methods for C# scripting actions
        void OpenCSharpProjectManager();
        void CreateCSharpProject();
        void CreateCSharpScript();
        void BuildCSharpProjects();
    };
} // namespace O3DESharp
