/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "EditorCSharpScriptComponent.h"

#include <AzCore/Serialization/SerializeContext.h>
#include <AzCore/Serialization/EditContext.h>
#include <AzCore/IO/FileIO.h>
#include <AzCore/IO/Path/Path.h>
#include <AzCore/Settings/SettingsRegistryMergeUtils.h>
#include <AzFramework/API/ApplicationAPI.h>
#include <AzToolsFramework/API/EditorPythonRunnerRequestsBus.h>
#include <AzToolsFramework/API/ToolsApplicationAPI.h>

namespace O3DESharp
{
    // ============================================================
    // EditorCSharpScriptConfig
    // ============================================================

    void EditorCSharpScriptConfig::Reflect(AZ::ReflectContext* context)
    {
        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            // Guard against double-reflection
            if (serializeContext->FindClassData(azrtti_typeid<EditorCSharpScriptConfig>()))
            {
                return;
            }

            serializeContext->Class<EditorCSharpScriptConfig, AZ::ComponentConfig>()
                ->Version(1)
                ->Field("ScriptClassName", &EditorCSharpScriptConfig::m_scriptClassName)
                ->Field("AssemblyPath", &EditorCSharpScriptConfig::m_assemblyPath)
                // Note: m_validationStatus and m_isValid are not serialized - they are runtime state
                ;

            if (AZ::EditContext* editContext = serializeContext->GetEditContext())
            {
                editContext->Class<EditorCSharpScriptConfig>("C# Script Configuration", "Configuration for a C# script component")
                    ->ClassElement(AZ::Edit::ClassElements::EditorData, "")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                    ->DataElement(AZ::Edit::UIHandlers::Default, &EditorCSharpScriptConfig::m_scriptClassName,
                        "Script Class", "The fully qualified C# class name (e.g., MyGame.PlayerController)")
                    ->DataElement(AZ::Edit::UIHandlers::Default, &EditorCSharpScriptConfig::m_assemblyPath,
                        "Assembly Path", "Optional: Path to the assembly containing the script (leave empty for default)")
                    // Note: m_validationStatus is NOT shown here because O3DE requires editable fields to be serializable
                    ;
            }
        }
    }

    // ============================================================
    // EditorCSharpScriptComponent
    // ============================================================

    void EditorCSharpScriptComponent::Reflect(AZ::ReflectContext* context)
    {
        // Reflect editor config
        EditorCSharpScriptConfig::Reflect(context);

        // Also reflect the runtime config (it may already be reflected)
        CSharpScriptComponentConfig::Reflect(context);

        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            serializeContext->Class<EditorCSharpScriptComponent, AzToolsFramework::Components::EditorComponentBase>()
                ->Version(2)
                ->Field("Configuration", &EditorCSharpScriptComponent::m_config)
                ;

            if (AZ::EditContext* editContext = serializeContext->GetEditContext())
            {
                editContext->Class<EditorCSharpScriptComponent>("C# Script", "Attaches a C# script to this entity")
                    ->ClassElement(AZ::Edit::ClassElements::EditorData, "")
                        ->Attribute(AZ::Edit::Attributes::Category, "Scripting")
                        ->Attribute(AZ::Edit::Attributes::Icon, "Icons/Components/Script.svg")
                        ->Attribute(AZ::Edit::Attributes::ViewportIcon, "Icons/Components/Viewport/Script.svg")
                        ->Attribute(AZ::Edit::Attributes::AppearsInAddComponentMenu, AZ_CRC_CE("Game"))
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                        ->Attribute(AZ::Edit::Attributes::HelpPageURL, "https://docs.o3de.org/docs/user-guide/components/reference/scripting/csharp-script/")

                    // Embed the configuration - it has its own EditContext
                    ->DataElement(AZ::Edit::UIHandlers::Default, &EditorCSharpScriptComponent::m_config, "Configuration", "")
                        ->Attribute(AZ::Edit::Attributes::Visibility, AZ::Edit::PropertyVisibility::ShowChildrenOnly)
                        ->Attribute(AZ::Edit::Attributes::ChangeNotify, &EditorCSharpScriptComponent::OnScriptClassNameChanged)

                    // Action buttons
                    ->ClassElement(AZ::Edit::ClassElements::Group, "Actions")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                    
                    ->UIElement(AZ::Edit::UIHandlers::Button, "Browse...", "Browse for existing C# scripts")
                        ->Attribute(AZ::Edit::Attributes::ButtonText, "Browse Scripts...")
                        ->Attribute(AZ::Edit::Attributes::ChangeNotify, &EditorCSharpScriptComponent::OnBrowseScript)
                    
                    ->UIElement(AZ::Edit::UIHandlers::Button, "Create New", "Create a new C# script file")
                        ->Attribute(AZ::Edit::Attributes::ButtonText, "Create New Script...")
                        ->Attribute(AZ::Edit::Attributes::ChangeNotify, &EditorCSharpScriptComponent::OnCreateScript)
                    
                    ->UIElement(AZ::Edit::UIHandlers::Button, "Edit", "Open script in default IDE")
                        ->Attribute(AZ::Edit::Attributes::ButtonText, "Edit Script")
                        ->Attribute(AZ::Edit::Attributes::ChangeNotify, &EditorCSharpScriptComponent::OnEditScript)
                    ;
            }
        }
    }

    void EditorCSharpScriptComponent::GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided)
    {
        provided.push_back(AZ_CRC_CE("CSharpScriptService"));
    }

    void EditorCSharpScriptComponent::GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible)
    {
        // Multiple C# scripts can be on the same entity
        AZ_UNUSED(incompatible);
    }

    void EditorCSharpScriptComponent::GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required)
    {
        required.push_back(AZ_CRC_CE("TransformService"));
    }

    void EditorCSharpScriptComponent::GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent)
    {
        dependent.push_back(AZ_CRC_CE("O3DESharpSystemService"));
    }

    void EditorCSharpScriptComponent::SetConfiguration(const CSharpScriptComponentConfig& config)
    {
        m_config.m_scriptClassName = config.m_scriptClassName;
        m_config.m_assemblyPath = config.m_assemblyPath;
        ValidateScript();
    }

    CSharpScriptComponentConfig EditorCSharpScriptComponent::GetConfiguration() const
    {
        CSharpScriptComponentConfig config;
        config.m_scriptClassName = m_config.m_scriptClassName;
        config.m_assemblyPath = m_config.m_assemblyPath;
        return config;
    }

    void EditorCSharpScriptComponent::ValidateScript()
    {
        if (m_config.m_scriptClassName.empty())
        {
            m_config.m_validationStatus = "No script class specified";
            m_config.m_isValid = false;
            return;
        }

        // Basic format validation - check for namespace.classname pattern
        if (m_config.m_scriptClassName.find('.') == AZStd::string::npos)
        {
            m_config.m_validationStatus = "Warning: Class should include namespace (e.g., MyGame.MyScript)";
            m_config.m_isValid = false;
            return;
        }

        // TODO: When the O3DESharp system is initialized, we could validate
        // that the class actually exists in the assembly. For now, we mark
        // it as "Ready" if the format looks correct.
        m_config.m_validationStatus = "Ready";
        m_config.m_isValid = true;
    }

    AZ::Crc32 EditorCSharpScriptComponent::OnScriptClassNameChanged()
    {
        ValidateScript();
        return AZ::Edit::PropertyRefreshLevels::EntireTree;
    }

    AZ::Crc32 EditorCSharpScriptComponent::OnBrowseScript()
    {
        // Call Python script to show the script browser dialog
        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            R"(
import azlmbr.editor as editor
try:
    from O3DESharp.Editor.Scripts import csharp_editor_tools
    dialog = csharp_editor_tools.ScriptBrowserDialog()
    if dialog.exec_():
        selected_class = dialog.get_selected_class()
        if selected_class:
            # For now just log it - in a full implementation, we'd need
            # a way to pass this back to the C++ component
            print(f"Selected script class: {selected_class}")
except ImportError as e:
    print(f"Could not load C# editor tools: {e}")
)",
            false /* is file path */
        );

        return AZ::Edit::PropertyRefreshLevels::EntireTree;
    }

    AZ::Crc32 EditorCSharpScriptComponent::OnCreateScript()
    {
        // Call Python script to show the create script dialog
        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            R"(
import azlmbr.editor as editor
try:
    from O3DESharp.Editor.Scripts import csharp_editor_tools
    dialog = csharp_editor_tools.CreateScriptDialog()
    if dialog.exec_():
        class_name = dialog.get_created_class_name()
        if class_name:
            print(f"Created new script class: {class_name}")
except ImportError as e:
    print(f"Could not load C# editor tools: {e}")
)",
            false /* is file path */
        );

        return AZ::Edit::PropertyRefreshLevels::EntireTree;
    }

    AZ::Crc32 EditorCSharpScriptComponent::OnEditScript()
    {
        if (m_config.m_scriptClassName.empty())
        {
            AZ_Warning("O3DESharp", false, "No script class specified to edit");
            return AZ::Edit::PropertyRefreshLevels::None;
        }

        // Call Python script to open the script in the IDE
        AZStd::string pythonScript = AZStd::string::format(
            R"(
import azlmbr.editor as editor
import os
import subprocess

try:
    from O3DESharp.Editor.Scripts import csharp_project_manager
    
    manager = csharp_project_manager.CSharpProjectManager()
    class_name = "%s"
    
    # Find the script file based on class name
    for project_path in manager.list_projects():
        for script_path in manager.list_scripts(project_path):
            # Check if this script contains our class
            with open(script_path, 'r') as f:
                content = f.read()
                if class_name in content:
                    # Open in default editor
                    if os.name == 'nt':
                        os.startfile(script_path)
                    else:
                        subprocess.run(['xdg-open', script_path])
                    print(f"Opened script: {script_path}")
                    break
except Exception as e:
    print(f"Could not open script: {e}")
)",
            m_config.m_scriptClassName.c_str()
        );

        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            pythonScript.c_str(),
            false /* is file path */
        );

        return AZ::Edit::PropertyRefreshLevels::None;
    }

    AZStd::vector<AZStd::string> EditorCSharpScriptComponent::FindCSharpProjects() const
    {
        AZStd::vector<AZStd::string> projects;

        // Get the project root
        AZ::IO::Path projectPath;
        if (auto settingsRegistry = AZ::SettingsRegistry::Get())
        {
            settingsRegistry->Get(projectPath.Native(), AZ::SettingsRegistryMergeUtils::FilePathKey_ProjectPath);
        }

        if (!projectPath.empty())
        {
            // Look for .csproj files in common locations
            AZStd::vector<AZ::IO::Path> searchPaths = {
                projectPath / "Scripts",
                projectPath / "CSharp",
                projectPath / "Gem" / "Code" / "Scripts",
            };

            auto fileIO = AZ::IO::FileIOBase::GetInstance();
            if (fileIO)
            {
                for (const auto& searchPath : searchPaths)
                {
                    if (fileIO->Exists(searchPath.c_str()))
                    {
                        fileIO->FindFiles(
                            searchPath.c_str(),
                            "*.csproj",
                            [&projects](const char* filePath) -> bool
                            {
                                projects.push_back(filePath);
                                return true; // continue searching
                            }
                        );
                    }
                }
            }
        }

        return projects;
    }

    bool EditorCSharpScriptComponent::ClassExistsInAssembly(
        [[maybe_unused]] const AZStd::string& className, 
        [[maybe_unused]] const AZStd::string& assemblyPath) const
    {
        // TODO: Implement actual assembly inspection using Coral
        // For now, return true to allow runtime validation
        return true;
    }

    void EditorCSharpScriptComponent::Init()
    {
    }

    void EditorCSharpScriptComponent::Activate()
    {
        // Validate the script when the component is activated in the editor
        ValidateScript();
    }

    void EditorCSharpScriptComponent::Deactivate()
    {
    }

    void EditorCSharpScriptComponent::BuildGameEntity(AZ::Entity* gameEntity)
    {
        // Create the runtime component with our configuration
        CSharpScriptComponentConfig runtimeConfig;
        runtimeConfig.m_scriptClassName = m_config.m_scriptClassName;
        runtimeConfig.m_assemblyPath = m_config.m_assemblyPath;

        auto* runtimeComponent = gameEntity->CreateComponent<CSharpScriptComponent>(runtimeConfig);
        AZ_UNUSED(runtimeComponent);
    }
}
