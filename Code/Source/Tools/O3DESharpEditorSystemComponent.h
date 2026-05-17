/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/std/smart_ptr/unique_ptr.h>
#include <AzToolsFramework/API/ToolsApplicationAPI.h>
#include <AzToolsFramework/ActionManager/ActionManagerRegistrationNotificationBus.h>
#include <EditorPythonBindings/EditorPythonBindingsBus.h>

#include <Clients/O3DESharpSystemComponent.h>

namespace O3DESharp
{
    class CSharpScriptClassPropertyHandler;
    class CSharpExposedPropertiesHandler;
    class CSharpAssemblyWatcher;
}

// AZStd::unique_ptr<QObject-derived> needs the full destructor visible at
// the point where the unique_ptr is destroyed, but we only forward-declare
// CSharpAssemblyWatcher here. Out-of-line destructor on the system
// component does the actual delete in the .cpp where the watcher header is
// included.

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
        , protected EditorPythonBindings::EditorPythonBindingsNotificationBus::Handler
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

        // EditorPythonBindingsNotificationBus - fires when the Python VM is
        // ready. We use it to import csharp_editor_bootstrap (which in turn
        // imports csharp_editor_tools, which connects the CSharpEditorToolsBus
        // Python handler). Without this hook the handler is only created when
        // the user clicks one of the Tools > C# Scripting menu items, so the
        // Component Inspector's Browse Scripts button doesn't work until then.
        void OnPostInitialize() override;

        // Helper methods for C# scripting actions
        void OpenCSharpProjectManager();
        void CreateCSharpProject();
        void CreateCSharpScript();
        void BuildCSharpProjects();
        void ReloadCSharpScripts();
        void ToggleAutoReloadScripts();
        void MigrateCSharpProjects();
        void CopyDebuggerAttachInfo();

        // Phase 17a: one-click managed debugger attach. Each helper
        // trampolines through EditorPythonRunnerRequestBus to the same-
        // named function in csharp_editor_bootstrap. The Python side owns
        // the IDE detection + subprocess spawn so this file stays decoupled
        // from any specific debugger binary.
        void TriggerJitDebugger();
        void AttachWithRider();
        void AttachWithVSCode();

        // Phase 16: file watcher lifecycle helpers. StartAssemblyWatcher
        // reads the AutoReload settings, decides whether the watcher should
        // run, and (if so) starts it on <ProjectPath>/Bin/Scripts/.
        // StopAssemblyWatcher tears it down on Deactivate.
        void StartAssemblyWatcher();
        void StopAssemblyWatcher();
        bool IsAutoReloadEnabled() const;

        // Owned by this component; raw pointer because the property-handler bus
        // takes ownership semantics through Register/UnregisterPropertyType.
        CSharpScriptClassPropertyHandler* m_scriptClassPropertyHandler = nullptr;

        // Phase 10 scaffolding for typed exposed-property editor widgets.
        // Registered with the property-handler bus under
        // AZ_CRC_CE("CSharpExposedProperties") but the EditContext for
        // CSharpScriptComponentConfig::m_exposedPropertyValues is still on
        // the default UIHandler, so this handler is dormant until a
        // follow-up flips that switch.
        CSharpExposedPropertiesHandler* m_exposedPropertiesHandler = nullptr;

        // Phase 16: file watcher that auto-reloads user assemblies when DLLs
        // change in Bin/Scripts/. Editor-only - never linked into the runtime
        // module. Owned by AZStd::unique_ptr so the watcher's QObject parent
        // is the system component lifetime, not a transient stack frame.
        // Out-of-line destructor in the .cpp so the unique_ptr can see the
        // full CSharpAssemblyWatcher class.
        AZStd::unique_ptr<CSharpAssemblyWatcher> m_assemblyWatcher;
    };
} // namespace O3DESharp
