using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteCandidateFactory
    {
        private static void AddHistoricalLapClearanceEnvelope(IDictionary<string, LinkageRouteSolid> destination,
            PlanarBendBusbarSetting target, bool preferredTargetStart, PlanarBendBusbarSetting neighbor,
            bool preferredNeighborStart, ISet<PlanarBendBusbarSetting> historicalBlocks,
            LinkageRouteGroupRequest group, double clearance, string interfaceKey, double tolerance)
        {
            if (destination == null || target == null || neighbor == null || group == null) return;
            var targetIsBlock = historicalBlocks.Contains(target);
            var neighborIsBlock = historicalBlocks.Contains(neighbor);
            if (!targetIsBlock && !neighborIsBlock)
            {
                // Matching uses the actual patch center, not a rectangle edge as
                // the centerline. Keep the ordinary-path envelope builder isolated.
                foreach (var targetStart in new[] { preferredTargetStart, !preferredTargetStart })
                foreach (var neighborStart in new[] { preferredNeighborStart, !preferredNeighborStart })
                {
                    if (!LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(target, targetStart,
                        neighbor, neighborStart, Math.Max(target.BusBarWidth, neighbor.BusBarWidth),
                        out var directPatch, tolerance: tolerance)) continue;
                    AddCenteredHistoricalLapClearanceEnvelope(destination,
                        CreateBoundarySolidForLapEnvelope(target, targetStart, group),
                        CreateBoundarySolidForLapEnvelope(neighbor, neighborStart, group),
                        directPatch, group, clearance, interfaceKey);
                    return;
                }
                return;
            }
            if (!LinkageSchemeLibraryRouteRebuilder.TryGetHistoricalSupportBlockContact(target, neighbor,
                targetIsBlock, neighborIsBlock, out var targetSlice, out var neighborSlice, out var patch)) return;
            AddCenteredHistoricalLapClearanceEnvelope(destination,
                CreateBoundarySolidForLapEnvelope(targetSlice, true, group),
                CreateBoundarySolidForLapEnvelope(neighborSlice, true, group), patch, group, clearance, interfaceKey);
        }

        private static void AddCenteredHistoricalLapClearanceEnvelope(IDictionary<string, LinkageRouteSolid> destination,
            LinkageRouteSolid targetBoundary, LinkageRouteSolid neighborBoundary, LinkageRouteLapFootprint patch,
            LinkageRouteGroupRequest group, double clearance, string interfaceKey)
        {
            if (targetBoundary == null || neighborBoundary == null || patch?.Corners == null || patch.Corners.Count < 4 ||
                patch.PlaneNormal == null || patch.PlaneNormal.Norm <= GeometryTolerance) return;
            var outward = patch.PlaneNormal.Normalized;
            if (new Vector3d(targetBoundary.Center, neighborBoundary.Center).Dot(outward) > 0) outward *= -1;
            var patchOrigin = patch.Corners[0];
            var lengths = patch.Corners.Select(point => new Vector3d(patchOrigin, point).Dot(patch.LengthDirection)).ToList();
            var widths = patch.Corners.Select(point => new Vector3d(patchOrigin, point).Dot(patch.WidthDirection)).ToList();
            if (lengths.Max() - lengths.Min() <= GeometryTolerance || widths.Max() - widths.Min() <= GeometryTolerance) return;
            var widthCenter = LinkageRouteSolidGeometry.Translate(patchOrigin, patch.WidthDirection, (widths.Min() + widths.Max()) / 2);
            var centerlineStart = LinkageRouteSolidGeometry.Translate(widthCenter, patch.LengthDirection, lengths.Min());
            var centerlineEnd = LinkageRouteSolidGeometry.Translate(widthCenter, patch.LengthDirection, lengths.Max());
            var envelope = LinkageRouteSolidGeometry.CreateSegmentSolid(group.GroupKey, centerlineStart, centerlineEnd,
                patch.WidthDirection, outward, widths.Max() - widths.Min(), clearance, -1,
                group.GetClearanceGroupKey(), LinkageRouteSolidRole.LapClearanceEnvelope, "LAP-CLEARANCE-" + interfaceKey);
            envelope.Center = LinkageRouteSolidGeometry.Translate(envelope.Center, outward,
                targetBoundary.HalfThickness * Math.Abs(targetBoundary.ThicknessDirection.Dot(outward)) + clearance / 2);
            destination[interfaceKey] = envelope;
        }
    }
}
