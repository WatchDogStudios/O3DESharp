/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <AzCore/Math/Uuid.h>
#include <AzCore/RTTI/BehaviorContext.h>
#include <AzCore/std/string/string.h>
#include <AzCore/std/containers/span.h>
#include <AzCore/std/containers/unordered_map.h>

namespace O3DESharp
{
    /**
     * BindingRegistry - runtime lookup table from a stable binding-id
     * string (NativeBindingManifest's ManifestMethodEntry::BindingId(),
     * e.g. "Vector3::GetLength") to a generated native trampoline
     * function pointer.
     *
     * This is the runtime half of 1B's "native-symbol binding generator"
     * (spec §6.1 / PART 1 of this task). The offline pipeline is:
     *
     *   1. NativeBindingManifestExporter dumps the runtime-observable
     *      manifest (this gem, C++, in-process).
     *   2. ReflectionCallSiteParser.cs (libclang, offline, out-of-process)
     *      recovers native_symbol for each reflected method by reading
     *      the gem's reflection .cpp Method(...)/Property(...)/Event(...)
     *      call-site expressions.
     *   3. NativeBindingGenerator.cs joins the two and, for every entry
     *      classified bindable=true, emits a REAL native-call trampoline
     *      function (shape: reinterpret_cast<const C*>(self)->Method(...)
     *      then result->StoreResult(ret) for member calls; see that
     *      file's header comment for the exact emission templates) into
     *      a generated .cpp per gem.
     *   4. Each generated trampoline TU self-registers into this registry
     *      at static-init time via BindingRegistry::Register(bindingId, fn),
     *      mirroring the self-registration pattern already used by
     *      ExecutionStateNative/NativeGraphRegistry in the ScriptCanvas
     *      half of 1B (GUID-keyed factory registered at static-init, no
     *      build dependency from the registry back to generated code).
     *
     * Lookup returns nullptr for anything not registered (unbound
     * methods, or gems whose generated TU hasn't linked into this
     * binary) - callers MUST treat that as "use BehaviorMethod::Call"
     * rather than a fatal error. This is what the spec calls "dynamic
     * fallback first-class" (D1/§2 point 6): correctness is identical
     * either way, native is purely an accelerator.
     *
     * Deliberately NOT thread-safe for Register() (static-init / module-
     * load time only, same assumption BehaviorContext reflection itself
     * makes); Lookup() is safe for concurrent readers once registration
     * has quiesced, matching BehaviorContext's own "reflect at load,
     * read for the rest of the process" lifecycle.
     */
    class BindingRegistry
    {
    public:
        /// A native trampoline's signature, uniform across every bound
        /// method regardless of its real C++ signature. This is EXACTLY
        /// the shape verified by the 1B P0 probe (commit 9a2c1b1018,
        /// fixed up in c7f4cd7719): Gems/ScriptCanvasTesting/Code/Tests/
        /// Native/BehaviorNativeBindingTests.cpp's
        /// Trampoline_Vector3_GetLength, proven byte-identical to
        /// AZ::BehaviorMethod::Call for AZ::Vector3::GetLength() ==
        /// 13.0f. Do not change this signature without re-verifying
        /// against that test.
        ///
        ///   self    - null for static/global methods. For member calls,
        ///             the raw object address (e.g. `&v`); the generated
        ///             trampoline body does
        ///             `reinterpret_cast<const C*>(self)->Method(...)`
        ///             directly - self is NOT a BehaviorArgument or
        ///             BehaviorObject inside the trampoline, it's the
        ///             bare pointer. (The BehaviorObject wrapping only
        ///             happens on the DYNAMIC fallback path below, which
        ///             is BehaviorMethod::Call's own requirement, not the
        ///             trampoline's.)
        ///   args    - already-marshaled BehaviorArguments for the
        ///             method's real parameters, EXCLUDING 'this' (self
        ///             carries that separately) - mirrors the P0 probe's
        ///             `AZStd::span<AZ::BehaviorArgument>` args param.
        ///   result  - optional; nullptr for void-returning methods.
        ///             Trampoline calls result->StoreResult(returnValue)
        ///             (plain StoreResult(x), NOT StoreResult<T>(x) - the
        ///             explicit template arg makes T a value type that a
        ///             const-lvalue can't bind to; see c7f4cd7719).
        using TrampolineFn = void(*)(
            const void* self,
            AZStd::span<AZ::BehaviorArgument> args,
            AZ::BehaviorArgument* result);

        /// Register a trampoline under a stable binding-id. Called from
        /// generated TUs' static-init (see class doc). Last registration
        /// for a given id wins - intentional, so a hot-reloaded/regenerated
        /// gem module can supersede a stale registration without requiring
        /// an explicit Unregister first (mirrors ScriptEvents' runtime
        /// m_ebuses/m_methods mutation convention noted in the 1B spec's
        /// produce-half section, which is also last-write-wins).
        static void Register(AZStd::string_view bindingId, TrampolineFn fn);

        /// Remove a registration (module unload / hot-reload teardown).
        /// No-op if bindingId was never registered.
        static void Unregister(AZStd::string_view bindingId);

        /// Look up a trampoline by binding-id. Returns nullptr if no
        /// native trampoline is registered for this id - callers MUST
        /// fall back to AZ::BehaviorMethod::Call in that case (this is
        /// the "dynamic fallback first-class" contract; see class doc).
        static TrampolineFn Lookup(AZStd::string_view bindingId);

        /// True if a trampoline is registered for this id. Equivalent to
        /// `Lookup(id) != nullptr`; provided for call sites that want to
        /// branch without holding onto the function pointer.
        static bool IsBound(AZStd::string_view bindingId);

        /// Number of currently-registered trampolines. Diagnostic /
        /// test-assertion use (e.g. "P0 registered exactly one binding").
        static size_t Count();

        /// Invoke a registered trampoline if bound, otherwise invoke
        /// AZ::BehaviorMethod::Call as the dynamic fallback. This is the
        /// single call site every consumer (GenericDispatcher today;
        /// compiled ScriptCanvas native trampolines and the C# lane in
        /// future phases) should route through, so the bound-vs-dynamic
        /// choice is made in exactly one place and is trivially
        /// differential-testable (spec §7 "equivalence enforcement" -
        /// call both and assert equal result, for side-effect-free
        /// non-random leaves only).
        ///
        /// 'method' supplies the dynamic-fallback path. 'selfTypeId' is
        /// the owning class's AZ::Uuid, needed to construct the
        /// AZ::BehaviorObject the dynamic fallback requires for member
        /// calls (Uuid::CreateNull() for static/global methods, where
        /// 'self' must also be nullptr).
        ///
        /// A mismatch between 'args' and method->GetNumArguments() means
        /// the manifest and the live BehaviorContext have drifted (e.g.
        /// stale generated code against a newer gem) - exactly the
        /// "runtime downgrade-to-interpreted on mismatch" staleness case
        /// called out in spec §6 phase 2 (not yet built - this call site
        /// is where that check will eventually plug in; for now a debug
        /// AZ_Assert catches it rather than a silent downgrade).
        static void Invoke(
            AZStd::string_view bindingId,
            AZ::BehaviorMethod* method,
            const void* self,
            const AZ::Uuid& selfTypeId,
            AZStd::span<AZ::BehaviorArgument> args,
            AZ::BehaviorArgument* result);

    private:
        // Function-local static map (construct-on-first-use) rather than a
        // namespace-scope global, to sidestep static-init-order-fiasco
        // between this registry and the generated TUs that call Register()
        // from their own static initializers - the map is guaranteed
        // constructed before the first Register() call reaches it,
        // regardless of TU link order. Mirrors the GUID-keyed
        // NativeGraphRegistry pattern already established for the
        // ScriptCanvas half of 1B (see the P1 scaffolding memory note).
        static AZStd::unordered_map<AZStd::string, TrampolineFn>& GetMap();
    };

} // namespace O3DESharp
