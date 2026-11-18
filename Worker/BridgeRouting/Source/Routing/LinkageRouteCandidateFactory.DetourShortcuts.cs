using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System.Collections.Generic;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        private static void AddDetourShortcutCandidates(LinkageRouteCandidatePool shortcutPool,
            IDictionary<string, LinkageRouteCandidate> evaluatedShortcuts, LinkageMultiGroupRouteRequest shortcutRequest,
            LinkageRouteCandidate shortcutSource, IReadOnlyList<LinkageRouteSolid> shortcutClearanceSolids,
            CancellationToken shortcutToken)
        {
            foreach (var shortcutLine in LinkageRouteAxisPathBuilder.BuildDetourShortcutCenterlines(
                shortcutRequest, shortcutSource.GroupRequest, shortcutSource.ManufacturableCenterline, shortcutToken))
            {
                if (shortcutToken.IsCancellationRequested) return;
                var shortcutSignature = GetCenterlineSignature(shortcutLine);
                if (evaluatedShortcuts.ContainsKey(shortcutSignature)) continue;
                shortcutPool.EvaluatedSkeletonCount++;
                var shortcutCandidate = BuildUniqueCandidateCore(shortcutRequest, shortcutSource.GroupRequest,
                    shortcutLine, shortcutLine, shortcutClearanceSolids, false, "DETOUR-SHORTCUT",
                    shortcutSource.TraversalDirection, shortcutToken, null);
                if (shortcutCandidate == null) continue;
                evaluatedShortcuts[shortcutSignature] = shortcutCandidate;
                if (shortcutCandidate.IsValid && shortcutCandidate.PlanarConversion.ActualCopperLength + GeometryTolerance <
                    shortcutSource.PlanarConversion.ActualCopperLength) AddCandidate(shortcutPool, shortcutCandidate);
            }
        }
    }
}
