/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <AzCore/std/string/string.h>
#include <AzCore/std/containers/unordered_map.h>
#include <AzCore/std/smart_ptr/unique_ptr.h>

namespace O3DESharp
{
    /**
     * CoralNativeThunkHost - a native->managed PINNED-THUNK extension that
     * bypasses Coral::ManagedObject::InvokeMethod's string-lookup path.
     *
     * VERIFIED GAP (1B spec §3 point 3 / §6.3): Coral exposes exactly one
     * native->managed call path, ManagedObject::InvokeMethod<T>(string_view
     * methodName, ...), which on every call does a managed-side string
     * lookup + reflection dispatch (Coral.Managed's ManagedHost resolves
     * the method by name via .NET reflection, then invokes it). There is
     * NO Coral API that hands native code a raw function pointer into a
     * managed method the way AddInternalCall hands MANAGED code a raw
     * function pointer into NATIVE code. The C#->native direction
     * (AddInternalCall + `delegate* unmanaged<...>` fields, see
     * ScriptBindings.cpp / InternalCalls.cs) is a proven, zero-per-call-
     * overhead pinned thunk; nothing analogous exists for native->C#.
     *
     * THE PRIMITIVE THIS BUILDS ON (public, not Coral-internal):
     * .NET's hosting API exposes exactly this capability already -
     * hostfxr_get_runtime_delegate(ctx, hdt_load_assembly_and_get_function_pointer, ...)
     * returns a `load_assembly_and_get_function_pointer_fn` that, given an
     * assembly-qualified type name + a static method name marked
     * `[UnmanagedCallersOnly]`, hands back a RAW native-callable function
     * pointer with zero managed-side lookup per call thereafter. Coral's
     * own HostInstance ALREADY uses exactly this delegate internally
     * (HostInstance::InitializeCoralManaged, see
     * s_CoreCLRFunctions.GetManagedFunctionPtr) to bootstrap
     * Coral.Managed's own entry points (LoadCoralManagedFunctionPtr) -
     * this class is doing nothing more than what Coral does for itself,
     * just for O3DE's own sim-lane assembly instead of Coral.Managed.
     *
     * WHY THIS COULD NOT BE ADDED INSIDE CORAL ITSELF (task constraint:
     * only files under Gems/O3DESharp/ may be touched; Coral lives in
     * build/windows/_deps/coral-src, a FetchContent'd external repo, not
     * this gem): HostInstance::LoadCoralManagedFunctionPtr - the exact
     * method that would give us this for free - is `private`, and
     * HostInstance exposes no public accessor for its `m_HostFXRContext`
     * handle or `s_CoreCLRFunctions.GetManagedFunctionPtr`. So this class
     * does NOT reach into Coral's internals; it independently re-resolves
     * hostfxr and re-runs `hostfxr_initialize_for_runtime_config` against
     * the SAME Coral.Managed.runtimeconfig.json Coral itself already
     * initialized against. Per the hostfxr contract (and per Coral's own
     * tolerance check - see HostInstance.cpp's
     * `CORAL_VERIFY(status == Success || status == Success_HostAlreadyInitialized
     * || status == Success_DifferentRuntimeProperties)`), a second
     * `hostfxr_initialize_for_runtime_config` call against an
     * already-running CLR in the SAME PROCESS returns
     * Success_HostAlreadyInitialized (or Success_DifferentRuntimeProperties
     * if some runtime property already diverges, which is also treated as
     * success) and hands back a context handle valid against the SAME
     * running CLR instance - not a second CLR. This is documented .NET
     * hosting behavior, not a Coral quirk, and it's exactly what lets this
     * class avoid needing anything Coral doesn't already expose publicly.
     *
     * USAGE:
     *   1. Call Initialize() once, AFTER CoralHostManager::Initialize has
     *      already brought up the CLR (order matters - see Initialize's
     *      doc comment for why).
     *   2. Author a managed static method decorated
     *      [UnmanagedCallersOnly] in the sim-lane assembly (see
     *      O3DE.Core's ISimSystem/SimSystemBase - PART 2 of this task) -
     *      e.g. `public static class SimBridge { [UnmanagedCallersOnly]
     *      public static void Step(IntPtr instanceHandle, uint
     *      frameIndex) { ... } }`.
     *   3. Call GetPinnedThunk("O3DE.Sim.SimBridge, O3DE.Core", "Step")
     *      to receive a `delegate* unmanaged<IntPtr, uint, void>` (the
     *      SAME shape as an internal-call field on the C#->native side -
     *      see ScriptBindings.h's InteropVector3-style convention) that
     *      can be called directly, with NO string lookup, NO managed
     *      reflection, and NO per-call marshaling overhead beyond the
     *      blittable arguments themselves.
     *
     * DETERMINISM NOTE: this class does not itself make anything
     * deterministic - it only removes the InvokeMethod string-lookup /
     * reflection-dispatch variable-cost path from the predicted sim
     * step's hot loop. Determinism of what the managed Step() method DOES
     * once called is the C# lane's responsibility (see PART 2's
     * SimSystemBase + the runtimeconfig tiering-lock companion piece).
     */
    class CoralNativeThunkHost
    {
    public:
        /// A resolved pinned thunk. Same shape as Coral's
        /// `component_entry_point_fn` (see NetCore/coreclr_delegates.h,
        /// a PUBLIC, non-Coral-specific .NET hosting header vendored
        /// alongside Coral) generalized to an opaque function pointer -
        /// callers cast to the delegate* unmanaged<...> shape matching
        /// the [UnmanagedCallersOnly] method's actual signature, exactly
        /// as InternalCalls.cs's generated fields do on the other side of
        /// the boundary.
        using PinnedThunk = void*;

        CoralNativeThunkHost();
        ~CoralNativeThunkHost();

        CoralNativeThunkHost(const CoralNativeThunkHost&) = delete;
        CoralNativeThunkHost& operator=(const CoralNativeThunkHost&) = delete;

        /// Initialize this thunk host against the SAME
        /// Coral.Managed.runtimeconfig.json Coral's own HostInstance was
        /// initialized against.
        ///
        /// MUST be called AFTER CoralHostManager::Initialize() has
        /// already brought up the CLR (i.e. after
        /// ICoralHostManager::IsInitialized() is true). Calling this
        /// FIRST would make THIS class's hostfxr_initialize_for_runtime_config
        /// call the FIRST one in the process, which changes the expected
        /// status code (would return plain Success instead of
        /// Success_HostAlreadyInitialized) and - more importantly - would
        /// race Coral's own subsequent init for who "owns" the runtime
        /// property values (TieredCompilation/TieredPGO determinism keys;
        /// see PART 2's runtimeconfig companion). Ordering is enforced by
        /// the caller (CoralHostManager wires this up post-Initialize);
        /// this method itself only asserts/logs if called too early
        /// (coralDirectory's runtimeconfig.json not found is the
        /// practical signal - same failure Coral's own Initialize
        /// surfaces for the identical reason).
        ///
        /// @param coralDirectory Same directory CoralHostConfig::coralDirectory
        ///        pointed at (contains Coral.Managed.runtimeconfig.json).
        /// @return true on success. False leaves this host permanently
        ///         disabled for the process (GetPinnedThunk always
        ///         returns nullptr) - callers MUST treat every consumer
        ///         of this host as optional/best-effort, mirroring 1B's
        ///         "dynamic fallback first-class" philosophy: a sim
        ///         system that can't get a pinned thunk falls back to
        ///         ManagedObject::InvokeMethod, slower but correct.
        bool Initialize(const AZStd::string& coralDirectory);

        void Shutdown();

        bool IsInitialized() const { return m_initialized; }

        /// Resolve a pinned thunk to a static, [UnmanagedCallersOnly]
        /// managed method. Results are cached (keyed on
        /// "assemblyQualifiedTypeName::methodName") so repeat calls after
        /// the first are a hash-map lookup, not a re-resolution through
        /// hostfxr.
        ///
        /// @param assemblyQualifiedTypeName e.g. "O3DE.Sim.SimBridge, O3DE.Core"
        /// @param methodName e.g. "Step" - must name a `static` method
        ///        marked `[UnmanagedCallersOnly]` in managed code; hostfxr
        ///        itself enforces this (passing UNMANAGEDCALLERSONLY_METHOD
        ///        as the delegate-type-name argument, same convention
        ///        Coral's own LoadCoralManagedFunctionPtr uses via its
        ///        CORAL_UNMANAGED_CALLERS_ONLY default parameter) and
        ///        fails the resolve if the target isn't decorated that way.
        /// @return non-null pinned function pointer on success; nullptr
        ///         if unresolved (type/method not found, wrong signature
        ///         decoration, or this host isn't initialized). Cache a
        ///         nullptr result is NOT retried automatically - call
        ///         InvalidateCache() after a hot-reload before re-resolving.
        PinnedThunk GetPinnedThunk(const AZStd::string& assemblyQualifiedTypeName, const AZStd::string& methodName);

        /// Drop all cached thunks. Call after a user-assembly hot-reload
        /// (O3DESharpHotReloadNotificationBus::OnAfterUserAssemblyReload)
        /// since a reloaded assembly's methods live at new addresses -
        /// every previously-resolved PinnedThunk is dangling the instant
        /// the old AssemblyLoadContext unloads, exactly the same hazard
        /// CoralHostManager::ReloadUserAssemblies already documents for
        /// Coral::Type*/Coral::ManagedObject handles.
        void InvalidateCache();

    private:
        bool m_initialized = false;

        // Opaque native handles, typed as void* here so this header does
        // not need to #include NetCore/hostfxr.h (a header that lives
        // under Coral's vendored source tree, not a stable O3DESharp
        // public include path - keeping it out of this header means
        // consumers of CoralNativeThunkHost.h don't need Coral's include
        // dirs wired in just to see this type declared). The .cpp
        // includes hostfxr.h/coreclr_delegates.h directly (both are
        // vendored alongside Coral.Native, already on this gem's include
        // path transitively via the Coral.Native CMake target - see
        // CoralNativeThunkHost.cpp's include comment for the exact path).
        void* m_hostfxrLibrary = nullptr;
        void* m_hostfxrContext = nullptr;

        // hostfxr_get_runtime_delegate(hdt_load_assembly_and_get_function_pointer)
        // result - the SAME kind of delegate Coral's own
        // s_CoreCLRFunctions.GetManagedFunctionPtr is, resolved
        // independently by this class (see class doc for why it can't
        // reuse Coral's).
        void* m_loadAssemblyAndGetFunctionPointerFn = nullptr;

        AZStd::unordered_map<AZStd::string, PinnedThunk> m_thunkCache;
    };

} // namespace O3DESharp
