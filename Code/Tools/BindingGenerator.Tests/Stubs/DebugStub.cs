//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

namespace O3DE
{
    /// <summary>
    /// Test-only stub of <c>O3DE.Debug</c>. The real implementation in
    /// <c>Assets/Scripts/O3DE.Core/Debug.cs</c> routes through Coral's
    /// InternalCalls function-pointer fields, which are uninitialised when the
    /// assembly is loaded outside the Coral host. We only need
    /// <c>Debug.LogError</c> to satisfy the default error-callback path in
    /// <c>ExposedPropertyHelpers</c>; tests that exercise error handling pass
    /// an explicit logger callback instead.
    /// </summary>
    internal static class Debug
    {
        public static System.Collections.Generic.List<string> CapturedErrors { get; } = new();

        public static void LogError(string message)
        {
            CapturedErrors.Add(message);
        }
    }
}
