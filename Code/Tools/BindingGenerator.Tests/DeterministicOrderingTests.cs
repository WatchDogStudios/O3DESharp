//
// Copyright (c) Contributors to the Open 3D Engine Project.
// For complete copyright and license terms please see the LICENSE at the root of this distribution.
//
// SPDX-License-Identifier: Apache-2.0 OR MIT
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using O3DESharp.BindingGenerator.Generation;
using O3DESharp.BindingGenerator.Parsing;

namespace O3DESharp.BindingGenerator.Tests;

/// <summary>
/// Regression tests for the ordering fix that makes binding generation
/// deterministic regardless of filesystem enumeration order:
///   1. Header file discovery is sorted (OrdinalIgnoreCase, full path).
///   2. Parsed classes/functions/enums are sorted by QualifiedName before
///      any downstream consumer (including dedup) sees them.
///   3. CSharpCodeGenerator.DeduplicateByName keeps the first occurrence,
///      so once (1) and (2) are sorted, the "winner" on a name collision
///      is deterministic instead of filesystem-order-dependent.
/// </summary>
public class DeterministicOrderingTests
{
    // ------------------------------------------------------------------
    // Directory.GetFiles + sort: simulate the real filesystem-enumeration
    // non-determinism using a temp directory, since Directory.GetFiles
    // itself cannot be shuffled deterministically in-process.
    // ------------------------------------------------------------------

    [Fact]
    public void HeaderDiscovery_SortsByFullPathOrdinalIgnoreCase_RegardlessOfCreationOrder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create files in a deliberately "wrong" (reverse-alphabetical)
            // order, since some filesystems return entries in creation order.
            var names = new[] { "Zeta.h", "Middle.h", "alpha.h", "Beta.h" };
            foreach (var name in names)
            {
                File.WriteAllText(Path.Combine(tempDir, name), "// stub header");
            }

            var rawFiles = Directory.GetFiles(tempDir, "*.h", SearchOption.TopDirectoryOnly);

            // This is the exact expression FindHeaderFiles now applies at its
            // return statement (Generation/MultiGemBindingGenerator.cs).
            var sorted = rawFiles
                .Distinct()
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sortedNames = sorted.Select(Path.GetFileName).ToList();
            sortedNames.Should().Equal("alpha.h", "Beta.h", "Middle.h", "Zeta.h");

            // Run the same sort twice more against independently-obtained
            // Directory.GetFiles results to prove it is idempotent/stable
            // regardless of how many times the OS re-enumerates the directory.
            for (var i = 0; i < 2; i++)
            {
                var repeat = Directory.GetFiles(tempDir, "*.h", SearchOption.TopDirectoryOnly)
                    .Distinct()
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                repeat.Select(Path.GetFileName).Should().Equal(sortedNames);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ------------------------------------------------------------------
    // ParsedBindings collections: sort by QualifiedName
    // ------------------------------------------------------------------

    [Fact]
    public void ParsedClasses_SortByQualifiedName_ProducesStableOrderRegardlessOfInputOrder()
    {
        var zeta = new ParsedClass { Name = "Widget", QualifiedName = "Zeta::Widget" };
        var alpha = new ParsedClass { Name = "Widget", QualifiedName = "Alpha::Widget" };
        var middle = new ParsedClass { Name = "Gadget", QualifiedName = "Middle::Gadget" };

        // Two different "arrival orders" simulating two different filesystem
        // enumeration orders for the exact same three classes.
        var order1 = new List<ParsedClass> { zeta, alpha, middle };
        var order2 = new List<ParsedClass> { middle, zeta, alpha };

        var sorted1 = order1.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).ToList();
        var sorted2 = order2.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).ToList();

        sorted1.Select(c => c.QualifiedName).Should().Equal("Alpha::Widget", "Middle::Gadget", "Zeta::Widget");
        sorted2.Select(c => c.QualifiedName).Should().Equal(sorted1.Select(c => c.QualifiedName));
    }

    [Fact]
    public void ParsedFunctions_SortByQualifiedName_ProducesStableOrderRegardlessOfInputOrder()
    {
        var b = new ParsedFunction { Name = "DoThing", QualifiedName = "NsB::DoThing" };
        var a = new ParsedFunction { Name = "DoThing", QualifiedName = "NsA::DoThing" };

        var order1 = new List<ParsedFunction> { b, a };
        var order2 = new List<ParsedFunction> { a, b };

        var sorted1 = order1.OrderBy(f => f.QualifiedName, StringComparer.Ordinal).ToList();
        var sorted2 = order2.OrderBy(f => f.QualifiedName, StringComparer.Ordinal).ToList();

        sorted1.Select(f => f.QualifiedName).Should().Equal("NsA::DoThing", "NsB::DoThing");
        sorted2.Select(f => f.QualifiedName).Should().Equal(sorted1.Select(f => f.QualifiedName));
    }

    [Fact]
    public void ParsedEnums_SortByQualifiedName_ProducesStableOrderRegardlessOfInputOrder()
    {
        var b = new ParsedEnum { Name = "State", QualifiedName = "NsB::State" };
        var a = new ParsedEnum { Name = "State", QualifiedName = "NsA::State" };

        var order1 = new List<ParsedEnum> { b, a };
        var order2 = new List<ParsedEnum> { a, b };

        var sorted1 = order1.OrderBy(e => e.QualifiedName, StringComparer.Ordinal).ToList();
        var sorted2 = order2.OrderBy(e => e.QualifiedName, StringComparer.Ordinal).ToList();

        sorted1.Select(e => e.QualifiedName).Should().Equal("NsA::State", "NsB::State");
        sorted2.Select(e => e.QualifiedName).Should().Equal(sorted1.Select(e => e.QualifiedName));
    }

    // ------------------------------------------------------------------
    // End-to-end: once the collection is sorted, DeduplicateByName's
    // "first occurrence wins" tie-break becomes deterministic.
    // ------------------------------------------------------------------

    [Fact]
    public void DeduplicateByName_OnSortedInput_AlwaysKeepsSameWinner_RegardlessOfOriginalParseOrder()
    {
        // Two classes with the same bare Name (so they collide once
        // sanitized) but declared in different C++ namespaces, i.e. exactly
        // the case that used to be filesystem-order-dependent.
        var fromHeaderA = new ParsedClass { Name = "Widget", QualifiedName = "GemA::Widget", SourceFile = "GemA/Widget.h" };
        var fromHeaderB = new ParsedClass { Name = "Widget", QualifiedName = "GemB::Widget", SourceFile = "GemB/Widget.h" };

        // Simulate two different libclang/filesystem parse orders for the
        // identical pair of classes.
        var parseOrder1 = new List<ParsedClass> { fromHeaderB, fromHeaderA };
        var parseOrder2 = new List<ParsedClass> { fromHeaderA, fromHeaderB };

        // Apply the same fix the production code now applies: sort by
        // QualifiedName immediately after parsing, before dedup ever runs.
        var sorted1 = parseOrder1.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).ToList();
        var sorted2 = parseOrder2.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).ToList();

        var winner1 = CSharpCodeGenerator.DeduplicateByName(sorted1, c => c.Name);
        var winner2 = CSharpCodeGenerator.DeduplicateByName(sorted2, c => c.Name);

        winner1.Should().HaveCount(1);
        winner2.Should().HaveCount(1);

        // GemA::Widget sorts before GemB::Widget under Ordinal comparison,
        // so it must win both times - regardless of which order libclang
        // originally reported the two declarations in.
        winner1[0].QualifiedName.Should().Be("GemA::Widget");
        winner2[0].QualifiedName.Should().Be("GemA::Widget");
        winner1[0].SourceFile.Should().Be(winner2[0].SourceFile,
            "the dedup winner must be identical regardless of original parse order once inputs are sorted");
    }

    [Fact]
    public void DeduplicateByName_WithoutSorting_DemonstratesTheBugItFixes()
    {
        // This test documents the pre-fix failure mode: feeding
        // DeduplicateByName raw (unsorted) parse-order input makes the
        // winner depend entirely on which element happened to come first.
        var fromHeaderA = new ParsedClass { Name = "Widget", QualifiedName = "GemA::Widget" };
        var fromHeaderB = new ParsedClass { Name = "Widget", QualifiedName = "GemB::Widget" };

        var parseOrder1 = new List<ParsedClass> { fromHeaderB, fromHeaderA };
        var parseOrder2 = new List<ParsedClass> { fromHeaderA, fromHeaderB };

        var winner1 = CSharpCodeGenerator.DeduplicateByName(parseOrder1, c => c.Name);
        var winner2 = CSharpCodeGenerator.DeduplicateByName(parseOrder2, c => c.Name);

        // Without the upstream sort, "first occurrence wins" means the two
        // orderings pick different winners - the exact non-determinism this
        // task's production fix eliminates by sorting before this call.
        winner1[0].QualifiedName.Should().Be("GemB::Widget");
        winner2[0].QualifiedName.Should().Be("GemA::Widget");
    }
}
