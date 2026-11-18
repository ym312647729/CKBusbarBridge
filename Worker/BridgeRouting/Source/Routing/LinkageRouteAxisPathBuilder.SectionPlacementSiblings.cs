using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteAxisPathBuilder
    {
        // Only propose another already permitted physical terminal pose. Each
        // straight segment keeps its direction; the candidate factory rebuilds
        // all local thickness contacts and bend intersections from the request.
        internal static IEnumerable<IList<Point3d>> BuildTerminalPoseAlignedSiblings(
            LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group,
            IList<Point3d> source,
            CancellationToken token)
        {
            if (source == null || source.Count < 3 || !IsAxisAligned(source)) yield break;
            var signatures = new HashSet<string>(StringComparer.Ordinal) { GetSignature(source) };
            var initialDirection = new Vector3d(source[0], source[1]);
            var finalDirection = new Vector3d(source[source.Count - 2], source.Last());
            foreach (var useStart in new[] { true, false })
            {
                var endpointIndex = useStart ? 0 : source.Count - 1;
                var step = useStart ? 1 : -1;
                var originalEndpoint = source[endpointIndex];
                var terminal = useStart ? group.StartTerminal : group.EndTerminal;
                var opposite = useStart ? group.EndTerminal : group.StartTerminal;
                var actualWidth = useStart ? group.GetStartLapBusbarWidth(request) : group.GetCabinetTopLapBusbarWidth(request);
                var poseEndpoints = (request.RequireFixedTerminalEndLap
                    ? GetFixedTerminalLapSearchWidthPairs(request, group).Select(pair => pair[useStart ? 0 : 1])
                    : new[] { actualWidth, group.GetBusbarWidth(request) }).Distinct()
                    .SelectMany(width => WithHoleCenteredPoses(request,group,useStart,LinkageRouteTerminalPoseFactory.BuildWithShortStartLapPolicy(
                        terminal, opposite.RouteAnchor, useStart, width,
                        Math.Max(request.MinimumSegmentLength, width),
                        Math.Max(request.MinimumSegmentLength, GetCollectivePerpendicularLapSpan(terminal, width)),
                        request.MinimumSegmentLength, null,
                        IsDoubleStartCollectiveParallelLapAllowed(request,group,LinkageRouteTraversalDirection.Forward,useStart),
                        IsSingleStartMiddleParallelLapRequired(request,group,LinkageRouteTraversalDirection.Forward,useStart),
                        false, 0d, GetWidthSizedOverlapBoundaryReferencePoint(group,
                            group.GetStartLapBusbarWidth(request),group.GetCabinetTopLapBusbarWidth(request)),
                        group.GetShortPhysicalStartLapLength(request,useStart),
                requireFixedTerminalEndLap: request.RequireFixedTerminalEndLap,
                        minimumParallelLapLength: group.GetPhysicalStartParallelLapLength(request,useStart))))
                    .Select(pose => pose.LapEndpoint)
                    .GroupBy(point => GetSignature(new[] { point }))
                    .Select(points => points.First()).ToArray();
                for (var axis = 0; axis < 3; axis++)
                {
                    // Terminal thickness layers are fixed by their real faces.
                    // Only exchange an allowed in-plane attachment coordinate.
                    if (Math.Abs(Component(terminal.ThicknessDirection.Normalized,axis)) > GeometryTolerance) continue;
                    var originalCoordinate = GetCoordinate(originalEndpoint, axis);
                    var lastRunIndex = endpointIndex;
                    while (lastRunIndex + step >= 0 && lastRunIndex + step < source.Count &&
                        Math.Abs(GetCoordinate(source[lastRunIndex + step], axis) - originalCoordinate) <= GeometryTolerance)
                        lastRunIndex += step;
                    if (lastRunIndex == endpointIndex || lastRunIndex == (useStart ? source.Count - 1 : 0)) continue;
                    foreach (var poseEndpoint in poseEndpoints)
                    {
                        if (token.IsCancellationRequested) yield break;
                        var coordinate = GetCoordinate(poseEndpoint, axis);
                        if (Math.Abs(coordinate - originalCoordinate) <= GeometryTolerance) continue;
                        var sibling = source.Select(Copy).ToList();
                        for (var index = endpointIndex; ; index += step)
                        {
                            sibling[index] = WithCoordinate(sibling[index], axis, coordinate);
                            if (index == lastRunIndex) break;
                        }
                        var normalized = Normalize(sibling);
                        if (!HasMinimumSegments(normalized, request.MinimumSegmentLength)) continue;
                        var accepted = new List<IList<Point3d>>();
                        AddCandidate(accepted, normalized, initialDirection, finalDirection, signatures);
                        foreach (var candidate in accepted) yield return candidate;
                    }
                }
            }
        }
    }
}
