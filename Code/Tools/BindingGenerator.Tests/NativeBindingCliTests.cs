//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.IO;
using System.Text.Json;
using O3DESharp.BindingGenerator.Configuration;
using O3DESharp.BindingGenerator.Generation;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// End-to-end over the offline half: a manifest emitted by the C++ pass plus a
/// set of recovered call sites produces a joined manifest whose bound entries
/// carry real symbols. The runtime half (registry, load-time validation,
/// dispatch) is SP-1b-2 and not exercised here.
/// </summary>
public class NativeBindingCliTests
{
    [Fact]
    public void JoinedManifest_IsWrittenWithSymbolsFilledIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var manifestPath = Path.Combine(dir, "native_bindings.json");
            File.WriteAllText(manifestPath, """
            {"methods":[{
              "reflected_name":"GetLength","owning_class_name":"Vector3",
              "owning_class_type_id":"{0}","owning_class_size_bytes":16,"owning_class_align_bytes":16,
              "is_static":false,"is_const":true,"native_qualified_symbol":"",
              "return":{"name":"","cpp_type_name":"float","type_id":"{1}","storage_class":"Value","size_bytes":4,"align_bytes":4},
              "arguments":[],"bindable":false,"non_bindable_reason":"NoNativeSideCounterpart",
              "binding_id":"Vector3::GetLength"}]}
            """);

            var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>(
                File.ReadAllText(manifestPath))!;

            var report = NativeBindingJoin.Apply(
                doc,
                new[] { new CallSiteSymbol("Vector3", "GetLength", "AZ::Vector3::GetLength", false) });

            var outPath = Path.Combine(dir, "native_bindings.joined.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(doc));

            var reloaded = JsonSerializer.Deserialize<NativeBindingManifestDocument>(
                File.ReadAllText(outPath))!;

            report.Bound.Should().Be(1);
            reloaded.Methods[0].Bindable.Should().BeTrue();
            reloaded.Methods[0].NativeQualifiedSymbol.Should().Be("AZ::Vector3::GetLength");
            reloaded.Methods[0].BindingId.Should().Be("Vector3::GetLength");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
