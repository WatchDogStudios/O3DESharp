/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using Coral.Managed.Interop;

namespace O3DE
{
    /// <summary>
    /// Represents an O3DE Entity in managed code. 
    /// An entity is a container for components that define its behavior and properties.
    /// </summary>
    public class Entity : IEquatable<Entity>
    {
        /// <summary>
        /// Invalid entity ID constant
        /// </summary>
        public const ulong InvalidId = 0;

        /// <summary>
        /// The native entity ID
        /// </summary>
        private readonly ulong m_id;

        /// <summary>
        /// Cached transform component (lazily initialized)
        /// </summary>
        private Transform? m_transform;

        #region Constructors

        /// <summary>
        /// Creates a new Entity wrapper for the specified entity ID
        /// </summary>
        /// <param name="entityId">The native entity ID</param>
        public Entity(ulong entityId)
        {
            m_id = entityId;
        }

        /// <summary>
        /// Creates an invalid entity wrapper
        /// </summary>
        public Entity()
        {
            m_id = InvalidId;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the native entity ID
        /// </summary>
        public ulong Id => m_id;

        /// <summary>
        /// Gets whether this entity reference is valid (exists in the engine)
        /// </summary>
        public bool IsValid
        {
            get
            {
                if (m_id == InvalidId) return false;
                unsafe { return InternalCalls.Entity_IsValid(m_id); }
            }
        }

        /// <summary>
        /// Gets or sets the name of this entity
        /// </summary>
        public string Name
        {
            get
            {
                if (!IsValid)
                    return string.Empty;
                unsafe { return InternalCalls.Entity_GetName(m_id); }
            }
            set
            {
                if (IsValid)
                    unsafe { InternalCalls.Entity_SetName(m_id, value ?? string.Empty); }
            }
        }

        /// <summary>
        /// Gets whether this entity is currently active
        /// </summary>
        public bool IsActive
        {
            get
            {
                if (!IsValid)
                    return false;
                unsafe { return InternalCalls.Entity_IsActive(m_id); }
            }
        }

        /// <summary>
        /// Gets the Transform component of this entity.
        /// This is cached for performance.
        /// </summary>
        public Transform Transform
        {
            get
            {
                m_transform ??= new Transform(this);
                return m_transform;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Activates this entity (enables all components)
        /// </summary>
        public void Activate()
        {
            if (IsValid)
            {
                unsafe { InternalCalls.Entity_Activate(m_id); }
            }
        }

        /// <summary>
        /// Deactivates this entity (disables all components)
        /// </summary>
        public void Deactivate()
        {
            if (IsValid)
            {
                unsafe { InternalCalls.Entity_Deactivate(m_id); }
            }
        }

        /// <summary>
        /// Destroys this entity. After calling this, the entity reference becomes invalid.
        /// </summary>
        public void Destroy()
        {
            if (IsValid)
            {
                unsafe { InternalCalls.Entity_Destroy(m_id); }
            }
        }

        /// <summary>
        /// Gets all child entities of this entity.
        /// </summary>
        /// <returns>A list of child Entity objects</returns>
        public List<Entity> GetChildren()
        {
            var children = new List<Entity>();
            if (!IsValid) return children;

            int count;
            unsafe { count = InternalCalls.Entity_GetChildCount(m_id); }

            for (int i = 0; i < count; i++)
            {
                ulong childId;
                unsafe { childId = InternalCalls.Entity_GetChildAtIndex(m_id, i); }
                if (childId != InvalidId)
                {
                    children.Add(new Entity(childId));
                }
            }
            return children;
        }

        /// <summary>
        /// Gets the number of child entities.
        /// </summary>
        public int ChildCount
        {
            get
            {
                if (!IsValid) return 0;
                unsafe { return InternalCalls.Entity_GetChildCount(m_id); }
            }
        }

        /// <summary>
        /// Finds a direct child entity by name.
        /// </summary>
        /// <param name="childName">The name of the child entity to find</param>
        /// <returns>The child Entity, or null if not found</returns>
        public Entity? FindChild(string childName)
        {
            foreach (var child in GetChildren())
            {
                if (child.Name == childName)
                    return child;
            }
            return null;
        }

        /// <summary>
        /// Checks if this entity has a component of the specified type
        /// </summary>
        /// <param name="componentTypeName">The fully qualified component type name</param>
        /// <returns>True if the entity has the component</returns>
        public bool HasComponent(string componentTypeName)
        {
            if (!IsValid)
                return false;
            unsafe { return InternalCalls.Component_HasComponent(m_id, componentTypeName); }
        }

        /// <summary>
        /// Checks if this entity has a component of type T
        /// </summary>
        /// <typeparam name="T">The component type to check for</typeparam>
        /// <returns>True if the entity has the component</returns>
        public bool HasComponent<T>() where T : class
        {
            return HasComponent(typeof(T).FullName ?? typeof(T).Name);
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Finds an entity in the scene by name.
        /// </summary>
        /// <param name="name">The name of the entity to find</param>
        /// <returns>The Entity if found, or null if no entity with that name exists</returns>
        public static Entity? Find(string name)
        {
            ulong id;
            unsafe { id = InternalCalls.Entity_FindByName(name); }
            if (id == InvalidId)
                return null;
            return new Entity(id);
        }

        /// <summary>
        /// Creates an Entity wrapper from a native entity ID
        /// </summary>
        /// <param name="entityId">The native entity ID</param>
        /// <returns>An Entity wrapper, or null if the ID is invalid</returns>
        public static Entity? FromId(ulong entityId)
        {
            if (entityId == InvalidId)
                return null;

            bool isValid;
            unsafe { isValid = InternalCalls.Entity_IsValid(entityId); }
            if (!isValid)
                return null;

            return new Entity(entityId);
        }

        /// <summary>
        /// Returns an invalid entity reference
        /// </summary>
        public static Entity Null => new Entity(InvalidId);

        #endregion

        #region Equality

        public bool Equals(Entity? other)
        {
            if (other is null)
                return false;
            return m_id == other.m_id;
        }

        public override bool Equals(object? obj)
        {
            return obj is Entity other && Equals(other);
        }

        public override int GetHashCode()
        {
            return m_id.GetHashCode();
        }

        public static bool operator ==(Entity? left, Entity? right)
        {
            if (left is null)
                return right is null;
            return left.Equals(right);
        }

        public static bool operator !=(Entity? left, Entity? right)
        {
            return !(left == right);
        }

        #endregion

        #region Conversions

        /// <summary>
        /// Implicit conversion from Entity to bool (checks validity)
        /// </summary>
        public static implicit operator bool(Entity? entity)
        {
            return entity != null && entity.IsValid;
        }

        /// <summary>
        /// Implicit conversion from ulong to Entity
        /// </summary>
        public static implicit operator Entity(ulong entityId)
        {
            return new Entity(entityId);
        }

        /// <summary>
        /// Implicit conversion from Entity to ulong
        /// </summary>
        public static implicit operator ulong(Entity entity)
        {
            return entity?.m_id ?? InvalidId;
        }

        #endregion

        #region String

        public override string ToString()
        {
            if (!IsValid)
                return $"Entity(Invalid, Id={m_id})";
            return $"Entity(\"{Name}\", Id={m_id})";
        }

        #endregion
    }
}
