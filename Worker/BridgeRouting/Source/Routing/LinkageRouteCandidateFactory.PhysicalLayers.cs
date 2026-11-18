using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        internal static LinkageRouteCandidatePool BuildPhysicalLayerSeedPool(
            LinkageMultiGroupRouteRequest request,LinkageRouteGroupRequest group,
            IReadOnlyList<LinkageRouteSolid> fixedSolids,CancellationToken cancellationToken,
            Action<LinkageRouteCandidatePool> completedCandidate = null)
        {
            var pool=new LinkageRouteCandidatePool { GroupRequest=group };
            pool.TraversalDirections.Add(LinkageRouteTraversalDirection.Forward);
            var evaluated=new Dictionary<string,LinkageRouteCandidate>(StringComparer.Ordinal);
            try
            {
                foreach(var skeleton in LinkageRouteAxisPathBuilder.BuildPhysicalLayerCandidates(request,group,fixedSolids,cancellationToken))
                {
                    if(cancellationToken.IsCancellationRequested) break;
                    pool.EvaluatedSkeletonCount++;
                    BuildAndAddUniqueCandidate(pool,evaluated,request,group,skeleton,skeleton,fixedSolids,
                        false,"PHYSICAL-LAYER-XYZ",LinkageRouteTraversalDirection.Forward,cancellationToken);
                    completedCandidate?.Invoke(pool);
                    if(pool.ValidCandidates.Count>=request.SearchOptions.MaximumCandidatesPerGroup) break;
                }
            }
            catch(OperationCanceledException stopped) when(cancellationToken.IsCancellationRequested && stopped.CancellationToken==cancellationToken) { }
            // Only completed candidate objects enter this list. The owner decides
            // whether to continue after its slice; no truncated cache is created.
            return pool;
        }
    }
}
