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
    /// Provides access to time-related functionality in O3DE.
    /// All time values are in seconds unless otherwise specified.
    /// </summary>
    public static class Time
    {
        #region Properties

        /// <summary>
        /// Gets the time in seconds since the last frame (delta time).
        /// Use this to make movement and other updates frame-rate independent.
        /// </summary>
        public static float DeltaTime
        {
            get { unsafe { return InternalCalls.Time_GetDeltaTime(); } }
        }

        /// <summary>
        /// Gets the total time in seconds since the application started.
        /// </summary>
        public static float TotalTime
        {
            get { unsafe { return InternalCalls.Time_GetTotalTime(); } }
        }

        /// <summary>
        /// Gets or sets the time scale (simulation speed multiplier).
        /// A value of 1.0 is normal speed, 0.5 is half speed, 2.0 is double speed.
        /// Setting this to 0 effectively pauses the simulation.
        /// </summary>
        public static float TimeScale
        {
            get { unsafe { return InternalCalls.Time_GetTimeScale(); } }
            set { unsafe { InternalCalls.Time_SetTimeScale(value); } }
        }

        /// <summary>
        /// Gets the current frame count since the application started.
        /// </summary>
        public static ulong FrameCount
        {
            get { unsafe { return InternalCalls.Time_GetFrameCount(); } }
        }

        /// <summary>
        /// Gets the scaled delta time (DeltaTime * TimeScale).
        /// This is useful for time-scaled gameplay logic.
        /// </summary>
        public static float ScaledDeltaTime => DeltaTime * TimeScale;

        /// <summary>
        /// Gets the unscaled delta time (not affected by TimeScale).
        /// Use this for UI and other elements that shouldn't be affected by time scale.
        /// </summary>
        public static float UnscaledDeltaTime => DeltaTime;

        /// <summary>
        /// Gets the frames per second based on the current delta time.
        /// </summary>
        public static float FPS
        {
            get
            {
                float dt = DeltaTime;
                return dt > 0f ? 1f / dt : 0f;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Pauses the simulation by setting TimeScale to 0.
        /// </summary>
        public static void Pause()
        {
            TimeScale = 0f;
        }

        /// <summary>
        /// Resumes the simulation by setting TimeScale to 1.
        /// </summary>
        public static void Resume()
        {
            TimeScale = 1f;
        }

        /// <summary>
        /// Checks if the simulation is paused (TimeScale is 0).
        /// </summary>
        public static bool IsPaused => TimeScale <= 0f;

        /// <summary>
        /// Smoothly interpolates a value over time (useful for frame-rate independent lerping).
        /// </summary>
        /// <param name="current">The current value</param>
        /// <param name="target">The target value</param>
        /// <param name="smoothTime">The approximate time it takes to reach the target</param>
        /// <returns>The smoothed value</returns>
        public static float SmoothDamp(float current, float target, float smoothTime)
        {
            if (smoothTime <= 0f)
                return target;

            float omega = 2f / smoothTime;
            float x = omega * DeltaTime;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float temp = (change + omega * change * DeltaTime) * exp;
            return target + temp;
        }

        /// <summary>
        /// Returns a value that oscillates between 0 and 1 over time using a sine wave.
        /// </summary>
        /// <param name="frequency">The frequency of oscillation (cycles per second)</param>
        /// <returns>A value between 0 and 1</returns>
        public static float PingPong(float frequency = 1f)
        {
            return (MathF.Sin(TotalTime * frequency * MathF.PI * 2f) + 1f) * 0.5f;
        }

        /// <summary>
        /// Returns a value that oscillates between min and max over time.
        /// </summary>
        /// <param name="min">The minimum value</param>
        /// <param name="max">The maximum value</param>
        /// <param name="frequency">The frequency of oscillation (cycles per second)</param>
        /// <returns>A value between min and max</returns>
        public static float PingPong(float min, float max, float frequency = 1f)
        {
            return min + (max - min) * PingPong(frequency);
        }

        #endregion

        #region Timer Utilities

        /// <summary>
        /// Checks if a certain amount of time has passed since a given start time.
        /// </summary>
        /// <param name="startTime">The start time (from TotalTime)</param>
        /// <param name="duration">The duration to check</param>
        /// <returns>True if the duration has passed</returns>
        public static bool HasElapsed(float startTime, float duration)
        {
            return TotalTime - startTime >= duration;
        }

        /// <summary>
        /// Gets the elapsed time since a given start time.
        /// </summary>
        /// <param name="startTime">The start time (from TotalTime)</param>
        /// <returns>The elapsed time in seconds</returns>
        public static float ElapsedSince(float startTime)
        {
            return TotalTime - startTime;
        }

        /// <summary>
        /// Gets the remaining time until a deadline.
        /// </summary>
        /// <param name="startTime">The start time (from TotalTime)</param>
        /// <param name="duration">The total duration</param>
        /// <returns>The remaining time in seconds (0 if past deadline)</returns>
        public static float RemainingTime(float startTime, float duration)
        {
            float remaining = duration - (TotalTime - startTime);
            return remaining > 0f ? remaining : 0f;
        }

        /// <summary>
        /// Gets the progress (0 to 1) of a duration since a start time.
        /// </summary>
        /// <param name="startTime">The start time (from TotalTime)</param>
        /// <param name="duration">The total duration</param>
        /// <returns>A value from 0 (start) to 1 (complete)</returns>
        public static float Progress(float startTime, float duration)
        {
            if (duration <= 0f)
                return 1f;
            float progress = (TotalTime - startTime) / duration;
            return Math.Clamp(progress, 0f, 1f);
        }

        #endregion
    }
}
