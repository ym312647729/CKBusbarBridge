using System;
using System.Linq;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    public static partial class LinkageSchemeLibraryRouteRebuilder
    {
        private static void FitNetworkIProjectedBounds(HistoricalPhysicalNetwork resizeNetwork, double resizeMinimumLength)
        {
            foreach (var resizeSource in resizeNetwork.Sources.Values.Where(source =>
                !source.IsFixed && !source.IsLockedBridge && source.Setting.PointList.Count == 2))
            {
                var resizeProposal = resizeSource.Setting.Copy();
                foreach (var resizeContact in resizeNetwork.Contacts.Where(contact =>
                    contact.First == resizeSource || contact.Second == resizeSource))
                {
                    if (HasNetworkContact(resizeContact)) continue;
                    var resizeOpposingSource = resizeContact.First == resizeSource ? resizeContact.Second : resizeContact.First;
                    var resizeOpposingSegment = resizeContact.First == resizeSource ? resizeContact.SecondSegment : resizeContact.FirstSegment;
                    var resizeOpposingSlice = NetworkSegment(resizeOpposingSource.Setting, resizeOpposingSegment);
                    if (resizeOpposingSlice == null) continue;
                    // 按唯一物理源汇总两端外延需求，再一次验收；不重新求解厚向位移。
                    ExtendHistoricalInterfaceEnd(resizeProposal, true, resizeOpposingSlice, true);
                    ExtendHistoricalInterfaceEnd(resizeProposal, false, resizeOpposingSlice, true);
                }
                if (resizeProposal.PointList.Zip(resizeSource.Setting.PointList,
                    (proposed, current) => proposed.DistanceTo(current)).Any(distance => distance > DirectPlacementTolerance))
                    TryResizeNetworkIEndpoints(resizeNetwork, resizeSource, resizeProposal, resizeMinimumLength);
                TrimNetworkIExcessEndpoints(resizeNetwork, resizeSource, resizeMinimumLength);
            }
        }

        private static void TrimNetworkIExcessEndpoints(HistoricalPhysicalNetwork trimNetwork,
            HistoricalPhysicalSource trimSource, double trimMinimumLength)
        {
            var trimOrigin = trimSource.Setting.PointList[0];
            var trimAxis = new Vector3d(trimOrigin, trimSource.Setting.PointList[1]).Normalized;
            var trimMinimum = double.PositiveInfinity;
            var trimMaximum = double.NegativeInfinity;
            foreach (var trimContact in trimNetwork.Contacts.Where(contact => contact.First == trimSource || contact.Second == trimSource))
            {
                var trimFirst = NetworkSegment(trimContact.First.Setting, trimContact.FirstSegment);
                var trimSecond = NetworkSegment(trimContact.Second.Setting, trimContact.SecondSegment);
                if (trimFirst == null || trimSecond == null ||
                    !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(trimFirst, true, trimSecond, true,
                        Math.Max(trimFirst.BusBarWidth, trimSecond.BusBarWidth), out var trimPatch)) return;
                if (trimContact.Region?.Count > 0)
                {
                    if (!TryRestrictHistoricalContactRegion(trimContact, trimPatch)) return;
                }
                else
                {
                    var trimOriginalFirst = OriginalNetworkSegment(trimContact.First, trimContact.FirstSegment);
                    var trimOriginalSecond = OriginalNetworkSegment(trimContact.Second, trimContact.SecondSegment);
                    if (trimOriginalFirst == null || trimOriginalSecond == null ||
                        !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(trimOriginalFirst, true, trimOriginalSecond, true,
                            Math.Max(trimOriginalFirst.BusBarWidth, trimOriginalSecond.BusBarWidth), out var trimOriginalPatch)) return;
                    var trimOriginalSource = OriginalNetworkSegment(trimSource, 0);
                    var trimOriginalAxis = new Vector3d(trimOriginalSource.PointList[0], trimOriginalSource.PointList[1]).Normalized;
                    var trimOriginalCoordinates = trimOriginalPatch.Corners.Select(point =>
                        new Vector3d(trimOriginalSource.PointList[0], point).Dot(trimOriginalAxis)).ToList();
                    var trimNeededSpan = trimOriginalCoordinates.Max() - trimOriginalCoordinates.Min();
                    var trimCoordinates = trimPatch.Corners.Select(point => new Vector3d(trimOrigin, point).Dot(trimAxis)).ToList();
                    if (trimCoordinates.Max() - trimCoordinates.Min() + DirectPlacementTolerance < trimNeededSpan) return;
                    var trimAtStart = trimOriginalCoordinates.Average() <=
                        trimOriginalSource.PointList[0].DistanceTo(trimOriginalSource.PointList[1]) / 2;
                    trimMinimum = Math.Min(trimMinimum, trimAtStart ? trimCoordinates.Max() - trimNeededSpan : trimCoordinates.Min());
                    trimMaximum = Math.Max(trimMaximum, trimAtStart ? trimCoordinates.Max() : trimCoordinates.Min() + trimNeededSpan);
                    continue;
                }
                var trimRegionCoordinates = trimPatch.Corners.Select(point => new Vector3d(trimOrigin, point).Dot(trimAxis)).ToList();
                trimMinimum = Math.Min(trimMinimum, trimRegionCoordinates.Min());
                trimMaximum = Math.Max(trimMaximum, trimRegionCoordinates.Max());
            }
            if (double.IsInfinity(trimMinimum) || double.IsInfinity(trimMaximum)) return;
            var trimProposal = trimSource.Setting.Copy();
            trimProposal.PointList[0] = MoveHistoricalPoint(trimOrigin, trimAxis * trimMinimum);
            trimProposal.PointList[1] = MoveHistoricalPoint(trimOrigin, trimAxis * trimMaximum);
            TryResizeNetworkIEndpoints(trimNetwork, trimSource, trimProposal, trimMinimumLength);
        }

        private static PlanarBendBusbarSetting OriginalNetworkSegment(HistoricalPhysicalSource originalSource, int originalSegment)
        {
            var originalMember = originalSource.OriginalMember;
            if (originalMember?.CenterlinePoints == null || originalMember.WidthDirection == null) return null;
            return NetworkSegment(new PlanarBendBusbarSetting
            {
                PointList = originalMember.CenterlinePoints.Select(HistoricalEvidencePoint).ToList(),
                WidthVector = ToVector(originalMember.WidthDirection),
                BusBarWidth = originalMember.BusbarWidth, BusBarThickness = originalMember.BusbarThickness
            }, originalSegment);
        }

        // 2026-10-07用户确认：保持直线和排规，允许沿排长伸缩；厚向位移仍沿I链传递。
        private static bool TryApplyNetworkIAdjustment(HistoricalPhysicalNetwork resizeNetwork,
            HistoricalPhysicalSource resizeSource, HistoricalPhysicalSource resizeAnchor,
            PlanarBendBusbarSetting resizeFitted, double resizeMinimumLength, CancellationToken resizeCancellation)
        {
            if (TryApplyNetworkITranslation(resizeNetwork, resizeSource, resizeAnchor,
                    resizeFitted, resizeMinimumLength, resizeCancellation)) return true;
            if (resizeCancellation.IsCancellationRequested || resizeSource.IsFixed || resizeSource.IsLockedBridge ||
                resizeSource.Setting.PointList.Count != 2 || resizeFitted?.PointList?.Count != 2) return false;
            var resizeAxis = new Vector3d(resizeSource.Setting.PointList[0], resizeSource.Setting.PointList[1]).Normalized;
            var resizeFirstDelta = new Vector3d(resizeSource.Setting.PointList[0], resizeFitted.PointList[0]);
            var resizeSecondDelta = new Vector3d(resizeSource.Setting.PointList[1], resizeFitted.PointList[1]);
            var resizeTransverse = resizeFirstDelta - resizeAxis * resizeFirstDelta.Dot(resizeAxis);
            if ((resizeSecondDelta - resizeAxis * resizeSecondDelta.Dot(resizeAxis) - resizeTransverse).Norm >
                DirectPlacementTolerance) return false;
            if (resizeTransverse.Norm > DirectPlacementTolerance)
            {
                var resizeTranslated = resizeSource.Setting.Copy();
                TranslateHistoricalPart(resizeTranslated, resizeTransverse);
                if (!TryApplyNetworkITranslation(resizeNetwork, resizeSource, resizeAnchor,
                        resizeTranslated, resizeMinimumLength, resizeCancellation)) return false;
            }
            return !resizeCancellation.IsCancellationRequested &&
                TryResizeNetworkIEndpoints(resizeNetwork, resizeSource, resizeFitted, resizeMinimumLength);
        }

        private static bool TryResizeNetworkIEndpoints(HistoricalPhysicalNetwork resizeNetwork,
            HistoricalPhysicalSource resizeSource, PlanarBendBusbarSetting resizeProposal, double resizeMinimumLength)
        {
            if (resizeSource.IsFixed || resizeSource.IsLockedBridge || resizeSource.Setting?.PointList?.Count != 2 ||
                resizeProposal?.PointList?.Count != 2 || resizeProposal.WidthVector == null ||
                Math.Abs(resizeProposal.BusBarWidth - resizeSource.Setting.BusBarWidth) > DirectPlacementTolerance ||
                Math.Abs(resizeProposal.BusBarThickness - resizeSource.Setting.BusBarThickness) > DirectPlacementTolerance ||
                (resizeProposal.WidthVector - resizeSource.Setting.WidthVector).Norm > DirectPlacementTolerance) return false;
            var resizeOriginalDirection = new Vector3d(resizeSource.Setting.PointList[0], resizeSource.Setting.PointList[1]);
            var resizeProposedDirection = new Vector3d(resizeProposal.PointList[0], resizeProposal.PointList[1]);
            if (resizeOriginalDirection.Norm <= DirectPlacementTolerance ||
                resizeProposedDirection.Norm + DirectPlacementTolerance < resizeMinimumLength ||
                resizeProposedDirection.Dot(resizeOriginalDirection) <= 0d) return false;
            var resizeUnitAxis = resizeOriginalDirection.Normalized;
            if (new Vector3d(resizeSource.Setting.PointList[0], resizeProposal.PointList[0]).Cross(resizeUnitAxis).Norm > DirectPlacementTolerance ||
                new Vector3d(resizeSource.Setting.PointList[1], resizeProposal.PointList[1]).Cross(resizeUnitAxis).Norm > DirectPlacementTolerance ||
                !PreservesFixedNetworkContacts(resizeNetwork, resizeSource, resizeProposal, checkStationarySources: true)) return false;
            foreach (var resizeContact in resizeNetwork.Contacts.Where(contact => contact.First == resizeSource || contact.Second == resizeSource))
            {
                // 长度调整不能截掉已经成功的支路，也不能缩小原直接接口的有效投影。
                if (HasNetworkContact(resizeContact) && !HasNetworkContact(resizeContact, resizeSource, resizeProposal)) return false;
                var resizeFirstSlice = NetworkSegment(resizeContact.First == resizeSource ? resizeProposal : resizeContact.First.Setting,
                    resizeContact.FirstSegment);
                var resizeSecondSlice = NetworkSegment(resizeContact.Second == resizeSource ? resizeProposal : resizeContact.Second.Setting,
                    resizeContact.SecondSegment);
                if (resizeFirstSlice == null || resizeSecondSlice == null ||
                    (resizeContact.Region?.Count > 0
                        ? !LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(resizeFirstSlice, true, resizeSecondSlice, true,
                            Math.Max(resizeFirstSlice.BusBarWidth, resizeSecondSlice.BusBarWidth), out var resizeRegion) ||
                            !TryRestrictHistoricalContactRegion(resizeContact, resizeRegion, resizeSource, resizeProposal)
                        : !PreservesOriginalDirectContactPatch(resizeContact, resizeFirstSlice, resizeSecondSlice))) return false;
            }
            resizeSource.Setting = resizeProposal.Copy();
            return true;
        }
    }
}
