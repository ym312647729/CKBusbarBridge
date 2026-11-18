using System;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        internal static bool IsHistoricalInterfaceSupportBlock(LinkageRouteAccessory accessory) =>
            accessory?.Kind == LinkageRouteAccessoryKind.SupportBlock &&
            (accessory.SupportBlockKey ?? "").StartsWith("historical-interface:", StringComparison.Ordinal);

        // Only historical materialized pads use their real full face as the
        // required patch. Ordinary busbar laps retain the existing width rule.
        internal static bool TryGetHistoricalSupportBlockContact(PlanarBendBusbarSetting first,
            PlanarBendBusbarSetting second, bool firstIsBlock, bool secondIsBlock,
            out PlanarBendBusbarSetting firstSlice, out PlanarBendBusbarSetting secondSlice,
            out LinkageRouteLapFootprint footprint)
        {
            firstSlice = secondSlice = null;
            footprint = null;
            if ((!firstIsBlock && !secondIsBlock) || first?.PointList == null || second?.PointList == null ||
                first.PointList.Count < 2 || second.PointList.Count < 2 ||
                firstIsBlock && first.PointList.Count != 2 || secondIsBlock && second.PointList.Count != 2) return false;
            for (var firstSegment = 0; firstSegment + 1 < first.PointList.Count; firstSegment++)
            for (var secondSegment = 0; secondSegment + 1 < second.PointList.Count; secondSegment++)
            {
                var leftSlice = NetworkSegment(first, firstSegment);
                var rightSlice = NetworkSegment(second, secondSegment);
                var blockSlice = firstIsBlock ? leftSlice : rightSlice;
                if (blockSlice == null || !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                    blockSlice, true, blockSlice, true, blockSlice.BusBarWidth, out var requiredPatch) ||
                    !TryCreateHistoricalPatchContact(leftSlice, rightSlice, requiredPatch, out var actualPatch)) continue;
                if (firstIsBlock && secondIsBlock &&
                    (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(rightSlice, true, rightSlice, true,
                        rightSlice.BusBarWidth, out var secondPatch) || !HistoricalPatchContains(actualPatch, secondPatch))) continue;
                firstSlice = leftSlice;
                secondSlice = rightSlice;
                footprint = actualPatch;
                return true;
            }
            return false;
        }

        private static bool TryCreateHistoricalPatchContact(PlanarBendBusbarSetting first,
            PlanarBendBusbarSetting second, LinkageRouteLapFootprint requiredPatch, out LinkageRouteLapFootprint actualPatch)
        {
            actualPatch = null;
            if (first == null || second == null || !IsFinitePositiveHistoricalDimension(first.BusBarThickness) ||
                !IsFinitePositiveHistoricalDimension(second.BusBarThickness) ||
                !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(first, true, second, true,
                    Math.Max(first.BusBarWidth, second.BusBarWidth), out var projectedPatch)) return false;
            var thicknessDistance = Math.Abs(new Vector3d(first.PointList[0], second.PointList[0]).Dot(projectedPatch.PlaneNormal));
            if (Math.Abs(thicknessDistance - (first.BusBarThickness + second.BusBarThickness) / 2) >
                LinkageRouteGeometryTolerances.FinalAcceptanceComparison || !HistoricalPatchContains(projectedPatch, requiredPatch)) return false;
            actualPatch = projectedPatch;
            return true;
        }

        private static bool HistoricalPatchContains(LinkageRouteLapFootprint available, LinkageRouteLapFootprint required)
        {
            if (available?.Corners == null || required?.Corners == null || available.Corners.Count < 4 || required.Corners.Count < 4 ||
                available.PlaneNormal.Cross(required.PlaneNormal).Norm > LinkageRouteGeometryTolerances.Direction) return false;
            var origin = available.Corners[0];
            foreach (var axis in new[] { available.LengthDirection, available.WidthDirection })
            {
                var suppliedCoordinates = available.Corners.Select(point => new Vector3d(origin, point).Dot(axis)).ToList();
                var requiredCoordinates = required.Corners.Select(point => new Vector3d(origin, point).Dot(axis)).ToList();
                if (requiredCoordinates.Min() < suppliedCoordinates.Min() - LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                    requiredCoordinates.Max() > suppliedCoordinates.Max() + LinkageRouteGeometryTolerances.FinalAcceptanceComparison) return false;
            }
            return true;
        }
    }
}
