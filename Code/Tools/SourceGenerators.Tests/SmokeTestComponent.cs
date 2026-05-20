/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using O3DE;

namespace O3DESharp.SourceGenerators.Smoke
{
    /// <summary>
    /// Smoke-test consumer for the [EBus] source generator.
    ///
    /// Builds a partial class with one bus and a handful of handler
    /// methods covering the marshaling shapes the generator needs to
    /// support:
    ///   - primitives (int, float, bool, string, ulong)
    ///   - math types (Vector3, Quaternion)
    ///   - zero-arg events
    ///   - many-arg events (verifies the array-index emit loop)
    ///
    /// Building this project triggers the source generator and writes
    /// the emitted .g.cs file under ./generated/ for inspection.
    /// The generator's correctness is verified by:
    ///   1. This file compiling at all (means generated Connect/
    ///      Disconnect / dispatch methods are syntactically valid).
    ///   2. Each generated case calling the right user method
    ///      (verified by humans + future CI grep against ./generated/).
    /// </summary>
    [EBus("SmokeTickBus")]
    [EBus("TransformNotificationBus")]
    public partial class SmokeTestComponent
    {
        // Tick-style void event with two primitive args.
        [EBusHandler("OnTick")]
        private void HandleTick(float deltaSeconds, ulong frameId)
        {
            _ = deltaSeconds;
            _ = frameId;
        }

        // Math-type args. Tests Vector3 + Quaternion unmarshaling.
        [EBusHandler("OnTransformChanged")]
        private void HandleTransformChanged(Vector3 newPos, Quaternion newRot)
        {
            _ = newPos;
            _ = newRot;
        }

        // Zero-arg event. Tests the empty-parameter emit branch.
        [EBusHandler("OnParentChanged")]
        private void HandleParentChanged()
        {
        }

        // String + bool args. Tests basic primitive marshaling.
        [EBusHandler("OnLabelUpdated")]
        private void HandleLabelUpdated(string label, bool visible)
        {
            _ = label;
            _ = visible;
        }

        // Long arg list. Stresses the generator's per-parameter emit
        // loop (comma placement, trailing-comma avoidance).
        [EBusHandler("OnBigEvent")]
        private void HandleBigEvent(
            int a,
            float b,
            double c,
            bool d,
            string e,
            ulong f,
            Vector3 g,
            Quaternion h)
        {
            _ = a;
            _ = b;
            _ = c;
            _ = d;
            _ = e;
            _ = f;
            _ = g;
            _ = h;
        }
    }
}
