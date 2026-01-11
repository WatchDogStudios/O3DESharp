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
    /// A quaternion representing a rotation.
    /// Layout matches O3DESharp::InteropQuaternion in C++ for seamless interop.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Quaternion : IEquatable<Quaternion>
    {
        /// <summary>X component of the quaternion</summary>
        public float X;

        /// <summary>Y component of the quaternion</summary>
        public float Y;

        /// <summary>Z component of the quaternion</summary>
        public float Z;

        /// <summary>W component of the quaternion (scalar part)</summary>
        public float W;

        #region Static Properties

        /// <summary>Returns the identity quaternion (no rotation)</summary>
        public static Quaternion Identity => new Quaternion(0f, 0f, 0f, 1f);

        #endregion

        #region Constructors

        /// <summary>
        /// Create a new Quaternion with the specified components
        /// </summary>
        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Returns the length (magnitude) of this quaternion
        /// </summary>
        public float Magnitude => MathF.Sqrt(X * X + Y * Y + Z * Z + W * W);

        /// <summary>
        /// Returns the squared length of this quaternion (faster than Magnitude)
        /// </summary>
        public float SqrMagnitude => X * X + Y * Y + Z * Z + W * W;

        /// <summary>
        /// Returns this quaternion with a magnitude of 1
        /// </summary>
        public Quaternion Normalized
        {
            get
            {
                float mag = Magnitude;
                if (mag > 1e-6f)
                {
                    return new Quaternion(X / mag, Y / mag, Z / mag, W / mag);
                }
                return Identity;
            }
        }

        /// <summary>
        /// Returns the conjugate of this quaternion
        /// </summary>
        public Quaternion Conjugate => new Quaternion(-X, -Y, -Z, W);

        /// <summary>
        /// Returns the inverse of this quaternion
        /// </summary>
        public Quaternion Inverse
        {
            get
            {
                float sqrMag = SqrMagnitude;
                if (sqrMag > 1e-6f)
                {
                    float invSqrMag = 1f / sqrMag;
                    return new Quaternion(-X * invSqrMag, -Y * invSqrMag, -Z * invSqrMag, W * invSqrMag);
                }
                return Identity;
            }
        }

        /// <summary>
        /// Returns the euler angles (in degrees) representing this rotation
        /// </summary>
        public Vector3 EulerAngles
        {
            get
            {
                Vector3 euler = new Vector3();

                // Roll (X-axis rotation)
                float sinr_cosp = 2f * (W * X + Y * Z);
                float cosr_cosp = 1f - 2f * (X * X + Y * Y);
                euler.X = MathF.Atan2(sinr_cosp, cosr_cosp) * (180f / MathF.PI);

                // Pitch (Y-axis rotation)
                float sinp = 2f * (W * Y - Z * X);
                if (MathF.Abs(sinp) >= 1f)
                    euler.Y = MathF.CopySign(90f, sinp);
                else
                    euler.Y = MathF.Asin(sinp) * (180f / MathF.PI);

                // Yaw (Z-axis rotation)
                float siny_cosp = 2f * (W * Z + X * Y);
                float cosy_cosp = 1f - 2f * (Y * Y + Z * Z);
                euler.Z = MathF.Atan2(siny_cosp, cosy_cosp) * (180f / MathF.PI);

                return euler;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Normalizes this quaternion in place
        /// </summary>
        public void Normalize()
        {
            float mag = Magnitude;
            if (mag > 1e-6f)
            {
                X /= mag;
                Y /= mag;
                Z /= mag;
                W /= mag;
            }
            else
            {
                X = 0;
                Y = 0;
                Z = 0;
                W = 1;
            }
        }

        /// <summary>
        /// Set all components of this quaternion
        /// </summary>
        public void Set(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Creates a quaternion from euler angles (in degrees)
        /// </summary>
        public static Quaternion FromEuler(float x, float y, float z)
        {
            // Convert to radians
            float rx = x * (MathF.PI / 180f) * 0.5f;
            float ry = y * (MathF.PI / 180f) * 0.5f;
            float rz = z * (MathF.PI / 180f) * 0.5f;

            float sinX = MathF.Sin(rx);
            float cosX = MathF.Cos(rx);
            float sinY = MathF.Sin(ry);
            float cosY = MathF.Cos(ry);
            float sinZ = MathF.Sin(rz);
            float cosZ = MathF.Cos(rz);

            return new Quaternion(
                sinX * cosY * cosZ - cosX * sinY * sinZ,
                cosX * sinY * cosZ + sinX * cosY * sinZ,
                cosX * cosY * sinZ - sinX * sinY * cosZ,
                cosX * cosY * cosZ + sinX * sinY * sinZ
            );
        }

        /// <summary>
        /// Creates a quaternion from euler angles (in degrees)
        /// </summary>
        public static Quaternion FromEuler(Vector3 euler)
        {
            return FromEuler(euler.X, euler.Y, euler.Z);
        }

        /// <summary>
        /// Creates a quaternion representing a rotation around an axis
        /// </summary>
        /// <param name="axis">The axis to rotate around (should be normalized)</param>
        /// <param name="angleDegrees">The angle in degrees</param>
        public static Quaternion AngleAxis(float angleDegrees, Vector3 axis)
        {
            float halfAngle = angleDegrees * (MathF.PI / 180f) * 0.5f;
            float sin = MathF.Sin(halfAngle);
            float cos = MathF.Cos(halfAngle);

            return new Quaternion(
                axis.X * sin,
                axis.Y * sin,
                axis.Z * sin,
                cos
            );
        }

        /// <summary>
        /// Creates a quaternion that rotates from one direction to another
        /// </summary>
        public static Quaternion FromToRotation(Vector3 from, Vector3 to)
        {
            from = from.Normalized;
            to = to.Normalized;

            float dot = Vector3.Dot(from, to);

            if (dot > 0.99999f)
            {
                return Identity;
            }
            else if (dot < -0.99999f)
            {
                // Vectors are opposite, find an orthogonal axis
                Vector3 axis = Vector3.Cross(Vector3.Right, from);
                if (axis.SqrMagnitude < 1e-6f)
                    axis = Vector3.Cross(Vector3.Up, from);
                axis = axis.Normalized;
                return AngleAxis(180f, axis);
            }
            else
            {
                Vector3 axis = Vector3.Cross(from, to);
                float w = MathF.Sqrt(from.SqrMagnitude * to.SqrMagnitude) + dot;
                return new Quaternion(axis.X, axis.Y, axis.Z, w).Normalized;
            }
        }

        /// <summary>
        /// Creates a rotation that looks in the specified direction
        /// </summary>
        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            forward = forward.Normalized;
            Vector3 right = Vector3.Cross(up, forward).Normalized;
            up = Vector3.Cross(forward, right);

            float m00 = right.X;
            float m01 = right.Y;
            float m02 = right.Z;
            float m10 = up.X;
            float m11 = up.Y;
            float m12 = up.Z;
            float m20 = forward.X;
            float m21 = forward.Y;
            float m22 = forward.Z;

            float trace = m00 + m11 + m22;
            Quaternion q;

            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                q = new Quaternion(
                    (m12 - m21) / s,
                    (m20 - m02) / s,
                    (m01 - m10) / s,
                    0.25f * s
                );
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(
                    0.25f * s,
                    (m01 + m10) / s,
                    (m02 + m20) / s,
                    (m12 - m21) / s
                );
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion(
                    (m01 + m10) / s,
                    0.25f * s,
                    (m12 + m21) / s,
                    (m20 - m02) / s
                );
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion(
                    (m02 + m20) / s,
                    (m12 + m21) / s,
                    0.25f * s,
                    (m01 - m10) / s
                );
            }

            return q.Normalized;
        }

        /// <summary>
        /// Creates a rotation that looks in the specified direction with up as Vector3.Up
        /// </summary>
        public static Quaternion LookRotation(Vector3 forward)
        {
            return LookRotation(forward, Vector3.Up);
        }

        /// <summary>
        /// Returns the dot product of two quaternions
        /// </summary>
        public static float Dot(Quaternion a, Quaternion b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        }

        /// <summary>
        /// Returns the angle in degrees between two rotations
        /// </summary>
        public static float Angle(Quaternion a, Quaternion b)
        {
            float dot = MathF.Abs(Dot(a, b));
            dot = MathF.Min(dot, 1f);
            return 2f * MathF.Acos(dot) * (180f / MathF.PI);
        }

        /// <summary>
        /// Spherically interpolates between two quaternions
        /// </summary>
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return SlerpUnclamped(a, b, t);
        }

        /// <summary>
        /// Spherically interpolates between two quaternions without clamping t
        /// </summary>
        public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t)
        {
            float dot = Dot(a, b);

            // If negative dot, negate one quaternion to take shorter path
            if (dot < 0f)
            {
                b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
                dot = -dot;
            }

            // If quaternions are very close, use linear interpolation
            if (dot > 0.9995f)
            {
                return new Quaternion(
                    a.X + t * (b.X - a.X),
                    a.Y + t * (b.Y - a.Y),
                    a.Z + t * (b.Z - a.Z),
                    a.W + t * (b.W - a.W)
                ).Normalized;
            }

            float theta0 = MathF.Acos(dot);
            float theta = theta0 * t;
            float sinTheta = MathF.Sin(theta);
            float sinTheta0 = MathF.Sin(theta0);

            float s0 = MathF.Cos(theta) - dot * sinTheta / sinTheta0;
            float s1 = sinTheta / sinTheta0;

            return new Quaternion(
                s0 * a.X + s1 * b.X,
                s0 * a.Y + s1 * b.Y,
                s0 * a.Z + s1 * b.Z,
                s0 * a.W + s1 * b.W
            );
        }

        /// <summary>
        /// Linearly interpolates between two quaternions (not recommended, use Slerp)
        /// </summary>
        public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new Quaternion(
                a.X + t * (b.X - a.X),
                a.Y + t * (b.Y - a.Y),
                a.Z + t * (b.Z - a.Z),
                a.W + t * (b.W - a.W)
            ).Normalized;
        }

        /// <summary>
        /// Rotates a point by this quaternion
        /// </summary>
        public Vector3 RotatePoint(Vector3 point)
        {
            // q * v * q^-1
            float x2 = X * 2f;
            float y2 = Y * 2f;
            float z2 = Z * 2f;
            float xx = X * x2;
            float yy = Y * y2;
            float zz = Z * z2;
            float xy = X * y2;
            float xz = X * z2;
            float yz = Y * z2;
            float wx = W * x2;
            float wy = W * y2;
            float wz = W * z2;

            return new Vector3(
                (1f - (yy + zz)) * point.X + (xy - wz) * point.Y + (xz + wy) * point.Z,
                (xy + wz) * point.X + (1f - (xx + zz)) * point.Y + (yz - wx) * point.Z,
                (xz - wy) * point.X + (yz + wx) * point.Y + (1f - (xx + yy)) * point.Z
            );
        }

        #endregion

        #region Operators

        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
                a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z
            );
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            return rotation.RotatePoint(point);
        }

        public static bool operator ==(Quaternion a, Quaternion b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Quaternion a, Quaternion b)
        {
            return !a.Equals(b);
        }

        #endregion

        #region Equality

        public bool Equals(Quaternion other)
        {
            const float epsilon = 1e-6f;
            return MathF.Abs(X - other.X) < epsilon &&
                   MathF.Abs(Y - other.Y) < epsilon &&
                   MathF.Abs(Z - other.Z) < epsilon &&
                   MathF.Abs(W - other.W) < epsilon;
        }

        public override bool Equals(object? obj)
        {
            return obj is Quaternion other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z, W);
        }

        #endregion

        #region String

        public override string ToString()
        {
            return $"({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";
        }

        public string ToString(string format)
        {
            return $"({X.ToString(format)}, {Y.ToString(format)}, {Z.ToString(format)}, {W.ToString(format)})";
        }

        #endregion
    }
}
