using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing.Algorithms
{
    internal static partial class LinkageMultiGroupRouteEngine
    {
        private const double CorridorPointTolerance = LinkageRouteGeometryTolerances.PointCoincidence;

        // 补充快速候选。仅三相参与规划；组合时仍保留 N、PE 和其他组的原候选及固定实体。
        private static IReadOnlyList<LinkageRouteCandidatePool> BuildCommonPhaseCorridorPools(
            SearchContext context, IReadOnlyList<LinkageRouteCandidatePool> ordinaryPools)
        {
            var request = context.RouteRequest;
            if (!request.RequireAxisAlignedRoutes || !request.UseExplicitContainmentBox ||
                context.MainSearchCancellationToken.IsCancellationRequested)
                return null;

            var phases = request.Groups.Where(group => group.Phase == "A" || group.Phase == "B" || group.Phase == "C").ToArray();
            if (phases.Length != 3 || phases.Select(group => group.Phase).Distinct().Count() != 3 ||
                phases.Any(group => group.StartTerminal?.RouteAnchor == null || group.EndTerminal?.RouteAnchor == null))
                return null;

            var lengthAxis = CorridorAxis(phases[0].EndTerminal.LengthDirection);
            var liftAxis = CorridorAxis(phases[0].EndTerminal.ThicknessDirection);
            if (lengthAxis < 0 || liftAxis < 0 || lengthAxis == liftAxis ||
                phases.Any(group => CorridorAxis(group.StartTerminal.LengthDirection) != lengthAxis ||
                    CorridorAxis(group.EndTerminal.LengthDirection) != lengthAxis ||
                    CorridorAxis(group.StartTerminal.ThicknessDirection) != liftAxis ||
                    CorridorAxis(group.EndTerminal.ThicknessDirection) != liftAxis))
                return null;

            var widthAxis = 3 - lengthAxis - liftAxis;
            double WidthCoordinate(Point3d point) => CorridorCoordinate(point, widthAxis);
            double LiftCoordinate(Point3d point) => CorridorCoordinate(point, liftAxis);
            var order = phases.OrderBy(group => WidthCoordinate(group.StartTerminal.RouteAnchor)).ToArray();
            var ranks = new int[order.Length];
            for (var current = 0; current < order.Length; current++)
            for (var earlier = 0; earlier < current; earlier++)
            {
                if (WidthCoordinate(order[current].StartTerminal.RouteAnchor) -
                    WidthCoordinate(order[earlier].StartTerminal.RouteAnchor) <= CorridorPointTolerance)
                    return null;
                if (WidthCoordinate(order[earlier].EndTerminal.RouteAnchor) -
                    WidthCoordinate(order[current].EndTerminal.RouteAnchor) > CorridorPointTolerance)
                    ranks[current] = Math.Max(ranks[current], ranks[earlier] + 1);
            }
            var layerCount = ranks.Max() + 1;
            if (layerCount == 1) return null;

            var sign = Math.Sign(CorridorCoordinate(order[0].EndTerminal.RouteAnchor, lengthAxis) -
                CorridorCoordinate(order[0].StartTerminal.RouteAnchor, lengthAxis));
            double Along(Point3d point) => sign * CorridorCoordinate(point, lengthAxis);
            if (sign == 0 || phases.Any(group => Along(group.EndTerminal.RouteAnchor) <= Along(group.StartTerminal.RouteAnchor)))
                return null;
            var rangeStart = phases.Max(group => Along(group.StartTerminal.RouteAnchor));
            var gap = phases.Min(group => Along(group.EndTerminal.RouteAnchor)) - rangeStart;
            var width = phases.Max(group => group.GetBusbarWidth(request));
            var leadReserve = Math.Max(width * 1.5d, request.MinimumSegmentLength * 2d);
            if (gap < 6d * leadReserve) return null;

            var liftMin = LiftCoordinate(request.ContainmentMinimum) + width / 2d;
            var liftMax = LiftCoordinate(request.ContainmentMaximum) - width / 2d;
            if (liftMax <= liftMin) return null;
            var lowerAnchor = phases.Min(group => Math.Min(LiftCoordinate(group.StartTerminal.RouteAnchor),
                LiftCoordinate(group.EndTerminal.RouteAnchor)));
            var higherAnchor = phases.Max(group => Math.Max(LiftCoordinate(group.StartTerminal.RouteAnchor),
                LiftCoordinate(group.EndTerminal.RouteAnchor)));
            var options = request.SearchOptions;
            var budget = (int)Math.Min(options.FirstSolutionTargetMilliseconds - context.SearchStopwatch.ElapsedMilliseconds,
                2L * options.ExtendedFastSearchStageLimitMilliseconds);
            if (budget <= 0) return null;

            Point3d CorridorPoint(double along, double lift, double across)
            {
                var axes = new double[3];
                axes[lengthAxis] = sign * along;
                axes[liftAxis] = lift;
                axes[widthAxis] = across;
                return new Point3d(axes[0], axes[1], axes[2]);
            }

            var corridorPools = ordinaryPools.ToArray();
            using (var slice = CancellationTokenSource.CreateLinkedTokenSource(context.MainSearchCancellationToken))
            {
                slice.CancelAfter(budget);
                Parallel.For(0, order.Length, new ParallelOptions
                    { MaxDegreeOfParallelism = ResolveMaximumDegreeOfParallelism(request) }, phaseIndex =>
                {
                    if (slice.IsCancellationRequested) return;
                    var group = order[phaseIndex];
                    var groupIndex = request.Groups.IndexOf(group);
                    var startLevels = GetCorridorLapPoints(request, group, group.StartTerminal, group.EndTerminal,
                        true, lengthAxis, widthAxis, sign);
                    var endLevels = GetCorridorLapPoints(request, group, group.EndTerminal, group.StartTerminal,
                        false, lengthAxis, widthAxis, sign);
                    var groupThickness = group.GetBusbarThickness(request);
                    var extra = new LinkageRouteCandidatePool { GroupRequest = group };
                    extra.TraversalDirections.Add(LinkageRouteTraversalDirection.Forward);
                    var evaluated = new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
                    try
                    {
                        foreach (var endpoints in (from start in startLevels from end in endLevels select (Start: start, End: end))
                            .OrderBy(pair => Math.Abs(LiftCoordinate(pair.Start) - LiftCoordinate(group.StartTerminal.RouteAnchor)) +
                                Math.Abs(LiftCoordinate(pair.End) - LiftCoordinate(group.EndTerminal.RouteAnchor))))
                        {
                            var fromLift = LiftCoordinate(endpoints.Start);
                            var toLift = LiftCoordinate(endpoints.End);
                            var fromWidth = WidthCoordinate(endpoints.Start);
                            var toWidth = WidthCoordinate(endpoints.End);
                            double[] LiftSamples(int rank)
                            {
                                if (rank == 0) return new[] { fromLift, lowerAnchor, liftMin, toLift };
                                if (rank == layerCount - 1) return new[] { liftMax - groupThickness * 3d, liftMax - groupThickness,
                                    (liftMax + higherAnchor) / 2d, higherAnchor, toLift };
                                return new[] { higherAnchor, (liftMin + liftMax) / 2d, toLift, fromLift };
                            }
                            var normalLifts = LiftSamples(ranks[phaseIndex]).Distinct().ToArray();
                            var mirroredLifts = LiftSamples(layerCount - 1 - ranks[phaseIndex]).Distinct().ToArray();
                            for (var sample = 0; sample < Math.Max(normalLifts.Length, mirroredLifts.Length); sample++)
                            foreach (var direction in new[] { -1, 1 })
                            foreach (var spread in new[] { 0.18d, 0.25d, 0.12d, 0d })
                            foreach (var lifts in new[] { normalLifts, mirroredLifts })
                            foreach (var lead in new[] { leadReserve, leadReserve * 1.5d, leadReserve * 0.75d })
                            {
                                if (slice.IsCancellationRequested || extra.ValidCandidates.Count >= options.MaximumCandidatesPerGroup) return;
                                if (sample >= lifts.Length) continue;
                                var lift = lifts[sample];
                                if (lift < liftMin - CorridorPointTolerance || lift > liftMax + CorridorPointTolerance) continue;
                                var crossing = rangeStart + gap * (0.5d + direction * (phaseIndex - 1) * spread);
                                var points = new List<Point3d> { endpoints.Start };
                                void Add(double along, double height, double across)
                                {
                                    var next = CorridorPoint(along, height, across);
                                    if (points[points.Count - 1].DistanceTo(next) > CorridorPointTolerance) points.Add(next);
                                }

                                if (Math.Abs(lift - fromLift) > CorridorPointTolerance)
                                {
                                    Add(crossing - lead, fromLift, fromWidth);
                                    Add(crossing - lead, lift, fromWidth);
                                }
                                Add(crossing, lift, fromWidth);
                                Add(crossing, lift, toWidth);
                                if (Math.Abs(lift - toLift) > CorridorPointTolerance)
                                {
                                    Add(crossing + lead, lift, toWidth);
                                    Add(crossing + lead, toLift, toWidth);
                                }
                                points.Add(endpoints.End);
                                if (points.Zip(points.Skip(1), (first, second) => first.DistanceTo(second))
                                    .Any(distance => distance < request.MinimumSegmentLength - CorridorPointTolerance)) continue;
                                LinkageRouteCandidateFactory.AddCommonPhaseCorridorSeed(extra, evaluated, request, group,
                                    context.FixedTerminalAndObstacleSolids, points, slice.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (slice.IsCancellationRequested) { }
                    finally
                    {
                        if (extra.ValidCandidates.Count != 0) corridorPools[groupIndex] = extra;
                    }
                });
            }
            return corridorPools.Where((pool, index) => !ReferenceEquals(pool, ordinaryPools[index])).Any() &&
                !HasEmptyCandidatePool(corridorPools) ? corridorPools : null;
        }

        private static Point3d[] GetCorridorLapPoints(LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, LinkageTerminalAssembly terminal, LinkageTerminalAssembly opposite,
            bool atStart, int lengthAxis, int widthAxis, int directionSign)
        {
            var width = group.GetBusbarWidth(request);
            var thickness = group.GetBusbarThickness(request);
            var count = group.GetStackCount(request);
            var reserve = LinkageRouteStackGeometry.GetStackBendAllowance(thickness, count,
                group.StackMiddleSpace, request.GetEffectiveBendAllowance());
            var straight = Math.Max(request.MinimumSegmentLength, width + reserve);
            var pose = LinkageRouteTerminalPoseFactory.Build(terminal, opposite.RouteAnchor, atStart,
                    width, straight, straight, request.RequireFixedTerminalEndLap)
                .Where(candidate => !candidate.IsPerpendicularLap && CorridorAxis(candidate.RouteDirection) == lengthAxis &&
                    Math.Sign(CorridorComponent(candidate.RouteDirection, lengthAxis)) == directionSign)
                .OrderBy(candidate => Math.Abs(CorridorCoordinate(candidate.LapEndpoint, widthAxis) -
                    CorridorCoordinate(terminal.RouteAnchor, widthAxis))).FirstOrDefault();
            if (pose == null || !LinkageRouteStackPlacementSolver.TryMeasureBaseCenterOffset(
                terminal, pose.LapEndpoint, out var poseOffset))
                return Array.Empty<Point3d>();
            var placements = LinkageRouteStackPlacementSolver.ResolveTerminalPlacements(terminal, count,
                thickness, atStart ? group.StackStartSpace : group.StackEndSpace, group.StackMiddleSpace, false, atStart);
            if (atStart && request.EnableSingleStartConnectionBar)
                placements = LinkageRouteTerminalAccessoryBuilder.PrepareSingleStartHorizontalConnectionPlacements(
                    placements, request, group, thickness);
            var normal = terminal.ThicknessDirection.Normalized;
            return placements.Select(placement => placement.BaseCenterOffset - poseOffset).Distinct()
                .Select(offset => new Point3d(pose.LapEndpoint.X + normal.X * offset,
                    pose.LapEndpoint.Y + normal.Y * offset, pose.LapEndpoint.Z + normal.Z * offset))
                .ToArray();
        }

        private static int CorridorAxis(Vector3d direction)
        {
            if (direction == null || direction.Norm <= LinkageRouteGeometryTolerances.ExactDirection) return -1;
            var axis = direction.Normalized;
            var tolerance = LinkageRouteGeometryTolerances.Direction;
            if (Math.Abs(axis.Y) <= tolerance && Math.Abs(axis.Z) <= tolerance) return 0;
            if (Math.Abs(axis.X) <= tolerance && Math.Abs(axis.Z) <= tolerance) return 1;
            if (Math.Abs(axis.X) <= tolerance && Math.Abs(axis.Y) <= tolerance) return 2;
            return -1;
        }

        private static double CorridorCoordinate(Point3d point, int axis) => axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;
        private static double CorridorComponent(Vector3d vector, int axis) => axis == 0 ? vector.X : axis == 1 ? vector.Y : vector.Z;
    }
}
