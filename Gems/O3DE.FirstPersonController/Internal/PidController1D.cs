/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;

namespace O3DE.FirstPersonController.Internal;

/// <summary>
/// Scalar PID controller used by the crouch-height transition.
/// Struct-typed so it can live as a member field with zero GC
/// pressure. Includes an integral clamp (IntegralMin/IntegralMax) to
/// prevent windup under sustained error.
/// </summary>
public struct PidController1D
{
    public float Kp;
    public float Ki;
    public float Kd;

    /// <summary>
    /// Lower bound for the accumulated integral. Defaults to
    /// -float.MaxValue (effectively unclamped); set to a finite value
    /// for windup protection.
    /// </summary>
    public float IntegralMin;

    /// <summary>
    /// Upper bound for the accumulated integral. Defaults to
    /// +float.MaxValue (effectively unclamped); set to a finite value
    /// for windup protection.
    /// </summary>
    public float IntegralMax;

    private float m_integral;
    private float m_lastError;

    /// <summary>
    /// Compute the next output. error = (target - current); dt is the
    /// elapsed time since the previous Step call.
    /// </summary>
    public float Step(float error, float dt)
    {
        // Default-constructed struct has IntegralMin = IntegralMax = 0,
        // which would clamp away every integral contribution. Detect
        // that and treat it as "unclamped".
        float lo = IntegralMin == 0f && IntegralMax == 0f ? float.MinValue : IntegralMin;
        float hi = IntegralMin == 0f && IntegralMax == 0f ? float.MaxValue : IntegralMax;

        m_integral = Math.Clamp(m_integral + error * dt, lo, hi);
        float derivative = dt > 0f ? (error - m_lastError) / dt : 0f;
        m_lastError = error;
        return Kp * error + Ki * m_integral + Kd * derivative;
    }

    /// <summary>
    /// Zero internal accumulators (integral + last-error). Call on
    /// state-machine transitions where the previous error history is
    /// no longer meaningful (e.g., crouch was interrupted).
    /// </summary>
    public void Reset()
    {
        m_integral = 0f;
        m_lastError = 0f;
    }
}
