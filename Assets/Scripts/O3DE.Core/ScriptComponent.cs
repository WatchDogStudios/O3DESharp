/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;

namespace O3DE
{
    /// <summary>
    /// Base class for all C# scripts that can be attached to O3DE entities.
    ///
    /// To create a script, inherit from this class and override the lifecycle methods:
    /// - OnCreate(): Called when the component is activated
    /// - OnUpdate(float deltaTime): Called every frame
    /// - OnDestroy(): Called when the component is deactivated
    /// - OnTransformChanged(): Called when the entity's transform changes
    ///
    /// Example:
    /// <code>
    /// namespace MyGame
    /// {
    ///     public class PlayerController : ScriptComponent
    ///     {
    ///         private float speed = 10.0f;
    ///
    ///         public override void OnCreate()
    ///         {
    ///             Debug.Log("PlayerController created!");
    ///         }
    ///
    ///         public override void OnUpdate(float deltaTime)
    ///         {
    ///             // Move forward
    ///             Transform.Translate(Transform.Forward * speed * deltaTime);
    ///         }
    ///
    ///         public override void OnDestroy()
    ///         {
    ///             Debug.Log("PlayerController destroyed!");
    ///         }
    ///     }
    /// }
    /// </code>
    /// </summary>
    public abstract class ScriptComponent
    {
        /// <summary>
        /// The native entity ID. This field is set by the C++ CSharpScriptComponent.
        /// DO NOT modify this field manually.
        /// </summary>
        protected ulong m_entityId;

        /// <summary>
        /// Cached Entity wrapper (lazily initialized)
        /// </summary>
        private Entity? m_entity;

        /// <summary>
        /// Cached Transform wrapper (lazily initialized)
        /// </summary>
        private Transform? m_transform;

        #region Properties

        /// <summary>
        /// Gets the Entity this script is attached to.
        /// </summary>
        public Entity Entity
        {
            get
            {
                m_entity ??= new Entity(m_entityId);
                return m_entity;
            }
        }

        /// <summary>
        /// Gets the Transform of the entity this script is attached to.
        /// This is a shortcut for Entity.Transform.
        /// </summary>
        public Transform Transform
        {
            get
            {
                m_transform ??= new Transform(m_entityId);
                return m_transform;
            }
        }

        /// <summary>
        /// Gets the native entity ID.
        /// </summary>
        public ulong EntityId => m_entityId;

        /// <summary>
        /// Gets the name of the entity this script is attached to.
        /// </summary>
        public string Name => Entity.Name;

        /// <summary>
        /// Gets whether the entity is currently active.
        /// </summary>
        public bool IsActive => Entity.IsActive;

        #endregion

        #region Lifecycle Methods

        /// <summary>
        /// Called when the script component is first activated.
        /// Use this for initialization logic.
        /// </summary>
        public virtual void OnCreate()
        {
        }

        /// <summary>
        /// Called every frame while the component is active.
        /// </summary>
        /// <param name="deltaTime">The time in seconds since the last frame</param>
        public virtual void OnUpdate(float deltaTime)
        {
        }

        /// <summary>
        /// Called when the script component is deactivated.
        /// Use this for cleanup logic.
        /// </summary>
        public virtual void OnDestroy()
        {
        }

        /// <summary>
        /// Called when the entity's transform changes (position, rotation, or scale).
        /// Override this to react to transform changes.
        /// </summary>
        public virtual void OnTransformChanged()
        {
        }

        /// <summary>
        /// Called when the component is enabled (after being disabled).
        /// </summary>
        public virtual void OnEnable()
        {
        }

        /// <summary>
        /// Called when the component is disabled.
        /// </summary>
        public virtual void OnDisable()
        {
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Logs an informational message to the O3DE console.
        /// Shortcut for Debug.Log().
        /// </summary>
        /// <param name="message">The message to log</param>
        protected void Log(string message)
        {
            Debug.Log(message);
        }

        /// <summary>
        /// Logs a formatted informational message to the O3DE console.
        /// </summary>
        /// <param name="format">The format string</param>
        /// <param name="args">The format arguments</param>
        protected void Log(string format, params object[] args)
        {
            Debug.Log(format, args);
        }

        /// <summary>
        /// Logs a warning message to the O3DE console.
        /// </summary>
        /// <param name="message">The warning message</param>
        protected void LogWarning(string message)
        {
            Debug.LogWarning(message);
        }

        /// <summary>
        /// Logs an error message to the O3DE console.
        /// </summary>
        /// <param name="message">The error message</param>
        protected void LogError(string message)
        {
            Debug.LogError(message);
        }

        /// <summary>
        /// Checks if the entity has a component of the specified type.
        /// </summary>
        /// <param name="componentTypeName">The fully qualified component type name</param>
        /// <returns>True if the entity has the component</returns>
        protected bool HasComponent(string componentTypeName)
        {
            return Entity.HasComponent(componentTypeName);
        }

        /// <summary>
        /// Activates the entity this script is attached to.
        /// </summary>
        protected void ActivateEntity()
        {
            Entity.Activate();
        }

        /// <summary>
        /// Deactivates the entity this script is attached to.
        /// </summary>
        protected void DeactivateEntity()
        {
            Entity.Deactivate();
        }

        #endregion

        #region String

        /// <summary>
        /// Returns a string representation of this script component.
        /// </summary>
        public override string ToString()
        {
            return $"{GetType().Name}(Entity=\"{Name}\", Id={EntityId})";
        }

        #endregion
    }
}
