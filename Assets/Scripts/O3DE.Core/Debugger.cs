/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Threading;

namespace O3DE
{
    /// <summary>
    /// Managed-side debugger helpers for O3DESharp scripts.
    ///
    /// Coral hosts the .NET runtime in-process with the O3DE editor (or game
    /// launcher), so any standard managed-mode debugger - Rider, Visual
    /// Studio, VS Code with the C# extension - can attach to the editor
    /// process via "Attach to Process" and hit breakpoints in your scripts.
    /// No special debug build of the gem is required: portable PDBs are
    /// shipped alongside the DLL by the Phase 16b DeployToBinScripts MSBuild
    /// target and Coral loads them automatically.
    ///
    /// This class is the small layer on top of <see cref="System.Diagnostics.Debugger"/>
    /// that gives script authors a more O3DE-flavored API.
    ///
    /// Typical use in a script:
    /// <code>
    /// public override void OnCreate()
    /// {
    ///     // Block until I attach my IDE - useful when the breakpoint I
    ///     // care about is in OnCreate itself.
    ///     Debugger.WaitForAttach(TimeSpan.FromSeconds(30));
    ///     Debug.Log("Debugger attached, continuing OnCreate");
    /// }
    /// </code>
    /// </summary>
    public static class Debugger
    {
        /// <summary>
        /// True when a managed debugger is currently attached to the
        /// hosting process.
        /// </summary>
        public static bool IsAttached => System.Diagnostics.Debugger.IsAttached;

        /// <summary>
        /// Block the calling thread until a managed debugger attaches to
        /// the process, or until <paramref name="timeout"/> elapses (default:
        /// no timeout - wait forever).
        ///
        /// Polls <see cref="System.Diagnostics.Debugger.IsAttached"/> every
        /// 100 ms. Returns immediately if a debugger is already attached.
        /// Returns <c>false</c> if the wait timed out, <c>true</c> if a
        /// debugger attached in time.
        ///
        /// Calling this on the main editor thread WILL freeze the editor
        /// while you wait - that's the whole point, since the alternative is
        /// the next frame races past your breakpoint. Use a finite timeout in
        /// production code so a forgotten WaitForAttach call doesn't lock up
        /// a player's machine on a Release build where IsAttached can never
        /// become true.
        ///
        /// In Release builds where hot-reload is off, this still works for
        /// attach-time debugging - the runtime PDB loading is independent of
        /// the host's hot-reload gate.
        /// </summary>
        /// <param name="timeout">Maximum time to wait. <see cref="TimeSpan.Zero"/>
        /// means "check once and return", a negative value or
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> means "wait forever".</param>
        /// <returns>True if a debugger was attached before the timeout; false otherwise.</returns>
        public static bool WaitForAttach(TimeSpan timeout = default)
        {
            // Default(TimeSpan) is Zero - treat as "wait forever". Callers
            // that genuinely want a single-shot check should use IsAttached.
            // Negative spans (e.g. Timeout.InfiniteTimeSpan) also mean infinite.
            bool waitForever = timeout <= TimeSpan.Zero;
            DateTime deadline = waitForever
                ? DateTime.MaxValue
                : DateTime.UtcNow + timeout;

            if (IsAttached)
            {
                return true;
            }

            // Log once so the user knows the editor is paused on purpose.
            Debug.Log(waitForever
                ? "[O3DESharp] Waiting for debugger to attach (no timeout)..."
                : $"[O3DESharp] Waiting for debugger to attach (timeout {timeout.TotalSeconds:F1}s)...");

            while (DateTime.UtcNow < deadline)
            {
                if (IsAttached)
                {
                    Debug.Log("[O3DESharp] Debugger attached, resuming script.");
                    return true;
                }
                Thread.Sleep(100);
            }

            Debug.LogWarning("[O3DESharp] WaitForAttach timed out; continuing without debugger.");
            return false;
        }

        /// <summary>
        /// Ask the OS to launch the JIT debugger registered for managed code
        /// (on Windows: the "Just-In-Time Debugger" picker). On platforms
        /// where this isn't supported, falls back to a no-op + warning log -
        /// the caller can still <see cref="WaitForAttach"/> for a manual
        /// attach.
        ///
        /// Useful as a one-liner at the top of an OnCreate that you suspect
        /// is misbehaving: <c>O3DE.Debugger.Launch();</c>.
        /// </summary>
        public static void Launch()
        {
            if (IsAttached)
            {
                return;
            }
            try
            {
                if (!System.Diagnostics.Debugger.Launch())
                {
                    Debug.LogWarning("[O3DESharp] Debugger.Launch returned false; use Attach to Process from your IDE.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[O3DESharp] Debugger.Launch threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Issue a managed breakpoint (<see cref="System.Diagnostics.Debugger.Break"/>)
        /// when a debugger is attached. Silent no-op otherwise.
        ///
        /// Use this instead of a bare <c>System.Diagnostics.Debugger.Break</c>
        /// call when you want to ship the line in Release without crashing
        /// players whose machines have no JIT debugger registered.
        /// </summary>
        public static void Break()
        {
            if (IsAttached)
            {
                System.Diagnostics.Debugger.Break();
            }
        }

        /// <summary>
        /// Phase 17b gate called by C++ <c>CSharpScriptComponent::Activate</c>
        /// before user <c>OnCreate</c> when
        /// <c>/O3DE/O3DESharp/WaitForDebuggerOnActivate</c> is enabled. The
        /// C++ side mirrors the setting into the
        /// <c>O3DESHARP_WAIT_FOR_DEBUGGER</c> environment variable so we can
        /// read it here without a managed-side settings-registry round-trip.
        /// Additionally, the optional
        /// <c>O3DESHARP_WAIT_FOR_DEBUGGER_TIMEOUT_MS</c> env var overrides
        /// the default 120s timeout for users who need more time to
        /// confirm an IDE attach prompt (or zero to wait forever).
        ///
        /// Static (not instance) so the C++ side can invoke it via
        /// <c>Coral::Type::InvokeStaticMethod</c> on <c>O3DE.Debugger</c>
        /// directly, without having to look up the right type-with-method
        /// for an arbitrary user script.
        ///
        /// No-op when a debugger is already attached or when the env var
        /// isn't set to "1". Bounded by a 120 second timeout by default so
        /// a forgotten toggle on a CI runner without a debugger can never
        /// deadlock - but generous enough for users who need to click an
        /// "Attach?" confirmation prompt in Rider / VS / VS Code after
        /// the auto-attach URL fires.
        /// </summary>
        public static void WaitForAttachIfRequested()
        {
            if (IsAttached)
            {
                return;
            }
            string? want = System.Environment.GetEnvironmentVariable("O3DESHARP_WAIT_FOR_DEBUGGER");
            if (want != "1")
            {
                return;
            }

            // Default 120s. Raised from the original 60s because Rider's
            // jetbrains:// URL attach often pops a confirmation dialog,
            // and users sometimes have to alt-tab to find it. Configurable
            // via env var for CI / headless cases.
            TimeSpan timeout = TimeSpan.FromSeconds(120);
            string? overrideMs = System.Environment.GetEnvironmentVariable("O3DESHARP_WAIT_FOR_DEBUGGER_TIMEOUT_MS");
            if (!string.IsNullOrEmpty(overrideMs) && int.TryParse(overrideMs, out int ms))
            {
                timeout = ms > 0 ? TimeSpan.FromMilliseconds(ms) : Timeout.InfiniteTimeSpan;
            }

            bool attached = WaitForAttach(timeout);
            if (!attached)
            {
                // The default WaitForAttach already logged a generic
                // timeout message. Add a Phase 17d-specific hint: if the
                // IDE attach prompt is sitting open behind another window,
                // confirming it now and using Tools > C# Scripting >
                // Reload Scripts re-runs OnCreate with the debugger
                // attached - so OnCreate-only breakpoints still bind on
                // the second pass.
                Debug.Log(
                    "[O3DESharp] If your IDE shows an 'Attach to process' prompt now, " +
                    "confirm it - any breakpoints you set after this point will still " +
                    "bind. To re-run OnCreate under the debugger, hit " +
                    "Tools > C# Scripting > Reload Scripts after the attach completes.");
            }
        }
    }
}
