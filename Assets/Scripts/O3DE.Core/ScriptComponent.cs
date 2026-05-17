/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;

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
        /// Phase 17b: bridge invoked by the C++ CSharpScriptComponent::Activate
        /// before <see cref="OnCreate"/> when
        /// <c>/O3DE/O3DESharp/WaitForDebuggerOnActivate</c> is enabled in the
        /// settings registry. The C++ side mirrors the setting into an
        /// environment variable so we don't need a settings-registry round
        /// trip from managed code; reading <c>O3DESHARP_WAIT_FOR_DEBUGGER</c>
        /// here also lets external tooling (CI, test runners) force the
        /// pause without touching the registry.
        ///
        /// Method intentionally has an underscore prefix and an internal-y
        /// shape so user scripts don't accidentally override it. Don't
        /// rename without updating CSharpScriptComponent::Activate.
        /// </summary>
        public void _O3DESharpWaitForAttachIfRequested()
        {
            string? want = System.Environment.GetEnvironmentVariable("O3DESHARP_WAIT_FOR_DEBUGGER");
            if (want == "1" && !Debugger.IsAttached)
            {
                // 60-second timeout balances "give me time to click Attach"
                // with "don't deadlock CI runners that have no debugger".
                Debugger.WaitForAttach(System.TimeSpan.FromSeconds(60));
            }
        }

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

        #region Delayed Actions

        /// <summary>
        /// Internal representation of a scheduled action.
        /// </summary>
        private class ScheduledAction
        {
            public Action Callback;
            public float TimeRemaining;
            public float RepeatInterval; // 0 = one-shot
            public bool Cancelled;

            public ScheduledAction(Action callback, float delay, float repeatInterval = 0f)
            {
                Callback = callback;
                TimeRemaining = delay;
                RepeatInterval = repeatInterval;
                Cancelled = false;
            }
        }

        /// <summary>
        /// List of pending scheduled actions.
        /// </summary>
        private List<ScheduledAction>? m_scheduledActions;

        /// <summary>
        /// Invokes an action after a delay (in seconds).
        /// </summary>
        /// <param name="action">The action to invoke</param>
        /// <param name="delay">Delay in seconds before invoking</param>
        public void Invoke(Action action, float delay)
        {
            m_scheduledActions ??= new List<ScheduledAction>();
            m_scheduledActions.Add(new ScheduledAction(action, delay));
        }

        /// <summary>
        /// Invokes an action repeatedly, starting after an initial delay.
        /// </summary>
        /// <param name="action">The action to invoke</param>
        /// <param name="delay">Initial delay in seconds</param>
        /// <param name="repeatInterval">Time between subsequent invocations</param>
        public void InvokeRepeating(Action action, float delay, float repeatInterval)
        {
            m_scheduledActions ??= new List<ScheduledAction>();
            m_scheduledActions.Add(new ScheduledAction(action, delay, repeatInterval));
        }

        /// <summary>
        /// Cancels all scheduled invocations for this component.
        /// </summary>
        public void CancelInvoke()
        {
            m_scheduledActions?.Clear();
        }

        /// <summary>
        /// Cancels all scheduled invocations of a specific action.
        /// </summary>
        /// <param name="action">The action to cancel</param>
        public void CancelInvoke(Action action)
        {
            if (m_scheduledActions == null) return;
            for (int i = m_scheduledActions.Count - 1; i >= 0; i--)
            {
                if (m_scheduledActions[i].Callback == action)
                {
                    m_scheduledActions[i].Cancelled = true;
                }
            }
        }

        /// <summary>
        /// Apply a JSON-encoded { "fieldName": "stringValue", ... } map of
        /// <see cref="ExposedPropertyAttribute"/>-decorated values. Invoked by
        /// the C++ <c>CSharpScriptComponent</c> after the managed instance has
        /// been constructed but before <c>OnCreate</c> runs, so user code in
        /// <c>OnCreate</c> sees the editor-configured values.
        ///
        /// Marked non-virtual so user subclasses can't accidentally short-
        /// circuit the property application.
        /// </summary>
        public void ApplyExposedProperties(string valuesJson)
        {
            ExposedPropertyHelpers.ApplyFromJson(this, valuesJson);
        }

        /// <summary>
        /// Return a JSON-encoded array describing every
        /// <see cref="ExposedPropertyAttribute"/>-decorated member on this
        /// instance's type. Used by the editor (Phase 7.5) to decide which
        /// typed inspector widget to render for each field.
        ///
        /// Marked non-virtual to match <see cref="ApplyExposedProperties"/>.
        /// </summary>
        public string GetExposedPropertySchemaJson()
        {
            return ExposedPropertyHelpers.GetSchemaJson(this);
        }

        /// <summary>
        /// Combined per-frame entry point invoked by the C++ CSharpScriptComponent.
        /// Runs the user's overridden OnUpdate then drains any scheduled
        /// Invoke / InvokeRepeating actions in a single managed transition.
        /// Marked non-virtual so user scripts cannot accidentally break the
        /// scheduler by forgetting to call base.Tick.
        /// </summary>
        /// <param name="deltaTime">Seconds since the last tick.</param>
        public void Tick(float deltaTime)
        {
            OnUpdate(deltaTime);

            // Cheap early-out: skip the inner loop entirely when nothing is
            // scheduled. m_scheduledActions stays null for scripts that never
            // call Invoke / InvokeRepeating, which is the common case.
            if (m_scheduledActions != null && m_scheduledActions.Count > 0)
            {
                ProcessPendingInvocations(deltaTime);
            }
        }

        /// <summary>
        /// Processes pending scheduled actions. Called internally by Tick.
        /// DO NOT call this directly.
        /// </summary>
        internal void ProcessPendingInvocations(float deltaTime)
        {
            if (m_scheduledActions == null || m_scheduledActions.Count == 0)
                return;

            // Process backwards so we can safely remove items
            for (int i = m_scheduledActions.Count - 1; i >= 0; i--)
            {
                var action = m_scheduledActions[i];
                if (action.Cancelled)
                {
                    m_scheduledActions.RemoveAt(i);
                    continue;
                }

                action.TimeRemaining -= deltaTime;
                if (action.TimeRemaining <= 0f)
                {
                    try
                    {
                        action.Callback();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error in scheduled action: {ex.Message}");
                    }

                    if (action.RepeatInterval > 0f)
                    {
                        // Reschedule for next repeat
                        action.TimeRemaining += action.RepeatInterval;
                    }
                    else
                    {
                        // One-shot: remove
                        m_scheduledActions.RemoveAt(i);
                    }
                }
            }
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
