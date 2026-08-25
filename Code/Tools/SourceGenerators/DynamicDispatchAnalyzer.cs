/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 */

using System.Collections.Immutable;
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

        // Method name -> (bus arg index, event arg index). Both must be
        // constant for the call to be statically dispatchable.
        private const string BroadcastName = "BroadcastEBusEvent";
        private const string SendName = "SendEBusEvent";
        private const string BroadcastResultName = "BroadcastResultEBusEvent";
        private const string SendResultName = "SendResultEBusEvent";
        private const string NativeReflectionFullName = "O3DE.Reflection.NativeReflection";

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

            // Cheap syntactic reject first: only four method names matter.
            // Two syntax shapes reach here - `NativeReflection.Foo(...)`
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
            if (methodName != BroadcastName && methodName != SendName &&
                methodName != BroadcastResultName && methodName != SendResultName)
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

            // NativeReflection's own Result-suffixed wrappers forward their
            // (necessarily non-constant, they're just method parameters) bus
            // and event names to the plain Broadcast/Send methods internally
            // - e.g. BroadcastResultEBusEvent calls BroadcastEBusEvent(busName,
            // eventName, args). That's implementation plumbing inside
            // NativeReflection itself, not a game call site the closed-world
            // generator ever sees, and it would otherwise warn unconditionally
            // on every use of the Result variants regardless of what the real
            // caller passed. Skip it; the outer call (if reached from game
            // code) is what actually gets analyzed.
            if (context.ContainingSymbol?.ContainingType?.ToDisplayString() == NativeReflectionFullName)
            {
                return;
            }

            var args = invocation.ArgumentList.Arguments;
            if (args.Count < 2)
            {
                return;
            }

            // Argument 0 is the bus name, argument 1 the event name, on all
            // four overloads (the addressed variants take the bus id third).
            if (!IsConstant(context, args[0].Expression))
            {
                Report(context, args[0].Expression, methodName, "bus");
            }
            if (!IsConstant(context, args[1].Expression))
            {
                Report(context, args[1].Expression, methodName, "event");
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
