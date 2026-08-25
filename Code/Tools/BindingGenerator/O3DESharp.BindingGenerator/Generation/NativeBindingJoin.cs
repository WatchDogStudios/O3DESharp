/*
 * Copyright (c) Contributors to the Open 3D Engine Project.
 * For complete copyright and license terms please see the LICENSE at the root of this distribution.
 *
 * SPDX-License-Identifier: Apache-2.0 OR MIT
 *
 */

using System;
using System.Collections.Generic;
using System.Linq;
using O3DESharp.BindingGenerator.Configuration;

namespace O3DESharp.BindingGenerator.Generation
{
    /// <summary>One recovered `->Method("name", &amp;C::Method)` reflection call site.</summary>
    /// <param name="ClassName">Reflected class name, matching the manifest's owning_class_name.</param>
    /// <param name="ReflectedName">The script-visible name passed to ->Method(...).</param>
    /// <param name="NativeQualifiedSymbol">e.g. "AZ::Vector3::GetLength". Empty when ViaLambda.</param>
    /// <param name="ViaLambda">True when the call site passed a lambda/wrapper rather than &amp;C::Method.</param>
    public sealed record CallSiteSymbol(
        string ClassName,
        string ReflectedName,
        string NativeQualifiedSymbol,
        bool ViaLambda);

    /// <summary>Aggregate outcome of a join, for logging and coverage assertions.</summary>
    public sealed record JoinReport(
        int Total,
        int Bound,
        int Unbound,
        IReadOnlyDictionary<string, int> ReasonCounts);

    /// <summary>
    /// Joins the C++ runtime manifest against libclang-recovered call sites and
    /// classifies what may be bound as a native trampoline.
    ///
    /// Deliberately pure: no file, no libclang, no engine. The join is the one
    /// place an error yields a WRONG binding rather than a missing one - calling
    /// a function pointer with a mismatched signature - so it has to be provable
    /// with plain in-memory data.
    ///
    /// Every ambiguity resolves toward NOT binding. An unbound method falls back
    /// to BehaviorMethod::Call and merely costs speed.
    /// </summary>
    public static class NativeBindingJoin
    {
        private const string StorageUnknown = "Unknown";

        public static JoinReport Apply(
            NativeBindingManifestDocument manifest,
            IReadOnlyCollection<CallSiteSymbol> callSites)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(callSites);

            // Key on (className, reflectedName). Never on reflectedName alone:
            // Transform::GetLength and Vector3::GetLength are different methods
            // that share a script name, and cross-joining them would bind a
            // plausible-looking but wrong pointer.
            var byKey = new Dictionary<(string, string), List<CallSiteSymbol>>();
            foreach (var site in callSites)
            {
                var key = (site.ClassName, site.ReflectedName);
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new List<CallSiteSymbol>();
                    byKey[key] = list;
                }
                list.Add(site);
            }

            var reasons = new Dictionary<string, int>();
            int bound = 0;

            foreach (var method in manifest.Methods)
            {
                var reason = Classify(method, byKey, out string symbol);

                if (reason == "None")
                {
                    method.NativeQualifiedSymbol = symbol;
                    method.Bindable = true;
                    method.NonBindableReason = "None";
                    bound++;
                }
                else
                {
                    method.NativeQualifiedSymbol = string.Empty;
                    method.Bindable = false;
                    method.NonBindableReason = reason;
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                }
            }

            return new JoinReport(
                manifest.Methods.Count, bound, manifest.Methods.Count - bound, reasons);
        }

        private static string Classify(
            NativeBindingManifestMethod method,
            Dictionary<(string, string), List<CallSiteSymbol>> byKey,
            out string symbol)
        {
            symbol = string.Empty;

            if (!byKey.TryGetValue((method.OwningClassName, method.ReflectedName), out var sites)
                || sites.Count == 0)
            {
                return "UnresolvedNativeSymbol";
            }

            // More than one distinct &C::Method under a single script name is an
            // overload set. Choosing either is a coin flip; bind neither.
            var distinct = sites
                .Select(s => s.NativeQualifiedSymbol)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (sites.Count > 1 && distinct.Count > 1)
            {
                return "Overloaded";
            }

            if (sites.Any(s => s.ViaLambda))
            {
                return "ReflectedViaLambda";
            }

            if (string.IsNullOrEmpty(distinct[0]))
            {
                return "UnresolvedNativeSymbol";
            }

            // "void" is spelled with StorageClass Value but is not a value the
            // emitter has to unpack, so it must not trip the Unknown check.
            bool returnIsVoid = string.Equals(method.Return?.CppTypeName, "void", StringComparison.Ordinal);
            if (!returnIsVoid && method.Return?.StorageClass == StorageUnknown)
            {
                return "UnsupportedArgStorage";
            }

            if (method.Arguments.Any(a => a.StorageClass == StorageUnknown))
            {
                return "UnsupportedArgStorage";
            }

            symbol = distinct[0];
            return "None";
        }
    }
}
