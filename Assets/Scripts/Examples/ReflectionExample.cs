/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using O3DE;
using O3DE.Reflection;
using System;
using System.Collections.Generic;

namespace Examples
{
    /// <summary>
    /// Example demonstrating the automated BehaviorContext reflection system.
    ///
    /// This example shows how to:
    /// - Query available classes and methods from O3DE's BehaviorContext
    /// - Create instances of reflected native types
    /// - Invoke methods dynamically without compile-time bindings
    /// - Access properties on native objects
    /// - Broadcast and send EBus events
    ///
    /// This is "Option B" - automated reflection where any type reflected
    /// to O3DE's BehaviorContext is automatically accessible from C#.
    /// </summary>
    public class ReflectionExample : ScriptComponent
    {
        private float _logTimer = 0f;
        private const float LogInterval = 5f;

        public override void OnCreate()
        {
            Debug.Log("=== ReflectionExample: Demonstrating Automated Reflection ===");

            // Example 1: Query available reflected classes
            QueryReflectedTypes();

            // Example 2: Query methods on a specific class
            QueryClassMethods();

            // Example 3: Create and use a native object dynamically
            DynamicObjectCreation();

            // Example 4: Invoke methods dynamically
            DynamicMethodInvocation();

            // Example 5: EBus interaction
            EBusExample();
        }

        public override void OnUpdate(float deltaTime)
        {
            _logTimer += deltaTime;

            // Periodically demonstrate dynamic method invocation
            if (_logTimer >= LogInterval)
            {
                _logTimer = 0f;
                PeriodicDynamicDemo();
            }
        }

        public override void OnDestroy()
        {
            Debug.Log("ReflectionExample: Destroyed");
        }

        /// <summary>
        /// Demonstrates querying the list of reflected types from BehaviorContext
        /// </summary>
        private void QueryReflectedTypes()
        {
            Debug.Log("\n--- Example 1: Query Reflected Types ---");

            // Get all reflected class names
            IReadOnlyList<string> classNames = NativeReflection.GetClassNames();
            Debug.Log($"Total reflected classes: {classNames.Count}");

            // Log the first 10 classes as a sample
            int count = Math.Min(10, classNames.Count);
            Debug.Log($"Sample of reflected classes (first {count}):");
            for (int i = 0; i < count; i++)
            {
                Debug.Log($"  - {classNames[i]}");
            }

            // Check for specific classes
            bool hasVector3 = NativeReflection.ClassExists("Vector3");
            bool hasTransform = NativeReflection.ClassExists("Transform");
            bool hasEntityId = NativeReflection.ClassExists("EntityId");

            Debug.Log($"Has Vector3: {hasVector3}");
            Debug.Log($"Has Transform: {hasTransform}");
            Debug.Log($"Has EntityId: {hasEntityId}");

            // Get all reflected EBus names
            IReadOnlyList<string> ebusNames = NativeReflection.GetEBusNames();
            Debug.Log($"Total reflected EBuses: {ebusNames.Count}");

            count = Math.Min(5, ebusNames.Count);
            if (count > 0)
            {
                Debug.Log($"Sample of reflected EBuses (first {count}):");
                for (int i = 0; i < count; i++)
                {
                    Debug.Log($"  - {ebusNames[i]}");
                }
            }
        }

        /// <summary>
        /// Demonstrates querying methods and properties on a reflected class
        /// </summary>
        private void QueryClassMethods()
        {
            Debug.Log("\n--- Example 2: Query Class Methods ---");

            string className = "Vector3";

            if (!NativeReflection.ClassExists(className))
            {
                Debug.LogWarning($"Class '{className}' not found in reflection");
                return;
            }

            // Get methods
            IReadOnlyList<string> methods = NativeReflection.GetMethodNames(className);
            Debug.Log($"{className} has {methods.Count} methods:");

            int count = Math.Min(15, methods.Count);
            for (int i = 0; i < count; i++)
            {
                Debug.Log($"  - {methods[i]}()");
            }

            if (methods.Count > count)
            {
                Debug.Log($"  ... and {methods.Count - count} more");
            }

            // Get properties
            IReadOnlyList<string> properties = NativeReflection.GetPropertyNames(className);
            Debug.Log($"{className} has {properties.Count} properties:");

            foreach (string prop in properties)
            {
                Debug.Log($"  - {prop}");
            }

            // Check for specific methods
            bool hasNormalize = NativeReflection.MethodExists(className, "Normalize");
            bool hasGetLength = NativeReflection.MethodExists(className, "GetLength");

            Debug.Log($"Has Normalize: {hasNormalize}");
            Debug.Log($"Has GetLength: {hasGetLength}");
        }

        /// <summary>
        /// Demonstrates creating instances of reflected types dynamically
        /// </summary>
        private void DynamicObjectCreation()
        {
            Debug.Log("\n--- Example 3: Dynamic Object Creation ---");

            try
            {
                // Create a Vector3 instance
                // Note: This creates a native O3DE Vector3, not our managed Vector3 struct
                using (NativeObject nativeVector = NativeReflection.CreateInstance("Vector3"))
                {
                    Debug.Log($"Created native Vector3: {nativeVector}");

                    // The object is automatically disposed when leaving the using block
                }

                Debug.Log("Native Vector3 was created and destroyed successfully");
            }
            catch (Exception ex)
            {
                // This is expected if Vector3 doesn't have a default constructor reflected
                Debug.LogWarning($"Could not create Vector3 dynamically: {ex.Message}");
                Debug.Log("(This is expected - not all types support dynamic construction)");
            }
        }

        /// <summary>
        /// Demonstrates invoking methods dynamically on reflected types
        /// </summary>
        private void DynamicMethodInvocation()
        {
            Debug.Log("\n--- Example 4: Dynamic Method Invocation ---");

            // Example: Call a static method on a math class
            try
            {
                // Try to call a global math function if available
                object? result = NativeReflection.InvokeGlobalMethod("MathSin", 1.5707963f); // PI/2
                if (result != null)
                {
                    Debug.Log($"MathSin(PI/2) = {result}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not invoke MathSin: {ex.Message}");
            }

            // Example: Call a static method on Vector3
            try
            {
                object? result = NativeReflection.InvokeStaticMethod("Vector3", "CreateZero");
                Debug.Log($"Vector3.CreateZero() = {result}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not invoke Vector3.CreateZero: {ex.Message}");
            }

            // Using our built-in Vector3 directly (non-reflection approach for comparison)
            Vector3 position = Transform.Position;
            Debug.Log($"Current position (via direct API): {position}");
        }

        /// <summary>
        /// Demonstrates EBus interaction through reflection
        /// </summary>
        private void EBusExample()
        {
            Debug.Log("\n--- Example 5: EBus Interaction ---");

            // Get available events on TransformBus if it exists
            IReadOnlyList<string> ebusNames = NativeReflection.GetEBusNames();

            // Look for transform-related buses
            foreach (string busName in ebusNames)
            {
                if (busName.Contains("Transform", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"Found transform-related EBus: {busName}");

                    IReadOnlyList<string> events = NativeReflection.GetEBusEventNames(busName);
                    if (events.Count > 0)
                    {
                        Debug.Log($"  Events on {busName}:");
                        int count = Math.Min(5, events.Count);
                        for (int i = 0; i < count; i++)
                        {
                            Debug.Log($"    - {events[i]}");
                        }
                        if (events.Count > count)
                        {
                            Debug.Log($"    ... and {events.Count - count} more");
                        }
                    }
                    break; // Just show one example
                }
            }

            // Example of sending an EBus event (commented out as it may not exist)
            /*
            try
            {
                // Broadcast to all handlers
                NativeReflection.BroadcastEBusEvent("TickBus", "OnTick", Time.DeltaTime);

                // Or send to a specific entity
                NativeReflection.SendEBusEvent("TransformBus", "GetWorldTranslation", Entity.Id);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"EBus event failed: {ex.Message}");
            }
            */

            Debug.Log("EBus interaction is available through NativeReflection.BroadcastEBusEvent/SendEBusEvent");
        }

        /// <summary>
        /// Called periodically to demonstrate dynamic capabilities
        /// </summary>
        private void PeriodicDynamicDemo()
        {
            Debug.Log("\n--- Periodic Dynamic Demo ---");

            // Get current position using our direct API
            Vector3 pos = Transform.Position;

            // Log using the reflection info
            IReadOnlyList<string> transformMethods = NativeReflection.GetMethodNames("Transform");

            Debug.Log($"Entity '{Name}' position: {pos}");
            Debug.Log($"Transform class has {transformMethods.Count} reflected methods available");

            // Count total reflected types as a health check
            int totalClasses = NativeReflection.GetClassNames().Count;
            int totalEBuses = NativeReflection.GetEBusNames().Count;

            Debug.Log($"Reflection system status: {totalClasses} classes, {totalEBuses} EBuses available");
        }
    }

    /// <summary>
    /// A more advanced example showing how to wrap reflected types in a type-safe manner.
    /// This demonstrates a pattern for creating strongly-typed wrappers around dynamic reflection.
    /// </summary>
    public class NativeVector3Wrapper : IDisposable
    {
        private NativeObject? _nativeObject;

        /// <summary>
        /// Create a new native Vector3 with default values
        /// </summary>
        public NativeVector3Wrapper()
        {
            try
            {
                _nativeObject = NativeReflection.CreateInstance("Vector3");
            }
            catch
            {
                _nativeObject = null;
            }
        }

        /// <summary>
        /// Create a new native Vector3 with specified values
        /// </summary>
        public NativeVector3Wrapper(float x, float y, float z)
        {
            try
            {
                _nativeObject = NativeReflection.CreateInstance("Vector3", x, y, z);
            }
            catch
            {
                _nativeObject = null;
            }
        }

        public bool IsValid => _nativeObject != null && _nativeObject.IsValid;

        /// <summary>
        /// Get the X component
        /// </summary>
        public float X
        {
            get
            {
                if (!IsValid) return 0f;
                return _nativeObject!.GetProperty<float>("x");
            }
        }

        /// <summary>
        /// Get the Y component
        /// </summary>
        public float Y
        {
            get
            {
                if (!IsValid) return 0f;
                return _nativeObject!.GetProperty<float>("y");
            }
        }

        /// <summary>
        /// Get the Z component
        /// </summary>
        public float Z
        {
            get
            {
                if (!IsValid) return 0f;
                return _nativeObject!.GetProperty<float>("z");
            }
        }

        /// <summary>
        /// Normalize this vector
        /// </summary>
        public void Normalize()
        {
            if (IsValid)
            {
                _nativeObject!.InvokeMethod("Normalize");
            }
        }

        /// <summary>
        /// Get the length of this vector
        /// </summary>
        public float GetLength()
        {
            if (!IsValid) return 0f;
            return _nativeObject!.InvokeMethod<float>("GetLength");
        }

        public void Dispose()
        {
            _nativeObject?.Dispose();
            _nativeObject = null;
        }

        public override string ToString()
        {
            if (!IsValid) return "NativeVector3(Invalid)";
            return $"NativeVector3({X}, {Y}, {Z})";
        }
    }
}
