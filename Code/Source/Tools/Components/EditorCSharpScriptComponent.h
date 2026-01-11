/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <Scripting/CSharpScriptComponent.h>
#include <AzToolsFramework/ToolsComponents/EditorComponentBase.h>
#include <AzToolsFramework/ToolsComponents/EditorComponentAdapter.h>

namespace O3DESharp
{
    /**
     * Editor configuration for C# script with enhanced UX
     * This extends the runtime config with editor-only validation and metadata
     */
    class EditorCSharpScriptConfig final
        : public AZ::ComponentConfig
    {
    public:
        AZ_RTTI(EditorCSharpScriptConfig, "{E7F8A9B0-C1D2-E3F4-5678-90ABCDEF1234}", AZ::ComponentConfig);
        AZ_CLASS_ALLOCATOR(EditorCSharpScriptConfig, AZ::SystemAllocator);

        static void Reflect(AZ::ReflectContext* context);

        EditorCSharpScriptConfig() = default;

        //! Fully qualified C# class name (e.g., "MyGame.PlayerController")
        AZStd::string m_scriptClassName;

        //! Optional path to the assembly containing the script
        AZStd::string m_assemblyPath;

        //! Script validation status (read-only, updated by the component)
        AZStd::string m_validationStatus = "Not Validated";

        //! Whether the script class was found in the assembly
        bool m_isValid = false;
    };

    /**
     * Editor-time version of the CSharpScriptComponent
     * 
     * This wraps the runtime CSharpScriptComponent for use in the O3DE Editor.
     * When the entity enters game mode or is exported, this component is replaced
     * with the runtime CSharpScriptComponent.
     * 
     * Features:
     * - Script class validation
     * - Visual feedback on script status
     * - Browse button for script selection (via Python editor tools)
     * - Create new script option
     */
    class EditorCSharpScriptComponent
        : public AzToolsFramework::Components::EditorComponentBase
    {
    public:
        AZ_EDITOR_COMPONENT(EditorCSharpScriptComponent, "{B2C3D4E5-F6A7-8901-BCDE-F23456789012}");

        static void Reflect(AZ::ReflectContext* context);

        static void GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided);
        static void GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible);
        static void GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required);
        static void GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent);

        EditorCSharpScriptComponent() = default;
        ~EditorCSharpScriptComponent() override = default;

        // EditorComponentBase
        void BuildGameEntity(AZ::Entity* gameEntity) override;

        // Configuration access
        void SetConfiguration(const CSharpScriptComponentConfig& config);
        CSharpScriptComponentConfig GetConfiguration() const;

        //! Validate the current script class
        void ValidateScript();

        //! Opens the script browser dialog
        AZ::Crc32 OnBrowseScript();

        //! Opens the create new script dialog
        AZ::Crc32 OnCreateScript();

        //! Opens the script in the default IDE
        AZ::Crc32 OnEditScript();

        //! Callback when script class name changes
        AZ::Crc32 OnScriptClassNameChanged();
        
        //! Get list of available C# script classes for the ComboBox
        AZStd::vector<AZStd::string> GetAvailableScriptClasses() const;

    protected:
        // AZ::Component
        void Init() override;
        void Activate() override;
        void Deactivate() override;

    private:
        //! Find available C# project paths in the gem/project
        AZStd::vector<AZStd::string> FindCSharpProjects() const;

        //! Checks if a C# class exists in the given assembly
        bool ClassExistsInAssembly(const AZStd::string& className, const AZStd::string& assemblyPath) const;

        EditorCSharpScriptConfig m_config;
    };
}
