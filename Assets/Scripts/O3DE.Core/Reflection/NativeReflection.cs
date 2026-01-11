/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Coral.Managed.Interop;

namespace O3DE.Reflection
{
    /// <summary>
    /// Internal calls for the reflection system.
    /// These map directly to C++ functions in GenericDispatcher.
    /// Coral populates these function pointer fields at runtime via AddInternalCall().
    /// </summary>
    internal static unsafe class ReflectionInternalCalls
    {
        // Suppress warnings about unassigned fields - Coral populates these
        #pragma warning disable 0649

        // Reflection queries
        internal static delegate* unmanaged<NativeString> Reflection_GetClassNames;
        internal static delegate* unmanaged<NativeString, NativeString> Reflection_GetMethodNames;
        internal static delegate* unmanaged<NativeString, NativeString> Reflection_GetPropertyNames;
        internal static delegate* unmanaged<NativeString> Reflection_GetEBusNames;
        internal static delegate* unmanaged<NativeString, NativeString> Reflection_GetEBusEventNames;
        internal static delegate* unmanaged<NativeString, Bool32> Reflection_ClassExists;
        internal static delegate* unmanaged<NativeString, NativeString, Bool32> Reflection_MethodExists;

        // Method invocation
        internal static delegate* unmanaged<NativeString, NativeString, NativeString, NativeString> Reflection_InvokeStaticMethod;
        internal static delegate* unmanaged<NativeString, NativeString, long, NativeString, NativeString> Reflection_InvokeInstanceMethod;
        internal static delegate* unmanaged<NativeString, NativeString, NativeString> Reflection_InvokeGlobalMethod;

        // Property access
        internal static delegate* unmanaged<NativeString, NativeString, long, NativeString> Reflection_GetProperty;
        internal static delegate* unmanaged<NativeString, NativeString, long, NativeString, Bool32> Reflection_SetProperty;
        internal static delegate* unmanaged<NativeString, NativeString> Reflection_GetGlobalProperty;
        internal static delegate* unmanaged<NativeString, NativeString, Bool32> Reflection_SetGlobalProperty;

        // EBus
        internal static delegate* unmanaged<NativeString, NativeString, NativeString, NativeString> Reflection_BroadcastEBusEvent;
        internal static delegate* unmanaged<NativeString, NativeString, long, NativeString, NativeString> Reflection_SendEBusEvent;

        // Object lifecycle
        internal static delegate* unmanaged<NativeString, NativeString, long> Reflection_CreateInstance;
        internal static delegate* unmanaged<NativeString, long, void> Reflection_DestroyInstance;

        #pragma warning restore 0649
    }

    /// <summary>
    /// Provides access to O3DE's BehaviorContext reflection system from C#.
    ///
    /// This allows C# code to:
    /// - Query available classes, methods, and properties
    /// - Create instances of reflected types
    /// - Invoke methods and access properties dynamically
    /// - Broadcast and send EBus events
    ///
    /// This is the "Option B" automated reflection approach where any type
    /// reflected to BehaviorContext becomes accessible from C# automatically.
    /// </summary>
    public static class NativeReflection
    {
        // Cache for class names to avoid repeated native calls
        private static List<string>? _cachedClassNames;
        private static List<string>? _cachedEBusNames;
        private static readonly Dictionary<string, List<string>> _cachedMethodNames = new();
        private static readonly Dictionary<string, List<string>> _cachedPropertyNames = new();
        private static readonly Dictionary<string, List<string>> _cachedEBusEventNames = new();

        #region Type Queries

        /// <summary>
        /// Get all class names reflected to BehaviorContext.
        /// Results are cached for performance.
        /// </summary>
        public static IReadOnlyList<string> GetClassNames()
        {
            if (_cachedClassNames == null)
            {
                string json;
                unsafe { json = ReflectionInternalCalls.Reflection_GetClassNames(); }
                _cachedClassNames = ParseJsonStringArray(json);
            }
            return _cachedClassNames;
        }

        /// <summary>
        /// Get all method names for a reflected class.
        /// Results are cached for performance.
        /// </summary>
        /// <param name="className">The class name to query</param>
        public static IReadOnlyList<string> GetMethodNames(string className)
        {
            if (!_cachedMethodNames.TryGetValue(className, out var methods))
            {
                string json;
                unsafe { json = ReflectionInternalCalls.Reflection_GetMethodNames(className); }
                methods = ParseJsonStringArray(json);
                _cachedMethodNames[className] = methods;
            }
            return methods;
        }

        /// <summary>
        /// Get all property names for a reflected class.
        /// Results are cached for performance.
        /// </summary>
        /// <param name="className">The class name to query</param>
        public static IReadOnlyList<string> GetPropertyNames(string className)
        {
            if (!_cachedPropertyNames.TryGetValue(className, out var properties))
            {
                string json;
                unsafe { json = ReflectionInternalCalls.Reflection_GetPropertyNames(className); }
                properties = ParseJsonStringArray(json);
                _cachedPropertyNames[className] = properties;
            }
            return properties;
        }

        /// <summary>
        /// Get all EBus names reflected to BehaviorContext.
        /// Results are cached for performance.
        /// </summary>
        public static IReadOnlyList<string> GetEBusNames()
        {
            if (_cachedEBusNames == null)
            {
                string json;
                unsafe { json = ReflectionInternalCalls.Reflection_GetEBusNames(); }
                _cachedEBusNames = ParseJsonStringArray(json);
            }
            return _cachedEBusNames;
        }

        /// <summary>
        /// Get all event names for a reflected EBus.
        /// Results are cached for performance.
        /// </summary>
        /// <param name="busName">The EBus name to query</param>
        public static IReadOnlyList<string> GetEBusEventNames(string busName)
        {
            if (!_cachedEBusEventNames.TryGetValue(busName, out var events))
            {
                string json;
                unsafe { json = ReflectionInternalCalls.Reflection_GetEBusEventNames(busName); }
                events = ParseJsonStringArray(json);
                _cachedEBusEventNames[busName] = events;
            }
            return events;
        }

        /// <summary>
        /// Check if a class is reflected to BehaviorContext.
        /// </summary>
        /// <param name="className">The class name to check</param>
        public static bool ClassExists(string className)
        {
            unsafe { return ReflectionInternalCalls.Reflection_ClassExists(className); }
        }

        /// <summary>
        /// Check if a method exists on a reflected class.
        /// </summary>
        /// <param name="className">The class name</param>
        /// <param name="methodName">The method name to check</param>
        public static bool MethodExists(string className, string methodName)
        {
            unsafe { return ReflectionInternalCalls.Reflection_MethodExists(className, methodName); }
        }

        /// <summary>
        /// Clear all cached reflection data.
        /// Call this after hot-reload to refresh the cache.
        /// </summary>
        public static void ClearCache()
        {
            _cachedClassNames = null;
            _cachedEBusNames = null;
            _cachedMethodNames.Clear();
            _cachedPropertyNames.Clear();
            _cachedEBusEventNames.Clear();
        }

        #endregion

        #region Method Invocation

        /// <summary>
        /// Invoke a static method on a reflected class.
        /// </summary>
        /// <param name="className">The class name</param>
        /// <param name="methodName">The method name</param>
        /// <param name="args">Arguments to pass to the method</param>
        /// <returns>The result as a dynamic object, or null for void methods</returns>
        public static object? InvokeStaticMethod(string className, string methodName, params object[] args)
        {
            string argsJson = SerializeArguments(args);
            string resultJson;
            unsafe { resultJson = ReflectionInternalCalls.Reflection_InvokeStaticMethod(className, methodName, argsJson); }
            return DeserializeResult(resultJson);
        }

        /// <summary>
        /// Invoke an instance method on a native object.
        /// </summary>
        /// <param name="instance">The native object wrapper</param>
        /// <param name="methodName">The method name</param>
        /// <param name="args">Arguments to pass to the method</param>
        /// <returns>The result as a dynamic object, or null for void methods</returns>
        public static object? InvokeInstanceMethod(NativeObject instance, string methodName, params object[] args)
        {
            if (instance == null || !instance.IsValid)
            {
                throw new ArgumentException("Invalid native object instance");
            }

            string argsJson = SerializeArguments(args);
            string resultJson;
            unsafe
            {
                resultJson = ReflectionInternalCalls.Reflection_InvokeInstanceMethod(
                    instance.TypeName,
                    methodName,
                    instance.Handle,
                    argsJson);
            }
            return DeserializeResult(resultJson);
        }

        /// <summary>
        /// Invoke a global method (not part of any class).
        /// </summary>
        /// <param name="methodName">The method name</param>
        /// <param name="args">Arguments to pass to the method</param>
        /// <returns>The result as a dynamic object, or null for void methods</returns>
        public static object? InvokeGlobalMethod(string methodName, params object[] args)
        {
            string argsJson = SerializeArguments(args);
            string resultJson;
            unsafe { resultJson = ReflectionInternalCalls.Reflection_InvokeGlobalMethod(methodName, argsJson); }
            return DeserializeResult(resultJson);
        }

        #endregion

        #region Property Access

        /// <summary>
        /// Get a property value from a native object.
        /// </summary>
        /// <typeparam name="T">The expected type of the property</typeparam>
        /// <param name="instance">The native object wrapper</param>
        /// <param name="propertyName">The property name</param>
        /// <returns>The property value</returns>
        public static T? GetProperty<T>(NativeObject instance, string propertyName)
        {
            if (instance == null || !instance.IsValid)
            {
                throw new ArgumentException("Invalid native object instance");
            }

            string resultJson;
            unsafe
            {
                resultJson = ReflectionInternalCalls.Reflection_GetProperty(
                    instance.TypeName,
                    propertyName,
                    instance.Handle);
            }

            object? result = DeserializeResult(resultJson);
            if (result == null)
            {
                return default;
            }

            return (T)Convert.ChangeType(result, typeof(T));
        }

        /// <summary>
        /// Set a property value on a native object.
        /// </summary>
        /// <param name="instance">The native object wrapper</param>
        /// <param name="propertyName">The property name</param>
        /// <param name="value">The value to set</param>
        public static void SetProperty(NativeObject instance, string propertyName, object value)
        {
            if (instance == null || !instance.IsValid)
            {
                throw new ArgumentException("Invalid native object instance");
            }

            string valueJson = SerializeValue(value);
            bool success;
            unsafe
            {
                success = ReflectionInternalCalls.Reflection_SetProperty(
                    instance.TypeName,
                    propertyName,
                    instance.Handle,
                    valueJson);
            }

            if (!success)
            {
                throw new InvalidOperationException($"Failed to set property {instance.TypeName}.{propertyName}");
            }
        }

        /// <summary>
        /// Get a global property value.
        /// </summary>
        /// <typeparam name="T">The expected type of the property</typeparam>
        /// <param name="propertyName">The property name</param>
        /// <returns>The property value</returns>
        public static T? GetGlobalProperty<T>(string propertyName)
        {
            string resultJson;
            unsafe { resultJson = ReflectionInternalCalls.Reflection_GetGlobalProperty(propertyName); }
            object? result = DeserializeResult(resultJson);
            if (result == null)
            {
                return default;
            }
            return (T)Convert.ChangeType(result, typeof(T));
        }

        /// <summary>
        /// Set a global property value.
        /// </summary>
        /// <param name="propertyName">The property name</param>
        /// <param name="value">The value to set</param>
        public static void SetGlobalProperty(string propertyName, object value)
        {
            string valueJson = SerializeValue(value);
            bool success;
            unsafe { success = ReflectionInternalCalls.Reflection_SetGlobalProperty(propertyName, valueJson); }

            if (!success)
            {
                throw new InvalidOperationException($"Failed to set global property {propertyName}");
            }
        }

        #endregion

        #region EBus

        /// <summary>
        /// Broadcast an event on an EBus (sends to all handlers).
        /// </summary>
        /// <param name="busName">The EBus name</param>
        /// <param name="eventName">The event name</param>
        /// <param name="args">Arguments for the event</param>
        /// <returns>The result if the event has a return value</returns>
        public static object? BroadcastEBusEvent(string busName, string eventName, params object[] args)
        {
            string argsJson = SerializeArguments(args);
            string resultJson;
            unsafe { resultJson = ReflectionInternalCalls.Reflection_BroadcastEBusEvent(busName, eventName, argsJson); }
            return DeserializeResult(resultJson);
        }

        /// <summary>
        /// Send an event to a specific address on an EBus.
        /// </summary>
        /// <param name="busName">The EBus name</param>
        /// <param name="eventName">The event name</param>
        /// <param name="entityId">The entity ID to send to</param>
        /// <param name="args">Arguments for the event</param>
        /// <returns>The result if the event has a return value</returns>
        public static object? SendEBusEvent(string busName, string eventName, ulong entityId, params object[] args)
        {
            string argsJson = SerializeArguments(args);
            string resultJson;
            unsafe { resultJson = ReflectionInternalCalls.Reflection_SendEBusEvent(busName, eventName, (long)entityId, argsJson); }
            return DeserializeResult(resultJson);
        }

        /// <summary>
        /// Send an event to a specific entity on an EBus.
        /// </summary>
        /// <param name="busName">The EBus name</param>
        /// <param name="eventName">The event name</param>
        /// <param name="entity">The entity to send to</param>
        /// <param name="args">Arguments for the event</param>
        /// <returns>The result if the event has a return value</returns>
        public static object? SendEBusEvent(string busName, string eventName, Entity entity, params object[] args)
        {
            return SendEBusEvent(busName, eventName, entity.Id, args);
        }

        #endregion

        #region Object Lifecycle

        /// <summary>
        /// Create an instance of a reflected native class.
        /// </summary>
        /// <param name="className">The class name to instantiate</param>
        /// <param name="constructorArgs">Arguments for the constructor</param>
        /// <returns>A NativeObject wrapper for the created instance</returns>
        public static NativeObject CreateInstance(string className, params object[] constructorArgs)
        {
            string argsJson = SerializeArguments(constructorArgs);
            long handle;
            unsafe { handle = ReflectionInternalCalls.Reflection_CreateInstance(className, argsJson); }

            if (handle == 0)
            {
                throw new InvalidOperationException($"Failed to create instance of {className}");
            }

            return new NativeObject(className, handle);
        }

        /// <summary>
        /// Destroy a native object instance.
        /// </summary>
        /// <param name="instance">The object to destroy</param>
        public static void DestroyInstance(NativeObject instance)
        {
            if (instance != null && instance.IsValid)
            {
                unsafe { ReflectionInternalCalls.Reflection_DestroyInstance(instance.TypeName, instance.Handle); }
                instance.Invalidate();
            }
        }

        #endregion

        #region Serialization Helpers

        private static List<string> ParseJsonStringArray(string json)
        {
            var result = new List<string>();

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement element in doc.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.String)
                        {
                            string? value = element.GetString();
                            if (value != null)
                            {
                                result.Add(value);
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                Debug.LogError($"Failed to parse JSON array: {ex.Message}");
            }

            return result;
        }

        private static string SerializeArguments(object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "[]";
            }

            var elements = new List<object>();
            foreach (var arg in args)
            {
                elements.Add(SerializeArgumentToObject(arg));
            }

            return JsonSerializer.Serialize(elements);
        }

        private static object SerializeArgumentToObject(object arg)
        {
            if (arg == null)
            {
                return new { type = "null" };
            }

            return arg switch
            {
                bool b => new { type = "bool", value = b },
                int i => new { type = "int32", value = i },
                long l => new { type = "int64", value = l },
                float f => new { type = "float", value = f },
                double d => new { type = "double", value = d },
                string s => new { type = "string", value = s },
                Vector3 v => new { type = "vector3", x = v.X, y = v.Y, z = v.Z },
                Quaternion q => new { type = "quaternion", x = q.X, y = q.Y, z = q.Z, w = q.W },
                Entity e => new { type = "entityid", value = e.Id },
                NativeObject no => new { type = "object", handle = no.Handle, typeName = no.TypeName },
                _ => new { type = "unknown", value = arg.ToString() }
            };
        }

        private static string SerializeValue(object value)
        {
            return JsonSerializer.Serialize(SerializeArgumentToObject(value));
        }

        private static object? DeserializeResult(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                // Check for error
                if (root.TryGetProperty("error", out JsonElement errorElement))
                {
                    string? errorMessage = errorElement.GetString();
                    Debug.LogError($"Native call error: {errorMessage}");
                    return null;
                }

                // Check for typed result
                if (root.TryGetProperty("type", out JsonElement typeElement))
                {
                    string? type = typeElement.GetString();

                    return type switch
                    {
                        "void" => null,
                        "bool" => root.GetProperty("value").GetBoolean(),
                        "int32" => root.GetProperty("value").GetInt32(),
                        "int64" => root.GetProperty("value").GetInt64(),
                        "float" => root.GetProperty("value").GetSingle(),
                        "double" => root.GetProperty("value").GetDouble(),
                        "string" => root.GetProperty("value").GetString(),
                        "vector3" => new Vector3(
                            root.GetProperty("x").GetSingle(),
                            root.GetProperty("y").GetSingle(),
                            root.GetProperty("z").GetSingle()),
                        "quaternion" => new Quaternion(
                            root.GetProperty("x").GetSingle(),
                            root.GetProperty("y").GetSingle(),
                            root.GetProperty("z").GetSingle(),
                            root.GetProperty("w").GetSingle()),
                        "entityid" => new Entity((ulong)root.GetProperty("value").GetInt64()),
                        "object" => new NativeObject(
                            root.GetProperty("typeName").GetString() ?? "Unknown",
                            root.GetProperty("handle").GetInt64()),
                        _ => null
                    };
                }

                // Try to return primitive value directly
                return root.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => root.TryGetInt64(out long l) ? l : root.GetDouble(),
                    JsonValueKind.String => root.GetString(),
                    _ => null
                };
            }
            catch (JsonException ex)
            {
                Debug.LogError($"Failed to deserialize result: {ex.Message}");
                return null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Represents a native O3DE object created through reflection.
    /// This is a wrapper around a native pointer that allows method invocation
    /// and property access through the reflection system.
    /// </summary>
    public class NativeObject : IDisposable
    {
        /// <summary>
        /// The native handle (pointer) to the object
        /// </summary>
        public long Handle { get; private set; }

        /// <summary>
        /// The type name of the native class
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// Whether this object reference is valid
        /// </summary>
        public bool IsValid => Handle != 0;

        /// <summary>
        /// Whether this object has been disposed
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Create a new NativeObject wrapper
        /// </summary>
        /// <param name="typeName">The native type name</param>
        /// <param name="handle">The native handle</param>
        public NativeObject(string typeName, long handle)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            Handle = handle;
            IsDisposed = false;
        }

        /// <summary>
        /// Invoke a method on this object
        /// </summary>
        /// <param name="methodName">The method name</param>
        /// <param name="args">Method arguments</param>
        /// <returns>The return value, or null for void methods</returns>
        public object? InvokeMethod(string methodName, params object[] args)
        {
            ThrowIfDisposed();
            return NativeReflection.InvokeInstanceMethod(this, methodName, args);
        }

        /// <summary>
        /// Invoke a method with a typed return value
        /// </summary>
        /// <typeparam name="T">The return type</typeparam>
        /// <param name="methodName">The method name</param>
        /// <param name="args">Method arguments</param>
        /// <returns>The return value</returns>
        public T? InvokeMethod<T>(string methodName, params object[] args)
        {
            object? result = InvokeMethod(methodName, args);
            if (result == null)
            {
                return default;
            }
            return (T)Convert.ChangeType(result, typeof(T));
        }

        /// <summary>
        /// Get a property value
        /// </summary>
        /// <typeparam name="T">The property type</typeparam>
        /// <param name="propertyName">The property name</param>
        /// <returns>The property value</returns>
        public T? GetProperty<T>(string propertyName)
        {
            ThrowIfDisposed();
            return NativeReflection.GetProperty<T>(this, propertyName);
        }

        /// <summary>
        /// Set a property value
        /// </summary>
        /// <param name="propertyName">The property name</param>
        /// <param name="value">The value to set</param>
        public void SetProperty(string propertyName, object value)
        {
            ThrowIfDisposed();
            NativeReflection.SetProperty(this, propertyName, value);
        }

        /// <summary>
        /// Mark this object as invalid (called after destruction)
        /// </summary>
        internal void Invalidate()
        {
            Handle = 0;
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(NativeObject));
            }
            if (!IsValid)
            {
                throw new InvalidOperationException("Native object is not valid");
            }
        }

        #region IDisposable

        /// <summary>
        /// Dispose of the native object
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (IsValid)
                {
                    NativeReflection.DestroyInstance(this);
                }
                IsDisposed = true;
            }
        }

        ~NativeObject()
        {
            Dispose(false);
        }

        #endregion

        public override string ToString()
        {
            if (!IsValid)
            {
                return $"NativeObject({TypeName}, Invalid)";
            }
            return $"NativeObject({TypeName}, Handle=0x{Handle:X})";
        }
    }
}
