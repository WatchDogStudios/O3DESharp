/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include <AzCore/Serialization/SerializeContext.h>
#include "O3DESharpEditorSystemComponent.h"

#include <O3DESharp/O3DESharpTypeIds.h>

#include <AzToolsFramework/ActionManager/Action/ActionManagerInterface.h>
#include <AzToolsFramework/ActionManager/Menu/MenuManagerInterface.h>
#include <AzToolsFramework/API/EditorPythonRunnerRequestsBus.h>
#include <AzToolsFramework/Editor/ActionManagerIdentifiers/EditorContextIdentifiers.h>
#include <AzToolsFramework/Editor/ActionManagerIdentifiers/EditorMenuIdentifiers.h>

namespace O3DESharp
{
    // Action identifiers for C# scripting
    static constexpr const char* CSharpProjectManagerActionId = "o3de.action.o3desharp.openProjectManager";
    static constexpr const char* CSharpCreateProjectActionId = "o3de.action.o3desharp.createProject";
    static constexpr const char* CSharpCreateScriptActionId = "o3de.action.o3desharp.createScript";
    static constexpr const char* CSharpBuildProjectsActionId = "o3de.action.o3desharp.buildProjects";

    // Menu identifier for our submenu
    static constexpr const char* CSharpScriptingMenuId = "o3de.menu.o3desharp.scripting";

    AZ_COMPONENT_IMPL(O3DESharpEditorSystemComponent, "O3DESharpEditorSystemComponent",
        O3DESharpEditorSystemComponentTypeId, BaseSystemComponent);

    void O3DESharpEditorSystemComponent::Reflect(AZ::ReflectContext* context)
    {
        // Reflect base class first to ensure full hierarchy is registered
        BaseSystemComponent::Reflect(context);

        if (auto serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            serializeContext->Class<O3DESharpEditorSystemComponent, O3DESharpSystemComponent>()
                ->Version(0);
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
    }

    void O3DESharpEditorSystemComponent::Deactivate()
    {
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
