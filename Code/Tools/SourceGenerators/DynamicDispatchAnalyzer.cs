/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace O3DESharp.SourceGenerators
{
    /// <summary>
    /// Flags EBus dispatch whose bus or event name is not a compile-time
    /// constant.
    ///
    /// Static dispatch on the desktop NativeAOT build can only cover the
    /// closed world: names the generator can read out of the source. A
    /// runtime-computed name has no generated path, and NativeAOT desktop
    /// deliberately does NOT provide an interpreter tail for it (that is the
    /// console/mobile Mono backend's job, M5).
    ///
    /// The design decision is that this restriction is LOUD: a build warning
    /// here, and a runtime hard error naming the site if the call is reached in
    /// a shipping image. Never a silent degrade to something slower or subtly
    /// different.
    ///
    /// Warning rather than error on purpose - the editor build handles these
    /// call sites fine, so a game that never ships an AOT desktop artifact is
    /// not blocked by them.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class DynamicDispatchAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "O3DESHARP1001";

        private static readonly DiagnosticDescriptor s_rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "EBus dispatch target is not a compile-time constant",
            messageFormat:
                "'{0}' is called with a non-constant {1} name, so it cannot be statically " +
                "dispatched. This call works in the editor but is a hard runtime error in a " +
                "NativeAOT desktop build - constant-fold the name, or ship the Mono backend.",
            category: "O3DESharp.AOT",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description:
                "Desktop NativeAOT supports only closed-world BehaviorContext dispatch: bus and " +
                "event names the generator can resolve at compile time. Runtime-computed names " +
                "are out of scope by design and are diagnosed here rather than degrading " +
                "silently at runtime.");

        private const string NativeReflectionFullName = "O3DE.Reflection.NativeReflection";

        // Types whose own members forward a caller-supplied name straight into
        // a watched NativeReflection method. Those forwarding calls are
        // implementation plumbing, never a game call site the closed-world
        // generator sees, and their name argument is a method parameter by
        // construction - so they would warn unconditionally, carrying no
        // signal about whether the real outer call was safe. NativeObject's
        // InvokeMethod / GetProperty<T> / SetProperty wrappers are the second
        // case, alongside NativeReflection's own Result-suffixed wrappers.
        //
        // This is deliberately type-granular, not method-granular: keying the
        // watch table by (type, method) so NativeObject.GetProperty is
        // distinguishable from NativeReflection.GetProperty is the real fix
        // and is a larger change. Consequence of the coarser version: a
        // genuinely dynamic call written *inside* NativeObject would not warn.
        private static readonly HashSet<string> s_selfCallExemptTypes = new()
        {
            NativeReflectionFullName,
            "O3DE.Reflection.NativeObject",
        };

        // Method name -> the call-site argument position(s) that must be
        // constant, and the human-readable name for each (used in the
        // diagnostic message as "non-constant {kind} name"). Not every
        // watched method checks the same positions as the EBus methods -
        // e.g. InvokeInstanceMethod's class name comes from
        // instance.TypeName at runtime, not a call-site literal, so only its
        // method-name argument (position 1) is checked. DestroyInstance has
        // no name-shaped call-site argument at all and is not in this table.
        private static readonly Dictionary<string, (int Index, string Kind)[]> s_watchedMethods =
            new()
            {
                ["BroadcastEBusEvent"] = new[] { (0, "bus"), (1, "event") },
                ["SendEBusEvent"] = new[] { (0, "bus"), (1, "event") },
                ["BroadcastResultEBusEvent"] = new[] { (0, "bus"), (1, "event") },
                ["SendResultEBusEvent"] = new[] { (0, "bus"), (1, "event") },
                ["InvokeStaticMethod"] = new[] { (0, "class"), (1, "method") },
                ["InvokeInstanceMethod"] = new[] { (1, "method") },
                ["InvokeGlobalMethod"] = new[] { (0, "method") },
                ["GetProperty"] = new[] { (1, "property") },
                ["SetProperty"] = new[] { (1, "property") },
                ["GetGlobalProperty"] = new[] { (0, "property") },
                ["SetGlobalProperty"] = new[] { (0, "property") },
                ["CreateInstance"] = new[] { (0, "class") },
            };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(s_rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
        }

        private static void Analyze(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            // Cheap syntactic reject first: only the watched method names
            // matter. Two syntax shapes reach here - `NativeReflection.Foo(...)`
            // (MemberAccessExpressionSyntax) and an unqualified `Foo(...)`
            // via `using static O3DE.Reflection.NativeReflection;`
            // (IdentifierNameSyntax). The semantic ContainingType check
            // below is what actually decides correctness in both cases;
            // this is just a cheap pre-reject on the name.
            string? methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                _ => null,
            };
            if (methodName is null)
            {
                return;
            }
            if (!s_watchedMethods.TryGetValue(methodName, out (int Index, string Kind)[] checkedArgs))
            {
                return;
            }

            // Then confirm it really is NativeReflection and not a same-named
            // method on some unrelated type.
            if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol symbol)
            {
                return;
            }
            if (symbol.ContainingType?.ToDisplayString() != NativeReflectionFullName)
            {
                return;
            }

            // Forwarding call inside one of the wrapper types? Skip it - see
            // s_selfCallExemptTypes. The outer call (if reached from game
            // code) is what actually gets analyzed.
            string? containingType = context.ContainingSymbol?.ContainingType?.ToDisplayString();
            if (containingType is not null && s_selfCallExemptTypes.Contains(containingType))
            {
                return;
            }

            var args = invocation.ArgumentList.Arguments;
            int maxIndex = checkedArgs.Max(c => c.Index);
            if (args.Count <= maxIndex)
            {
                return;
            }

            foreach ((int index, string kind) in checkedArgs)
            {
                if (!IsConstant(context, args[index].Expression))
                {
                    Report(context, args[index].Expression, methodName, kind);
                }
            }
        }

        private static bool IsConstant(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
        {
            // GetConstantValue folds literals, const fields and constant
            // concatenation ("Tick" + "Bus"), which is exactly the set the
            // generator can also resolve. Interpolated strings are not folded,
            // and correctly so - their value is a runtime decision.
            return context.SemanticModel.GetConstantValue(expression).HasValue;
        }

        private static void Report(
            SyntaxNodeAnalysisContext context,
            ExpressionSyntax expression,
            string methodName,
            string argumentKind)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(s_rule, expression.GetLocation(), methodName, argumentKind));
        }
    }
}
