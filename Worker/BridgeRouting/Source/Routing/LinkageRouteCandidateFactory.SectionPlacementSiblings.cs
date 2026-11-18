using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        internal static LinkageRouteCandidatePool BuildSectionPlacementSiblingSeedPool(
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group,
            LinkageRouteCandidatePool retainedPool, IReadOnlyList<LinkageRouteSolid> fixedSolids,
            CancellationToken token, Action<LinkageRouteCandidatePool> completedCandidate = null)
        {
            var pool = new LinkageRouteCandidatePool { GroupRequest = group };
            pool.TraversalDirections.Add(LinkageRouteTraversalDirection.Forward);
            if (retainedPool == null || (!group.HasSectionSpecificBusbarSettings(request) &&
                group.EndTerminal?.CabinetBridgeAssembly == null && !LinkageRouteTerminalHoleLapValidator.HasStartHoles(group))) return pool;
            var evaluated = new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            try
            {
                // A long retained skeleton exposes its movable internal planes.
                // Keep each earlier successful candidate in the caller's pool.
                foreach (var retained in retainedPool.ValidCandidates
                    .OrderByDescending(candidate => candidate.AxisSkeleton.Count))
                {
                    if (token.IsCancellationRequested) break;
                    foreach (var sibling in LinkageRouteAxisPathBuilder.BuildTerminalPoseAlignedSiblings(
                        request, group, retained.AxisSkeleton, token))
                    {
                        if (token.IsCancellationRequested) break;
                        pool.EvaluatedSkeletonCount++;
                        BuildAndAddUniqueCandidate(pool, evaluated, request, group, sibling, sibling,
                            fixedSolids, false, "SECTION-TERMINAL-POSE", LinkageRouteTraversalDirection.Forward, token);
                        completedCandidate?.Invoke(pool);
                        if (pool.ValidCandidates.Count >= request.SearchOptions.MaximumCandidatesPerGroup) return pool;
                    }
                    foreach (var sibling in LinkageRouteAxisPathBuilder.BuildCriticalPlaneAlignedSiblings(
                        request, group, retained.AxisSkeleton, fixedSolids, token))
                    {
                        if (token.IsCancellationRequested) break;
                        pool.EvaluatedSkeletonCount++;
                        BuildAndAddUniqueCandidate(pool, evaluated, request, group, sibling, sibling,
                            fixedSolids, false, "SECTION-CRITICAL-PLANE", LinkageRouteTraversalDirection.Forward, token);
                        completedCandidate?.Invoke(pool);
                        if (pool.ValidCandidates.Count >= request.SearchOptions.MaximumCandidatesPerGroup) return pool;
                    }
                }
            }
            catch (OperationCanceledException stopped) when (token.IsCancellationRequested && stopped.CancellationToken == token) { }
            return pool;
        }
    }
}
