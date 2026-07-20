/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

#pragma once

#include <AzCore/base.h>
#include <AzCore/Memory/SystemAllocator.h>
#include <AzCore/RTTI/RTTI.h>
#include <AzCore/RTTI/BehaviorContext.h>
#include <AzCore/std/string/string.h>
#include <AzCore/std/containers/vector.h>
#include <AzCore/IO/Path/Path.h>

#include <Scripting/Reflection/BehaviorContextReflector.h>

namespace O3DESharp
{
    /**
     * NativeBindingManifest - build-time snapshot of "what BehaviorContext
     * exposes AND whether/how it can be called as a direct native C++
     * trampoline instead of through BehaviorMethod::Call's
     * virtual -> AZStd::function -> per-arg RTTI ConvertTo path".
     *
     * This is 1B §6.1's "manifest schema (native_symbol, per-arg storage
     * class, bindable flag/reason, size/align)". It is deliberately a
     * SEPARATE, additive layer over BehaviorContextReflector/
     * ReflectedMethod rather than a modification of those structures:
     * BehaviorContextReflector's job is "what got reflected", this
     * layer's job is "of that, what can we bind natively, and to what
     * symbol". Two concerns, two types - keeps the existing consumers of
     * BehaviorContextReflector (GenericDispatcher, ReflectionDataExporter's
     * existing JSON) untouched.
     *
     * IMPORTANT (verified, see the 1B spec's corrected reality ledger):
     * AZ::BehaviorContext / BehaviorMethodImpl stores NO native C++ symbol.
     * The functor that backs a reflected method captures a raw function
     * pointer inside a type-erased AZStd::function; nothing in the runtime
     * BehaviorContext object exposes that pointer or the qualified C++ name
     * that produced it - only the reflected (script) name survives. So the
     * "native_symbol" field below can NEVER be populated by walking a live
     * BehaviorContext. It is populated by a *second, independent* pass: the
     * libclang reflection-call-site parser (see
     * Code/Tools/BindingGenerator/.../Parsing/ReflectionCallSiteParser.cs)
     * that reads each gem's reflection .cpp and extracts the `&C::Method`
     * expression passed to `->Method("name", &C::Method)`. The manifest
     * "join" between the two passes is done by the offline C# generator
     * (Generation/NativeBindingGenerator.cs), keyed on (className,
     * reflected script-name) - see that file's header comment for why a
     * decl-name join does not work in general.
     *
     * This C++ side owns HALF the manifest: the runtime-observable half
     * (owning class type_id/size/align, reflected name, per-arg storage
     * class inferred from AZ::BehaviorParameter traits, return type,
     * arity). It does not and cannot fill in native_symbol - that column
     * starts empty here and is populated by the offline C# join, which
     * re-emits the manifest JSON with native_symbol filled in for the
     * subset it could recover. See NativeBindingManifestExporter::Export.
     */

    /// Coarse per-argument storage classification used by the trampoline
    /// emitter to decide how to unpack a BehaviorArgument's raw m_value
    /// pointer. This is finer than ReflectedParameter's isPointer/
    /// isReference/isConst booleans because the emitter needs a single
    /// enum to switch on rather than re-deriving the combination every
    /// time (and because "value taken by copy" vs "value taken by const
    /// ref" are DIFFERENT trampoline shapes even though BehaviorParameter
    /// often represents both as "not a pointer").
    enum class ArgStorageClass : AZ::u8
    {
        Value,          // T arg            - copy-construct from *static_cast<T*>(m_value)
        Pointer,        // T* arg           - m_value reinterpreted directly as T*
        Reference,      // T& arg           - *static_cast<T*>(m_value)
        ConstReference, // const T& arg     - *static_cast<const T*>(m_value)
        Unknown         // couldn't classify - forces bindable=false
    };

    AZStd::string_view ToString(ArgStorageClass storageClass);

    /// Why a method/property could NOT be selected for native binding.
    /// Deliberately conservative (1B §6.1 "never bind overloaded/OnDemand/
    /// lambda/EBus-by-id in v1") - each reason maps 1:1 to a check the
    /// classifier performs; see NativeBindingClassifier::Classify.
    enum class NonBindableReason : AZ::u8
    {
        None,                    // bindable == true; reason unused
        Overloaded,               // method participates in BehaviorMethod::m_overload
        ReflectedViaLambda,        // reflection call-site passed a lambda/wrapper, not &C::Method
        OnDemandTemplateType,      // owning class (or an arg type) is an OnDemand-reflected template
        EBusAddressedById,         // EBus event addressed (non-broadcast) - dispatch is by-id, not a plain call
        UnresolvedNativeSymbol,    // clang call-site pass found no matching &C::Method for this reflected name
        UnsupportedArgStorage,     // one or more arguments/return classified as ArgStorageClass::Unknown
        NoNativeSideCounterpart,   // method exists in BehaviorContext but its owning class wasn't parsed by clang
    };

    AZStd::string_view ToString(NonBindableReason reason);

    /// Per-argument manifest entry: type + storage class + naming, enough
    /// for the trampoline emitter to generate a parameter unpack
    /// expression without re-deriving anything from AZ::BehaviorParameter.
    struct ManifestArgument
    {
        AZStd::string name;               // parameter name (synthesized "argN" if BehaviorContext has none)
        AZStd::string cppTypeName;         // best-effort C++ spelling (e.g. "AZ::Vector3", "float"); Unknown if unresolved
        AZ::Uuid typeId = AZ::Uuid::CreateNull();
        ArgStorageClass storageClass = ArgStorageClass::Unknown;
        size_t sizeBytes = 0;              // 0 if unknown (not yet joined against a clang-derived size)
        size_t alignBytes = 0;
    };

    /// One reflected method, manifest-shaped: everything the trampoline
    /// emitter and the BindingRegistry need to either emit a real native
    /// call or fall back to BehaviorMethod::Call, plus WHY it can't be
    /// bound when it can't.
    struct ManifestMethodEntry
    {
        // ---- identity (matches BehaviorContextReflector::ReflectedMethod) ----
        AZStd::string reflectedName;       // the script-visible name passed to ->Method(...)
        AZStd::string owningClassName;     // empty for global methods
        AZ::Uuid owningClassTypeId = AZ::Uuid::CreateNull();
        size_t owningClassSizeBytes = 0;   // 0 if unknown / global method
        size_t owningClassAlignBytes = 0;
        bool isStatic = false;
        bool isConst = false;

        // ---- native-symbol recovery (filled by the OFFLINE clang join; always
        //      empty coming out of ExportFromContext - see class comment) ----
        AZStd::string nativeQualifiedSymbol; // e.g. "AZ::Vector3::GetLength" - empty until joined

        // ---- signature, manifest-shaped ----
        ManifestArgument returnValue;                 // storageClass meaningless for Value(void); check cppTypeName=="void"
        AZStd::vector<ManifestArgument> arguments;     // 'this' NOT included - trampoline emitter adds it separately for members

        // ---- bindability classification (see NativeBindingClassifier) ----
        bool bindable = false;
        NonBindableReason nonBindableReason = NonBindableReason::NoNativeSideCounterpart;

        // A stable string binding-id the BindingRegistry keys on, independent
        // of pointer identity or reflection order (which are both dump-order
        // dependent and would break byte-for-byte-reproducible codegen).
        // Shape: "<ClassName>::<ReflectedName>" or "::<ReflectedName>" for
        // globals. Collisions (overloaded reflected names) are exactly the
        // case the classifier marks NonBindableReason::Overloaded for, so
        // the id stays unique for anything actually bindable.
        AZStd::string BindingId() const;
    };

    /// Top-level manifest: every class + global method the BehaviorContext
    /// reflector saw, re-shaped for native-binding purposes. Mirrors
    /// ReflectionDataExporter's JSON top-level shape (classes / ebuses /
    /// global_methods) so the two exports can be correlated by a human
    /// reading both files, but this is a DIFFERENT schema (see class doc
    /// on NativeBindingManifest) - not a superset/subset of the existing
    /// reflection_data.json.
    struct NativeBindingManifest
    {
        AZStd::vector<ManifestMethodEntry> methods; // both member and global methods; owningClassName distinguishes

        // EBus events are intentionally NOT included as bindable-candidates
        // in v1: the 1B determinism rule (spec §7, "library dispatch")
        // requires sim-scoped calls to be direct methods, never EBus
        // messaging, and EBus dispatch's NullMutex + pointer-ordered
        // handler iteration make bind-vs-dynamic differential testing
        // unsound for it anyway (D5/§8 R... equivalence-enforcement note).
        // A future phase MAY add EBus broadcast-only (non-addressed)
        // events once that's needed; addressed (by-id) events are excluded
        // permanently per NonBindableReason::EBusAddressedById.

        size_t BindableCount() const;
        size_t TotalCount() const { return methods.size(); }
    };

    /**
     * NativeBindingManifestExporter - builds the runtime-observable half
     * of the manifest from a live BehaviorContext (via
     * BehaviorContextReflector) and serializes it to JSON for the offline
     * C# join (ReflectionCallSiteParser + NativeBindingGenerator) to
     * consume and complete.
     *
     * Two-pass split (this is the answer to spec §3 point 2's "the
     * manifest join isn't constructible" critique): this C++ pass runs
     * IN-PROCESS against the live, fully-reflected BehaviorContext (so it
     * has real type_id/size/align/traits) but has NO access to C++ source
     * text, so it cannot recover native_symbol. The C# pass
     * (NativeBindingGenerator) runs OUT-OF-PROCESS against gem source via
     * libclang (so it CAN read `&C::Method` call-site expressions) but has
     * no live BehaviorContext to query traits from. Joining happens after
     * both passes complete, keyed on (owningClassName, reflectedName) -
     * see NativeBindingGenerator.cs for the join + its documented
     * limitations (name divergence when a gem reflects under a script
     * name that isn't found verbatim at any call site is the expected,
     * conservative failure mode: falls back to bindable=false).
     */
    class NativeBindingManifestExporter
    {
    public:
        AZ_RTTI(NativeBindingManifestExporter, "{E5F6A7B8-C9D0-1234-DEF0-56789ABCDEF0}");
        AZ_CLASS_ALLOCATOR(NativeBindingManifestExporter, AZ::SystemAllocator);

        NativeBindingManifestExporter() = default;
        ~NativeBindingManifestExporter() = default;

        /// Build the manifest from an already-populated BehaviorContextReflector.
        /// Every entry starts with bindable=false / nativeQualifiedSymbol
        /// empty / nonBindableReason=NoNativeSideCounterpart; this pass does
        /// NOT run the classifier (that requires the clang-side join data,
        /// which only exists offline) - it only shapes the data.
        NativeBindingManifest BuildManifest(const BehaviorContextReflector& reflector) const;

        /// Convenience: reflect directly from a BehaviorContext and build.
        NativeBindingManifest BuildManifestFromContext(AZ::BehaviorContext* context) const;

        /// Serialize a manifest to JSON (schema documented at the top of
        /// NativeBindingManifest.cpp). Mirrors ReflectionDataExporter's
        /// JSON writer conventions (2-space indent by default, same
        /// escaping helper shape) but is a distinct schema/writer since
        /// the two manifests answer different questions.
        AZStd::string ExportToString(const NativeBindingManifest& manifest, bool prettyPrint = true) const;

        /// Serialize + write to disk. Returns false (and logs) on I/O failure.
        bool ExportToFile(const NativeBindingManifest& manifest, const AZ::IO::Path& outputPath) const;

    private:
        ManifestArgument BuildManifestArgument(const ReflectedParameter& param) const;
        ManifestMethodEntry BuildManifestMethod(
            const ReflectedMethod& method,
            const ReflectedClass* owningClass) const;

        AZStd::string GenerateMethodJson(const ManifestMethodEntry& method, int indentSize, int indentLevel) const;
        AZStd::string GenerateArgumentJson(const ManifestArgument& arg, int indentSize, int indentLevel) const;
        AZStd::string Indent(int level, int indentSize) const;
        AZStd::string EscapeJsonString(const AZStd::string& input) const;
    };

} // namespace O3DESharp
