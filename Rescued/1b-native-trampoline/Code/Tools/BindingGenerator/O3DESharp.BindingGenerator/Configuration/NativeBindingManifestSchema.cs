/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace O3DESharp.BindingGenerator.Configuration
{
    // POCO mirror of the JSON schema emitted by O3DESharp's C++
    // NativeBindingManifestExporter (Code/Source/Scripting/Reflection/
    // NativeBindingManifest.cpp). This is a DIFFERENT schema from
    // ReflectionDataSchema.cs's ReflectionDocument - that one mirrors
    // ReflectionDataExporter's reflection_data.json (the general "what
    // got reflected" dump used by the C#-wrapper generator);  this one
    // mirrors the native-binding manifest (the 1B-specific "what can be
    // called as a direct native trampoline" dump). Any change to the C++
    // NativeBindingManifestExporter needs a matching change here.

    public sealed class NativeBindingManifestArgument
    {
        [JsonPropertyName("name")]          public string Name { get; set; } = string.Empty;
        [JsonPropertyName("cpp_type_name")] public string CppTypeName { get; set; } = string.Empty;
        [JsonPropertyName("type_id")]       public string TypeId { get; set; } = string.Empty;

        /// <summary>One of: Value, Pointer, Reference, ConstReference, Unknown.</summary>
        [JsonPropertyName("storage_class")] public string StorageClass { get; set; } = "Unknown";
        [JsonPropertyName("size_bytes")]    public long SizeBytes { get; set; }
        [JsonPropertyName("align_bytes")]   public long AlignBytes { get; set; }
    }

    public sealed class NativeBindingManifestMethod
    {
        [JsonPropertyName("reflected_name")]           public string ReflectedName { get; set; } = string.Empty;
        [JsonPropertyName("owning_class_name")]         public string OwningClassName { get; set; } = string.Empty;
        [JsonPropertyName("owning_class_type_id")]      public string OwningClassTypeId { get; set; } = string.Empty;
        [JsonPropertyName("owning_class_size_bytes")]   public long OwningClassSizeBytes { get; set; }
        [JsonPropertyName("owning_class_align_bytes")]  public long OwningClassAlignBytes { get; set; }
        [JsonPropertyName("is_static")]                 public bool IsStatic { get; set; }
        [JsonPropertyName("is_const")]                   public bool IsConst { get; set; }

        /// <summary>
        /// Always empty as emitted by the C++ exporter (see
        /// NativeBindingManifest.h's class doc - the runtime
        /// BehaviorContext pass cannot recover this). Populated by
        /// NativeBindingGenerator's join against ReflectionCallSiteParser
        /// output before this record is re-serialized / used to emit a
        /// trampoline.
        /// </summary>
        [JsonPropertyName("native_qualified_symbol")]   public string NativeQualifiedSymbol { get; set; } = string.Empty;

        [JsonPropertyName("return")]     public NativeBindingManifestArgument Return { get; set; } = new();
        [JsonPropertyName("arguments")]  public List<NativeBindingManifestArgument> Arguments { get; set; } = new();

        /// <summary>
        /// Always false as emitted by the C++ exporter. Set by
        /// NativeBindingGenerator's BindingClassifier after the join.
        /// </summary>
        [JsonPropertyName("bindable")]            public bool Bindable { get; set; }

        /// <summary>
        /// One of the NonBindableReason enumerators (see
        /// NativeBindingManifest.h): None, Overloaded, ReflectedViaLambda,
        /// OnDemandTemplateType, EBusAddressedById, UnresolvedNativeSymbol,
        /// UnsupportedArgStorage, NoNativeSideCounterpart.
        /// </summary>
        [JsonPropertyName("non_bindable_reason")]  public string NonBindableReason { get; set; } = "NoNativeSideCounterpart";

        [JsonPropertyName("binding_id")]           public string BindingId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Top-level shape of the native-binding manifest JSON dumped by
    /// NativeBindingManifestExporter::ExportToString. Loaded once by
    /// NativeBindingGenerator, joined against ReflectionCallSiteParser
    /// output, classified, and used to drive trampoline emission.
    /// </summary>
    public sealed class NativeBindingManifestDocument
    {
        [JsonPropertyName("methods")] public List<NativeBindingManifestMethod> Methods { get; set; } = new();
    }
}
