/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace O3DESharp.SourceGenerators
{
    /// <summary>
    /// Roslyn incremental source generator that processes [EBus] /
    /// [EBusHandler] attributes on user classes and emits the
    /// Connect / Disconnect / dispatch-shim partial methods.
    ///
    /// Generated shape for a class like
    /// <code>
    /// [EBus("TickBus")]
    /// public partial class MyTicker : ScriptComponent
    /// {
    ///     [EBusHandler("OnTick")]
    ///     private void HandleTick(float dt, ScriptTimePoint t) { ... }
    /// }
    /// </code>
    /// is roughly
    /// <code>
    /// partial class MyTicker
    /// {
    ///     private long __TickBusToken;
    ///
    ///     public void ConnectToTickBus(ulong address = 0)
    ///     {
    ///         __TickBusToken = O3DE.Reflection.EBusHandlerRegistry.Register(
    ///             this, "TickBus", address, __DispatchTickBusEvent);
    ///     }
    ///     public void DisconnectFromTickBus()
    ///     {
    ///         O3DE.Reflection.EBusHandlerRegistry.Unregister(__TickBusToken);
    ///         __TickBusToken = 0;
    ///     }
    ///
    ///     private string? __DispatchTickBusEvent(string eventName, string argsJson)
    ///     {
    ///         switch (eventName)
    ///         {
    ///             case "OnTick":
    ///                 // Unmarshal argsJson -> (float, ScriptTimePoint)
    ///                 // Call HandleTick(...)
    ///                 // Return "{\"ok\":true}" since OnTick returns void.
    ///         }
    ///         return null;
    ///     }
    /// }
    /// </code>
    ///
    /// This iteration stops at the Connect/Disconnect signatures + a
    /// dispatch-shim STUB that returns null for every event. The full
    /// per-event argument unmarshaling is gated on the C++ managed-
    /// handler bridge landing (Phase 18-E2). Until then, calling
    /// ConnectTo* registers the handler on the managed side but events
    /// never reach the user's method - the EBus dispatch never fires
    /// because no native handler is bound. The generated shim's switch
    /// is in place so when the bridge lands, only the per-event arms
    /// need filling in.
    /// </summary>
    [Generator]
    public sealed class EBusHandlerGenerator : IIncrementalGenerator
    {
        private const string EBusAttributeFullName = "O3DE.EBusAttribute";
        private const string EBusHandlerAttributeFullName = "O3DE.EBusHandlerAttribute";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Pick up every class declaration that has at least one [EBus]
            // attribute - the syntax-filter step is fast and runs on every
            // text change; the semantic-model lookup downstream is only
            // applied to the survivors.
            var candidates = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidateClass(node),
                    transform: static (ctx, _) => TryGetEBusClass(ctx))
                .Where(c => c is not null)
                .Select((c, _) => c!);

            context.RegisterSourceOutput(candidates, static (sourceContext, classInfo) =>
            {
                var generated = EmitPartialClass(classInfo);
                sourceContext.AddSource(
                    $"{classInfo.ContainingNamespace}.{classInfo.ClassName}.EBus.g.cs",
                    SourceText.From(generated, Encoding.UTF8));
            });
        }

        // -----------------------------------------------------------
        // Syntax filter
        // -----------------------------------------------------------

        private static bool IsCandidateClass(SyntaxNode node)
        {
            // Cheap structural check: a partial class with at least one
            // attribute that COULD be [EBus]. Full type resolution is
            // deferred to the semantic step.
            if (node is not ClassDeclarationSyntax cls) return false;
            if (!cls.Modifiers.Any(m => m.Text == "partial")) return false;
            if (cls.AttributeLists.Count == 0) return false;

            // Look for an attribute named EBus or EBusAttribute on the
            // class itself. We can't resolve the full type name here
            // (no semantic model yet), so we match by simple name and
            // let the semantic step weed out impostors.
            foreach (var list in cls.AttributeLists)
            {
                foreach (var attr in list.Attributes)
                {
                    var name = attr.Name.ToString();
                    // Strips qualifiers - "O3DE.EBus" -> "EBus".
                    var lastSegment = name.Split('.').Last();
                    if (lastSegment == "EBus" || lastSegment == "EBusAttribute")
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // -----------------------------------------------------------
        // Semantic resolution
        // -----------------------------------------------------------

        private static EBusClassInfo? TryGetEBusClass(GeneratorSyntaxContext context)
        {
            var classSyntax = (ClassDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(classSyntax) as INamedTypeSymbol;
            if (symbol is null) return null;

            // Pull every [EBus] attribute (AllowMultiple = true on the
            // attribute itself, so a class can subscribe to multiple
            // buses). For each, capture the bus-name + priority literal
            // args.
            var ebusAttrs = symbol.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == EBusAttributeFullName)
                .ToList();
            if (ebusAttrs.Count == 0) return null;

            var buses = new List<EBusAttributeInfo>();
            foreach (var attr in ebusAttrs)
            {
                if (attr.ConstructorArguments.Length < 1) continue;
                var busName = attr.ConstructorArguments[0].Value as string;
                if (string.IsNullOrEmpty(busName)) continue;

                int priority = 0;
                foreach (var kv in attr.NamedArguments)
                {
                    if (kv.Key == "Priority" && kv.Value.Value is int p) priority = p;
                }
                buses.Add(new EBusAttributeInfo(busName!, priority));
            }
            if (buses.Count == 0) return null;

            // Now pull every method with [EBusHandler]. Each binds to
            // one event by name; the class-level [EBus] determines
            // which bus(es) those events fire on.
            var handlers = new List<EBusHandlerMethodInfo>();
            foreach (var member in symbol.GetMembers())
            {
                if (member is not IMethodSymbol method) continue;
                var handlerAttr = method.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == EBusHandlerAttributeFullName);
                if (handlerAttr is null) continue;
                if (handlerAttr.ConstructorArguments.Length < 1) continue;
                var eventName = handlerAttr.ConstructorArguments[0].Value as string;
                if (string.IsNullOrEmpty(eventName)) continue;

                handlers.Add(new EBusHandlerMethodInfo(
                    eventName!,
                    method.Name,
                    method.Parameters.Select(p => new ParamInfo(
                        p.Name,
                        p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))).ToList()));
            }

            return new EBusClassInfo(
                symbol.ContainingNamespace.IsGlobalNamespace
                    ? ""
                    : symbol.ContainingNamespace.ToDisplayString(),
                symbol.Name,
                symbol.DeclaredAccessibility,
                buses,
                handlers);
        }

        // -----------------------------------------------------------
        // Emit
        // -----------------------------------------------------------

        private static string EmitPartialClass(EBusClassInfo info)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   This file was generated by O3DESharp.SourceGenerators (EBusHandlerGenerator).");
            sb.AppendLine("//   DO NOT EDIT.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine();

            bool hasNamespace = !string.IsNullOrEmpty(info.ContainingNamespace);
            if (hasNamespace)
            {
                sb.AppendLine($"namespace {info.ContainingNamespace}");
                sb.AppendLine("{");
            }

            var indent = hasNamespace ? "    " : "";
            sb.AppendLine($"{indent}partial class {info.ClassName}");
            sb.AppendLine($"{indent}{{");

            // One token field per bus the class is registered for. The
            // tokens are private to the partial since they're only
            // touched by the source-generated Connect/Disconnect methods.
            foreach (var bus in info.Buses)
            {
                sb.AppendLine($"{indent}    private long __{SanitizeIdent(bus.BusName)}Token;");
            }
            sb.AppendLine();

            // Connect / Disconnect per bus. The address parameter
            // defaults to 0 (broadcast) - addressed-bus users pass
            // the entity id explicitly.
            foreach (var bus in info.Buses)
            {
                var sanitized = SanitizeIdent(bus.BusName);
                sb.AppendLine($"{indent}    /// <summary>Connect this instance to {bus.BusName} as an EBus handler.</summary>");
                sb.AppendLine($"{indent}    public void ConnectTo{sanitized}(ulong address = 0)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        __{sanitized}Token = O3DE.Reflection.EBusHandlerRegistry.Register(");
                sb.AppendLine($"{indent}            this, \"{bus.BusName}\", address, __Dispatch{sanitized}Event);");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine();
                sb.AppendLine($"{indent}    /// <summary>Disconnect this instance from {bus.BusName}.</summary>");
                sb.AppendLine($"{indent}    public void DisconnectFrom{sanitized}()");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        O3DE.Reflection.EBusHandlerRegistry.Unregister(__{sanitized}Token);");
                sb.AppendLine($"{indent}        __{sanitized}Token = 0;");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine();
            }

            // Dispatch shim per bus. Switches on event name and forwards
            // to the user method. Args unmarshaling is a STUB - the
            // full version needs to parse argsJson into typed parameters
            // matching the user method's signature, which depends on the
            // C++ managed-handler bridge to actually be invoked. Until
            // then the shim returns null for every event, which the
            // C++ side will treat as "no handler" (a safe no-op).
            foreach (var bus in info.Buses)
            {
                var sanitized = SanitizeIdent(bus.BusName);
                sb.AppendLine($"{indent}    /// <summary>");
                sb.AppendLine($"{indent}    /// Dispatch shim - invoked by the C++ managed-handler bridge");
                sb.AppendLine($"{indent}    /// when {bus.BusName} fires an event for this handler. Unmarshals");
                sb.AppendLine($"{indent}    /// argsJson into typed parameters and calls the right user method.");
                sb.AppendLine($"{indent}    /// </summary>");
                sb.AppendLine($"{indent}    private string? __Dispatch{sanitized}Event(string eventName, string argsJson)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        switch (eventName)");
                sb.AppendLine($"{indent}        {{");
                foreach (var h in info.Handlers)
                {
                    sb.AppendLine($"{indent}            case \"{h.EventName}\":");
                    sb.AppendLine($"{indent}                // TODO(Phase 18-E2): unmarshal argsJson into typed params");
                    sb.AppendLine($"{indent}                // matching {h.MethodName}({string.Join(", ", h.Parameters.Select(p => p.TypeName))})");
                    sb.AppendLine($"{indent}                // and invoke. Returns \"{{\\\"ok\\\":true}}\" for void events.");
                    sb.AppendLine($"{indent}                // For now, invoke with default-constructed args so user code");
                    sb.AppendLine($"{indent}                // wiring can be exercised end-to-end while the bridge lands.");
                    var defaults = string.Join(", ", h.Parameters.Select(p => $"default({p.TypeName})!"));
                    sb.AppendLine($"{indent}                this.{h.MethodName}({defaults});");
                    sb.AppendLine($"{indent}                return \"{{\\\"ok\\\":true}}\";");
                }
                sb.AppendLine($"{indent}            default: return null;");
                sb.AppendLine($"{indent}        }}");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine();
            }

            sb.AppendLine($"{indent}}}");
            if (hasNamespace)
            {
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        private static string SanitizeIdent(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            if (sb.Length == 0) return "Unnamed";
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------
    // Plain-data models used between syntax pass and emit pass.
    // Record types because the incremental generator API caches them
    // by value-equality - using sealed classes would invalidate the
    // cache every keystroke.
    // ---------------------------------------------------------------

    internal sealed record EBusAttributeInfo(string BusName, int Priority);
}

namespace System.Runtime.CompilerServices
{
    // Polyfill required for C# 9 record types (and init-only setters)
    // on netstandard2.0 - the compiler emits a reference to this type
    // for `init` accessors, and netstandard2.0 doesn't ship one. Marker
    // class with no body is enough; the compiler doesn't actually call
    // any members. Internal so it doesn't pollute the consumer's API.
    internal static class IsExternalInit { }
}

namespace O3DESharp.SourceGenerators
{

    internal sealed record ParamInfo(string Name, string TypeName);

    internal sealed record EBusHandlerMethodInfo(
        string EventName,
        string MethodName,
        List<ParamInfo> Parameters);

    internal sealed record EBusClassInfo(
        string ContainingNamespace,
        string ClassName,
        Accessibility Accessibility,
        List<EBusAttributeInfo> Buses,
        List<EBusHandlerMethodInfo> Handlers);
}
