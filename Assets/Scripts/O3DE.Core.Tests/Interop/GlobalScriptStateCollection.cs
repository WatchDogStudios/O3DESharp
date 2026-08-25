//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using Xunit;

namespace O3DE.Core.Tests.Interop;

/// <summary>
/// Collection definition for tests that share global script state
/// (ScriptTypeRegistry, ScriptComponentBridge). xUnit runs tests
/// concurrently across classes by default; this definition serializes
/// them to prevent races on static state.
/// </summary>
[CollectionDefinition("GlobalScriptState")]
public class GlobalScriptStateCollection
{
    // This class has no code, it exists only to declare the collection.
}
