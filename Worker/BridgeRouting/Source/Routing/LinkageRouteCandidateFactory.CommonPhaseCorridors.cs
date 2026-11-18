using System.Collections.Generic;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        // 通道仅提供骨架，叠排实体、搭接、圆弧、净距及包围盒仍使用原候选入口。
        internal static void AddCommonPhaseCorridorSeed(LinkageRouteCandidatePool pool,
            IDictionary<string, LinkageRouteCandidate> evaluated, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, IReadOnlyList<LinkageRouteSolid> fixedSolids,
            IList<Point3d> skeleton, CancellationToken token)
        {
            pool.EvaluatedSkeletonCount++;
            BuildAndAddUniqueCandidate(pool, evaluated, request, group, skeleton, skeleton, fixedSolids,
                false, "THREE-PHASE-COMMON-CORRIDOR", LinkageRouteTraversalDirection.Forward, token);
        }
    }
}
