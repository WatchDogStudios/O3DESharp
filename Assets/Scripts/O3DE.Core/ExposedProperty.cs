/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace O3DE
{
    /// <summary>
    /// Marks a public field or public auto-property on a <see cref="ScriptComponent"/>
    /// subclass as editable in the O3DE editor's inspector.
    ///
    /// First-slice scope (Phase 7):
    /// values round-trip as strings between the editor's <c>CSharpScriptComponentConfig</c>
    /// map and the managed instance. This means the inspector currently shows
    /// a generic key/value map editor rather than per-type widgets, but
    /// behavior is correct end-to-end: edits persist with the entity and are
    /// pushed back into the script on Activate. Typed editor widgets
    /// (sliders, color pickers, ...) are a planned follow-up.
    ///
    /// Supported value types in this slice: <c>bool</c>, <c>byte</c>, <c>sbyte</c>,
    /// <c>short</c>, <c>ushort</c>, <c>int</c>, <c>uint</c>, <c>long</c>,
    /// <c>ulong</c>, <c>float</c>, <c>double</c>, <c>string</c>. Vector3 /
    /// Quaternion / enum support arrives with the typed-editor follow-up.
    ///
    /// Example:
    /// <code>
    /// public class PlayerController : ScriptComponent
    /// {
    ///     [ExposedProperty]
    ///     public float Speed = 10.0f;
    ///
    ///     [ExposedProperty("Max Health")]
    ///     public int MaxHealth = 100;
    /// }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class ExposedPropertyAttribute : Attribute
    {
        /// <summary>
        /// Optional human-readable label shown in the inspector. If null or
        /// empty the field/property name is used.
        /// </summary>
        public string? DisplayName { get; }

        /// <summary>
        /// Optional tooltip / longer description.
        /// </summary>
        public string? Tooltip { get; set; }

        public ExposedPropertyAttribute()
        {
            DisplayName = null;
        }

        public ExposedPropertyAttribute(string displayName)
        {
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Helpers that enumerate, serialize, and apply <see cref="ExposedPropertyAttribute"/>
    /// values on a <see cref="ScriptComponent"/> instance. Kept separate from
    /// <c>ScriptComponent</c> so the reflection helpers can be unit-tested
    /// against a plain object.
    /// </summary>
    public static class ExposedPropertyHelpers
    {
        /// <summary>
        /// Enumerate every <c>[ExposedProperty]</c>-decorated public field and
        /// public auto-property declared on <paramref name="instance"/>'s type
        /// (and its base types up to but not including <see cref="ScriptComponent"/>).
        /// </summary>
        public static IEnumerable<ExposedMember> Enumerate(object instance)
        {
            if (instance is null) yield break;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = instance.GetType();
            while (type != null && type != typeof(object))
            {
                foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    var attr = field.GetCustomAttribute<ExposedPropertyAttribute>(inherit: true);
                    if (attr != null && !field.IsStatic)
                    {
                        yield return new ExposedMember(field, attr);
                    }
                }
                foreach (var prop in type.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    var attr = prop.GetCustomAttribute<ExposedPropertyAttribute>(inherit: true);
                    if (attr != null && prop.CanRead && prop.CanWrite)
                    {
                        yield return new ExposedMember(prop, attr);
                    }
                }
                type = type.BaseType;
            }
        }

        /// <summary>
        /// Return a mapping of (member name) -> (current value serialized as string).
        /// Used by the editor to populate the inspector with the script's
        /// default values when the component is first added to an entity.
        /// </summary>
        public static Dictionary<string, string> SnapshotDefaults(object instance)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in Enumerate(instance))
            {
                var value = member.GetValue(instance);
                result[member.Name] = SerializeValue(value);
            }
            return result;
        }

        /// <summary>
        /// Apply the (name -> string-value) entries in <paramref name="values"/>
        /// to the matching exposed members of <paramref name="instance"/>.
        /// Unrecognized names are ignored (the script may have been recompiled);
        /// values that fail to parse are reported via <paramref name="errorLogger"/>
        /// (defaults to <see cref="Debug.LogError"/>) and the corresponding
        /// member keeps its previous value.
        ///
        /// Returns the number of members that failed to apply, so callers can
        /// detect partial failures without scraping log output.
        /// </summary>
        public static int Apply(object instance, IReadOnlyDictionary<string, string>? values, Action<string>? errorLogger = null)
        {
            if (instance is null || values is null || values.Count == 0)
            {
                return 0;
            }

            var log = errorLogger ?? Debug.LogError;
            int failureCount = 0;

            foreach (var member in Enumerate(instance))
            {
                if (!values.TryGetValue(member.Name, out var raw))
                {
                    continue;
                }

                try
                {
                    var parsed = ParseValue(raw, member.MemberType);
                    member.SetValue(instance, parsed);
                }
                catch (Exception ex)
                {
                    failureCount++;
                    log($"[ExposedProperty] Failed to apply '{member.Name}' = '{raw}' on {instance.GetType().FullName}: {ex.Message}");
                }
            }
            return failureCount;
        }

        /// <summary>
        /// Apply a JSON-encoded <c>{"name":"value", ...}</c> dictionary to
        /// <paramref name="instance"/>. The string-string shape mirrors what
        /// the C++ <c>CSharpScriptComponentConfig</c> stores so the C++ side
        /// can hand the map across the interop boundary with one
        /// <c>InvokeMethod</c> call.
        /// </summary>
        public static void ApplyFromJson(object instance, string? json, Action<string>? errorLogger = null)
        {
            if (instance is null || string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var log = errorLogger ?? Debug.LogError;
            try
            {
                var parsed = ParseSimpleStringMap(json);
                Apply(instance, parsed, log);
            }
            catch (Exception ex)
            {
                log($"[ExposedProperty] Failed to parse JSON value map: {ex.Message}");
            }
        }

        // -------------------------------------------------------------
        // Value (de)serialization. Kept narrow on purpose; per the attribute
        // doc-comment only primitives + string are supported in this slice.
        // -------------------------------------------------------------

        private static string SerializeValue(object? value)
        {
            if (value is null) return string.Empty;

            return value switch
            {
                bool b   => b ? "true" : "false",
                float f  => f.ToString("R", CultureInfo.InvariantCulture),
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                string s => s,
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
        }

        private static object? ParseValue(string raw, Type targetType)
        {
            if (targetType == typeof(string))           return raw;
            if (targetType == typeof(bool))             return bool.Parse(raw);
            if (targetType == typeof(byte))             return byte.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(sbyte))            return sbyte.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(short))            return short.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(ushort))           return ushort.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(int))              return int.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(uint))             return uint.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))             return long.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(ulong))            return ulong.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))            return float.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))           return double.Parse(raw, CultureInfo.InvariantCulture);

            // Convert.ChangeType handles any IConvertible we didn't enumerate
            // above (e.g. decimal). Will throw on enums / complex types; the
            // typed-editor follow-up adds enum + Vector3 + Quaternion support.
            return Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Minimal JSON-of-string-to-string parser. We deliberately avoid
        /// pulling in System.Text.Json here so this works in trimming /
        /// AOT-friendly contexts; the input shape is constrained to the
        /// flat dictionary the C++ side emits.
        /// </summary>
        internal static Dictionary<string, string> ParseSimpleStringMap(string json)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{')
            {
                throw new FormatException("Expected '{' at start of value map.");
            }
            i++;
            SkipWs(json, ref i);
            if (i < json.Length && json[i] == '}') return result;

            while (i < json.Length)
            {
                SkipWs(json, ref i);
                var key = ReadJsonString(json, ref i);
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != ':')
                {
                    throw new FormatException("Expected ':' after key.");
                }
                i++;
                SkipWs(json, ref i);
                var val = ReadJsonString(json, ref i);
                result[key] = val;
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                if (i < json.Length && json[i] == '}') { i++; break; }
                throw new FormatException("Expected ',' or '}'.");
            }

            return result;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static string ReadJsonString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"')
            {
                throw new FormatException("Expected '\"' starting a string.");
            }
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i < s.Length)
                {
                    var esc = s[i++];
                    sb.Append(esc switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => esc,
                    });
                    continue;
                }
                sb.Append(c);
            }
            throw new FormatException("Unterminated string.");
        }
    }

    /// <summary>
    /// One discovered exposed member - either a field or an auto-property.
    /// Abstracts over MemberInfo so callers don't have to special-case.
    /// </summary>
    public readonly struct ExposedMember
    {
        public string Name { get; }
        public string DisplayName { get; }
        public string? Tooltip { get; }
        public Type MemberType { get; }

        private readonly FieldInfo? _field;
        private readonly PropertyInfo? _property;

        internal ExposedMember(FieldInfo field, ExposedPropertyAttribute attr)
        {
            _field = field;
            _property = null;
            Name = field.Name;
            DisplayName = string.IsNullOrEmpty(attr.DisplayName) ? field.Name : attr.DisplayName!;
            Tooltip = attr.Tooltip;
            MemberType = field.FieldType;
        }

        internal ExposedMember(PropertyInfo property, ExposedPropertyAttribute attr)
        {
            _field = null;
            _property = property;
            Name = property.Name;
            DisplayName = string.IsNullOrEmpty(attr.DisplayName) ? property.Name : attr.DisplayName!;
            Tooltip = attr.Tooltip;
            MemberType = property.PropertyType;
        }

        public object? GetValue(object instance)
        {
            return _field?.GetValue(instance) ?? _property!.GetValue(instance);
        }

        public void SetValue(object instance, object? value)
        {
            if (_field != null) _field.SetValue(instance, value);
            else _property!.SetValue(instance, value);
        }
    }
}
