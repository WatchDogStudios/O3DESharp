/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include <AzCore/Serialization/SerializeContext.h>
#include "O3DESharpEditorSystemComponent.h"
#include "CSharpEditorToolsBus.h"
#include "Components/CSharpScriptClassPropertyHandler.h"
#include "Components/CSharpExposedPropertiesHandler.h"

#include <O3DESharp/O3DESharpBus.h>
#include <O3DESharp/O3DESharpTypeIds.h>

#include <AzToolsFramework/ActionManager/Action/ActionManagerInterface.h>
#include <AzToolsFramework/ActionManager/Menu/MenuManagerInterface.h>
#include <AzToolsFramework/API/EditorPythonRunnerRequestsBus.h>
#include <AzToolsFramework/Editor/ActionManagerIdentifiers/EditorContextIdentifiers.h>
#include <AzToolsFramework/Editor/ActionManagerIdentifiers/EditorMenuIdentifiers.h>
#include <AzToolsFramework/UI/PropertyEditor/PropertyEditorAPI.h>

namespace O3DESharp
{
    // Action identifiers for C# scripting
    static constexpr const char* CSharpProjectManagerActionId = "o3de.action.o3desharp.openProjectManager";
    static constexpr const char* CSharpCreateProjectActionId = "o3de.action.o3desharp.createProject";
    static constexpr const char* CSharpCreateScriptActionId = "o3de.action.o3desharp.createScript";
    static constexpr const char* CSharpBuildProjectsActionId = "o3de.action.o3desharp.buildProjects";
    static constexpr const char* CSharpReloadScriptsActionId = "o3de.action.o3desharp.reloadScripts";

    // Menu identifier for our submenu
    static constexpr const char* CSharpScriptingMenuId = "o3de.menu.o3desharp.scripting";

    AZ_COMPONENT_IMPL(O3DESharpEditorSystemComponent, "O3DESharpEditorSystemComponent",
        O3DESharpEditorSystemComponentTypeId, BaseSystemComponent);

    void O3DESharpEditorSystemComponent::Reflect(AZ::ReflectContext* context)
    {
        // Reflect base class first to ensure full hierarchy is registered
        BaseSystemComponent::Reflect(context);

        // Reflect CSharpEditorTools types for Python binding
        ScriptClassInfo::Reflect(context);
        ScriptValidationResult::Reflect(context);

        if (auto serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            serializeContext->Class<O3DESharpEditorSystemComponent, O3DESharpSystemComponent>()
                ->Version(0);
        }

        if (auto* behaviorContext = azrtti_cast<AZ::BehaviorContext*>(context))
        {
            behaviorContext->EBus<CSharpEditorToolsBus>("CSharpEditorToolsBus")
                ->Attribute(AZ::Script::Attributes::Scope, AZ::Script::Attributes::ScopeFlags::Automation)
                ->Attribute(AZ::Script::Attributes::Module, "editor")
                // Handler<> exposes a Python-implementable handler class as
                // azlmbr.editor.CSharpEditorToolsBusHandler. The Python side
                // (csharp_editor_tools.py) derives from it and calls connect().
                ->Handler<CSharpEditorToolsBusHandler>()
                ->Event("GetAvailableScriptClasses", &CSharpEditorToolsBus::Events::GetAvailableScriptClasses)
                ->Event("GetScriptClassNames", &CSharpEditorToolsBus::Events::GetScriptClassNames)
                ->Event("ValidateScriptClass", &CSharpEditorToolsBus::Events::ValidateScriptClass)
                ->Event("OpenScriptPicker", &CSharpEditorToolsBus::Events::OpenScriptPicker)
                ->Event("CreateNewScript", &CSharpEditorToolsBus::Events::CreateNewScript)
                ->Event("OpenScriptInEditor", &CSharpEditorToolsBus::Events::OpenScriptInEditor)
                ->Event("InvalidateCache", &CSharpEditorToolsBus::Events::InvalidateCache)
                ->Event("AddToRecentClasses", &CSharpEditorToolsBus::Events::AddToRecentClasses)
                ;
        }
    }

    O3DESharpEditorSystemComponent::O3DESharpEditorSystemComponent() = default;

    O3DESharpEditorSystemComponent::~O3DESharpEditorSystemComponent() = default;

    void O3DESharpEditorSystemComponent::GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided)
    {
        BaseSystemComponent::GetProvidedServices(provided);
        provided.push_back(AZ_CRC_CE("O3DESharpSystemEditorService"));
    }

    void O3DESharpEditorSystemComponent::GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible)
    {
        BaseSystemComponent::GetIncompatibleServices(incompatible);
        incompatible.push_back(AZ_CRC_CE("O3DESharpSystemEditorService"));
    }

    void O3DESharpEditorSystemComponent::GetRequiredServices([[maybe_unused]] AZ::ComponentDescriptor::DependencyArrayType& required)
    {
        BaseSystemComponent::GetRequiredServices(required);
    }

    void O3DESharpEditorSystemComponent::GetDependentServices([[maybe_unused]] AZ::ComponentDescriptor::DependencyArrayType& dependent)
    {
        BaseSystemComponent::GetDependentServices(dependent);
    }

    void O3DESharpEditorSystemComponent::Activate()
    {
        O3DESharpSystemComponent::Activate();
        AzToolsFramework::EditorEvents::Bus::Handler::BusConnect();
        AzToolsFramework::ActionManagerRegistrationNotificationBus::Handler::BusConnect();

        // Register the C# script class property handler so EditorCSharpScriptComponent's
        // m_scriptClassName field gets the rich combo box + completer instead of a plain
        // text edit. The handler matches via AZ_CRC_CE("CSharpScriptClass").
        using AzToolsFramework::PropertyTypeRegistrationMessages;
        if (m_scriptClassPropertyHandler == nullptr)
        {
            m_scriptClassPropertyHandler = aznew CSharpScriptClassPropertyHandler();
            PropertyTypeRegistrationMessages::Bus::Broadcast(
                &PropertyTypeRegistrationMessages::Bus::Events::RegisterPropertyType,
                m_scriptClassPropertyHandler);
        }

        // Phase 10 scaffolding: register the typed exposed-properties handler
        // under AZ_CRC_CE("CSharpExposedProperties"). Nothing in the
        // EditContext refers to that CRC yet, so this is dormant - a future
        // commit will flip CSharpScriptComponentConfig::m_exposedPropertyValues'
        // DataElement UIHandler from Default to the new CRC after the typed
        // widget tree has been validated in an editor.
        if (m_exposedPropertiesHandler == nullptr)
        {
            m_exposedPropertiesHandler = aznew CSharpExposedPropertiesHandler();
            PropertyTypeRegistrationMessages::Bus::Broadcast(
                &PropertyTypeRegistrationMessages::Bus::Events::RegisterPropertyType,
                m_exposedPropertiesHandler);
        }
    }

    void O3DESharpEditorSystemComponent::Deactivate()
    {
        // Unregister + delete the property handlers before tearing down anything
        // else that they might depend on.
        using AzToolsFramework::PropertyTypeRegistrationMessages;
        if (m_exposedPropertiesHandler != nullptr)
        {
            PropertyTypeRegistrationMessages::Bus::Broadcast(
                &PropertyTypeRegistrationMessages::Bus::Events::UnregisterPropertyType,
                m_exposedPropertiesHandler);
            delete m_exposedPropertiesHandler;
            m_exposedPropertiesHandler = nullptr;
        }
        if (m_scriptClassPropertyHandler != nullptr)
        {
            PropertyTypeRegistrationMessages::Bus::Broadcast(
                &PropertyTypeRegistrationMessages::Bus::Events::UnregisterPropertyType,
                m_scriptClassPropertyHandler);
            delete m_scriptClassPropertyHandler;
            m_scriptClassPropertyHandler = nullptr;
        }

        AzToolsFramework::ActionManagerRegistrationNotificationBus::Handler::BusDisconnect();
        AzToolsFramework::EditorEvents::Bus::Handler::BusDisconnect();
        O3DESharpSystemComponent::Deactivate();
    }

    void O3DESharpEditorSystemComponent::OnActionRegistrationHook()
    {
        auto actionManagerInterface = AZ::Interface<AzToolsFramework::ActionManagerInterface>::Get();
        if (!actionManagerInterface)
        {
            AZ_Warning("O3DESharp", false, "ActionManagerInterface not available, cannot register C# scripting actions");
            return;
        }

        // Register "Open C# Project Manager" action
        {
            AzToolsFramework::ActionProperties actionProperties;
            actionProperties.m_name = "C# Project Manager...";
            actionProperties.m_description = "Open the C# Project Manager to create and manage C# script projects";
            actionProperties.m_category = "Scripting";

            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpProjectManagerActionId,
                actionProperties,
                [this]() { OpenCSharpProjectManager(); }
            );
        }

        // Register "Create C# Project" action
        {
            AzToolsFramework::ActionProperties actionProperties;
            actionProperties.m_name = "Create C# Project...";
            actionProperties.m_description = "Create a new C# script project";
            actionProperties.m_category = "Scripting";

            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpCreateProjectActionId,
                actionProperties,
                [this]() { CreateCSharpProject(); }
            );
        }

        // Register "Create C# Script" action
        {
            AzToolsFramework::ActionProperties actionProperties;
            actionProperties.m_name = "Create C# Script...";
            actionProperties.m_description = "Create a new C# script file";
            actionProperties.m_category = "Scripting";

            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpCreateScriptActionId,
                actionProperties,
                [this]() { CreateCSharpScript(); }
            );
        }

        // Register "Build C# Projects" action
        {
            AzToolsFramework::ActionProperties actionProperties;
            actionProperties.m_name = "Build C# Projects";
            actionProperties.m_description = "Build all C# script projects in the current project";
            actionProperties.m_category = "Scripting";

            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpBuildProjectsActionId,
                actionProperties,
                [this]() { BuildCSharpProjects(); }
            );
        }

        // Register "Reload Scripts" action - triggers Coral host to unload and
        // reload all user assemblies. Useful after rebuilding without restarting
        // the editor; the README has long advertised this as "exact mechanism TBD",
        // this is the explicit mechanism.
        {
            AzToolsFramework::ActionProperties actionProperties;
            actionProperties.m_name = "Reload Scripts";
            actionProperties.m_description = "Unload and reload all C# user assemblies (Debug / Profile builds only)";
            actionProperties.m_category = "Scripting";

            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpReloadScriptsActionId,
                actionProperties,
                [this]() { ReloadCSharpScripts(); }
            );
        }
    }

    void O3DESharpEditorSystemComponent::OnMenuBindingHook()
    {
        auto menuManagerInterface = AZ::Interface<AzToolsFramework::MenuManagerInterface>::Get();
        if (!menuManagerInterface)
        {
            AZ_Warning("O3DESharp", false, "MenuManagerInterface not available, cannot bind C# scripting menus");
            return;
        }

        // Register the C# Scripting submenu
        {
            AzToolsFramework::MenuProperties menuProperties;
            menuProperties.m_name = "C# Scripting";
            
            menuManagerInterface->RegisterMenu(CSharpScriptingMenuId, menuProperties);
        }

        // Add our submenu to the Tools menu
        // Sort key of 6000 puts it after most existing items
        menuManagerInterface->AddSubMenuToMenu(EditorIdentifiers::ToolsMenuIdentifier, CSharpScriptingMenuId, 6000);

        // Add actions to our submenu
        menuManagerInterface->AddActionToMenu(CSharpScriptingMenuId, CSharpProjectManagerActionId, 100);
        menuManagerInterface->AddSeparatorToMenu(CSharpScriptingMenuId, 150);
        menuManagerInterface->AddActionToMenu(CSharpScriptingMenuId, CSharpCreateProjectActionId, 200);
        menuManagerInterface->AddActionToMenu(CSharpScriptingMenuId, CSharpCreateScriptActionId, 300);
        menuManagerInterface->AddSeparatorToMenu(CSharpScriptingMenuId, 350);
        menuManagerInterface->AddActionToMenu(CSharpScriptingMenuId, CSharpBuildProjectsActionId, 400);
        menuManagerInterface->AddActionToMenu(CSharpScriptingMenuId, CSharpReloadScriptsActionId, 500);
    }

    // Helper to get Python code that sets up the O3DESharp module path
    static constexpr const char* GetPythonPathSetup()
    {
        return R"(
import sys
import os
import azlmbr.paths

# Add O3DESharp Editor/Scripts to Python path if not already there
o3desharp_scripts_path = os.path.join(azlmbr.paths.engroot, 'Gems', 'O3DESharp', 'Editor', 'Scripts')
if o3desharp_scripts_path not in sys.path:
    sys.path.insert(0, o3desharp_scripts_path)
)";
    }

    void O3DESharpEditorSystemComponent::OpenCSharpProjectManager()
    {
        AZStd::string pythonCode = AZStd::string::format(R"(
%s
try:
    import csharp_editor_bootstrap
    csharp_editor_bootstrap.open_csharp_project_manager()
except Exception as e:
    import azlmbr.legacy.general as general
    general.log(f"Could not open C# Project Manager: {e}")
)", GetPythonPathSetup());

        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            pythonCode.c_str(),
            false
        );
    }

    void O3DESharpEditorSystemComponent::CreateCSharpProject()
    {
        AZStd::string pythonCode = AZStd::string::format(R"(
%s
try:
    import csharp_editor_bootstrap
    csharp_editor_bootstrap.create_csharp_project()
except Exception as e:
    import azlmbr.legacy.general as general
    general.log(f"Could not create C# project: {e}")
)", GetPythonPathSetup());

        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            pythonCode.c_str(),
            false
        );
    }

    void O3DESharpEditorSystemComponent::CreateCSharpScript()
    {
        AZStd::string pythonCode = AZStd::string::format(R"(
%s
try:
    import csharp_editor_bootstrap
    csharp_editor_bootstrap.create_csharp_script()
except Exception as e:
    import azlmbr.legacy.general as general
    general.log(f"Could not create C# script: {e}")
)", GetPythonPathSetup());

        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            pythonCode.c_str(),
            false
        );
    }

    void O3DESharpEditorSystemComponent::ReloadCSharpScripts()
    {
        // Dispatch to the runtime O3DESharpRequestBus which owns the Coral host
        // and knows how to flush user assemblies. If the bus has no handler
        // (system component not activated yet) we surface that to the editor log
        // rather than silently dropping the action.
        bool result = false;
        O3DESharpRequestBus::BroadcastResult(result, &O3DESharpRequests::ReloadUserAssemblies);
        if (result)
        {
            AZ_Printf("O3DESharp", "Reload Scripts: user assemblies reloaded successfully");
        }
        else
        {
            AZ_Warning(
                "O3DESharp",
                false,
                "Reload Scripts: ReloadUserAssemblies returned false. Hot reload is only "
                "available in Debug/Profile builds and requires the O3DESharp system "
                "component to be active. Check the log for details.");
        }
    }

    void O3DESharpEditorSystemComponent::BuildCSharpProjects()
    {
        AZStd::string pythonCode = AZStd::string::format(R"(
%s
try:
    import csharp_editor_bootstrap
    csharp_editor_bootstrap.build_csharp_projects()
except Exception as e:
    import azlmbr.legacy.general as general
    general.log(f"Could not build C# projects: {e}")
)", GetPythonPathSetup());

        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            pythonCode.c_str(),
            false
        );
    }

} // namespace O3DESharp
