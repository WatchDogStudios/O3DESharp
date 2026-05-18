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
    // POCO classes that mirror the JSON schema produced by O3DESharp's
    // C++ ReflectionDataExporter. The shapes here are derived from a
    // live dump of reflection_data.json - any change to the C++ side
    // emitter needs a matching change here.
    //
    // Used by ReflectionBindingGenerator. We use System.Text.Json with
    // snake_case → PascalCase property name mapping driven by explicit
    // [JsonPropertyName] attributes, because the C++ side emits
    // snake_case (matches the rest of O3DE's JSON conventions) and we
    // want idiomatic C# property names on the consuming side.

    public sealed class ReflectionTypeInfo
    {
        [JsonPropertyName("name")]              public string Name { get; set; } = string.Empty;
        [JsonPropertyName("type_name")]         public string TypeName { get; set; } = string.Empty;
        [JsonPropertyName("type_id")]           public string TypeId { get; set; } = string.Empty;
        [JsonPropertyName("is_pointer")]        public bool IsPointer { get; set; }
        [JsonPropertyName("is_reference")]      public bool IsReference { get; set; }
        [JsonPropertyName("is_const")]          public bool IsConst { get; set; }

        /// <summary>
        /// One of: Bool, Int8, Int16, Int32, Int64, UInt8, UInt16,
        /// UInt32, UInt64, Float, Double, String, EntityId, Vector2,
        /// Vector3, Vector4, Quaternion, Color, Transform, Aabb,
        /// Object, Void, Unknown. Drives both the C# parameter type
        /// the wrapper takes AND the runtime marshaling path used
        /// by NativeReflection.InvokeMethod / BroadcastEBusEvent.
        /// </summary>
        [JsonPropertyName("marshal_type")]      public string MarshalType { get; set; } = "Unknown";
    }

    public sealed class ReflectionParameter
    {
        [JsonPropertyName("name")]              public string Name { get; set; } = string.Empty;
        [JsonPropertyName("type_name")]         public string TypeName { get; set; } = string.Empty;
        [JsonPropertyName("type_id")]           public string TypeId { get; set; } = string.Empty;
        [JsonPropertyName("is_pointer")]        public bool IsPointer { get; set; }
        [JsonPropertyName("is_reference")]      public bool IsReference { get; set; }
        [JsonPropertyName("is_const")]          public bool IsConst { get; set; }
        [JsonPropertyName("marshal_type")]      public string MarshalType { get; set; } = "Unknown";
    }

    public sealed class ReflectionMethod
    {
        [JsonPropertyName("name")]                  public string Name { get; set; } = string.Empty;
        [JsonPropertyName("class_name")]            public string ClassName { get; set; } = string.Empty;
        [JsonPropertyName("is_static")]             public bool IsStatic { get; set; }
        [JsonPropertyName("is_const")]              public bool IsConst { get; set; }
        [JsonPropertyName("description")]           public string Description { get; set; } = string.Empty;
        [JsonPropertyName("category")]              public string Category { get; set; } = string.Empty;
        [JsonPropertyName("is_deprecated")]         public bool IsDeprecated { get; set; }
        [JsonPropertyName("deprecation_message")]   public string DeprecationMessage { get; set; } = string.Empty;
        [JsonPropertyName("return_type")]           public ReflectionTypeInfo ReturnType { get; set; } = new();
        [JsonPropertyName("parameters")]            public List<ReflectionParameter> Parameters { get; set; } = new();
    }

    public sealed class ReflectionProperty
    {
        [JsonPropertyName("name")]              public string Name { get; set; } = string.Empty;
        [JsonPropertyName("class_name")]        public string ClassName { get; set; } = string.Empty;
        [JsonPropertyName("is_readonly")]       public bool IsReadOnly { get; set; }
        [JsonPropertyName("description")]       public string Description { get; set; } = string.Empty;
        [JsonPropertyName("type")]              public ReflectionTypeInfo Type { get; set; } = new();
    }

    public sealed class ReflectionConstructor
    {
        [JsonPropertyName("class_name")]    public string ClassName { get; set; } = string.Empty;
        [JsonPropertyName("parameters")]    public List<ReflectionParameter> Parameters { get; set; } = new();
    }

    public sealed class ReflectionClass
    {
        [JsonPropertyName("name")]              public string Name { get; set; } = string.Empty;
        [JsonPropertyName("type_id")]           public string TypeId { get; set; } = string.Empty;
        [JsonPropertyName("description")]       public string Description { get; set; } = string.Empty;
        [JsonPropertyName("category")]          public string Category { get; set; } = string.Empty;
        [JsonPropertyName("is_deprecated")]     public bool IsDeprecated { get; set; }
        [JsonPropertyName("source_gem_name")]   public string SourceGemName { get; set; } = string.Empty;
        [JsonPropertyName("base_classes")]      public List<string> BaseClasses { get; set; } = new();
        [JsonPropertyName("constructors")]      public List<ReflectionConstructor> Constructors { get; set; } = new();
        [JsonPropertyName("methods")]           public List<ReflectionMethod> Methods { get; set; } = new();
        [JsonPropertyName("properties")]        public List<ReflectionProperty> Properties { get; set; } = new();
    }

    public sealed class ReflectionEvent
    {
        [JsonPropertyName("name")]          public string Name { get; set; } = string.Empty;
        [JsonPropertyName("bus_name")]      public string BusName { get; set; } = string.Empty;
        /// <summary>
        /// True for broadcast events (all handlers receive). False for
        /// addressed events (delivered to a specific handler by bus ID).
        /// </summary>
        [JsonPropertyName("is_broadcast")]  public bool IsBroadcast { get; set; }
        [JsonPropertyName("return_type")]   public ReflectionTypeInfo ReturnType { get; set; } = new();
        [JsonPropertyName("parameters")]    public List<ReflectionParameter> Parameters { get; set; } = new();
    }

    public sealed class ReflectionEBus
    {
        [JsonPropertyName("name")]              public string Name { get; set; } = string.Empty;
        [JsonPropertyName("description")]       public string Description { get; set; } = string.Empty;
        [JsonPropertyName("category")]          public string Category { get; set; } = string.Empty;
        [JsonPropertyName("source_gem_name")]   public string SourceGemName { get; set; } = string.Empty;
        /// <summary>
        /// Type of the bus's address handle (typically AZ::EntityId for
        /// component-bound buses, AZ::Crc32 for tag buses, empty for
        /// pure broadcast-only buses).
        /// </summary>
        [JsonPropertyName("address_type")]      public ReflectionTypeInfo AddressType { get; set; } = new();
        [JsonPropertyName("events")]            public List<ReflectionEvent> Events { get; set; } = new();
    }

    public sealed class ReflectionGlobalMethod
    {
        [JsonPropertyName("name")]                  public string Name { get; set; } = string.Empty;
        [JsonPropertyName("description")]           public string Description { get; set; } = string.Empty;
        [JsonPropertyName("category")]              public string Category { get; set; } = string.Empty;
        [JsonPropertyName("source_gem_name")]       public string SourceGemName { get; set; } = string.Empty;
        [JsonPropertyName("is_deprecated")]         public bool IsDeprecated { get; set; }
        [JsonPropertyName("deprecation_message")]   public string DeprecationMessage { get; set; } = string.Empty;
        [JsonPropertyName("return_type")]           public ReflectionTypeInfo ReturnType { get; set; } = new();
        [JsonPropertyName("parameters")]            public List<ReflectionParameter> Parameters { get; set; } = new();
    }

    public sealed class ReflectionGlobalProperty
    {
        [JsonPropertyName("name")]              public string Name { get; set; } = string.Empty;
        [JsonPropertyName("description")]       public string Description { get; set; } = string.Empty;
        [JsonPropertyName("category")]          public string Category { get; set; } = string.Empty;
        [JsonPropertyName("source_gem_name")]   public string SourceGemName { get; set; } = string.Empty;
        [JsonPropertyName("is_readonly")]       public bool IsReadOnly { get; set; }
        [JsonPropertyName("type")]              public ReflectionTypeInfo Type { get; set; } = new();
    }

    /// <summary>
    /// Top-level shape of reflection_data.json. Loaded once by the
    /// generator, then walked to emit one C# file per class / EBus,
    /// plus a Globals.g.cs per gem for global methods + properties.
    /// </summary>
    public sealed class ReflectionDocument
    {
        [JsonPropertyName("classes")]               public List<ReflectionClass> Classes { get; set; } = new();
        [JsonPropertyName("ebuses")]                public List<ReflectionEBus> EBuses { get; set; } = new();
        [JsonPropertyName("global_methods")]        public List<ReflectionGlobalMethod> GlobalMethods { get; set; } = new();
        [JsonPropertyName("global_properties")]     public List<ReflectionGlobalProperty> GlobalProperties { get; set; } = new();
    }
}
