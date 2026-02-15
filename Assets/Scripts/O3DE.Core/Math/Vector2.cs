/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Runtime.InteropServices;

namespace O3DE
{
    /// <summary>
    /// A 2D vector with float components.
    /// Layout matches O3DESharp interop structures in C++ for seamless interop.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Vector2 : IEquatable<Vector2>
    {
        /// <summary>X component of the vector</summary>
        public float X;

        /// <summary>Y component of the vector</summary>
        public float Y;

        #region Static Properties

        /// <summary>Returns Vector2(0, 0)</summary>
        public static Vector2 Zero => new Vector2(0f, 0f);

        /// <summary>Returns Vector2(1, 1)</summary>
        public static Vector2 One => new Vector2(1f, 1f);

        /// <summary>Returns Vector2(1, 0)</summary>
        public static Vector2 Right => new Vector2(1f, 0f);

        /// <summary>Returns Vector2(-1, 0)</summary>
        public static Vector2 Left => new Vector2(-1f, 0f);

        /// <summary>Returns Vector2(0, 1)</summary>
        public static Vector2 Up => new Vector2(0f, 1f);

        /// <summary>Returns Vector2(0, -1)</summary>
        public static Vector2 Down => new Vector2(0f, -1f);

        #endregion

        #region Constructors

        /// <summary>
        /// Create a new Vector2 with the specified components
        /// </summary>
        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        /// <summary>
        /// Create a new Vector2 with all components set to the same value
        /// </summary>
        public Vector2(float value)
        {
            X = value;
            Y = value;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Returns the length (magnitude) of this vector
        /// </summary>
        public float Magnitude => MathF.Sqrt(X * X + Y * Y);

        /// <summary>
        /// Returns the squared length of this vector (faster than Magnitude)
        /// </summary>
        public float SqrMagnitude => X * X + Y * Y;

        /// <summary>
        /// Returns this vector with a magnitude of 1
        /// </summary>
        public Vector2 Normalized
        {
            get
            {
                float mag = Magnitude;
                if (mag > 1e-6f)
                {
                    return new Vector2(X / mag, Y / mag);
                }
                return Zero;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Normalizes this vector in place
        /// </summary>
        public void Normalize()
        {
            float mag = Magnitude;
            if (mag > 1e-6f)
            {
                X /= mag;
                Y /= mag;
            }
            else
            {
                X = 0;
                Y = 0;
            }
        }

        /// <summary>
        /// Set all components of this vector
        /// </summary>
        public void Set(float x, float y)
        {
            X = x;
            Y = y;
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Returns the dot product of two vectors
        /// </summary>
        public static float Dot(Vector2 a, Vector2 b)
        {
            return a.X * b.X + a.Y * b.Y;
        }

        /// <summary>
        /// Returns the distance between two points
        /// </summary>
        public static float Distance(Vector2 a, Vector2 b)
        {
            return (a - b).Magnitude;
        }

        /// <summary>
        /// Linearly interpolates between two vectors
        /// </summary>
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new Vector2(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t
            );
        }

        /// <summary>
        /// Returns a vector with the minimum components of both vectors
        /// </summary>
        public static Vector2 Min(Vector2 a, Vector2 b)
        {
            return new Vector2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
        }

        /// <summary>
        /// Returns a vector with the maximum components of both vectors
        /// </summary>
        public static Vector2 Max(Vector2 a, Vector2 b)
        {
            return new Vector2(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        }

        #endregion

        #region Operators

        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.X + b.X, a.Y + b.Y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.X - b.X, a.Y - b.Y);
        public static Vector2 operator -(Vector2 a) => new Vector2(-a.X, -a.Y);
        public static Vector2 operator *(Vector2 a, float d) => new Vector2(a.X * d, a.Y * d);
        public static Vector2 operator *(float d, Vector2 a) => new Vector2(a.X * d, a.Y * d);
        public static Vector2 operator /(Vector2 a, float d) => new Vector2(a.X / d, a.Y / d);
        public static bool operator ==(Vector2 a, Vector2 b) => a.Equals(b);
        public static bool operator !=(Vector2 a, Vector2 b) => !a.Equals(b);

        #endregion

        #region Equality

        public bool Equals(Vector2 other)
        {
            const float epsilon = 1e-6f;
            return MathF.Abs(X - other.X) < epsilon &&
                   MathF.Abs(Y - other.Y) < epsilon;
        }

        public override bool Equals(object? obj)
        {
            return obj is Vector2 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y);
        }

        #endregion

        #region String

        public override string ToString()
        {
            return $"({X:F3}, {Y:F3})";
        }

        #endregion
    }
}
