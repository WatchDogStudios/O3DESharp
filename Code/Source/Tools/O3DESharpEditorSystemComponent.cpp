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
#include "CSharpAssemblyWatcher.h"
#include "Components/CSharpScriptClassPropertyHandler.h"
#include "Components/CSharpExposedPropertiesHandler.h"

#include <O3DESharp/O3DESharpBus.h>
#include <O3DESharp/O3DESharpTypeIds.h>

#include <AzCore/Platform.h>
#include <AzCore/Settings/SettingsRegistry.h>
#include <AzCore/Utils/Utils.h>
#include <AzCore/IO/Path/Path.h>
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
    static constexpr const char* CSharpAutoReloadToggleActionId = "o3de.action.o3desharp.autoReloadToggle";
    static constexpr const char* CSharpMigrateProjectsActionId = "o3de.action.o3desharp.migrateProjects";
    static constexpr const char* CSharpCopyAttachInfoActionId = "o3de.action.o3desharp.copyAttachInfo";

    // Phase 17a: Attach Debugger submenu actions.
    static constexpr const char* CSharpAttachJitActionId    = "o3de.action.o3desharp.attachJit";
    static constexpr const char* CSharpAttachRiderActionId  = "o3de.action.o3desharp.attachRider";
    static constexpr const char* CSharpAttachVSCodeActionId = "o3de.action.o3desharp.attachVSCode";
    static constexpr const char* CSharpAttachDebuggerMenuId = "o3de.menu.o3desharp.attachDebugger";

    // Phase 17b: settings-driven auto-attach hooks.
    static constexpr const char* CSharpAutoAttachOnPlayActionId      = "o3de.action.o3desharp.autoAttachOnPlay";
    static constexpr const char* CSharpWaitForDebuggerActionId       = "o3de.action.o3desharp.waitForDebuggerOnActivate";

    // Settings key for /O3DE/O3DESharp/AutoAttachOnPlay. Valid values:
    //   ""        - disabled (default)
    //   "jit"     - trigger_jit_debugger
    //   "rider"   - attach_with_rider
    //   "vscode"  - attach_with_vscode
    // The menu cycles between these values; cycle order matches the menu
    // tooltip so the user can see what they just enabled.
    static constexpr const char* AutoAttachOnPlaySettingKey = "/O3DE/O3DESharp/AutoAttachOnPlay";

    // When true, CSharpScriptComponent::Activate blocks before OnCreate
    // until a managed debugger attaches. Mirrored into the
    // O3DESHARP_WAIT_FOR_DEBUGGER env var so the managed ScriptComponent
    // base class can read it without a Coral round-trip.
    static constexpr const char* WaitForDebuggerSettingKey = "/O3DE/O3DESharp/WaitForDebuggerOnActivate";

    // Menu identifier for our submenu
    static constexpr const char* CSharpScriptingMenuId = "o3de.menu.o3desharp.scripting";

    // Phase 16 settings keys.
    //   /O3DE/O3DESharp/AutoReload          - bool. When true, the editor
    //                                         watches Bin/Scripts/ and auto-
    //                                         reloads on DLL change. Default
    //                                         honors the same Debug/Profile
    //                                         gate as CoralHostConfig
    //                                         ::enableHotReload.
    //   /O3DE/O3DESharp/AutoReloadDebounceMs - int. ms to wait after last file
    //                                          event before firing the reload.
    //                                          Default 500.
    static constexpr const char* AutoReloadSettingKey =
        "/O3DE/O3DESharp/AutoReload";
    static constexpr const char* AutoReloadDebounceSettingKey =
        "/O3DE/O3DESharp/AutoReloadDebounceMs";

    // Forward declaration so OnPostInitialize (defined high in the file) can
    // call GetPythonPathSetup() (defined lower for proximity to its other
    // users).
    static constexpr const char* GetPythonPathSetup();

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
            // ->Handler<CSharpEditorToolsBusHandler>() on the bus reflection
            // is the canonical and ONLY registration needed - the binder is
            // exposed to Python via the EBus reflection, NOT the class registry.
            // EditorPythonBindings reads m_createHandler from BehaviorEBus and
            // surfaces it as
            //     azlmbr.<module>.<ebusName>Handler()
            // where <module> = "editor" (from Script::Attributes::Module below)
            // and <ebusName> = "CSharpEditorToolsBus", giving Python
            //     azlmbr.editor.CSharpEditorToolsBusHandler()
            // Calling that factory returns a PythonProxyNotificationHandler with
            // connect() / disconnect() / add_callback().
            //
            // Do NOT add ->Class<CSharpEditorToolsBusHandler>(...) here. That
            // populates BehaviorContext::m_classes for the binder and lets
            // azlmbr.object.create("CSharpEditorToolsBusHandler") return a
            // PythonProxyObject - but a PythonProxyObject has NO connect /
            // add_callback methods, so the Python side ends up with handler.connect
            // is None and the bus never wires up. Pattern verified against
            // Gems/PythonAssetBuilder/Code/Source/PythonBuilderNotificationHandler.cpp
            // and its mock_asset_builder.py usage.
            behaviorContext->EBus<CSharpEditorToolsBus>("CSharpEditorToolsBus")
                ->Attribute(AZ::Script::Attributes::Scope, AZ::Script::Attributes::ScopeFlags::Automation)
                ->Attribute(AZ::Script::Attributes::Module, "editor")
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
        AzToolsFramework::EditorEntityContextNotificationBus::Handler::BusConnect();
        EditorPythonBindings::EditorPythonBindingsNotificationBus::Handler::BusConnect();

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

        // Phase 16: auto-reload watcher on <ProjectPath>/Bin/Scripts/. Starts
        // only when the AutoReload setting allows it; defaults follow the
        // existing CoralHostConfig::enableHotReload gate (Debug/Profile yes,
        // Release no). The watcher itself broadcasts on O3DESharpRequestBus
        // and the runtime gem owns the bus, so we need O3DESharpSystemComponent
        // already activated above - which BaseSystemComponent::Activate
        // guarantees.
        StartAssemblyWatcher();
    }

    void O3DESharpEditorSystemComponent::Deactivate()
    {
        // Stop the auto-reload watcher first - it broadcasts on
        // O3DESharpRequestBus which is about to drop its handler when
        // BaseSystemComponent::Deactivate runs.
        StopAssemblyWatcher();

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

        EditorPythonBindings::EditorPythonBindingsNotificationBus::Handler::BusDisconnect();
        AzToolsFramework::EditorEntityContextNotificationBus::Handler::BusDisconnect();
        AzToolsFramework::ActionManagerRegistrationNotificationBus::Handler::BusDisconnect();
        AzToolsFramework::EditorEvents::Bus::Handler::BusDisconnect();
        O3DESharpSystemComponent::Deactivate();
    }

    void O3DESharpEditorSystemComponent::OnPostInitialize()
    {
        // Python VM is now up and EBus reflection has completed. Import
        // csharp_editor_bootstrap proactively so the CSharpEditorToolsBus
        // Python handler is connected for the Component Inspector's Browse
        // Scripts button - WITHOUT requiring the user to first open one of
        // the Tools > C# Scripting menu items as a side effect to load it.
        //
        // The bootstrap's __init__.py chain imports csharp_editor_tools, and
        // the bottom of that module calls connect_ebus_handler() which:
        //   1. creates the handler via azlmbr.editor.CSharpEditorToolsBusHandler()
        //   2. handler.connect() to attach to the bus
        //   3. handler.add_callback(...) for each of the 8 EBus events
        // After this hook fires, OnBrowseScript / OnCreateScript work end to end.
        AZ_TracePrintf("O3DESharp", "OnPostInitialize: importing csharp_editor_bootstrap to wire the editor tools bus");

        AZStd::string pythonCode = AZStd::string::format(R"(
%s
try:
    import csharp_editor_bootstrap  # noqa: F401  - side-effect import: connects CSharpEditorToolsBus handler
except Exception as e:
    import azlmbr.legacy.general as general
    import traceback
    general.log(f"[O3DESharp] Failed to import csharp_editor_bootstrap on startup: {e}")
    traceback.print_exc()
)", GetPythonPathSetup());

        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            pythonCode.c_str(),
            false /* is file path */);
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

        // Phase 16: "Reload Scripts on file change" toggle. Flips the
        // /O3DE/O3DESharp/AutoReload setting and Starts / Stops the watcher.
        // Registered as a checkable action so the menu shows a checkmark when
        // auto-reload is on.
        {
            AzToolsFramework::ActionProperties actionProperties;
            actionProperties.m_name = "Reload Scripts on File Change";
            actionProperties.m_description =
                "Watch Bin/Scripts/ and automatically reload user assemblies when DLLs change.";
            actionProperties.m_category = "Scripting";
            actionProperties.m_iconPath = ""; // checkable, no icon

            actionManagerInterface->RegisterCheckableAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpAutoReloadToggleActionId,
                actionProperties,
                [this]() { ToggleAutoReloadScripts(); },
                [this]() -> bool { return IsAutoReloadEnabled(); }
            );
        }

        // Phase 16b: "Migrate C# Project Files" - one-shot action that walks
        // the project's .csproj files and injects the Phase 16b auto-deploy
        // MSBuild target into any that don't already have it. Run once after
        // upgrading the gem; subsequent invocations are no-ops thanks to the
        // migration marker check in migrate_csproj_to_deploy_target.
        {
            AzToolsFramework::ActionProperties actionProperties;
            actionProperties.m_name = "Migrate C# Project Files";
            actionProperties.m_description =
                "Add the auto-deploy MSBuild target to existing user .csproj files so IDE builds "
                "(Rider, Visual Studio) drop their output into Bin/Scripts/ for the editor's file "
                "watcher to pick up.";
            actionProperties.m_category = "Scripting";

            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpMigrateProjectsActionId,
                actionProperties,
                [this]() { MigrateCSharpProjects(); }
            );
        }

        // "Copy Debugger Attach Info" - drops the current process ID and a
        // ready-made "Attach to Process" hint string onto the clipboard.
        // Saves the user from hunting through Task Manager for Editor.exe
        // each debug session.
        {
            AzToolsFramework::ActionProperties actionProperties;
            actionProperties.m_name = "Copy Debugger Attach Info";
            actionProperties.m_description =
                "Copy the editor's process ID and a managed-mode attach hint to the clipboard. "
                "Paste into Rider / VS / VS Code's 'Attach to Process' filter to attach the "
                "managed-mode debugger and set breakpoints in your C# scripts.";
            actionProperties.m_category = "Scripting";

            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpCopyAttachInfoActionId,
                actionProperties,
                [this]() { CopyDebuggerAttachInfo(); }
            );
        }

        // Phase 17a: one-click attach. Each option dispatches to its
        // namesake Python helper which does the IDE detection + subprocess
        // spawn. Failures are surfaced in the editor log via general.log
        // rather than as silent no-ops.
        {
            AzToolsFramework::ActionProperties props;
            props.m_name = "Trigger JIT Debugger";
            props.m_description =
                "Windows: pop the system JIT debugger picker (vsjitdebugger.exe -p <pid>) "
                "listing every registered managed-mode debugger. One click on your IDE in "
                "the picker attaches it to the editor. No effect on Linux / macOS.";
            props.m_category = "Scripting";
            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpAttachJitActionId, props,
                [this]() { TriggerJitDebugger(); });
        }
        {
            AzToolsFramework::ActionProperties props;
            props.m_name = "Attach with Rider";
            props.m_description =
                "Locate rider64.exe (PATH / Program Files / JetBrains Toolbox / registry) and "
                "invoke 'rider64.exe attach-to-process <pid>'. Falls back to a log line if no "
                "Rider install is detected.";
            props.m_category = "Scripting";
            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpAttachRiderActionId, props,
                [this]() { AttachWithRider(); });
        }
        {
            AzToolsFramework::ActionProperties props;
            props.m_name = "Attach with VS Code";
            props.m_description =
                "Open the current O3DE project in VS Code with the bundled "
                "'O3DESharp: Attach to Editor' launch configuration. Press F5 in VS Code to "
                "attach. The launch.json template lives at <project>/.vscode/launch.json and "
                "is dropped by Create C# Project automatically.";
            props.m_category = "Scripting";
            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpAttachVSCodeActionId, props,
                [this]() { AttachWithVSCode(); });
        }

        // Phase 17b: cycle action for auto-attach on play. The menu label
        // self-updates by feeding a name callback that reads the current
        // setting. RegisterAction does the basic registration; we then
        // refresh the action's display name from the setting on every
        // toggle. Using a plain (non-checkable) action because the option
        // has four states (off / jit / rider / vscode), not two.
        {
            AzToolsFramework::ActionProperties props;
            const AZStd::string current = GetAutoAttachOnPlay();
            props.m_name = current.empty()
                ? AZStd::string("Auto-attach on Game Mode: Off")
                : AZStd::string::format("Auto-attach on Game Mode: %s", current.c_str());
            props.m_description =
                "Cycle through Off / JIT / Rider / VS Code. When set, the editor invokes the "
                "chosen attach method right before Ctrl+G enters game mode, so scripts that "
                "block in OnCreate (e.g. via O3DE.Debugger.WaitForAttach) have a debugger "
                "ready before they're activated. Persisted in /O3DE/O3DESharp/AutoAttachOnPlay.";
            props.m_category = "Scripting";

            actionManagerInterface->RegisterAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpAutoAttachOnPlayActionId, props,
                [this]() { CycleAutoAttachOnPlay(); });
        }

        // Phase 17b: Wait For Debugger On Script Activate. Checkable so the
        // menu shows the current state. Mirror of /O3DE/O3DESharp/
        // WaitForDebuggerOnActivate; CSharpScriptComponent::Activate reads
        // the same key and sets the O3DESHARP_WAIT_FOR_DEBUGGER env var the
        // managed ScriptComponent base reads to decide whether to block.
        {
            AzToolsFramework::ActionProperties props;
            props.m_name = "Wait For Debugger On Script Activate";
            props.m_description =
                "Block every CSharpScriptComponent::Activate before user OnCreate runs until a "
                "managed debugger is attached (or 60s timeout). Useful for catching init-time "
                "bugs without sprinkling O3DE.Debugger.WaitForAttach into every script. Pairs "
                "well with Auto-attach on Game Mode.";
            props.m_category = "Scripting";

            actionManagerInterface->RegisterCheckableAction(
                EditorIdentifiers::MainWindowActionContextIdentifier,
                CSharpWaitForDebuggerActionId, props,
                [this]() { ToggleWaitForDebuggerOnActivate(); },
                [this]() -> bool { return IsWaitForDebuggerOnActivateEnabled(); });
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
        menuManagerInterface->AddActionToMenu(CSharpScriptingMenuId, CSharpAutoReloadToggleActionId, 510);
        menuManagerInterface->AddSeparatorToMenu(CSharpScriptingMenuId, 550);
        menuManagerInterface->AddActionToMenu(CSharpScriptingMenuId, CSharpMigrateProjectsActionId, 600);

        // Phase 17a: "Attach Debugger" submenu rolls up the four attach
        // entry points (JIT picker, Rider, VS Code, Copy Info). Group them
        // so the top-level submenu doesn't become a wall of debugger items.
        {
            AzToolsFramework::MenuProperties menuProperties;
            menuProperties.m_name = "Attach Debugger";
            menuManagerInterface->RegisterMenu(CSharpAttachDebuggerMenuId, menuProperties);
        }
        menuManagerInterface->AddSubMenuToMenu(CSharpScriptingMenuId, CSharpAttachDebuggerMenuId, 700);
        menuManagerInterface->AddActionToMenu(CSharpAttachDebuggerMenuId, CSharpAttachJitActionId,        100);
        menuManagerInterface->AddSeparatorToMenu(CSharpAttachDebuggerMenuId, 150);
        menuManagerInterface->AddActionToMenu(CSharpAttachDebuggerMenuId, CSharpAttachRiderActionId,      200);
        menuManagerInterface->AddActionToMenu(CSharpAttachDebuggerMenuId, CSharpAttachVSCodeActionId,     300);
        menuManagerInterface->AddSeparatorToMenu(CSharpAttachDebuggerMenuId, 350);
        menuManagerInterface->AddActionToMenu(CSharpAttachDebuggerMenuId, CSharpCopyAttachInfoActionId,   400);
        menuManagerInterface->AddSeparatorToMenu(CSharpAttachDebuggerMenuId, 450);
        menuManagerInterface->AddActionToMenu(CSharpAttachDebuggerMenuId, CSharpAutoAttachOnPlayActionId, 500);
        menuManagerInterface->AddActionToMenu(CSharpAttachDebuggerMenuId, CSharpWaitForDebuggerActionId, 600);
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

    bool O3DESharpEditorSystemComponent::IsAutoReloadEnabled() const
    {
        // Settings registry wins. If the user has explicitly toggled it
        // (via the menu, or via a setreg file in the project), that value is
        // authoritative. Otherwise default to the same gate that
        // CoralHostConfig::enableHotReload uses: on in Debug/Profile, off in
        // Release. The runtime hot-reload mechanism is itself gated by the
        // same flag, so this avoids the watcher running in Release where its
        // dispatched ReloadUserAssemblies would no-op anyway.
        bool value = false;
        if (auto* registry = AZ::SettingsRegistry::Get())
        {
            if (registry->Get(value, AutoReloadSettingKey))
            {
                return value;
            }
        }

#if defined(AZ_DEBUG_BUILD) || defined(AZ_PROFILE_BUILD)
        return true;
#else
        return false;
#endif
    }

    void O3DESharpEditorSystemComponent::StartAssemblyWatcher()
    {
        if (!IsAutoReloadEnabled())
        {
            AZ_TracePrintf(
                "O3DESharp",
                "Auto-reload disabled (/O3DE/O3DESharp/AutoReload). Use Tools > C# Scripting > "
                "Reload Scripts on File Change to enable it, or Reload Scripts to trigger a manual "
                "reload after every build.\n");
            return;
        }

        // Resolve the watch directory from the live project path. Same path
        // the runtime uses for assembly resolution in InitializeCoralHost.
        AZ::IO::FixedMaxPath projectPath = AZ::Utils::GetProjectPath();
        AZ::IO::FixedMaxPath binScriptsPath = projectPath / "Bin" / "Scripts";

        // Honor the debounce setting, defaulting to 500ms. The default is
        // chosen so that dotnet build's multi-chunk DLL write (typically
        // tens to low-hundreds of ms) coalesces into one reload.
        AZ::s64 debounceMs = 500;
        if (auto* registry = AZ::SettingsRegistry::Get())
        {
            registry->Get(debounceMs, AutoReloadDebounceSettingKey);
        }

        if (m_assemblyWatcher == nullptr)
        {
            m_assemblyWatcher = AZStd::make_unique<CSharpAssemblyWatcher>();
        }
        m_assemblyWatcher->Start(binScriptsPath.String(), static_cast<int>(debounceMs));
    }

    void O3DESharpEditorSystemComponent::StopAssemblyWatcher()
    {
        if (m_assemblyWatcher != nullptr)
        {
            m_assemblyWatcher->Stop();
        }
    }

    void O3DESharpEditorSystemComponent::MigrateCSharpProjects()
    {
        // Trampoline to csharp_editor_bootstrap.migrate_csharp_projects_to_deploy_target,
        // which walks the project's csprojs and patches each one that doesn't
        // already carry the Phase 16b deploy target marker. Output goes to
        // general.log so the user sees one line per migrated/skipped file in
        // the editor console.
        AZStd::string pythonCode = AZStd::string::format(R"(
%s
try:
    import csharp_editor_bootstrap
    csharp_editor_bootstrap.migrate_csharp_projects_to_deploy_target()
except Exception as e:
    import azlmbr.legacy.general as general
    general.log(f"Could not migrate C# projects: {e}")
)", GetPythonPathSetup());

        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            pythonCode.c_str(),
            false /* is file path */);
    }

    void O3DESharpEditorSystemComponent::CopyDebuggerAttachInfo()
    {
        // Compose a short, paste-friendly blob with the PID and a hint
        // string. We log to the console too so users in a remote shell
        // (no clipboard) can still read the values.
        const AZ::u32 pid = AZ::Platform::GetCurrentProcessId();

        // The "Attach to Process" filter in Rider / Visual Studio matches
        // both the PID and the process name, so giving them either works.
        AZStd::string blob = AZStd::string::format(
            "O3DE Editor (PID %u)\n"
            "Attach with: Rider Run > Attach to Process > pick PID %u > select .NET runtime.\n"
            "             Visual Studio Debug > Attach to Process > Connection target: localhost,"
            " pick PID %u, set Attach to: 'Managed (.NET Core, .NET 5+)'.",
            pid, pid, pid);

        // Use Qt's clipboard - already linked through AzQtComponents. We
        // execute the clipboard set via a Python runner trampoline rather
        // than a direct QApplication::clipboard() call to avoid pulling
        // <QApplication> into this TU and to keep the action thread-safe
        // (Python runner marshals to the main thread).
        AZStd::string pythonCode = AZStd::string::format(R"(
import azlmbr.legacy.general as general
try:
    from PySide2.QtWidgets import QApplication
except ImportError:
    from PySide6.QtWidgets import QApplication
QApplication.clipboard().setText('''%s''')
general.log('[O3DESharp] Debugger attach info copied to clipboard:\n' + '''%s''')
)", blob.c_str(), blob.c_str());

        AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
            &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
            pythonCode.c_str(),
            false /* is file path */);
    }

    namespace
    {
        // Helper for the Phase 17 attach actions - all three share the same
        // shape: call csharp_editor_bootstrap.<func>() through the Python
        // runner bus and let the Python side report status to general.log.
        void RunBootstrapFunctionAsync(const char* bootstrapFunctionName)
        {
            AZStd::string pythonCode = AZStd::string::format(R"(
%s
try:
    import csharp_editor_bootstrap
    csharp_editor_bootstrap.%s()
except Exception as e:
    import azlmbr.legacy.general as general
    general.log(f"[O3DESharp] csharp_editor_bootstrap.%s() failed: {e}")
)", GetPythonPathSetup(), bootstrapFunctionName, bootstrapFunctionName);

            AzToolsFramework::EditorPythonRunnerRequestBus::Broadcast(
                &AzToolsFramework::EditorPythonRunnerRequestBus::Events::ExecuteByString,
                pythonCode.c_str(),
                false /* is file path */);
        }
    } // namespace

    void O3DESharpEditorSystemComponent::TriggerJitDebugger()
    {
        RunBootstrapFunctionAsync("trigger_jit_debugger");
    }

    void O3DESharpEditorSystemComponent::AttachWithRider()
    {
        RunBootstrapFunctionAsync("attach_with_rider");
    }

    void O3DESharpEditorSystemComponent::AttachWithVSCode()
    {
        RunBootstrapFunctionAsync("attach_with_vscode");
    }

    AZStd::string O3DESharpEditorSystemComponent::GetAutoAttachOnPlay() const
    {
        AZStd::string value;
        if (auto* registry = AZ::SettingsRegistry::Get())
        {
            registry->Get(value, AutoAttachOnPlaySettingKey);
        }
        return value;
    }

    void O3DESharpEditorSystemComponent::CycleAutoAttachOnPlay()
    {
        // off -> jit -> rider -> vscode -> off
        const AZStd::string current = GetAutoAttachOnPlay();
        AZStd::string next;
        if (current.empty())          { next = "jit"; }
        else if (current == "jit")    { next = "rider"; }
        else if (current == "rider")  { next = "vscode"; }
        else                          { next = ""; } // includes "vscode" and any garbage value

        if (auto* registry = AZ::SettingsRegistry::Get())
        {
            registry->Set(AutoAttachOnPlaySettingKey, next);
        }

        AZ_Printf("O3DESharp", "Auto-attach on Game Mode: %s\n",
            next.empty() ? "Off" : next.c_str());

        // The action's display name was captured at register time. Refresh
        // it so the menu reflects the new state without an editor restart.
        if (auto* actionManagerInterface = AZ::Interface<AzToolsFramework::ActionManagerInterface>::Get())
        {
            AZStd::string label = next.empty()
                ? AZStd::string("Auto-attach on Game Mode: Off")
                : AZStd::string::format("Auto-attach on Game Mode: %s", next.c_str());
            actionManagerInterface->SetActionName(CSharpAutoAttachOnPlayActionId, label);
        }
    }

    bool O3DESharpEditorSystemComponent::IsWaitForDebuggerOnActivateEnabled() const
    {
        bool value = false;
        if (auto* registry = AZ::SettingsRegistry::Get())
        {
            registry->Get(value, WaitForDebuggerSettingKey);
        }
        return value;
    }

    void O3DESharpEditorSystemComponent::ToggleWaitForDebuggerOnActivate()
    {
        const bool current = IsWaitForDebuggerOnActivateEnabled();
        if (auto* registry = AZ::SettingsRegistry::Get())
        {
            registry->Set(WaitForDebuggerSettingKey, !current);
        }
        AZ_Printf("O3DESharp", "Wait For Debugger On Script Activate: %s\n",
            !current ? "ON" : "off");
    }

    void O3DESharpEditorSystemComponent::OnStartPlayInEditorBegin()
    {
        // Fires from EditorEntityContextNotificationBus right before play
        // mode actually starts ticking entities. Perfect spot to kick off
        // an attach: the user has just hit Ctrl+G, and we've still got a
        // few editor frames before any script runs. By the time OnCreate
        // fires the debugger has usually finished attaching - and if not,
        // a script can pair this with Debugger.WaitForAttach to be sure.
        const AZStd::string method = GetAutoAttachOnPlay();
        if (method.empty())
        {
            return;
        }

        const char* bootstrapFn = nullptr;
        if (method == "jit")         { bootstrapFn = "trigger_jit_debugger"; }
        else if (method == "rider")  { bootstrapFn = "attach_with_rider"; }
        else if (method == "vscode") { bootstrapFn = "attach_with_vscode"; }
        else
        {
            AZ_Warning("O3DESharp", false,
                "Unknown AutoAttachOnPlay value '%s'; valid values are 'jit', 'rider', 'vscode', or empty.",
                method.c_str());
            return;
        }

        AZ_Printf("O3DESharp", "OnStartPlayInEditorBegin: auto-attaching via '%s'\n", method.c_str());
        RunBootstrapFunctionAsync(bootstrapFn);
    }

    void O3DESharpEditorSystemComponent::ToggleAutoReloadScripts()
    {
        // Flip the registry setting then start or stop the watcher
        // accordingly. The checkable menu item's "checked" callback re-reads
        // the same registry key, so the UI updates without an explicit poke.
        const bool wasEnabled = IsAutoReloadEnabled();
        const bool nowEnabled = !wasEnabled;

        if (auto* registry = AZ::SettingsRegistry::Get())
        {
            registry->Set(AutoReloadSettingKey, nowEnabled);
        }

        if (nowEnabled)
        {
            StartAssemblyWatcher();
            AZ_Printf("O3DESharp", "Auto-reload C# scripts: enabled\n");
        }
        else
        {
            StopAssemblyWatcher();
            AZ_Printf("O3DESharp", "Auto-reload C# scripts: disabled\n");
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
