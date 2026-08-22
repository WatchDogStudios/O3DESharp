/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace O3DESharp.SourceGenerators
{
    /// <summary>
    /// Turns the reflected EBus surface into a compile-time lookup table.
    ///
    /// The shipping NativeAOT build has to answer "is this (bus, event) real,
    /// and what shape are its arguments?" without any managed-side reflection.
    /// This emits that answer as a generated switch - resolved when the
    /// compiler runs, not built into a Dictionary during startup.
    ///
    /// Input is the EXISTING reflection_data.json, the dump the reflection
    /// binding backend already produces and ReflectionBindingGenerator already
    /// consumes. It is deliberately NOT SP-1b's native_bindings.json: that
    /// track recovers native C++ symbols for trampolines and is orthogonal to
    /// this one.
    ///
    /// A missing or corrupt dump yields an EMPTY table rather than a build
    /// failure. A fresh clone has no dump, and taking the build down over an
    /// optional optimisation input would be a worse failure than the empty
    /// table's - every dispatch then simply falls to the closed-world
    /// diagnostic, which is the honest outcome.
    /// </summary>
    [Generator]
    public sealed class StaticDispatchGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var emitEnabled = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) =>
            {
                provider.GlobalOptions.TryGetValue("build_property.O3DESharpEmitHostExports", out var emit);
                return string.Equals(emit, "true", StringComparison.OrdinalIgnoreCase);
            });

            var reflectionData = context.AdditionalTextsProvider
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Where(static pair =>
                {
                    var options = pair.Right.GetOptions(pair.Left);
                    options.TryGetValue("build_metadata.AdditionalFiles.O3DESharpKind", out var kind);
                    return string.Equals(kind, "ReflectionData", StringComparison.OrdinalIgnoreCase);
                })
                .Select(static (pair, token) => pair.Left.GetText(token)?.ToString() ?? string.Empty)
                .Collect();

            context.RegisterSourceOutput(
                emitEnabled.Combine(reflectionData),
                static (sourceContext, pair) =>
                {
                    var (enabled, texts) = pair;
                    if (!enabled)
                    {
                        return;
                    }

                    var events = texts.SelectMany(ParseEvents).ToImmutableArray();
                    sourceContext.AddSource(
                        "O3DE.Reflection.StaticEBusDispatch.g.cs",
                        SourceText.From(Emit(events), Encoding.UTF8));
                });
        }

        // -----------------------------------------------------------
        // Parse
        // -----------------------------------------------------------

        private static IEnumerable<EventShape> ParseEvents(string json)
        {
            // Hand-rolled over System.Text.Json's reader rather than
            // deserializing the full ReflectionDocument: the generator targets
            // netstandard2.0 and must not take a dependency on the
            // BindingGenerator assembly. Only four fields are needed.
            var results = new List<EventShape>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return results;
            }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("ebuses", out var buses) ||
                    buses.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return results;
                }

                foreach (var bus in buses.EnumerateArray())
                {
                    // A bus entry can be any JSON value in a malformed dump
                    // (e.g. "ebuses": ["not_an_object", 123, null]) -
                    // TryGetProperty throws InvalidOperationException on a
                    // non-object element, so guard the ValueKind first
                    // instead of relying solely on the catch below.
                    if (bus.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        continue;
                    }
                    if (!bus.TryGetProperty("name", out var busName) ||
                        busName.ValueKind != System.Text.Json.JsonValueKind.String)
                    {
                        continue;
                    }
                    if (!bus.TryGetProperty("events", out var events) ||
                        events.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var evt in events.EnumerateArray())
                    {
                        if (evt.ValueKind != System.Text.Json.JsonValueKind.Object)
                        {
                            continue;
                        }
                        if (!evt.TryGetProperty("name", out var eventName) ||
                            eventName.ValueKind != System.Text.Json.JsonValueKind.String)
                        {
                            continue;
                        }

                        int arity = evt.TryGetProperty("parameters", out var parameters) &&
                                    parameters.ValueKind == System.Text.Json.JsonValueKind.Array
                            ? parameters.GetArrayLength()
                            : 0;

                        bool isBroadcast = evt.TryGetProperty("is_broadcast", out var broadcast) &&
                                           broadcast.ValueKind == System.Text.Json.JsonValueKind.True;

                        results.Add(new EventShape(
                            busName.GetString()!, eventName.GetString()!, arity, isBroadcast));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                // A corrupt dump degrades to an empty table. Failing the build
                // over an optional optimisation input would be the worse
                // outcome; an empty table simply routes every dispatch to the
                // closed-world diagnostic instead. JsonException covers
                // syntactically-broken JSON; InvalidOperationException is the
                // backstop for a structurally-odd-but-syntactically-valid
                // shape slipping past the ValueKind guards above (e.g. some
                // other unanticipated JsonElement access on the wrong kind).
                return new List<EventShape>();
            }

            return results;
        }

        // -----------------------------------------------------------
        // Emit
        // -----------------------------------------------------------

        private static string Emit(ImmutableArray<EventShape> events)
        {
            var distinct = events
                .GroupBy(e => (e.BusName, e.EventName))
                .Select(g => g.First())
                .OrderBy(e => e.BusName, StringComparer.Ordinal)
                .ThenBy(e => e.EventName, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   This file was generated by O3DESharp.SourceGenerators (StaticDispatchGenerator).");
            sb.AppendLine("//   Source: reflection_data.json. DO NOT EDIT.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("namespace O3DE.Reflection");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Compile-time (bus, event) table built from the reflected EBus");
            sb.AppendLine("    /// surface. A generated switch rather than a Dictionary: the switch is");
            sb.AppendLine("    /// resolved when the compiler runs, whereas a Dictionary would be");
            sb.AppendLine("    /// runtime state built during startup, which is the managed-side work");
            sb.AppendLine("    /// the shipping build exists to avoid.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    internal static class StaticEBusDispatch");
            sb.AppendLine("    {");
            sb.AppendLine($"        /// <summary>Number of statically known events.</summary>");
            sb.AppendLine($"        internal static int EntryCount => {distinct.Count};");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Look up an event's shape. False means the pair is not in the");
            sb.AppendLine("        /// reflected surface this image was built against - which on a");
            sb.AppendLine("        /// shipping NativeAOT build is a hard error, not a fallback.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        internal static bool TryGetShape(string busName, string eventName, out int arity, out bool isBroadcast)");
            sb.AppendLine("        {");
            sb.AppendLine("            arity = 0;");
            sb.AppendLine("            isBroadcast = false;");
            sb.AppendLine("            switch (busName)");
            sb.AppendLine("            {");

            foreach (var busGroup in distinct.GroupBy(e => e.BusName, StringComparer.Ordinal))
            {
                sb.AppendLine($"                case {Literal(busGroup.Key)}:");
                sb.AppendLine("                    switch (eventName)");
                sb.AppendLine("                    {");
                foreach (var evt in busGroup)
                {
                    sb.AppendLine($"                        case {Literal(evt.EventName)}:");
                    sb.AppendLine($"                            arity = {evt.Arity};");
                    sb.AppendLine($"                            isBroadcast = {(evt.IsBroadcast ? "true" : "false")};");
                    sb.AppendLine("                            return true;");
                }
                sb.AppendLine("                        default: return false;");
                sb.AppendLine("                    }");
            }

            sb.AppendLine("                default: return false;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // Roslyn's own answer to "turn this string into a valid C# string
        // literal" - handles CR/LF/NEL/LS/PS and everything else illegal in
        // a non-verbatim literal, which a hand-rolled \\ / " escape misses.
        private static string Literal(string value) =>
            Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);
    }

    /// <summary>One reflected EBus event, reduced to what static dispatch needs.</summary>
    internal sealed record EventShape(string BusName, string EventName, int Arity, bool IsBroadcast);
}
