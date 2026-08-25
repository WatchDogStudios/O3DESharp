/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace O3DE.Reflection
{
    /// <summary>
    /// Source-generated serialization metadata for NativeReflection's argument
    /// envelope.
    ///
    /// Reflection-based JsonSerializer.Serialize needs runtime code generation,
    /// which a NativeAOT image does not have - the failure is a
    /// NotSupportedException at the first EBus broadcast, not a build error.
    /// This context makes the metadata compile-time.
    ///
    /// The registered set is exactly what NativeReflection.SerializeArgumentToObject
    /// can produce, and it is CLOSED by construction: that method has an
    /// explicit case per supported type and throws NotSupportedException on
    /// anything else. Arguments are boxed into List<object?>, and the
    /// object converter resolves each element's runtime type through this
    /// context - which succeeds precisely because the set is closed and every
    /// member is listed here. Adding a case to SerializeArgumentToObject
    /// therefore requires adding a JsonSerializable line here too, and
    /// NativeReflectionAotTests fails if it is forgotten.
    /// </summary>
    [JsonSerializable(typeof(List<object?>))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(long))]
    [JsonSerializable(typeof(ulong))]
    [JsonSerializable(typeof(float))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(float[]))]
    internal partial class NativeReflectionJsonContext : JsonSerializerContext
    {
    }
}
