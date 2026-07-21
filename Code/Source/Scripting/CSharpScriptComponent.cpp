/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#include "CSharpScriptComponent.h"
#include "CoralHostManager.h"
#include "CoralNativeThunkHost.h"

#include <AzCore/Console/ILogger.h>
#include <AzCore/Serialization/SerializeContext.h>
#include <AzCore/Serialization/EditContext.h>
#include <AzCore/RTTI/BehaviorContext.h>
#include <AzCore/Component/Entity.h>
#include <AzCore/Settings/SettingsRegistry.h>

#include <cstdlib> // _putenv_s / setenv for the Phase 17b debugger-wait gate

namespace O3DESharp
{
    namespace
    {
        // Process-wide cache of resolved pinned thunks, shared by every
        // CSharpScriptComponent instance. A single CoralHostManager backs the
        // whole gem, so one CoralNativeThunkHost is enough here; SetHost() is
        // a cheap no-op unless the host or scripts directory actually
        // changed, so calling it from every CreateScriptInstance() is fine.
        CoralNativeThunkHost& GetThunkHost()
        {
            static CoralNativeThunkHost s_thunkHost;
            return s_thunkHost;
        }
    } // namespace

    // ============================================================
    // CSharpScriptComponentConfig
    // ============================================================

    void CSharpScriptComponentConfig::Reflect(AZ::ReflectContext* context)
    {
        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            // Guard against double-reflection (can happen when both runtime and editor modules load)
            if (serializeContext->FindClassData(azrtti_typeid<CSharpScriptComponentConfig>()))
            {
                return;
            }

            serializeContext->Class<CSharpScriptComponentConfig, AZ::ComponentConfig>()
                ->Version(2) // bumped: added m_exposedPropertyValues
                ->Field("ScriptClassName", &CSharpScriptComponentConfig::m_scriptClassName)
                ->Field("AssemblyPath", &CSharpScriptComponentConfig::m_assemblyPath)
                ->Field("ExposedProperties", &CSharpScriptComponentConfig::m_exposedPropertyValues)
                ;

            if (AZ::EditContext* editContext = serializeContext->GetEditContext())
            {
                editContext->Class<CSharpScriptComponentConfig>("C# Script Configuration", "Configuration for a C# script component")
                    ->ClassElement(AZ::Edit::ClassElements::EditorData, "")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                    ->DataElement(AZ::Edit::UIHandlers::Default, &CSharpScriptComponentConfig::m_scriptClassName,
                        "Script Class", "The fully qualified C# class name (e.g., MyGame.PlayerController)")
                        ->Attribute(AZ::Edit::Attributes::ChangeNotify, AZ::Edit::PropertyRefreshLevels::EntireTree)
                    ->DataElement(AZ::Edit::UIHandlers::Default, &CSharpScriptComponentConfig::m_assemblyPath,
                        "Assembly Path", "Optional: Path to the assembly containing the script (leave empty for default)")
                    // Phase 7 first slice: exposed [ExposedProperty] values are
                    // shown as a generic key/value map. Typed per-field widgets
                    // (sliders, color pickers, ...) are the planned follow-up.
                    ->DataElement(AZ::Edit::UIHandlers::Default, &CSharpScriptComponentConfig::m_exposedPropertyValues,
                        "Exposed Properties",
                        "Values for [ExposedProperty]-decorated fields on the selected script. "
                        "Edit name->value entries here; they are applied to the managed "
                        "instance before OnCreate.")
                        ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                    ;
            }
        }
    }

    // ============================================================
    // CSharpScriptComponent
    // ============================================================

    void CSharpScriptComponent::Reflect(AZ::ReflectContext* context)
    {
        CSharpScriptComponentConfig::Reflect(context);

        if (auto* serializeContext = azrtti_cast<AZ::SerializeContext*>(context))
        {
            // Guard against double-reflection (can happen when both runtime and editor modules load)
            if (!serializeContext->FindClassData(azrtti_typeid<CSharpScriptComponent>()))
            {
                serializeContext->Class<CSharpScriptComponent, AZ::Component>()
                    ->Version(1)
                    ->Field("Configuration", &CSharpScriptComponent::m_config)
                    ;

                if (AZ::EditContext* editContext = serializeContext->GetEditContext())
                {
                    editContext->Class<CSharpScriptComponent>("C# Script", "Attaches a C# script to this entity")
                        ->ClassElement(AZ::Edit::ClassElements::EditorData, "")
                            ->Attribute(AZ::Edit::Attributes::Category, "Scripting")
                            ->Attribute(AZ::Edit::Attributes::Icon, "Icons/Components/csharp.svg")
                            ->Attribute(AZ::Edit::Attributes::ViewportIcon, "Icons/Components/Viewport/csharp.svg")
                            ->Attribute(AZ::Edit::Attributes::AutoExpand, true)
                            ->Attribute(AZ::Edit::Attributes::HelpPageURL, "")
                        ->DataElement(AZ::Edit::UIHandlers::Default, &CSharpScriptComponent::m_config, "Configuration", "")
                            ->Attribute(AZ::Edit::Attributes::Visibility, AZ::Edit::PropertyVisibility::ShowChildrenOnly)
                        ;
                }
            }
        }

        if (auto* behaviorContext = azrtti_cast<AZ::BehaviorContext*>(context))
        {
            // Guard against double registration (can happen when Editor module reflects base classes)
            if (behaviorContext->m_classes.find("CSharpScriptComponent") == behaviorContext->m_classes.end())
            {
                behaviorContext->Class<CSharpScriptComponent>("CSharpScriptComponent")
                    ->Attribute(AZ::Script::Attributes::Module, "scripting")
                    ->Attribute(AZ::Script::Attributes::Scope, AZ::Script::Attributes::ScopeFlags::Common)
                    ->Method("IsScriptValid", &CSharpScriptComponent::IsScriptValid)
                    ->Method("ReloadScript", &CSharpScriptComponent::ReloadScript)
                    ;
            }
        }
    }

    void CSharpScriptComponent::GetProvidedServices(AZ::ComponentDescriptor::DependencyArrayType& provided)
    {
        provided.push_back(AZ_CRC_CE("CSharpScriptService"));
    }

    void CSharpScriptComponent::GetIncompatibleServices(AZ::ComponentDescriptor::DependencyArrayType& incompatible)
    {
        // Multiple C# scripts can be on the same entity, so no incompatibilities
        AZ_UNUSED(incompatible);
    }

    void CSharpScriptComponent::GetRequiredServices(AZ::ComponentDescriptor::DependencyArrayType& required)
    {
        required.push_back(AZ_CRC_CE("TransformService"));
    }

    void CSharpScriptComponent::GetDependentServices(AZ::ComponentDescriptor::DependencyArrayType& dependent)
    {
        dependent.push_back(AZ_CRC_CE("O3DESharpSystemService"));
    }

    CSharpScriptComponent::CSharpScriptComponent(const CSharpScriptComponentConfig& config)
        : m_config(config)
    {
    }

    CSharpScriptComponent::~CSharpScriptComponent()
    {
        DestroyScriptInstance();
    }

    void CSharpScriptComponent::SetConfiguration(const CSharpScriptComponentConfig& config)
    {
        m_config = config;
    }

    const CSharpScriptComponentConfig& CSharpScriptComponent::GetConfiguration() const
    {
        return m_config;
    }

    bool CSharpScriptComponent::IsScriptValid() const
    {
        return m_scriptInstance.IsValid() && m_scriptInitialized;
    }

    void CSharpScriptComponent::ReloadScript()
    {
        AZLOG_INFO("CSharpScriptComponent: Reloading script '%s' on entity '%s'",
            m_config.m_scriptClassName.c_str(),
            GetEntity() ? GetEntity()->GetName().c_str() : "Unknown");

        // Destroy current instance
        DestroyScriptInstance();
        m_disabledByException = false; // re-arm for the new instance

        // Create new instance
        if (CreateScriptInstance())
        {
            // Call OnCreate on the new instance
            if (m_scriptInstance.IsValid())
            {
                SetEntityIdOnScript();
                PushExposedPropertiesToScript();
                SafeInvokeMethod("OnCreate");
            }
        }
    }

    void CSharpScriptComponent::Init()
    {
        // Initialization before activation
    }

    void CSharpScriptComponent::Activate()
    {
        if (m_isActivating)
        {
            return;
        }
        m_isActivating = true;
        m_disabledByException = false; // re-arm on (re)activation

        AZLOG_INFO("CSharpScriptComponent: Activating script '%s' on entity '%s'",
            m_config.m_scriptClassName.c_str(),
            GetEntity() ? GetEntity()->GetName().c_str() : "Unknown");

        // Create the managed script instance
        if (CreateScriptInstance())
        {
            // Pass entity ID to the script
            SetEntityIdOnScript();

            // ===========================================================
            // Debugger attach is non-blocking. (Was: Phase 17b/17d wait-
            // for-debugger gate that called O3DE.Debugger.WaitForAttachIfRequested
            // here, blocking the editor main thread for up to 120s.)
            //
            // Rationale for removing the block:
            //   - The editor freezing for 120s on every Game Mode entry
            //     was confusing UX. Users hitting Ctrl+G with the auto-
            //     attach IDE already launched but not yet attached saw the
            //     editor go non-responsive with no indication of progress.
            //   - The blocking call was implicitly enabled whenever
            //     AutoAttachOnPlay was set (the previous "implicit default"
            //     followed auto-attach), so configuring the seamless flow
            //     also configured the freeze. Most users didn't realize
            //     these were coupled.
            //   - There is no way to "stop a managed script for debugger
            //     attach" without also stopping the editor; scripts run on
            //     the editor main thread. So if you genuinely want a break
            //     in OnCreate, the user-facing path below is more honest
            //     (you're explicitly choosing to block).
            //
            // The supported paths for debugging at script-create time:
            //   1. ATTACH FIRST, THEN ENTER GAME MODE.
            //      Attach your IDE to Editor.exe before pressing Ctrl+G.
            //      OnCreate runs with the debugger already live so any
            //      breakpoint in OnCreate binds and hits.
            //   2. RELOAD SCRIPTS AFTER ATTACHING.
            //      If you forgot step 1, hit Tools > C# Scripting > Reload
            //      Scripts after the attach completes. That re-runs OnCreate
            //      under the now-attached debugger.
            //   3. EXPLICIT IN-SCRIPT BLOCK.
            //      Add  O3DE.Debugger.WaitForAttach(TimeSpan.FromSeconds(30))
            //      to the top of your OnCreate. This WILL block the editor
            //      main thread - that's intentional because YOU asked for
            //      it in YOUR script; that's a different proposition from
            //      the gem silently blocking on every Activate.
            //
            // We do NOT call WaitForAttachIfRequested here anymore.
            // ===========================================================

            // Push editor-configured [ExposedProperty] values into the managed
            // instance BEFORE OnCreate runs so user OnCreate code sees them.
            PushExposedPropertiesToScript();

            // Call OnCreate on the managed instance
            SafeInvokeMethod("OnCreate");
        }

        // Connect to tick bus to call OnUpdate
        AZ::TickBus::Handler::BusConnect();

        // Connect to transform notifications
        AZ::TransformNotificationBus::Handler::BusConnect(GetEntityId());

        // Connect to hot-reload notifications so we tear down + rebuild
        // our managed state around an assembly-context reload (Phase 13).
        O3DESharpHotReloadNotificationBus::Handler::BusConnect();

        // Connect to exposed-property-change notifications keyed by THIS
        // entity's id. The editor-side CSharpExposedPropertiesHandler
        // broadcasts to this bus when the user edits an [ExposedProperty]
        // widget in the inspector during Game Mode. Without this connect
        // the runtime would only see property values at Activate time
        // (from BuildGameEntity's config copy) and stay stale on every
        // inspector edit until the next Reload Scripts / Game Mode
        // re-enter.
        O3DESharpExposedPropertyNotificationBus::Handler::BusConnect(GetEntityId());

        m_isActivating = false;
    }

    void CSharpScriptComponent::Deactivate()
    {
        AZLOG_INFO("CSharpScriptComponent: Deactivating script '%s' on entity '%s'",
            m_config.m_scriptClassName.c_str(),
            GetEntity() ? GetEntity()->GetName().c_str() : "Unknown");

        // Disconnect from buses
        O3DESharpExposedPropertyNotificationBus::Handler::BusDisconnect();
        O3DESharpHotReloadNotificationBus::Handler::BusDisconnect();
        AZ::TransformNotificationBus::Handler::BusDisconnect();
        AZ::TickBus::Handler::BusDisconnect();

        // Call OnDestroy before destroying the instance. Use the safe wrapper so
        // a throwing OnDestroy doesn't tear down the rest of the gem shutdown.
        SafeInvokeMethod("OnDestroy");

        // Destroy the managed instance
        DestroyScriptInstance();
    }

    void CSharpScriptComponent::OnTick(float deltaTime, [[maybe_unused]] AZ::ScriptTimePoint time)
    {
        if (m_disabledByException)
        {
            return;
        }

        if (m_scriptInstance.IsValid() && m_scriptInitialized)
        {
            // Single managed transition per frame: ScriptComponent.Tick(dt) calls
            // OnUpdate then ProcessPendingInvocations on the managed side. This
            // replaces the previous pair of InvokeMethod calls (one of which
            // was unconditional even when no actions were scheduled).
            //
            // Fast path: a resolved pinned thunk costs an indirect call plus a
            // dictionary lookup. InvokeMethod would do a managed-side string
            // lookup plus reflection dispatch - every frame, per component.
            if (!TryInvokeViaThunk(LifecycleId::Tick, deltaTime))
            {
                SafeInvokeMethod("Tick", deltaTime);
            }
        }
    }

    void CSharpScriptComponent::OnTransformChanged(
        [[maybe_unused]] const AZ::Transform& local,
        [[maybe_unused]] const AZ::Transform& world)
    {
        if (m_disabledByException)
        {
            return;
        }

        // Optionally notify the script of transform changes
        // This could be used to call OnTransformChanged on the C# side
        if (m_scriptInstance.IsValid() && m_scriptInitialized)
        {
            // The script can query the transform via the Transform API
            // We don't pass the transform data directly to avoid complex marshalling
            // Scripts that need to react to transform changes can override OnTransformChanged
            SafeInvokeMethod("OnTransformChanged");
        }
    }

    bool CSharpScriptComponent::TryInvokeViaThunk(LifecycleId id, float arg) noexcept
    {
        if (m_bridgeInvoke == nullptr || m_bridgeHandle == 0)
        {
            return false;
        }

        // A 0 return means the managed handle is dead (component torn down).
        // That is NOT a reason to fall back to InvokeMethod - the instance is
        // gone, so doing nothing is correct.
        m_bridgeInvoke(m_bridgeHandle, static_cast<int>(id), arg);
        return true;
    }

    void CSharpScriptComponent::SafeInvokeMethod(const char* methodName) noexcept
    {
        if (m_disabledByException || !m_scriptInstance.IsValid())
        {
            return;
        }

        try
        {
            m_scriptInstance.InvokeMethod(methodName);
        }
        catch (const std::exception& ex)
        {
            DisableAfterUnhandledException(methodName, ex.what());
        }
        catch (...)
        {
            DisableAfterUnhandledException(methodName, "non-std::exception");
        }
    }

    void CSharpScriptComponent::SafeInvokeMethod(const char* methodName, float deltaTime) noexcept
    {
        if (m_disabledByException || !m_scriptInstance.IsValid())
        {
            return;
        }

        try
        {
            m_scriptInstance.InvokeMethod(methodName, deltaTime);
        }
        catch (const std::exception& ex)
        {
            DisableAfterUnhandledException(methodName, ex.what());
        }
        catch (...)
        {
            DisableAfterUnhandledException(methodName, "non-std::exception");
        }
    }

    void CSharpScriptComponent::PushExposedPropertiesToScript()
    {
        if (m_disabledByException || !m_scriptInstance.IsValid())
        {
            return;
        }
        if (m_config.m_exposedPropertyValues.empty())
        {
            return;
        }

        // Build a flat { "name": "value", ... } JSON object. The values are
        // already strings (the config map is string->string) so we just need
        // to handle JSON escaping. Keep the encoder small / inline rather than
        // pulling in rapidjson for one trivial use - O3DE.Core's
        // ExposedPropertyHelpers.ParseSimpleStringMap accepts this shape.
        auto escape = [](const AZStd::string& s) -> AZStd::string
        {
            AZStd::string out;
            out.reserve(s.size() + 2);
            for (char c : s)
            {
                switch (c)
                {
                case '"':  out += "\\\""; break;
                case '\\': out += "\\\\"; break;
                case '\n': out += "\\n";  break;
                case '\r': out += "\\r";  break;
                case '\t': out += "\\t";  break;
                default:   out += c;      break;
                }
            }
            return out;
        };

        AZStd::string json = "{";
        bool first = true;
        for (const auto& kv : m_config.m_exposedPropertyValues)
        {
            if (!first) { json += ","; }
            first = false;
            json += "\"";
            json += escape(kv.first);
            json += "\":\"";
            json += escape(kv.second);
            json += "\"";
        }
        json += "}";

        // Hand the JSON to the managed instance. Coral marshals const char* to
        // a managed string argument. The C# side reparses with its own minimal
        // parser (ExposedPropertyHelpers.ParseSimpleStringMap) so we don't have
        // to depend on System.Text.Json being trim-safe.
        try
        {
            m_scriptInstance.InvokeMethod("ApplyExposedProperties", json.c_str());
        }
        catch ([[maybe_unused]] const std::exception& ex)
        {
            AZ_Warning(
                "O3DESharp",
                false,
                "CSharpScriptComponent: ApplyExposedProperties failed on entity '%s' (script '%s'): %s",
                GetEntity() ? GetEntity()->GetName().c_str() : "Unknown",
                m_config.m_scriptClassName.c_str(),
                ex.what());
        }
        catch (...)
        {
            AZ_Warning(
                "O3DESharp",
                false,
                "CSharpScriptComponent: ApplyExposedProperties failed on entity '%s' (script '%s')",
                GetEntity() ? GetEntity()->GetName().c_str() : "Unknown",
                m_config.m_scriptClassName.c_str());
        }
    }

    void CSharpScriptComponent::OnBeforeUserAssemblyReload()
    {
        // The Coral context is about to be unloaded; m_scriptType and
        // m_scriptInstance will be dangling pointers in a moment. Tear them
        // down BEFORE that happens. Calling OnDestroy is intentional - it
        // mirrors what Deactivate does, giving user code a chance to clean
        // up state, but we use the safe wrapper so an exception inside
        // OnDestroy doesn't prevent the rest of the teardown.
        if (m_scriptInstance.IsValid())
        {
            SafeInvokeMethod("OnDestroy");
        }
        DestroyScriptInstance();

        // The unified assembly context (O3DE.Core.dll included) is about to
        // be unloaded by CoralHostManager::ReloadUserAssemblies, right after
        // this broadcast finishes. Every pinned thunk resolved against it -
        // including ScriptComponentBridge.Invoke - is about to dangle, so
        // drop the cache now. Redundant across multiple components handling
        // this same broadcast, but InvalidateCache() is just a map clear.
        GetThunkHost().InvalidateCache();

        // Detach from TickBus so we don't try to dispatch into the now-
        // invalid context before OnAfterUserAssemblyReload reconstructs us.
        AZ::TickBus::Handler::BusDisconnect();
    }

    void CSharpScriptComponent::OnAfterUserAssemblyReload()
    {
        // The user assemblies have been reloaded and internal calls are
        // re-registered. Rebuild the managed instance, push exposed
        // properties, and fire OnCreate - matching the Activate flow.
        m_disabledByException = false;

        if (CreateScriptInstance())
        {
            SetEntityIdOnScript();
            PushExposedPropertiesToScript();
            SafeInvokeMethod("OnCreate");
        }

        // Re-attach to TickBus so OnUpdate resumes firing.
        AZ::TickBus::Handler::BusConnect();
    }

    void CSharpScriptComponent::DisableAfterUnhandledException(
        [[maybe_unused]] const char* methodName,
        [[maybe_unused]] const char* what)
    {
        m_disabledByException = true;

        AZ_Error(
            "O3DESharp",
            false,
            "CSharpScriptComponent: unhandled exception in '%s' on entity '%s' (script '%s'): %s. "
            "Disabling this component for the rest of the session to avoid per-frame spam. "
            "Reactivate the entity (or hot-reload the assembly) to retry.",
            methodName,
            GetEntity() ? GetEntity()->GetName().c_str() : "Unknown",
            m_config.m_scriptClassName.c_str(),
            what ? what : "<no message>");

        // Detach from TickBus so we don't pay the dispatch cost every frame for
        // a component we'll just no-op anyway.
        AZ::TickBus::Handler::BusDisconnect();
    }

    void CSharpScriptComponent::OnExposedPropertyChanged(
        const AZStd::unordered_map<AZStd::string, AZStd::string>& newValues)
    {
        // Inspector edits flow through here during Game Mode. The editor
        // component on the same entity-id broadcasts to this bus from
        // CSharpExposedPropertiesHandler::WriteGUIValuesIntoProperty
        // whenever the user commits a value change.
        //
        // We replace the local map wholesale (the editor side sends the
        // full map, not a delta) and re-push to the managed instance.
        // Push is no-op if m_disabledByException or the script isn't
        // valid, so no extra guard needed here.
        if (m_disabledByException)
        {
            return;
        }

        AZLOG_INFO(
            "CSharpScriptComponent: Live-updating %zu exposed properties on entity '%s' "
            "(script '%s') from inspector edit.",
            newValues.size(),
            GetEntity() ? GetEntity()->GetName().c_str() : "Unknown",
            m_config.m_scriptClassName.c_str());

        m_config.m_exposedPropertyValues = newValues;
        PushExposedPropertiesToScript();
    }

    bool CSharpScriptComponent::CreateScriptInstance()
    {
        if (m_config.m_scriptClassName.empty())
        {
            AZLOG_WARN("CSharpScriptComponent: No script class name specified");
            return false;
        }

        // Get the Coral host manager
        ICoralHostManager* hostManager = CoralHostManagerInterface::Get();
        if (!hostManager || !hostManager->IsInitialized())
        {
            AZLOG_ERROR("CSharpScriptComponent: Coral host not initialized");
            return false;
        }

        // Try to find the type in the user assembly first, then core assembly
        Coral::Type* scriptType = hostManager->GetUserType(m_config.m_scriptClassName);
        if (!scriptType)
        {
            scriptType = hostManager->GetCoreType(m_config.m_scriptClassName);
        }

        if (!scriptType)
        {
            AZLOG_ERROR("CSharpScriptComponent: Script class not found: '%s'",
                m_config.m_scriptClassName.c_str());
            return false;
        }

        m_scriptType = scriptType;

        // Create an instance of the script class
        m_scriptInstance = hostManager->CreateInstance(*m_scriptType);

        if (!m_scriptInstance.IsValid())
        {
            AZLOG_ERROR("CSharpScriptComponent: Failed to create instance of script class: '%s'",
                m_config.m_scriptClassName.c_str());
            m_scriptType = nullptr;
            return false;
        }

        // Resolve the pinned thunk once per instance. Failure is expected and
        // survivable: m_bridgeInvoke stays null and every call site falls back
        // to SafeInvokeMethod.
        CoralNativeThunkHost& thunkHost = GetThunkHost();
        thunkHost.SetHost(hostManager->GetHostInstance(), hostManager->GetScriptsDirectory());
        m_bridgeInvoke = reinterpret_cast<BridgeInvokeFn>(
            thunkHost.Get("O3DE.Core.dll", "O3DE.Interop.ScriptComponentBridge, O3DE.Core", "Invoke"));

        // One-shot, so InvokeMethod's cost is irrelevant here. Uses the same
        // value-returning pattern already proven in the codebase (see
        // O3DESharpSystemComponent.cpp: InvokeMethod<Coral::String>(...)).
        m_bridgeHandle = m_scriptInstance.InvokeMethod<int>("AcquireBridgeHandle");

        m_scriptInitialized = true;

        AZLOG_INFO("CSharpScriptComponent: Successfully created script instance: '%s'",
            m_config.m_scriptClassName.c_str());

        return true;
    }

    void CSharpScriptComponent::DestroyScriptInstance()
    {
        if (m_scriptInstance.IsValid())
        {
            // Release the native bridge handle before destroying the managed
            // instance. Every caller of DestroyScriptInstance (Deactivate,
            // OnBeforeUserAssemblyReload, ReloadScript, the destructor)
            // dispatches OnDestroy first if it dispatches it at all, so the
            // handle is guaranteed to still be live for that dispatch - this
            // must run strictly after, never before.
            if (m_bridgeHandle != 0)
            {
                try
                {
                    m_scriptInstance.InvokeMethod("ReleaseBridgeHandle");
                }
                catch (...)
                {
                    // Teardown must proceed regardless - the handle table
                    // entry may leak in this case, but the alternative is an
                    // exception escaping Deactivate/reload teardown.
                    AZLOG_WARN("CSharpScriptComponent: ReleaseBridgeHandle threw during teardown of '%s'",
                        m_config.m_scriptClassName.c_str());
                }
            }

            m_scriptInstance.Destroy();
        }

        m_bridgeHandle = 0;
        m_bridgeInvoke = nullptr;
        m_scriptType = nullptr;
        m_scriptInitialized = false;
    }

    void CSharpScriptComponent::SetEntityIdOnScript()
    {
        if (!m_scriptInstance.IsValid())
        {
            return;
        }

        // Set the EntityId field on the script base class
        // The C# ScriptComponent base class has an EntityId property
        AZ::u64 entityId = static_cast<AZ::u64>(GetEntityId());

        try
        {
            // The C# ScriptComponent class has a field "m_entityId" that stores the native entity ID
            m_scriptInstance.SetFieldValue("m_entityId", entityId);
        }
        catch (...)
        {
            AZLOG_WARN("CSharpScriptComponent: Could not set entity ID on script. "
                "Make sure the script inherits from O3DE.ScriptComponent");
        }
    }

} // namespace O3DESharp
