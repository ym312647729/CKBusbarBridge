using System;
using System.Collections.Generic;
using System.Threading;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        // Full forward/reverse pools have already failed. Keep their completed
        // candidates and give each critical-coordinate segment layer a bounded
        // chance before reporting an empty group. Evaluate the original axis
        // immediately: length-only truncation can discard a legal LL conversion
        // in favour of shorter, unmanufacturable M/U paths.
        internal static LinkageRouteCandidatePool BuildEmptyPoolCriticalRepair(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            CancellationToken cancellationToken)
        {
            var repairPool = new LinkageRouteCandidatePool { GroupRequest = routeGroupRequest };
            var evaluatedCenterlines = new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            var maximumRepairCandidates = Math.Max(1,
                routeRequest.SearchOptions.MaximumClearanceRepairCandidatesPerState);
            var maximumLayerEvaluations = Math.Max(1,
                routeRequest.SearchOptions.ExtendedLayerMaximumEvaluatedSkeletonCount);
            for (var segmentCount = 2;
                segmentCount <= Math.Min(8, routeRequest.SearchOptions.ExtendedLayerMaximumSegmentCount);
                segmentCount++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                foreach (var direction in new[] { LinkageRouteTraversalDirection.Forward,
                    LinkageRouteTraversalDirection.Reverse })
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    repairPool.TraversalDirections.Add(direction);
                    using (var layerSlice = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        layerSlice.CancelAfter(routeRequest.SearchOptions.DeepTraversalTimeSliceMilliseconds);
                        var evaluatedInLayer = 0;
                        try
                        {
                            LinkageRouteAxisPathBuilder.VisitClearanceDrivenCandidatesForSegmentCount(
                                routeRequest, routeGroupRequest, fixedTerminalAndObstacleSolids,
                                segmentCount, axisSkeleton =>
                                {
                                    if (layerSlice.IsCancellationRequested) return false;
                                    repairPool.EvaluatedSkeletonCount++;
                                    evaluatedInLayer++;
                                    BuildAndAddUniqueCandidate(repairPool, evaluatedCenterlines,
                                        routeRequest, routeGroupRequest, axisSkeleton, axisSkeleton,
                                        fixedTerminalAndObstacleSolids, false, "XYZ-EMPTY-CRITICAL",
                                        direction, layerSlice.Token);
                                    return !layerSlice.IsCancellationRequested &&
                                        evaluatedInLayer < maximumLayerEvaluations &&
                                        repairPool.ValidCandidates.Count < maximumRepairCandidates;
                                }, null, direction, layerSlice.Token);
                        }
                        catch (OperationCanceledException stoppedLayer) when (
                            layerSlice.IsCancellationRequested &&
                            stoppedLayer.CancellationToken == layerSlice.Token)
                        {
                            // Only this slice's cancellation is consumed. Incomplete
                            // candidates never enter the pool; completed ones survive.
                        }
                    }
                    if (repairPool.ValidCandidates.Count >= maximumRepairCandidates) break;
                }
                if (repairPool.ValidCandidates.Count >= maximumRepairCandidates) break;
            }
            FinalizeCandidatePool(repairPool, maximumRepairCandidates,
                routeRequest.AllowPartialStartPerpendicularLap, cancellationToken);
            return repairPool;
        }
    }
}
