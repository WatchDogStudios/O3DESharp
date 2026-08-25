//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System.Text.Json;
using O3DESharp.BindingGenerator.Configuration;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// The manifest JSON is the contract between the C++ exporter
/// (NativeBindingManifestExporter) and this generator. The property names are
/// the wire format: renaming one silently produces a manifest where every
/// method looks unbindable, which degrades to the slow path rather than
/// failing loudly. These pin the names.
/// </summary>
public class NativeBindingManifestSchemaTests
{
    private const string SampleJson = """
    {
      "methods": [
        {
          "reflected_name": "GetLength",
          "owning_class_name": "Vector3",
          "owning_class_type_id": "{8379EB7D-01FA-4538-B64B-A6543B4BE73D}",
          "owning_class_size_bytes": 16,
          "owning_class_align_bytes": 16,
          "is_static": false,
          "is_const": true,
          "native_qualified_symbol": "",
          "return": {
            "name": "", "cpp_type_name": "float", "type_id": "{EA2C3E90-AFBE-44D4-A90D-FAAF79BAF93D}",
            "storage_class": "Value", "size_bytes": 4, "align_bytes": 4
          },
          "arguments": [],
          "bindable": false,
          "non_bindable_reason": "NoNativeSideCounterpart",
          "binding_id": "Vector3::GetLength"
        }
      ]
    }
    """;

    [Fact]
    public void Deserializes_TheCppExporterWireFormat()
    {
        var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>(SampleJson);

        doc.Should().NotBeNull();
        doc!.Methods.Should().ContainSingle();

        var m = doc.Methods[0];
        m.ReflectedName.Should().Be("GetLength");
        m.OwningClassName.Should().Be("Vector3");
        m.OwningClassSizeBytes.Should().Be(16);
        m.IsConst.Should().BeTrue();
        m.IsStatic.Should().BeFalse();
        m.BindingId.Should().Be("Vector3::GetLength");
        m.Return.CppTypeName.Should().Be("float");
        m.Return.StorageClass.Should().Be("Value");
    }

    [Fact]
    public void CppExporterLeavesTheJoinedFieldsUnset()
    {
        // The runtime BehaviorContext pass cannot recover a native symbol and
        // does not classify. Both are this generator's job; if the exporter
        // ever starts filling them, the join must be revisited.
        var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>(SampleJson)!;
        var m = doc.Methods[0];

        m.NativeQualifiedSymbol.Should().BeEmpty();
        m.Bindable.Should().BeFalse();
        m.NonBindableReason.Should().Be("NoNativeSideCounterpart");
    }

    [Fact]
    public void RoundTripsWithoutLosingFields()
    {
        var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>(SampleJson)!;
        doc.Methods[0].NativeQualifiedSymbol = "AZ::Vector3::GetLength";
        doc.Methods[0].Bindable = true;
        doc.Methods[0].NonBindableReason = "None";

        var json = JsonSerializer.Serialize(doc);
        var again = JsonSerializer.Deserialize<NativeBindingManifestDocument>(json)!;

        again.Methods[0].NativeQualifiedSymbol.Should().Be("AZ::Vector3::GetLength");
        again.Methods[0].Bindable.Should().BeTrue();
        again.Methods[0].BindingId.Should().Be("Vector3::GetLength");
    }

    [Fact]
    public void EmptyManifestIsValid()
    {
        var doc = JsonSerializer.Deserialize<NativeBindingManifestDocument>("""{"methods":[]}""");
        doc!.Methods.Should().BeEmpty();
    }
}
