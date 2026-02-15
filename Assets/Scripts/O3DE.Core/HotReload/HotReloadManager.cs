/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace O3DE.Core.HotReload
{
    /// <summary>
    /// Manages hot reload of C# script assemblies.
    /// Provides state preservation and assembly versioning for seamless script updates.
    /// </summary>
    public class HotReloadManager
    {
        private static HotReloadManager? _instance;
        private static readonly object _lock = new object();

        private readonly Dictionary<string, AssemblyState> _assemblyStates = new();
        private readonly Dictionary<string, ScriptState> _scriptStates = new();
        private int _reloadGeneration = 0;

        /// <summary>
        /// Gets the singleton instance of the HotReloadManager
        /// </summary>
        public static HotReloadManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new HotReloadManager();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Current reload generation for assembly versioning
        /// </summary>
        public int ReloadGeneration => _reloadGeneration;

        /// <summary>
        /// Event raised before an assembly is unloaded
        /// </summary>
        public event EventHandler<AssemblyUnloadingEventArgs>? AssemblyUnloading;

        /// <summary>
        /// Event raised after an assembly is loaded or reloaded
        /// </summary>
        public event EventHandler<AssemblyLoadedEventArgs>? AssemblyLoaded;

        /// <summary>
        /// Event raised when a script is about to be hot reloaded
        /// </summary>
        public event EventHandler<ScriptReloadEventArgs>? ScriptReloading;

        /// <summary>
        /// Event raised after a script has been hot reloaded
        /// </summary>
        public event EventHandler<ScriptReloadEventArgs>? ScriptReloaded;

        private HotReloadManager() { }

        /// <summary>
        /// Prepare for hot reload by saving script states
        /// </summary>
        /// <param name="assemblyName">Name of the assembly being reloaded</param>
        public void PrepareForReload(string assemblyName)
        {
            Debug.Log($"[HotReload] Preparing to reload assembly: {assemblyName}");

            var args = new AssemblyUnloadingEventArgs(assemblyName, _reloadGeneration);
            AssemblyUnloading?.Invoke(this, args);

            // Save state for all scripts in this assembly
            if (_assemblyStates.TryGetValue(assemblyName, out var assemblyState))
            {
                foreach (var scriptId in assemblyState.ScriptIds)
                {
                    SaveScriptState(scriptId);
                }
            }
        }

        /// <summary>
        /// Complete the hot reload process by restoring script states
        /// </summary>
        /// <param name="assemblyName">Name of the reloaded assembly</param>
        /// <param name="newAssembly">The newly loaded assembly</param>
        public void CompleteReload(string assemblyName, Assembly newAssembly)
        {
            _reloadGeneration++;
            Debug.Log($"[HotReload] Completing reload of assembly: {assemblyName} (generation {_reloadGeneration})");

            var args = new AssemblyLoadedEventArgs(assemblyName, newAssembly, _reloadGeneration);
            AssemblyLoaded?.Invoke(this, args);

            // Restore state for all scripts in this assembly
            if (_assemblyStates.TryGetValue(assemblyName, out var assemblyState))
            {
                foreach (var scriptId in assemblyState.ScriptIds.ToList())
                {
                    RestoreScriptState(scriptId, newAssembly);
                }
            }
        }

        /// <summary>
        /// Register a script instance for hot reload tracking
        /// </summary>
        /// <param name="scriptId">Unique identifier for the script instance</param>
        /// <param name="script">The script instance</param>
        /// <param name="assemblyName">Name of the assembly containing the script</param>
        public void RegisterScript(string scriptId, object script, string assemblyName)
        {
            if (!_assemblyStates.ContainsKey(assemblyName))
            {
                _assemblyStates[assemblyName] = new AssemblyState(assemblyName);
            }

            _assemblyStates[assemblyName].ScriptIds.Add(scriptId);
            _scriptStates[scriptId] = new ScriptState
            {
                ScriptId = scriptId,
                AssemblyName = assemblyName,
                TypeName = script.GetType().FullName ?? script.GetType().Name,
                Instance = script
            };

            Debug.Log($"[HotReload] Registered script: {scriptId} ({script.GetType().Name})");
        }

        /// <summary>
        /// Unregister a script instance from hot reload tracking
        /// </summary>
        /// <param name="scriptId">Unique identifier for the script instance</param>
        public void UnregisterScript(string scriptId)
        {
            if (_scriptStates.TryGetValue(scriptId, out var state))
            {
                if (_assemblyStates.TryGetValue(state.AssemblyName, out var assemblyState))
                {
                    assemblyState.ScriptIds.Remove(scriptId);
                }
                _scriptStates.Remove(scriptId);
                Debug.Log($"[HotReload] Unregistered script: {scriptId}");
            }
        }

        /// <summary>
        /// Save the state of a script instance for later restoration
        /// </summary>
        /// <param name="scriptId">Unique identifier for the script instance</param>
        private void SaveScriptState(string scriptId)
        {
            if (!_scriptStates.TryGetValue(scriptId, out var state) || state.Instance == null)
            {
                return;
            }

            ScriptReloading?.Invoke(this, new ScriptReloadEventArgs(scriptId, state.TypeName, true));

            var script = state.Instance;
            var type = script.GetType();

            // Save all serializable fields
            state.FieldValues.Clear();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                // Skip fields marked with NonSerialized attribute
                if (field.IsDefined(typeof(NonSerializedAttribute), true))
                    continue;

                // Skip compiler-generated fields
                if (field.IsDefined(typeof(CompilerGeneratedAttribute), true))
                    continue;

                try
                {
                    var value = field.GetValue(script);
                    if (IsSerializable(value))
                    {
                        state.FieldValues[field.Name] = value;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[HotReload] Failed to save field {field.Name}: {ex.Message}");
                }
            }

            Debug.Log($"[HotReload] Saved state for script: {scriptId} ({state.FieldValues.Count} fields)");
        }

        /// <summary>
        /// Restore the state of a script instance after hot reload
        /// </summary>
        /// <param name="scriptId">Unique identifier for the script instance</param>
        /// <param name="newAssembly">The newly loaded assembly</param>
        private void RestoreScriptState(string scriptId, Assembly newAssembly)
        {
            if (!_scriptStates.TryGetValue(scriptId, out var state))
            {
                return;
            }

            // Find the new type in the reloaded assembly
            var newType = newAssembly.GetType(state.TypeName);
            if (newType == null)
            {
                Debug.LogWarning($"[HotReload] Could not find type {state.TypeName} in reloaded assembly");
                return;
            }

            // Create new instance
            try
            {
                var newInstance = Activator.CreateInstance(newType);
                if (newInstance == null)
                {
                    Debug.LogWarning($"[HotReload] Failed to create instance of {state.TypeName}");
                    return;
                }

                // Restore saved field values
                foreach (var kvp in state.FieldValues)
                {
                    var field = newType.GetField(kvp.Key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        try
                        {
                            field.SetValue(newInstance, kvp.Value);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[HotReload] Failed to restore field {kvp.Key}: {ex.Message}");
                        }
                    }
                }

                // Update the script state with the new instance
                state.Instance = newInstance;
                state.FieldValues.Clear();

                ScriptReloaded?.Invoke(this, new ScriptReloadEventArgs(scriptId, state.TypeName, false));

                Debug.Log($"[HotReload] Restored state for script: {scriptId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HotReload] Failed to restore script {scriptId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if a value is safe to serialize for hot reload
        /// </summary>
        private static bool IsSerializable(object? value)
        {
            if (value == null) return true;
            
            var type = value.GetType();
            
            // Primitive types
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
                return true;
            
            // Common value types
            if (type.IsValueType)
                return true;
            
            // Arrays and lists of serializable types
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                return elementType != null && (elementType.IsPrimitive || elementType == typeof(string) || elementType.IsValueType);
            }

            // Generic lists
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = type.GetGenericArguments()[0];
                return elementType.IsPrimitive || elementType == typeof(string) || elementType.IsValueType;
            }

            return false;
        }

        /// <summary>
        /// Get the new instance of a script after hot reload
        /// </summary>
        /// <param name="scriptId">Unique identifier for the script instance</param>
        /// <returns>The new script instance, or null if not found</returns>
        public object? GetScriptInstance(string scriptId)
        {
            return _scriptStates.TryGetValue(scriptId, out var state) ? state.Instance : null;
        }

        /// <summary>
        /// Clear all tracked state (for testing or shutdown)
        /// </summary>
        public void Clear()
        {
            _assemblyStates.Clear();
            _scriptStates.Clear();
            _reloadGeneration = 0;
        }
    }

    /// <summary>
    /// State tracking for an assembly
    /// </summary>
    internal class AssemblyState
    {
        public string AssemblyName { get; }
        public HashSet<string> ScriptIds { get; } = new();

        public AssemblyState(string assemblyName)
        {
            AssemblyName = assemblyName;
        }
    }

    /// <summary>
    /// State tracking for a script instance
    /// </summary>
    internal class ScriptState
    {
        public string ScriptId { get; set; } = string.Empty;
        public string AssemblyName { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public object? Instance { get; set; }
        public Dictionary<string, object?> FieldValues { get; } = new();
    }

    /// <summary>
    /// Event arguments for assembly unloading
    /// </summary>
    public class AssemblyUnloadingEventArgs : EventArgs
    {
        public string AssemblyName { get; }
        public int Generation { get; }

        public AssemblyUnloadingEventArgs(string assemblyName, int generation)
        {
            AssemblyName = assemblyName;
            Generation = generation;
        }
    }

    /// <summary>
    /// Event arguments for assembly loaded
    /// </summary>
    public class AssemblyLoadedEventArgs : EventArgs
    {
        public string AssemblyName { get; }
        public Assembly Assembly { get; }
        public int Generation { get; }

        public AssemblyLoadedEventArgs(string assemblyName, Assembly assembly, int generation)
        {
            AssemblyName = assemblyName;
            Assembly = assembly;
            Generation = generation;
        }
    }

    /// <summary>
    /// Event arguments for script reload events
    /// </summary>
    public class ScriptReloadEventArgs : EventArgs
    {
        public string ScriptId { get; }
        public string TypeName { get; }
        public bool IsBeforeReload { get; }

        public ScriptReloadEventArgs(string scriptId, string typeName, bool isBeforeReload)
        {
            ScriptId = scriptId;
            TypeName = typeName;
            IsBeforeReload = isBeforeReload;
        }
    }
}
