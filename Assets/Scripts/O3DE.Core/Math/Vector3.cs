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
    /// A 3D vector with float components.
    /// Layout matches O3DESharp::InteropVector3 in C++ for seamless interop.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Vector3 : IEquatable<Vector3>
    {
        /// <summary>X component of the vector</summary>
        public float X;

        /// <summary>Y component of the vector</summary>
        public float Y;

        /// <summary>Z component of the vector</summary>
        public float Z;

        #region Static Properties

        /// <summary>Returns Vector3(0, 0, 0)</summary>
        public static Vector3 Zero => new Vector3(0f, 0f, 0f);

        /// <summary>Returns Vector3(1, 1, 1)</summary>
        public static Vector3 One => new Vector3(1f, 1f, 1f);

        /// <summary>Returns Vector3(1, 0, 0) - Right in O3DE coordinate system</summary>
        public static Vector3 Right => new Vector3(1f, 0f, 0f);

        /// <summary>Returns Vector3(-1, 0, 0)</summary>
        public static Vector3 Left => new Vector3(-1f, 0f, 0f);

        /// <summary>Returns Vector3(0, 1, 0) - Forward in O3DE coordinate system</summary>
        public static Vector3 Forward => new Vector3(0f, 1f, 0f);

        /// <summary>Returns Vector3(0, -1, 0)</summary>
        public static Vector3 Back => new Vector3(0f, -1f, 0f);

        /// <summary>Returns Vector3(0, 0, 1) - Up in O3DE coordinate system</summary>
        public static Vector3 Up => new Vector3(0f, 0f, 1f);

        /// <summary>Returns Vector3(0, 0, -1)</summary>
        public static Vector3 Down => new Vector3(0f, 0f, -1f);

        #endregion

        #region Constructors

        /// <summary>
        /// Create a new Vector3 with the specified components
        /// </summary>
        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>
        /// Create a new Vector3 with all components set to the same value
        /// </summary>
        public Vector3(float value)
        {
            X = value;
            Y = value;
            Z = value;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Returns the length (magnitude) of this vector
        /// </summary>
        public float Magnitude => MathF.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>
        /// Returns the squared length of this vector (faster than Magnitude)
        /// </summary>
        public float SqrMagnitude => X * X + Y * Y + Z * Z;

        /// <summary>
        /// Returns this vector with a magnitude of 1
        /// </summary>
        public Vector3 Normalized
        {
            get
            {
                float mag = Magnitude;
                if (mag > 1e-6f)
                {
                    return new Vector3(X / mag, Y / mag, Z / mag);
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
                Z /= mag;
            }
            else
            {
                X = 0;
                Y = 0;
                Z = 0;
            }
        }

        /// <summary>
        /// Set all components of this vector
        /// </summary>
        public void Set(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Returns the dot product of two vectors
        /// </summary>
        public static float Dot(Vector3 a, Vector3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        /// <summary>
        /// Returns the cross product of two vectors
        /// </summary>
        public static Vector3 Cross(Vector3 a, Vector3 b)
        {
            return new Vector3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X
            );
        }

        /// <summary>
        /// Returns the distance between two points
        /// </summary>
        public static float Distance(Vector3 a, Vector3 b)
        {
            return (a - b).Magnitude;
        }

        /// <summary>
        /// Returns the squared distance between two points (faster than Distance)
        /// </summary>
        public static float SqrDistance(Vector3 a, Vector3 b)
        {
            return (a - b).SqrMagnitude;
        }

        /// <summary>
        /// Linearly interpolates between two vectors
        /// </summary>
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new Vector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }

        /// <summary>
        /// Linearly interpolates between two vectors without clamping t
        /// </summary>
        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }

        /// <summary>
        /// Returns a vector with the minimum components of both vectors
        /// </summary>
        public static Vector3 Min(Vector3 a, Vector3 b)
        {
            return new Vector3(
                MathF.Min(a.X, b.X),
                MathF.Min(a.Y, b.Y),
                MathF.Min(a.Z, b.Z)
            );
        }

        /// <summary>
        /// Returns a vector with the maximum components of both vectors
        /// </summary>
        public static Vector3 Max(Vector3 a, Vector3 b)
        {
            return new Vector3(
                MathF.Max(a.X, b.X),
                MathF.Max(a.Y, b.Y),
                MathF.Max(a.Z, b.Z)
            );
        }

        /// <summary>
        /// Returns the angle in degrees between two vectors
        /// </summary>
        public static float Angle(Vector3 from, Vector3 to)
        {
            float dot = Dot(from.Normalized, to.Normalized);
            dot = Math.Clamp(dot, -1f, 1f);
            return MathF.Acos(dot) * (180f / MathF.PI);
        }

        /// <summary>
        /// Projects a vector onto another vector
        /// </summary>
        public static Vector3 Project(Vector3 vector, Vector3 onNormal)
        {
            float sqrMag = onNormal.SqrMagnitude;
            if (sqrMag < 1e-6f)
                return Zero;
            return onNormal * (Dot(vector, onNormal) / sqrMag);
        }

        /// <summary>
        /// Projects a vector onto a plane defined by a normal
        /// </summary>
        public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
        {
            return vector - Project(vector, planeNormal);
        }

        /// <summary>
        /// Reflects a vector off a surface with the specified normal
        /// </summary>
        public static Vector3 Reflect(Vector3 direction, Vector3 normal)
        {
            return direction - 2f * Dot(direction, normal) * normal;
        }

        /// <summary>
        /// Clamps the magnitude of a vector
        /// </summary>
        public static Vector3 ClampMagnitude(Vector3 vector, float maxLength)
        {
            float sqrMag = vector.SqrMagnitude;
            if (sqrMag > maxLength * maxLength)
            {
                float mag = MathF.Sqrt(sqrMag);
                return new Vector3(
                    vector.X / mag * maxLength,
                    vector.Y / mag * maxLength,
                    vector.Z / mag * maxLength
                );
            }
            return vector;
        }

        #endregion

        #region Operators

        public static Vector3 operator +(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static Vector3 operator -(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static Vector3 operator -(Vector3 a)
        {
            return new Vector3(-a.X, -a.Y, -a.Z);
        }

        public static Vector3 operator *(Vector3 a, float d)
        {
            return new Vector3(a.X * d, a.Y * d, a.Z * d);
        }

        public static Vector3 operator *(float d, Vector3 a)
        {
            return new Vector3(a.X * d, a.Y * d, a.Z * d);
        }

        public static Vector3 operator *(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        }

        public static Vector3 operator /(Vector3 a, float d)
        {
            return new Vector3(a.X / d, a.Y / d, a.Z / d);
        }

        public static Vector3 operator /(Vector3 a, Vector3 b)
        {
            return new Vector3(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
        }

        public static bool operator ==(Vector3 a, Vector3 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Vector3 a, Vector3 b)
        {
            return !a.Equals(b);
        }

        #endregion

        #region Equality

        public bool Equals(Vector3 other)
        {
            const float epsilon = 1e-6f;
            return MathF.Abs(X - other.X) < epsilon &&
                   MathF.Abs(Y - other.Y) < epsilon &&
                   MathF.Abs(Z - other.Z) < epsilon;
        }

        public override bool Equals(object? obj)
        {
            return obj is Vector3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }

        #endregion

        #region String

        public override string ToString()
        {
            return $"({X:F3}, {Y:F3}, {Z:F3})";
        }

        public string ToString(string format)
        {
            return $"({X.ToString(format)}, {Y.ToString(format)}, {Z.ToString(format)})";
        }

        #endregion
    }
}
