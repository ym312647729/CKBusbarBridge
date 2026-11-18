using System.Collections.Generic;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        internal const string ObstacleAwareGridTopologySignature = "OBSTACLE-AWARE-GRID";

        // 顺序避让重规划用：网格 A* 给出的轴向骨架走原实体化入口，
        // 搭接、折弯、最短段、包围盒、静态绝缘等候选门禁全部照常执行。
        internal static LinkageRouteCandidate AddObstacleAwareGridSeed(LinkageRouteCandidatePool pool,
            IDictionary<string, LinkageRouteCandidate> evaluated, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, IReadOnlyList<LinkageRouteSolid> staticSolids,
            IList<Point3d> skeleton, CancellationToken token)
        {
            pool.EvaluatedSkeletonCount++;
            return BuildAndAddUniqueCandidate(pool, evaluated, request, group, skeleton, skeleton, staticSolids,
                false, ObstacleAwareGridTopologySignature, LinkageRouteTraversalDirection.Forward, token);
        }
    }
}
