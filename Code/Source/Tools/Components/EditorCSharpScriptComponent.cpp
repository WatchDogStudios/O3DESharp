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
#include <AzCore/IO/SystemFile.h>
#include <AzCore/Settings/SettingsRegistryMergeUtils.h>
#include <AzCore/JSON/rapidjson.h>
#include <AzCore/JSON/document.h>
#include <AzCore/Interface/Interface.h>
#include <AzFramework/API/ApplicationAPI.h>
#include <AzToolsFramework/API/EditorPythonRunnerRequestsBus.h>
#include <AzToolsFramework/API/ToolsApplicationAPI.h>

#include <Scripting/CoralHostManager.h>
#include <Tools/CSharpEditorToolsBus.h>

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
                ->Version(2) // bumped: added ExposedProperties
                ->Field("ScriptClassName", &EditorCSharpScriptConfig::m_scriptClassName)
                ->Field("AssemblyPath", &EditorCSharpScriptConfig::m_assemblyPath)
                ->Field("ExposedProperties", &EditorCSharpScriptConfig::m_exposedPropertyValues)
                // Note: m_validationStatus and m_isValid are not serialized - they are runtime state
                ;

            if (AZ::EditContext* editContext = serializeContext->GetEditContext())
            {
                // Define the config fields here - they will be shown via ShowChildrenOnly.
                // The Script Class field uses the AZ_CRC_CE("CSharpScriptClass") UI handler
                // (CSharpScriptClassPropertyHandler, registered by the editor system
                // component). That gives the user a combo box backed by the Python
                // CSharpEditorToolsBus handler instead of a plain text edit.
                editContext->Class<EditorCSharpScriptConfig>("C# Script Configuration", "Configuration for a C# script component")
                    ->ClassElement(AZ::Edit::ClassElements::EditorData, "")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                    ->DataElement(AZ_CRC_CE("CSharpScriptClass"), &EditorCSharpScriptConfig::m_scriptClassName,
                        "Script Class", "The fully qualified C# class name (e.g., MyGame.PlayerController)")
                    ->DataElement(AZ::Edit::UIHandlers::Default, &EditorCSharpScriptConfig::m_assemblyPath,
                        "Assembly Path", "Optional: Path to the assembly containing the script (leave empty for default)")
                    // Read-only feedback line so users immediately see why a typed class
                    // name is unrecognised, instead of only finding out at runtime.
                    ->DataElement(AZ::Edit::UIHandlers::Default, &EditorCSharpScriptConfig::m_validationStatus,
                        "Status", "Result of validating the Script Class field against the loaded assemblies.")
                        ->Attribute(AZ::Edit::Attributes::ReadOnly, true)
                    // [ExposedProperty] values. First-slice UX: generic
                    // string->string map editor. Typed widgets (sliders, color
                    // pickers, ...) are a planned follow-up.
                    ->DataElement(AZ::Edit::UIHandlers::Default, &EditorCSharpScriptConfig::m_exposedPropertyValues,
                        "Exposed Properties",
                        "Values for [ExposedProperty]-decorated fields on the selected script. "
                        "These are applied to the managed instance before OnCreate.")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
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
                        ->Attribute(AZ::Edit::Attributes::Icon, "Icons/Components/csharp.svg")
                        ->Attribute(AZ::Edit::Attributes::ViewportIcon, "Icons/Components/Viewport/csharp.svg")
                        ->Attribute(AZ::Edit::Attributes::AppearsInAddComponentMenu, AZ_CRC_CE("Game"))
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                        ->Attribute(AZ::Edit::Attributes::HelpPageURL, "")

                    // Script Selection group with embedded config
                    ->ClassElement(AZ::Edit::ClassElements::Group, "Script Selection")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)

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
        m_config.m_exposedPropertyValues = config.m_exposedPropertyValues;
        ValidateScript();
    }

    CSharpScriptComponentConfig EditorCSharpScriptComponent::GetConfiguration() const
    {
        CSharpScriptComponentConfig config;
        config.m_scriptClassName = m_config.m_scriptClassName;
        config.m_assemblyPath = m_config.m_assemblyPath;
        config.m_exposedPropertyValues = m_config.m_exposedPropertyValues;
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

        // If the Coral host is up, ask it whether the class actually exists in any
        // loaded user assembly. This is the only way to catch typos against the
        // live assemblies short of running the game.
        if (ClassExistsInAssembly(m_config.m_scriptClassName, m_config.m_assemblyPath))
        {
            m_config.m_validationStatus = "OK - class found in loaded assemblies";
            m_config.m_isValid = true;
        }
        else
        {
            // Coral host not initialized yet, or the class isn't present in any
            // loaded user assembly. Report the latter; the former only happens
            // in early Activate / before the runtime gem comes up.
            if (auto* coralHost = AZ::Interface<ICoralHostManager>::Get())
            {
                AZ_UNUSED(coralHost);
                m_config.m_validationStatus = "Error: class not found in any loaded assembly";
            }
            else
            {
                m_config.m_validationStatus = "Coral host not initialized yet";
            }
            m_config.m_isValid = false;
        }
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
import sys
import os
import azlmbr.paths

# Add O3DESharp Editor/Scripts to Python path if not already there
o3desharp_scripts_path = os.path.join(azlmbr.paths.engroot, 'Gems', 'O3DESharp', 'Editor', 'Scripts')
if o3desharp_scripts_path not in sys.path:
    sys.path.insert(0, o3desharp_scripts_path)

try:
    import csharp_editor_tools
    dialog = csharp_editor_tools.ScriptBrowserDialog()
    if dialog.exec_():
        selected_class = dialog.get_selected_class()
        if selected_class:
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
import sys
import os
import azlmbr.paths

# Add O3DESharp Editor/Scripts to Python path if not already there
o3desharp_scripts_path = os.path.join(azlmbr.paths.engroot, 'Gems', 'O3DESharp', 'Editor', 'Scripts')
if o3desharp_scripts_path not in sys.path:
    sys.path.insert(0, o3desharp_scripts_path)

try:
    import csharp_editor_tools
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

        // Dispatch to the Python-side CSharpEditorToolsBus::OpenScriptInEditor.
        // That handler uses the cached class index to do an exact full-name
        // match against the pre-discovered file paths (O(N)), then launches the
        // OS-default editor for that path. Previously this method generated and
        // executed an inline Python script that:
        //   (a) walked every project and every script (O(projects * scripts)),
        //   (b) did a substring match on file contents (false positives), and
        //   (c) string-formatted m_scriptClassName directly into Python source
        //       (Python-injection risk if a class name contained a quote).
        // Going through the bus removes all three problems.
        bool ok = false;
        CSharpEditorToolsBus::BroadcastResult(
            ok,
            &CSharpEditorToolsBus::Events::OpenScriptInEditor,
            m_config.m_scriptClassName);

        if (!ok)
        {
            AZ_Warning(
                "O3DESharp",
                false,
                "Could not open script for class '%s'. Check that the class name is "
                "fully qualified and that the matching .cs file is in a discovered C# project.",
                m_config.m_scriptClassName.c_str());
        }

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
        const AZStd::string& className,
        [[maybe_unused]] const AZStd::string& assemblyPath) const
    {
        // assemblyPath is intentionally ignored here: CoralHostManager owns the unified
        // load context and any class name lookup is satisfied by GetUserType iterating
        // every currently-loaded user assembly. Per-assembly disambiguation can be
        // added later if scripts ever live in non-default assemblies.
        auto* coralHost = AZ::Interface<ICoralHostManager>::Get();
        if (coralHost == nullptr)
        {
            // Host not initialized yet - we cannot validate. Defer the verdict to the
            // ValidateScript caller, which surfaces this as "Coral host not initialized".
            return false;
        }

        return coralHost->GetUserType(className) != nullptr;
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
        // Create the runtime component with our configuration. Transfer the
        // exposed-property map so the runtime component can hand values to
        // the managed instance on Activate.
        CSharpScriptComponentConfig runtimeConfig;
        runtimeConfig.m_scriptClassName = m_config.m_scriptClassName;
        runtimeConfig.m_assemblyPath = m_config.m_assemblyPath;
        runtimeConfig.m_exposedPropertyValues = m_config.m_exposedPropertyValues;

        auto* runtimeComponent = gameEntity->CreateComponent<CSharpScriptComponent>(runtimeConfig);
        AZ_UNUSED(runtimeComponent);
    }
}
