/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using Coral.Managed.Interop;

namespace O3DE
{
    /// <summary>
    /// Provides access to an entity's transform (position, rotation, scale).
    /// This class wraps the native TransformBus functionality.
    ///
    /// O3DE uses a right-handed coordinate system:
    /// - X is Right
    /// - Y is Forward
    /// - Z is Up
    /// </summary>
    public class Transform
    {
        /// <summary>
        /// The entity this transform belongs to
        /// </summary>
        private readonly Entity m_entity;

        #region Constructors

        /// <summary>
        /// Creates a new Transform wrapper for the specified entity
        /// </summary>
        /// <param name="entity">The entity to wrap</param>
        internal Transform(Entity entity)
        {
            m_entity = entity ?? throw new ArgumentNullException(nameof(entity));
        }

        /// <summary>
        /// Creates a new Transform wrapper for the specified entity ID
        /// </summary>
        /// <param name="entityId">The entity ID</param>
        public Transform(ulong entityId)
        {
            m_entity = new Entity(entityId);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the entity this transform belongs to
        /// </summary>
        public Entity Entity => m_entity;

        /// <summary>
        /// Gets whether this transform is valid (entity exists)
        /// </summary>
        public bool IsValid => m_entity.IsValid;

        #endregion

        #region World Position

        /// <summary>
        /// Gets or sets the world position of this entity
        /// </summary>
        public Vector3 Position
        {
            get
            {
                if (!IsValid)
                    return Vector3.Zero;
                unsafe { return InternalCalls.Transform_GetWorldPosition(m_entity.Id); }
            }
            set
            {
                if (IsValid)
                    unsafe { InternalCalls.Transform_SetWorldPosition(m_entity.Id, value); }
            }
        }

        /// <summary>
        /// Gets or sets the local position of this entity (relative to parent)
        /// </summary>
        public Vector3 LocalPosition
        {
            get
            {
                if (!IsValid)
                    return Vector3.Zero;
                unsafe { return InternalCalls.Transform_GetLocalPosition(m_entity.Id); }
            }
            set
            {
                if (IsValid)
                    unsafe { InternalCalls.Transform_SetLocalPosition(m_entity.Id, value); }
            }
        }

        #endregion

        #region World Rotation

        /// <summary>
        /// Gets or sets the world rotation of this entity as a quaternion
        /// </summary>
        public Quaternion Rotation
        {
            get
            {
                if (!IsValid)
                    return Quaternion.Identity;
                unsafe { return InternalCalls.Transform_GetWorldRotation(m_entity.Id); }
            }
            set
            {
                if (IsValid)
                    unsafe { InternalCalls.Transform_SetWorldRotation(m_entity.Id, value); }
            }
        }

        /// <summary>
        /// Gets or sets the world rotation of this entity as euler angles (in degrees)
        /// </summary>
        public Vector3 EulerAngles
        {
            get
            {
                if (!IsValid)
                    return Vector3.Zero;
                unsafe { return InternalCalls.Transform_GetWorldRotationEuler(m_entity.Id); }
            }
            set
            {
                if (IsValid)
                    unsafe { InternalCalls.Transform_SetWorldRotationEuler(m_entity.Id, value); }
            }
        }

        #endregion

        #region Scale

        /// <summary>
        /// Gets or sets the local scale of this entity
        /// </summary>
        public Vector3 LocalScale
        {
            get
            {
                if (!IsValid)
                    return Vector3.One;
                unsafe { return InternalCalls.Transform_GetLocalScale(m_entity.Id); }
            }
            set
            {
                if (IsValid)
                    unsafe { InternalCalls.Transform_SetLocalScale(m_entity.Id, value); }
            }
        }

        /// <summary>
        /// Gets or sets the uniform local scale of this entity
        /// </summary>
        public float UniformScale
        {
            get
            {
                if (!IsValid)
                    return 1f;
                unsafe { return InternalCalls.Transform_GetLocalUniformScale(m_entity.Id); }
            }
            set
            {
                if (IsValid)
                    unsafe { InternalCalls.Transform_SetLocalUniformScale(m_entity.Id, value); }
            }
        }

        #endregion

        #region Direction Vectors

        /// <summary>
        /// Gets the forward direction of this entity (Y-axis in O3DE)
        /// </summary>
        public Vector3 Forward
        {
            get
            {
                if (!IsValid)
                    return Vector3.Forward;
                unsafe { return InternalCalls.Transform_GetForward(m_entity.Id); }
            }
        }

        /// <summary>
        /// Gets the right direction of this entity (X-axis in O3DE)
        /// </summary>
        public Vector3 Right
        {
            get
            {
                if (!IsValid)
                    return Vector3.Right;
                unsafe { return InternalCalls.Transform_GetRight(m_entity.Id); }
            }
        }

        /// <summary>
        /// Gets the up direction of this entity (Z-axis in O3DE)
        /// </summary>
        public Vector3 Up
        {
            get
            {
                if (!IsValid)
                    return Vector3.Up;
                unsafe { return InternalCalls.Transform_GetUp(m_entity.Id); }
            }
        }

        /// <summary>
        /// Gets the backward direction of this entity (negative Y-axis)
        /// </summary>
        public Vector3 Back => -Forward;

        /// <summary>
        /// Gets the left direction of this entity (negative X-axis)
        /// </summary>
        public Vector3 Left => -Right;

        /// <summary>
        /// Gets the down direction of this entity (negative Z-axis)
        /// </summary>
        public Vector3 Down => -Up;

        #endregion

        #region Parent/Child Hierarchy

        /// <summary>
        /// Gets the parent entity of this entity, or null if no parent
        /// </summary>
        public Entity? Parent
        {
            get
            {
                if (!IsValid)
                    return null;
                ulong parentId;
                unsafe { parentId = InternalCalls.Transform_GetParentId(m_entity.Id); }
                return parentId != Entity.InvalidId ? new Entity(parentId) : null;
            }
        }

        /// <summary>
        /// Sets the parent of this entity
        /// </summary>
        /// <param name="parent">The new parent entity, or null to unparent</param>
        public void SetParent(Entity? parent)
        {
            if (!IsValid)
                return;

            ulong parentId = parent?.Id ?? Entity.InvalidId;
            unsafe { InternalCalls.Transform_SetParent(m_entity.Id, parentId); }
        }

        /// <summary>
        /// Removes this entity's parent (makes it a root entity)
        /// </summary>
        public void ClearParent()
        {
            SetParent(null);
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Moves the entity by the specified offset in world space
        /// </summary>
        /// <param name="translation">The offset to apply</param>
        public void Translate(Vector3 translation)
        {
            Position += translation;
        }

        /// <summary>
        /// Moves the entity by the specified offset in local space
        /// </summary>
        /// <param name="translation">The offset to apply in local coordinates</param>
        public void TranslateLocal(Vector3 translation)
        {
            // Transform the local translation to world space using the entity's rotation
            Vector3 worldTranslation = Rotation * translation;
            Position += worldTranslation;
        }

        /// <summary>
        /// Rotates the entity by the specified euler angles (in degrees)
        /// </summary>
        /// <param name="eulerAngles">The rotation to apply (in degrees)</param>
        public void Rotate(Vector3 eulerAngles)
        {
            Quaternion deltaRotation = Quaternion.FromEuler(eulerAngles);
            Rotation = deltaRotation * Rotation;
        }

        /// <summary>
        /// Rotates the entity around an axis by the specified angle
        /// </summary>
        /// <param name="axis">The axis to rotate around</param>
        /// <param name="angleDegrees">The angle in degrees</param>
        public void RotateAround(Vector3 axis, float angleDegrees)
        {
            Quaternion deltaRotation = Quaternion.AngleAxis(angleDegrees, axis);
            Rotation = deltaRotation * Rotation;
        }

        /// <summary>
        /// Rotates the entity to look at a target position
        /// </summary>
        /// <param name="target">The position to look at</param>
        /// <param name="worldUp">The up direction (defaults to Vector3.Up)</param>
        public void LookAt(Vector3 target, Vector3? worldUp = null)
        {
            Vector3 direction = target - Position;
            if (direction.SqrMagnitude < 1e-6f)
                return;

            Rotation = Quaternion.LookRotation(direction, worldUp ?? Vector3.Up);
        }

        /// <summary>
        /// Rotates the entity to look at another entity
        /// </summary>
        /// <param name="target">The entity to look at</param>
        /// <param name="worldUp">The up direction (defaults to Vector3.Up)</param>
        public void LookAt(Entity target, Vector3? worldUp = null)
        {
            if (target == null || !target.IsValid)
                return;

            LookAt(target.Transform.Position, worldUp);
        }

        /// <summary>
        /// Transforms a point from local space to world space
        /// </summary>
        /// <param name="localPoint">The point in local space</param>
        /// <returns>The point in world space</returns>
        public Vector3 TransformPoint(Vector3 localPoint)
        {
            // Apply scale, rotation, then translation
            Vector3 scaled = localPoint * LocalScale;
            Vector3 rotated = Rotation * scaled;
            return rotated + Position;
        }

        /// <summary>
        /// Transforms a point from world space to local space
        /// </summary>
        /// <param name="worldPoint">The point in world space</param>
        /// <returns>The point in local space</returns>
        public Vector3 InverseTransformPoint(Vector3 worldPoint)
        {
            // Inverse of TransformPoint: subtract position, inverse rotate, inverse scale
            Vector3 translated = worldPoint - Position;
            Vector3 rotated = Rotation.Inverse * translated;
            return rotated / LocalScale;
        }

        /// <summary>
        /// Transforms a direction from local space to world space (ignores position and scale)
        /// </summary>
        /// <param name="localDirection">The direction in local space</param>
        /// <returns>The direction in world space</returns>
        public Vector3 TransformDirection(Vector3 localDirection)
        {
            return Rotation * localDirection;
        }

        /// <summary>
        /// Transforms a direction from world space to local space (ignores position and scale)
        /// </summary>
        /// <param name="worldDirection">The direction in world space</param>
        /// <returns>The direction in local space</returns>
        public Vector3 InverseTransformDirection(Vector3 worldDirection)
        {
            return Rotation.Inverse * worldDirection;
        }

        #endregion

        #region String

        public override string ToString()
        {
            if (!IsValid)
                return "Transform(Invalid)";
            return $"Transform(Position={Position}, Rotation={EulerAngles}, Scale={UniformScale})";
        }

        #endregion
    }
}
