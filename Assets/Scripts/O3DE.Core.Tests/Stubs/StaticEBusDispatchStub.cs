//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

namespace O3DE.Reflection
{
    /// <summary>
    /// Test-only stand-in for the generated StaticEBusDispatch. The real one is
    /// emitted into O3DE.Core's compilation from reflection_data.json, and this
    /// test assembly compiles source files rather than referencing that DLL (see
    /// the comment in O3DE.Core.Tests.csproj).
    ///
    /// Deliberately EMPTY: what these tests exercise is the miss path - the hard
    /// error - and an empty table makes every lookup a miss. The emit itself is
    /// covered by Editor/Tests/test_static_dispatch_emit.py.
    /// </summary>
    internal static class StaticEBusDispatch
    {
        internal static int EntryCount => 0;

        internal static bool TryGetShape(string busName, string eventName, out int arity, out bool isBroadcast)
        {
            arity = 0;
            isBroadcast = false;
            return false;
        }
    }
}
