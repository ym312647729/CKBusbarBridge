using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteSectionSpecificationPostProcessor
    {
        // One exact geometric boundary per affected interface, not a second path search.
        // When thickness compaction is blocked, a fully internal straight-I receiving
        // section may slide along its width. Both neighbouring free endpoints recover
        // full lap using the existing perpendicular endpoint geometry. Fixed ends stay put.
        private static bool TrySlideInternalIInterfaceForRoundedLap(
            LinkageRouteCenterlineConversionResult conversion, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, Func<bool> validateGeometry, out string failureReason)
        {
            failureReason = null;
            if (conversion == null || request == null || validateGeometry == null ||
                (!request.AllowBendGeometricLimit && !request.AllowConstrainedBendAllowanceReduction) ||
                !TryCollectRoundedLapRequirements(conversion, group, out var state,
                    out var requirements, out _)) return false;
            var shortages = requirements.Where(item => !item.IsFixed && item.SectionIndex >= 0 &&
                GetRoundedExtension(item, request, true) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                .GroupBy(item => new { item.SectionIndex, item.AtStart }).ToList();
            if (shortages.Count == 0) return false;
            var snapshot = conversion.StackMembers.SelectMany(member => member.PlanarParts).Distinct()
                .ToDictionary(part => part, part => part.Copy());
            var diagnosticCount = conversion.Diagnostics.Count;
            foreach (var boundary in shortages)
            {
                RestoreCompactionTrial(conversion, snapshot, diagnosticCount);
                var receivingIndex = boundary.Key.SectionIndex + (boundary.Key.AtStart ? -1 : 1);
                if (!state.SettingsBySectionIndex.TryGetValue(receivingIndex, out var receivers) ||
                    !state.SettingsBySectionIndex.TryGetValue(receivingIndex - 1, out var preceding) ||
                    !state.SettingsBySectionIndex.TryGetValue(receivingIndex + 1, out var following) ||
                    new[] { receivingIndex - 1, receivingIndex, receivingIndex + 1 }.Any(index =>
                        conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(index)) ||
                    receivers.Count == 0 || receivers.Any(item => item.Setting.PointList.Count != 2)) continue;
                var shortagePart = boundary.First().Member;
                var shortagePoints = shortagePart.PointList;
                var outward = boundary.Key.AtStart ? new Vector3d(shortagePoints[1], shortagePoints[0]).Normalized :
                    new Vector3d(shortagePoints[shortagePoints.Count - 2], shortagePoints.Last()).Normalized;
                if (receivers.Any(item => Math.Abs(item.Setting.WidthVector.Normalized.Dot(outward)) < 1d - GeometryTolerance))
                    continue;
                var slideDistance = boundary.Max(item => GetRoundedExtension(item, request, true));
                foreach (var receiver in receivers)
                    receiver.Setting.PointList = receiver.Setting.PointList.Select(point =>
                        LinkageRouteSolidGeometry.Translate(point, outward, slideDistance)).ToList();
                if (!TryRecoverSlidingInterfaceEnds(preceding, receivers, false, outward))
                { failureReason="前侧端头不能沿原直段恢复满搭接。"; continue; }
                if (!TryRecoverSlidingInterfaceEnds(following, receivers, true, outward))
                { failureReason="后侧端头不能沿原直段恢复满搭接。"; continue; }
                if (!PreservesCompactionDirectionsAndAnchors(conversion, snapshot))
                { failureReason="端头恢复会改变原直段方向或固定端锚点。"; continue; }
                if (!TryValidateRoundedLapInterfaces(conversion, request, group, out failureReason)) continue;
                if (!validateGeometry())
                { failureReason="端头及圆弧通过，但完整实体约束仍拒绝。"; continue; }
                RefreshRoundedCopperLength(conversion);
                conversion.Diagnostics.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "物理段{0}内部I接口沿排宽同向滑移{1:F3} mm，并按真实对侧排宽补齐两侧自由端；端部锚点及折弯不动，圆弧和完整实体复验通过。",
                    receivingIndex + 1, slideDistance));
                return true;
            }
            RestoreCompactionTrial(conversion, snapshot, diagnosticCount);
            failureReason = failureReason ?? "内部I接口无可行的同向排宽滑移。";
            return false;
        }

        private static bool TryRecoverSlidingInterfaceEnds(IEnumerable<SectionMemberSetting> hosts,
            IEnumerable<SectionMemberSetting> receivers, bool atStart, Vector3d slideDirection)
        {
            var receivingParts = receivers.Select(item => item.Setting).ToList();
            foreach (var host in hosts)
            {
                var part = host.Setting;
                var endpointIndex = atStart ? 0 : part.PointList.Count - 1;
                var interiorIndex = atStart ? 1 : part.PointList.Count - 2;
                var hostOutward = new Vector3d(part.PointList[interiorIndex], part.PointList[endpointIndex]);
                if (hostOutward.Norm <= GeometryTolerance ||
                    hostOutward.Normalized.Dot(slideDirection) < 1d - GeometryTolerance) return false;
                var receiver = FindNearestBoundarySetting(part, atStart, receivingParts, !atStart);
                if (receiver == null || !TryGetPerpendicularBoundaryEndpoint(part, atStart,
                        receiver, !atStart, receiver.BusBarWidth, out var requiredEndpoint)) return false;
                // This is endpoint recovery along the original segment, not an independent
                // thickness solver or a movement of the fixed endpoint of a terminal I bar.
                var requiredExtension = new Vector3d(part.PointList[endpointIndex], requiredEndpoint).Dot(slideDirection);
                if (requiredExtension > GeometryTolerance) part.PointList[endpointIndex] = requiredEndpoint;
                if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(part, atStart,
                    receiver, !atStart, Math.Min(part.BusBarWidth, receiver.BusBarWidth), out _,
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) return false;
            }
            return true;
        }
    }
}
