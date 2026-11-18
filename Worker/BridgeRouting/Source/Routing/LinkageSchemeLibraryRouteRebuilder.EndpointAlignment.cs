using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteLinkBar.Algorithm.Routing
{
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        internal static bool TryValidateHistoricalPerpendicularEndpoints(
            LinkageMultiGroupRouteRequest request, LinkageRouteSolution solution,
            out string diagnostic)
        {
            diagnostic = null;
            foreach (var group in solution.GroupSolutions)
            {
                var groupRequest = request.Groups.FirstOrDefault(item =>
                    string.Equals(item.GroupKey, group.GroupKey, StringComparison.OrdinalIgnoreCase));
                if (groupRequest == null) { diagnostic = "历史方案缺少端头验证的目标组。"; return false; }
                foreach (var member in group.StackMembers)
                {
                    if (!TryNormalizeHistoricalGraphPerpendicularEndpoints(member.PlanarParts,
                        groupRequest, request.MinimumSegmentLength, false, out diagnostic))
                    {
                        diagnostic = "组 " + group.GroupKey + "：" + diagnostic;
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool TryNormalizeHistoricalGraphPerpendicularEndpoints(
            IList<PlanarBendBusbarSetting> parts, LinkageRouteGroupRequest group,
            double minimumSegmentLength, bool applyAdjustment, out string diagnostic)
        {
            var boundaries = new List<HistoricalBoundaryRequirement>();
            for (var firstIndex = 0; firstIndex < parts.Count; firstIndex++)
            {
                var first = parts[firstIndex];
                foreach (var fixedMember in group.StartTerminal.Members.Concat(group.EndTerminal.Members))
                    if (TrySelectHistoricalContactEnds(first, fixedMember.FixedBusbar,
                        out var generatedStart, out var fixedStart, out _))
                        CollectHistoricalBoundary(first, generatedStart, fixedMember.FixedBusbar, fixedStart, boundaries);
                for (var secondIndex = firstIndex + 1; secondIndex < parts.Count; secondIndex++)
                {
                    var second = parts[secondIndex];
                    if (!TrySelectHistoricalContactEnds(first, second, out var firstStart, out var secondStart, out _))
                        continue;
                    CollectHistoricalBoundary(first, firstStart, second, secondStart, boundaries);
                    CollectHistoricalBoundary(second, secondStart, first, firstStart, boundaries);
                }
            }
            return TryApplyHistoricalBoundaryRequirements(boundaries, minimumSegmentLength, applyAdjustment, out diagnostic);
        }

        private static bool TryNormalizeHistoricalPerpendicularEndpoints(
            IList<PlanarBendBusbarSetting> parts, LinkageRouteGroupRequest group,
            double minimumSegmentLength, bool applyAdjustment, out string diagnostic)
        {
            diagnostic = null;
            if (parts == null || parts.Count == 0) { diagnostic = "历史排端头验证缺少实体。"; return false; }
            // 同一物理排的全部接口统一决定端头，不能逐接口回收截断其它始端。
            var boundaries = new List<HistoricalBoundaryRequirement>();
            if (!TryCollectHistoricalTerminalEnds(parts[0], group.StartTerminal, boundaries, out diagnostic) ||
                !TryCollectHistoricalTerminalEnds(parts[parts.Count - 1], group.EndTerminal,
                    boundaries, out diagnostic)) return false;
            for (var index = 0; index + 1 < parts.Count; index++)
            {
                if (!TrySelectHistoricalContactEnds(parts[index], parts[index + 1],
                    out var firstStart, out var secondStart, out _))
                { diagnostic = "历史内部接口未形成实际搭接面，无法处理末端。"; return false; }
                CollectHistoricalBoundary(parts[index], firstStart, parts[index + 1], secondStart, boundaries);
                CollectHistoricalBoundary(parts[index + 1], secondStart, parts[index], firstStart, boundaries);
            }
            return TryApplyHistoricalBoundaryRequirements(boundaries, minimumSegmentLength, applyAdjustment, out diagnostic);
        }

        private static bool TryApplyHistoricalBoundaryRequirements(
            IList<HistoricalBoundaryRequirement> boundaries, double minimumSegmentLength,
            bool applyAdjustment, out string diagnostic)
        {
            diagnostic = null;
            foreach (var endpointGroup in boundaries.GroupBy(boundary =>
                Tuple.Create(boundary.Generated, boundary.UseStart)))
            {
                var generated = endpointGroup.Key.Item1;
                var useStart = endpointGroup.Key.Item2;
                var endpointIndex = useStart ? 0 : generated.PointList.Count - 1;
                var interiorIndex = useStart ? 1 : generated.PointList.Count - 2;
                var interior = generated.PointList[interiorIndex];
                var outward = new Vector3d(interior, generated.PointList[endpointIndex]);
                var coveringBoundary = endpointGroup.OrderByDescending(boundary =>
                    new Vector3d(interior, boundary.RequiredEndpoint).Dot(outward)).First();
                if (!TryAlignHistoricalPerpendicularEnd(generated, useStart,
                    coveringBoundary.Opposing, coveringBoundary.OpposingStart,
                    minimumSegmentLength, applyAdjustment, out diagnostic)) return false;
            }
            if (boundaries.Any(boundary => !HasHistoricalLap(boundary.Generated, boundary.UseStart,
                boundary.Opposing, boundary.OpposingStart, LinkageRouteGeometryTolerances.FinalAcceptanceComparison)))
            { diagnostic = "垂直搭接端头回归后未保留全部实际满搭接。"; return false; }
            return true;
        }

        private static bool TryCollectHistoricalTerminalEnds(PlanarBendBusbarSetting generated,
            LinkageTerminalAssembly terminal, IList<HistoricalBoundaryRequirement> boundaries,
            out string diagnostic)
        {
            diagnostic = null;
            var contactCount = 0;
            foreach (var member in terminal?.Members ?? new List<LinkageTerminalMember>())
            {
                if (TrySelectHistoricalContactEnds(generated, member?.FixedBusbar,
                    out var candidateGeneratedStart, out var candidateFixedStart, out _))
                {
                    contactCount++;
                    CollectHistoricalBoundary(generated, candidateGeneratedStart, member.FixedBusbar,
                        candidateFixedStart, boundaries);
                }
            }
            if (contactCount == 0) { diagnostic = "历史端部未形成实际搭接面，无法处理末端。"; return false; }
            return true;
        }

        private static bool TrySelectHistoricalContactEnds(PlanarBendBusbarSetting first,
            PlanarBendBusbarSetting second, out bool useFirstStart, out bool useSecondStart,
            out double nearestEndpointDistance)
        {
            useFirstStart = false;
            useSecondStart = false;
            nearestEndpointDistance = double.PositiveInfinity;
            if (first?.PointList == null || second?.PointList == null ||
                first.PointList.Count < 2 || second.PointList.Count < 2) return false;
            foreach (var firstStart in new[] { true, false })
            foreach (var secondStart in new[] { true, false })
            {
                if (!LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(
                    first, firstStart, second, secondStart,
                    Math.Max(first.BusBarWidth, second.BusBarWidth), out var footprint,
                    tolerance: LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) continue;
                var center = new Point3d(footprint.Corners.Average(point => point.X),
                    footprint.Corners.Average(point => point.Y), footprint.Corners.Average(point => point.Z));
                var endpointDistance = first.PointList[firstStart ? 0 : first.PointList.Count - 1].DistanceTo(center) +
                    second.PointList[secondStart ? 0 : second.PointList.Count - 1].DistanceTo(center);
                if (endpointDistance < nearestEndpointDistance)
                {
                    nearestEndpointDistance = endpointDistance;
                    useFirstStart = firstStart;
                    useSecondStart = secondStart;
                }
            }
            return !double.IsPositiveInfinity(nearestEndpointDistance);
        }

        private sealed class HistoricalBoundaryRequirement
        {
            public PlanarBendBusbarSetting Generated { get; set; }
            public bool UseStart { get; set; }
            public PlanarBendBusbarSetting Opposing { get; set; }
            public bool OpposingStart { get; set; }
            public Point3d RequiredEndpoint { get; set; }
        }

        private static bool TryAlignHistoricalPerpendicularEnd(PlanarBendBusbarSetting generated,
            bool useGeneratedStart, PlanarBendBusbarSetting opposing, bool useOpposingStart,
            double minimumSegmentLength, bool applyAdjustment, out string diagnostic)
        {
            diagnostic = null;
            if (!LinkageRouteSectionSpecificationPostProcessor.TryGetPerpendicularBoundaryEndpoint(
                generated, useGeneratedStart, opposing, useOpposingStart, opposing.BusBarWidth,
                out var requiredEndpoint)) return true;
            var endpointIndex = useGeneratedStart ? 0 : generated.PointList.Count - 1;
            var interiorIndex = useGeneratedStart ? 1 : generated.PointList.Count - 2;
            var originalEndpoint = generated.PointList[endpointIndex];
            var endpointError = originalEndpoint.DistanceTo(requiredEndpoint);
            if (endpointError <= (applyAdjustment ? DirectPlacementTolerance :
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) return true;
            if (!applyAdjustment)
            {
                diagnostic = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "垂直搭接末端偏离全部实际接口的共同覆盖边界 {0:F6} mm。", endpointError);
                return false;
            }
            var interior = generated.PointList[interiorIndex];
            var oldOutward = new Vector3d(interior, originalEndpoint);
            var newOutward = new Vector3d(interior, requiredEndpoint);
            if (newOutward.Norm + DirectPlacementTolerance < minimumSegmentLength ||
                newOutward.Dot(oldOutward) <= DirectPlacementTolerance)
            { diagnostic = "垂直搭接端头回归后原直腿不足或反向。"; return false; }
            generated.PointList[endpointIndex] = requiredEndpoint;
            if (!HasHistoricalLap(generated, useGeneratedStart, opposing, useOpposingStart,
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
            { diagnostic = "垂直搭接端头回归后未形成实际满搭接。"; return false; }
            return true;
        }

        private static void CollectHistoricalBoundary(PlanarBendBusbarSetting generated,
            bool useGeneratedStart, PlanarBendBusbarSetting opposing, bool useOpposingStart,
            IList<HistoricalBoundaryRequirement> boundaries)
        {
            // Use the ordinary algorithm's exact endpoint formula, including
            // trimming excess length. Do not move the fixed bar or bend point.
            if (!LinkageRouteSectionSpecificationPostProcessor.TryGetPerpendicularBoundaryEndpoint(
                generated, useGeneratedStart, opposing, useOpposingStart, opposing.BusBarWidth,
                out var requiredEndpoint)) return;
            boundaries.Add(new HistoricalBoundaryRequirement
            {
                Generated = generated, UseStart = useGeneratedStart, Opposing = opposing,
                OpposingStart = useOpposingStart, RequiredEndpoint = requiredEndpoint
            });
        }
    }
}
