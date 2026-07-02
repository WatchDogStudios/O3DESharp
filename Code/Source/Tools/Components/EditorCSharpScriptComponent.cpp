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
                ->Version(3) // bumped: m_validationStatus now serialized for the inspector readout
                ->Field("ScriptClassName", &EditorCSharpScriptConfig::m_scriptClassName)
                ->Field("AssemblyPath", &EditorCSharpScriptConfig::m_assemblyPath)
                ->Field("ExposedProperties", &EditorCSharpScriptConfig::m_exposedPropertyValues)
                // m_validationStatus IS serialized: O3DE's EditContext requires every
                // DataElement to point at a serialized field, otherwise it asserts
                // "Class element for editor data element reflection 'Status' was NOT
                // found in the serialize context!" at module load. The cost of
                // serializing a short status string per entity is tiny and
                // ValidateScript() overwrites it on every Activate so any staleness
                // from a saved prefab is invisible in practice.
                // m_isValid stays unserialized: it's not referenced from EditContext.
                ->Field("ValidationStatus", &EditorCSharpScriptConfig::m_validationStatus)
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
                    // [ExposedProperty] values. Phase 14 switched the UI handler
                    // from Default (a generic key/value map editor) to the
                    // CSharpExposedPropertiesHandler, which queries the script's
                    // schema via O3DESharpRequests::GetExposedPropertySchemaJson
                    // and renders per-field typed widgets (QCheckBox / QSpinBox /
                    // QDoubleSpinBox / QLineEdit). The ScriptClassNameAttr
                    // attribute plumbs the sibling m_scriptClassName field into
                    // the handler so it knows which schema to query.
                    ->DataElement(AZ_CRC_CE("CSharpExposedProperties"), &EditorCSharpScriptConfig::m_exposedPropertyValues,
                        "Exposed Properties",
                        "Values for [ExposedProperty]-decorated fields on the selected script. "
                        "Applied to the managed instance before OnCreate, and pushed live to "
                        "the runtime instance when edited during Game Mode.")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                        ->Attribute(AZ_CRC_CE("ScriptClassNameAttr"), &EditorCSharpScriptConfig::m_scriptClassName)
                    ;
                    // (Entity-id discovery for the live-push broadcast is
                    // done from the handler's WriteGUIValuesIntoProperty
                    // via an InstanceDataNode walk - the EditContext
                    // attribute would need the callable to live on the
                    // same class as the field, which here is the config
                    // not the component.)
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
        // Go through the CSharpEditorToolsBus instead of inline Python. The bus
        // dispatches to OpenScriptPicker() on the connected Python handler,
        // which pops the dialog AND returns the selected class - so we can
        // assign it directly to the config field. The previous inline-Python
        // implementation opened the dialog but only printed the result, never
        // updating the component, which is why "Browse Scripts..." appeared to
        // do nothing.
        AZ_TracePrintf("O3DESharp", "OnBrowseScript: broadcasting OpenScriptPicker (current='%s')",
            m_config.m_scriptClassName.c_str());

        AZStd::string selectedClass;
        CSharpEditorToolsBus::BroadcastResult(
            selectedClass,
            &CSharpEditorToolsBus::Events::OpenScriptPicker,
            m_config.m_scriptClassName);

        AZ_TracePrintf("O3DESharp", "OnBrowseScript: BroadcastResult returned '%s'",
            selectedClass.c_str());

        // The Python-side picker returns one of three distinct values:
        //   1. A real class name            -> user picked a script.
        //   2. ScriptPickerClearedSentinel   -> user explicitly clicked
        //      "Clear Selection"; blank the field and re-validate exactly
        //      like a normal selection would (this is NOT an error).
        //   3. Empty string ""              -> user cancelled the dialog,
        //      or the Python EBus handler isn't connected. This is the
        //      only case that stays a true no-op.
        if (selectedClass == ScriptPickerClearedSentinel)
        {
            m_config.m_scriptClassName.clear();
            ValidateScript();
            return AZ::Edit::PropertyRefreshLevels::EntireTree;
        }

        if (!selectedClass.empty())
        {
            m_config.m_scriptClassName = selectedClass;
            ValidateScript();
            return AZ::Edit::PropertyRefreshLevels::EntireTree;
        }

        // Empty result = user cancelled or the Python EBus handler isn't
        // connected. Warn so the user can tell those two apart. Explicit
        // "Clear Selection" clicks are handled above and never reach here.
        AZ_Warning("O3DESharp", false,
            "OnBrowseScript: OpenScriptPicker returned empty. Either the user cancelled "
            "or the Python CSharpEditorToolsBus handler is not connected. Check the "
            "console for '[O3DESharp] CSharpEditorToolsBus handler connected' on editor "
            "startup; if missing, csharp_editor_bootstrap did not import the tools module.");
        return AZ::Edit::PropertyRefreshLevels::None;
    }

    AZ::Crc32 EditorCSharpScriptComponent::OnCreateScript()
    {
        // Same migration as OnBrowseScript: route through the EBus so the
        // created class name flows back into m_config.m_scriptClassName.
        AZStd::string createdClass;
        CSharpEditorToolsBus::BroadcastResult(
            createdClass,
            &CSharpEditorToolsBus::Events::CreateNewScript,
            AZStd::string{}, /* defaultName */
            AZStd::string{}  /* defaultNamespace */);

        if (!createdClass.empty())
        {
            m_config.m_scriptClassName = createdClass;
            ValidateScript();
            return AZ::Edit::PropertyRefreshLevels::EntireTree;
        }

        return AZ::Edit::PropertyRefreshLevels::None;
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
