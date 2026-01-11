/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Coral.Managed.Interop;

namespace O3DE
{
    /// <summary>
    /// Debug logging utilities for O3DE scripting.
    /// Messages are forwarded to the O3DE logging system.
    /// </summary>
    public static class Debug
    {
        /// <summary>
        /// Logs an informational message to the O3DE console.
        /// </summary>
        /// <param name="message">The message to log</param>
        public static void Log(string message)
        {
            unsafe { InternalCalls.Log_Info(message ?? "null"); }
        }

        /// <summary>
        /// Logs an informational message to the O3DE console.
        /// </summary>
        /// <param name="obj">The object to log (ToString() will be called)</param>
        public static void Log(object? obj)
        {
            unsafe { InternalCalls.Log_Info(obj?.ToString() ?? "null"); }
        }

        /// <summary>
        /// Logs a formatted informational message to the O3DE console.
        /// </summary>
        /// <param name="format">The format string</param>
        /// <param name="args">The format arguments</param>
        public static void Log(string format, params object[] args)
        {
            unsafe { InternalCalls.Log_Info(string.Format(format, args)); }
        }

        /// <summary>
        /// Logs a warning message to the O3DE console.
        /// </summary>
        /// <param name="message">The warning message</param>
        public static void LogWarning(string message)
        {
            unsafe { InternalCalls.Log_Warning(message ?? "null"); }
        }

        /// <summary>
        /// Logs a warning message to the O3DE console.
        /// </summary>
        /// <param name="obj">The object to log (ToString() will be called)</param>
        public static void LogWarning(object? obj)
        {
            unsafe { InternalCalls.Log_Warning(obj?.ToString() ?? "null"); }
        }

        /// <summary>
        /// Logs a formatted warning message to the O3DE console.
        /// </summary>
        /// <param name="format">The format string</param>
        /// <param name="args">The format arguments</param>
        public static void LogWarning(string format, params object[] args)
        {
            unsafe { InternalCalls.Log_Warning(string.Format(format, args)); }
        }

        /// <summary>
        /// Logs an error message to the O3DE console.
        /// </summary>
        /// <param name="message">The error message</param>
        public static void LogError(string message)
        {
            unsafe { InternalCalls.Log_Error(message ?? "null"); }
        }

        /// <summary>
        /// Logs an error message to the O3DE console.
        /// </summary>
        /// <param name="obj">The object to log (ToString() will be called)</param>
        public static void LogError(object? obj)
        {
            unsafe { InternalCalls.Log_Error(obj?.ToString() ?? "null"); }
        }

        /// <summary>
        /// Logs a formatted error message to the O3DE console.
        /// </summary>
        /// <param name="format">The format string</param>
        /// <param name="args">The format arguments</param>
        public static void LogError(string format, params object[] args)
        {
            unsafe { InternalCalls.Log_Error(string.Format(format, args)); }
        }

        /// <summary>
        /// Logs an exception to the O3DE console.
        /// </summary>
        /// <param name="exception">The exception to log</param>
        public static void LogException(Exception exception)
        {
            if (exception == null)
            {
                unsafe { InternalCalls.Log_Error("null exception"); }
                return;
            }

            unsafe
            {
                InternalCalls.Log_Error($"Exception: {exception.GetType().Name}: {exception.Message}");
                if (!string.IsNullOrEmpty(exception.StackTrace))
                {
                    InternalCalls.Log_Error($"Stack Trace:\n{exception.StackTrace}");
                }
            }

            if (exception.InnerException != null)
            {
                unsafe { InternalCalls.Log_Error("--- Inner Exception ---"); }
                LogException(exception.InnerException);
            }
        }

        /// <summary>
        /// Asserts that a condition is true. If false, logs an error with the specified message.
        /// </summary>
        /// <param name="condition">The condition to assert</param>
        /// <param name="message">The message to log if the assertion fails</param>
        [Conditional("DEBUG")]
        [Conditional("O3DE_DEBUG")]
        public static void Assert(bool condition, string? message = null)
        {
            if (!condition)
            {
                string assertMessage = string.IsNullOrEmpty(message)
                    ? "Assertion failed!"
                    : $"Assertion failed: {message}";
                unsafe { InternalCalls.Log_Error(assertMessage); }

#if DEBUG || O3DE_DEBUG
                // In debug builds, also throw to help catch issues
                throw new InvalidOperationException(assertMessage);
#endif
            }
        }

        /// <summary>
        /// Logs a message with caller information for debugging.
        /// </summary>
        /// <param name="message">The message to log</param>
        /// <param name="callerFilePath">Auto-filled by compiler</param>
        /// <param name="callerLineNumber">Auto-filled by compiler</param>
        /// <param name="callerMemberName">Auto-filled by compiler</param>
        public static void LogWithCaller(
            string message,
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0,
            [CallerMemberName] string callerMemberName = "")
        {
            string fileName = System.IO.Path.GetFileName(callerFilePath);
            unsafe { InternalCalls.Log_Info($"[{fileName}:{callerLineNumber} {callerMemberName}] {message}"); }
        }

        /// <summary>
        /// Logs a message only in debug builds.
        /// </summary>
        /// <param name="message">The message to log</param>
        [Conditional("DEBUG")]
        [Conditional("O3DE_DEBUG")]
        public static void LogDebug(string message)
        {
            unsafe { InternalCalls.Log_Info($"[DEBUG] {message}"); }
        }

        /// <summary>
        /// Logs a formatted message only in debug builds.
        /// </summary>
        /// <param name="format">The format string</param>
        /// <param name="args">The format arguments</param>
        [Conditional("DEBUG")]
        [Conditional("O3DE_DEBUG")]
        public static void LogDebug(string format, params object[] args)
        {
            unsafe { InternalCalls.Log_Info($"[DEBUG] {string.Format(format, args)}"); }
        }
    }
}
