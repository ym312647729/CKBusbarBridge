using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteAxisPathBuilder
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.ExactPointCoincidence;
        private sealed class TerminalOverlapPoseVariant
        {
            public LinkageRouteTerminalOverlapPosePair PosePair { get; set; }
            public bool UsesPartialStartPerpendicularLap { get; set; }
        }

        private static readonly int[][] ConnectorCoordinateOrders =
        {
            new[] { 0, 1, 2 },
            new[] { 0, 2, 1 },
            new[] { 1, 0, 2 },
            new[] { 1, 2, 0 },
            new[] { 2, 0, 1 },
            new[] { 2, 1, 0 }
        };

        public static IReadOnlyList<IList<Point3d>> BuildCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            return BuildCandidates(
                routeRequest,
                routeGroupRequest,
                CancellationToken.None);
        }

        public static IReadOnlyList<IList<Point3d>> BuildCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            CancellationToken cancellationToken)
        {
            return BuildCandidates(
                routeRequest,
                routeGroupRequest,
                LinkageRouteTraversalDirection.Forward,
                cancellationToken);
        }

        internal static IReadOnlyList<IList<Point3d>> BuildCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            routeRequest = routeRequest?.CreatePathSearchRequest();
            if (cancellationToken.IsCancellationRequested ||
                routeRequest == null || physicalRouteGroupRequest == null)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var traversalRouteGroupRequest = GetTraversalRouteGroup(
                physicalRouteGroupRequest,
                traversalDirection);
            var generatedSingleBusbarThickness = physicalRouteGroupRequest
                .GetBusbarThickness(routeRequest);
            var requiredPhysicalGroupInsulationClearance = physicalRouteGroupRequest
                .GetRequiredInsulationClearance(routeRequest);
            var maximumCandidateCount = Math.Max(
                256,
                Math.Max(1, routeRequest.SearchOptions.MaximumCandidatesPerGroup) * 8);
            var allowTraversalStartCollectiveParallelLap =
                IsDoubleStartCollectiveParallelLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    true);
            var allowTraversalEndCollectiveParallelLap =
                IsDoubleStartCollectiveParallelLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    false);
            var allowTraversalStartPartialPerpendicularLap =
                IsPartialStartPerpendicularLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    true);
            var allowTraversalEndPartialPerpendicularLap =
                IsPartialStartPerpendicularLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    false);
            var preserveTerminalLapVariants =
                allowTraversalStartPartialPerpendicularLap ||
                allowTraversalEndPartialPerpendicularLap;
            var fullLapTraversalCandidateSignatures =
                preserveTerminalLapVariants
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : null;
            var partialLapTraversalCandidateSignatures =
                preserveTerminalLapVariants
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : null;
            var traversalCandidates = BuildDirectionalCandidatesCore(
                routeRequest,
                traversalRouteGroupRequest,
                allowTraversalStartCollectiveParallelLap,
                allowTraversalEndCollectiveParallelLap,
                IsSingleStartMiddleParallelLapRequired(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    true),
                IsSingleStartMiddleParallelLapRequired(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    false),
                allowTraversalStartPartialPerpendicularLap,
                allowTraversalEndPartialPerpendicularLap,
                requiredPhysicalGroupInsulationClearance,
                fullLapTraversalCandidateSignatures,
                partialLapTraversalCandidateSignatures,
                cancellationToken);
            traversalCandidates = AdjustActualWidthInternalStraightSectionCandidates(
                traversalCandidates,
                routeRequest,
                traversalRouteGroupRequest);
            if (!preserveTerminalLapVariants)
            {
                return SelectNearestCandidatesByShape(
                    NormalizeTraversalCandidates(
                        traversalCandidates,
                        traversalDirection),
                    routeRequest,
                    physicalRouteGroupRequest,
                    generatedSingleBusbarThickness,
                    maximumCandidateCount,
                    IsSingleStartMiddleParallelLapRequired(
                        routeRequest,
                        physicalRouteGroupRequest,
                        LinkageRouteTraversalDirection.Forward,
                        true) ||
                    IsDoubleStartCollectiveParallelLapAllowed(
                        routeRequest,
                        physicalRouteGroupRequest,
                        LinkageRouteTraversalDirection.Forward,
                        true),
                    false,
                    traversalDirection == LinkageRouteTraversalDirection.Forward,
                    false,
                    null,
                    null,
                    cancellationToken);
            }

            var normalizedTraversalCandidates = new List<IList<Point3d>>();
            var fullLapPhysicalCandidateSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            var partialLapPhysicalCandidateSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var traversalCandidate in traversalCandidates)
            {
                var normalizedTraversalCandidate = NormalizeTraversalCandidate(
                    traversalCandidate,
                    traversalDirection);
                normalizedTraversalCandidates.Add(normalizedTraversalCandidate);
                var traversalCandidateSignature = GetSignature(
                    traversalCandidate);
                var normalizedTraversalCandidateSignature = GetSignature(
                    normalizedTraversalCandidate);
                if (fullLapTraversalCandidateSignatures.Contains(
                        traversalCandidateSignature))
                {
                    fullLapPhysicalCandidateSignatures.Add(
                        normalizedTraversalCandidateSignature);
                }

                if (partialLapTraversalCandidateSignatures.Contains(
                        traversalCandidateSignature) &&
                    !fullLapTraversalCandidateSignatures.Contains(
                        traversalCandidateSignature))
                {
                    partialLapPhysicalCandidateSignatures.Add(
                        normalizedTraversalCandidateSignature);
                }
            }

            return SelectNearestCandidatesByShape(
                normalizedTraversalCandidates,
                routeRequest,
                physicalRouteGroupRequest,
                generatedSingleBusbarThickness,
                maximumCandidateCount,
                IsSingleStartMiddleParallelLapRequired(
                    routeRequest,
                    physicalRouteGroupRequest,
                    LinkageRouteTraversalDirection.Forward,
                    true) ||
                IsDoubleStartCollectiveParallelLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    LinkageRouteTraversalDirection.Forward,
                    true),
                false,
                traversalDirection == LinkageRouteTraversalDirection.Forward,
                true,
                fullLapPhysicalCandidateSignatures,
                partialLapPhysicalCandidateSignatures,
                cancellationToken);
        }

        internal static IReadOnlyList<IList<Point3d>>
            BuildWidthSizedLengthOverlapCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            CancellationToken cancellationToken)
        {
            if (routeRequest?.RequireFixedTerminalEndLap != true)
                return BuildWidthSizedLengthOverlapCandidatesForWidths(
                    routeRequest,
                    routeGroupRequest,
                    cancellationToken,
                    0d,
                    0d);
            var widthBatches = new List<IList<Point3d>>();
            foreach (var widthPair in GetFixedTerminalLapSearchWidthPairs(routeRequest, routeGroupRequest))
            {
                if (cancellationToken.IsCancellationRequested) break;
                widthBatches.AddRange(BuildWidthSizedLengthOverlapCandidatesForWidths(routeRequest,
                    routeGroupRequest,
                    cancellationToken,
                    widthPair[0],
                    widthPair[1]));
            }
            return widthBatches.GroupBy(GetSignature).Select(batch => batch.First()).ToList();
        }

        private static IReadOnlyList<IList<Point3d>> BuildWidthSizedLengthOverlapCandidatesForWidths(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            CancellationToken cancellationToken,
            double lockedStartWidth,
            double lockedEndWidth)
        {
            routeRequest = routeRequest?.CreatePathSearchRequest();
            if (cancellationToken.IsCancellationRequested ||
                routeRequest == null ||
                routeGroupRequest?.StartTerminal == null ||
                routeGroupRequest.EndTerminal == null)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var generatedBusbarWidth = routeGroupRequest.GetBusbarWidth(
                routeRequest);
            var startPoseWidth = lockedStartWidth > GeometryTolerance ? lockedStartWidth : generatedBusbarWidth;
            var endPoseWidth = lockedEndWidth > GeometryTolerance ? lockedEndWidth : generatedBusbarWidth;
            var generatedSingleBusbarThickness = routeGroupRequest
                .GetBusbarThickness(routeRequest);
            var routeStackCount = routeGroupRequest.GetStackCount(routeRequest);
            var terminalBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    routeStackCount,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var startCollectivePerpendicularLapSpan =
                GetCollectivePerpendicularLapSpan(
                    routeGroupRequest.StartTerminal,
                    startPoseWidth);
            var endCollectivePerpendicularLapSpan =
                GetCollectivePerpendicularLapSpan(
                    routeGroupRequest.EndTerminal,
                    endPoseWidth);
            var startPerpendicularTerminalSegmentLength = Math.Max(
                routeRequest.MinimumSegmentLength,
                startCollectivePerpendicularLapSpan + terminalBendAllowance);
            var endPerpendicularTerminalSegmentLength = Math.Max(
                routeRequest.MinimumSegmentLength,
                endCollectivePerpendicularLapSpan + terminalBendAllowance);
            var terminalOverlapPosePairsWithVariants =
                new List<TerminalOverlapPoseVariant>();
            if (LinkageRouteTerminalPoseFactory
                    .TryBuildWidthSizedLengthOverlapPosePair(
                        routeGroupRequest.StartTerminal,
                        routeGroupRequest.EndTerminal,
                        startPoseWidth,
                        endPoseWidth,
                        startPerpendicularTerminalSegmentLength,
                        endPerpendicularTerminalSegmentLength,
                        false,
                        terminalBendAllowance,
                        terminalBendAllowance,
                        out var fullTerminalOverlapPosePair,
                requireFixedTerminalEndLap: routeRequest.RequireFixedTerminalEndLap))
            {
                terminalOverlapPosePairsWithVariants.Add(
                    new TerminalOverlapPoseVariant
                {
                    PosePair = fullTerminalOverlapPosePair,
                    UsesPartialStartPerpendicularLap = false
                });
            }

            if (routeRequest.AllowPartialStartPerpendicularLap &&
                LinkageRouteTerminalPoseFactory
                    .TryBuildWidthSizedLengthOverlapPosePair(
                        routeGroupRequest.StartTerminal,
                        routeGroupRequest.EndTerminal,
                        startPoseWidth,
                        endPoseWidth,
                        routeRequest.MinimumSegmentLength,
                        endPerpendicularTerminalSegmentLength,
                        true,
                        terminalBendAllowance,
                        terminalBendAllowance,
                        out var partialTerminalOverlapPosePair,
                requireFixedTerminalEndLap: routeRequest.RequireFixedTerminalEndLap))
            {
                terminalOverlapPosePairsWithVariants.Add(
                    new TerminalOverlapPoseVariant
                {
                    PosePair = partialTerminalOverlapPosePair,
                    UsesPartialStartPerpendicularLap = true
                });
            }

            if (terminalOverlapPosePairsWithVariants.Count == 0)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var startPerpendicularLapBendContinuationDistance =
                startCollectivePerpendicularLapSpan / 2d +
                terminalBendAllowance;
            var endPerpendicularLapBendContinuationDistance =
                endCollectivePerpendicularLapSpan / 2d +
                terminalBendAllowance;
            var startStraightDoublePerpendicularLapTerminalExtension =
                startCollectivePerpendicularLapSpan + startPoseWidth / 2d;
            var endStraightDoublePerpendicularLapTerminalExtension =
                endCollectivePerpendicularLapSpan + endPoseWidth / 2d;
            var startConversionTerminalSegmentLength =
                startCollectivePerpendicularLapSpan +
                terminalBendAllowance +
                routeRequest.MinimumSegmentLength;
            var endConversionTerminalSegmentLength =
                endCollectivePerpendicularLapSpan +
                terminalBendAllowance +
                routeRequest.MinimumSegmentLength;
            var startTerminalLayerOffsets = GetTerminalLayerOffsets(
                routeRequest,
                routeGroupRequest,
                routeGroupRequest.StartTerminal,
                routeStackCount,
                generatedSingleBusbarThickness,
                true);
            var endTerminalLayerOffsets = GetTerminalLayerOffsets(
                routeRequest,
                routeGroupRequest,
                routeGroupRequest.EndTerminal,
                routeStackCount,
                generatedSingleBusbarThickness,
                false);
            var overlapCandidatePaths = new List<IList<Point3d>>();
            var overlapCandidateSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            var fullLapOverlapCandidateSignatures =
                routeRequest.AllowPartialStartPerpendicularLap
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : null;
            var partialLapOverlapCandidateSignatures =
                routeRequest.AllowPartialStartPerpendicularLap
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : null;
            foreach (var terminalOverlapPoseVariant in
                terminalOverlapPosePairsWithVariants)
            {
                var terminalOverlapPosePair =
                    terminalOverlapPoseVariant.PosePair;
                foreach (var startTerminalLayerOffset in
                    !terminalOverlapPosePair.StartTerminalPose.IsPerpendicularLap &&
                    routeRequest.EnableSingleStartConnectionBar
                        ? GetTerminalLayerOffsets(routeRequest, routeGroupRequest,
                            routeGroupRequest.StartTerminal, routeStackCount,
                            generatedSingleBusbarThickness, true, true)
                        : startTerminalLayerOffsets)
                {
                    foreach (var endTerminalLayerOffset in
                        endTerminalLayerOffsets)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return Array.Empty<IList<Point3d>>();
                        }

                        var startLapPoint = Translate(
                            terminalOverlapPosePair.StartTerminalPose
                                .LapEndpoint,
                            routeGroupRequest.StartTerminal.ThicknessDirection
                                .Normalized,
                            startTerminalLayerOffset);
                        var endLapPoint = Translate(
                            terminalOverlapPosePair.EndTerminalPose.LapEndpoint,
                            routeGroupRequest.EndTerminal.ThicknessDirection
                                .Normalized,
                            endTerminalLayerOffset);
                        var shortestCandidateStartIndex =
                            overlapCandidatePaths.Count;
                        AddShortestCoplanarParallelConnectorCandidates(
                            overlapCandidatePaths,
                            startLapPoint,
                            endLapPoint,
                            terminalOverlapPosePair.StartTerminalPose
                                .RouteDirection,
                            terminalOverlapPosePair.EndTerminalPose
                                .RouteDirection,
                            terminalOverlapPosePair.StartTerminalPose
                                .RequiredTerminalSegmentLength,
                            terminalOverlapPosePair.EndTerminalPose
                                .RequiredTerminalSegmentLength,
                            overlapCandidateSignatures);
                        if (routeRequest.AllowPartialStartPerpendicularLap)
                        {
                            RecordTerminalLapVariantCandidateSignatures(
                                overlapCandidatePaths,
                                shortestCandidateStartIndex,
                                terminalOverlapPoseVariant
                                    .UsesPartialStartPerpendicularLap,
                                fullLapOverlapCandidateSignatures,
                                partialLapOverlapCandidateSignatures);
                        }
                        var startExitPoints = GetTerminalExtensionPoints(
                            startLapPoint,
                            endLapPoint,
                            terminalOverlapPosePair.StartTerminalPose
                                .RouteDirection,
                            terminalOverlapPosePair.StartTerminalPose
                                .RequiredTerminalSegmentLength,
                            startConversionTerminalSegmentLength,
                            startPerpendicularLapBendContinuationDistance,
                            startStraightDoublePerpendicularLapTerminalExtension,
                            true);
                        var endEntryPoints = GetTerminalExtensionPoints(
                            endLapPoint,
                            startLapPoint,
                            terminalOverlapPosePair.EndTerminalPose
                                .RouteDirection,
                            terminalOverlapPosePair.EndTerminalPose
                                .RequiredTerminalSegmentLength,
                            endConversionTerminalSegmentLength,
                            endPerpendicularLapBendContinuationDistance,
                            endStraightDoublePerpendicularLapTerminalExtension,
                            false);
                        foreach (var startExitPoint in startExitPoints)
                        {
                            foreach (var endEntryPoint in endEntryPoints)
                            {
                                var directCandidateStartIndex =
                                    overlapCandidatePaths.Count;
                                AddDirectConnectorCandidates(
                                    overlapCandidatePaths,
                                    startLapPoint,
                                    startExitPoint,
                                    endEntryPoint,
                                    endLapPoint,
                                    terminalOverlapPosePair.StartTerminalPose
                                        .RouteDirection,
                                    terminalOverlapPosePair.EndTerminalPose
                                        .RouteDirection,
                                    overlapCandidateSignatures);
                                if (routeRequest
                                    .AllowPartialStartPerpendicularLap)
                                {
                                    RecordTerminalLapVariantCandidateSignatures(
                                        overlapCandidatePaths,
                                        directCandidateStartIndex,
                                        terminalOverlapPoseVariant
                                            .UsesPartialStartPerpendicularLap,
                                        fullLapOverlapCandidateSignatures,
                                        partialLapOverlapCandidateSignatures);
                                }
                            }
                        }
                    }
                }
            }

            // 公共重叠区直搭的多条骨架可能具有相同的有向轴顺序和总长，
            // 但端点层位、转折坐标不同。普通候选按形状只保留最近一条会在
            // 实体绝缘校验前同时丢掉替代搭接层位和等长最短落位。这里按
            // “首末端点层位”分组轮询，每个层位有限保留多个最短转折位置；
            // 普通拓扑仍沿用原来的按形状压缩，不增加其候选数量。
            return AdjustActualWidthInternalStraightSectionCandidates(
                SelectWidthOverlapCandidatesByPlacement(
                overlapCandidatePaths,
                routeRequest,
                routeGroupRequest,
                generatedSingleBusbarThickness,
                Math.Max(
                    1,
                    Math.Min(
                        16,
                        routeRequest.SearchOptions.MaximumCandidatesPerGroup)),
                fullLapOverlapCandidateSignatures,
                partialLapOverlapCandidateSignatures,
                cancellationToken),
                routeRequest,
                routeGroupRequest);
        }

        internal static IReadOnlyList<IList<Point3d>> BuildActualSectionCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || routeRequest == null ||
                physicalRouteGroupRequest == null) return Array.Empty<IList<Point3d>>();
            var middleWidth = physicalRouteGroupRequest.GetBusbarWidth(routeRequest);
            if (Math.Abs(physicalRouteGroupRequest.GetStartLapBusbarWidth(routeRequest) - middleWidth) <= GeometryTolerance &&
                Math.Abs(physicalRouteGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest) - middleWidth) <= GeometryTolerance)
                return Array.Empty<IList<Point3d>>();

            // Independent batch: do not combine poses built with one width with
            // continuation spans computed for another, or evict the original batch.
            var pathRequest = routeRequest.CreatePathSearchRequest();
            var traversalGroup = GetTraversalRouteGroup(physicalRouteGroupRequest, traversalDirection);
            var directionalCandidates = BuildDirectionalCandidatesCore(pathRequest, traversalGroup,
                IsDoubleStartCollectiveParallelLapAllowed(pathRequest, physicalRouteGroupRequest, traversalDirection, true),
                IsDoubleStartCollectiveParallelLapAllowed(pathRequest, physicalRouteGroupRequest, traversalDirection, false),
                IsSingleStartMiddleParallelLapRequired(pathRequest, physicalRouteGroupRequest, traversalDirection, true),
                IsSingleStartMiddleParallelLapRequired(pathRequest, physicalRouteGroupRequest, traversalDirection, false),
                IsPartialStartPerpendicularLapAllowed(pathRequest, physicalRouteGroupRequest, traversalDirection, true),
                IsPartialStartPerpendicularLapAllowed(pathRequest, physicalRouteGroupRequest, traversalDirection, false),
                physicalRouteGroupRequest.GetRequiredInsulationClearance(pathRequest), null, null,
                cancellationToken, true);
            return NormalizeTraversalCandidates(AdjustActualWidthInternalStraightSectionCandidates(
                directionalCandidates, pathRequest, traversalGroup), traversalDirection).ToList();
        }

        private static IReadOnlyList<double[]> GetFixedTerminalLapSearchWidthPairs(
            LinkageMultiGroupRouteRequest request, LinkageRouteGroupRequest group)
        {
            var startWidth = group.PathSearchActualStartLapBusbarWidth > GeometryTolerance
                ? group.PathSearchActualStartLapBusbarWidth : group.GetStartLapBusbarWidth(request);
            var endWidth = group.PathSearchActualCabinetTopLapBusbarWidth > GeometryTolerance
                ? group.PathSearchActualCabinetTopLapBusbarWidth : group.GetCabinetTopLapBusbarWidth(request);
            var pairs = new List<double[]> { new[] { startWidth, endWidth } };
            // 单根排同时搭两头及现有单层输出模式沿用物理柜顶规格；
            // 反向遍历时物理柜顶位于遍历始端，不能把它交换成始端规格。
            var cabinetWidth = group.PathSearchIsReversed ? startWidth : endWidth;
            if (Math.Abs(startWidth - endWidth) > GeometryTolerance)
                pairs.Add(new[] { cabinetWidth, cabinetWidth });
            return pairs;
        }

        private static IReadOnlyList<IList<Point3d>> BuildDirectionalCandidatesCore(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            bool allowStartCollectiveParallelLap,
            bool allowEndCollectiveParallelLap,
            bool requireStartMiddleParallelLap,
            bool requireEndMiddleParallelLap,
            bool allowStartPartialPerpendicularLap,
            bool allowEndPartialPerpendicularLap,
            double requiredInsulationClearance,
            ISet<string> fullLapCandidateSignatures,
            ISet<string> partialLapCandidateSignatures,
            CancellationToken cancellationToken,
            bool useActualTerminalWidths = false)
        {
            if (routeRequest?.RequireFixedTerminalEndLap != true)
                return BuildDirectionalCandidatesCoreForWidths(
                    routeRequest,
                    routeGroupRequest,
                    allowStartCollectiveParallelLap,
                    allowEndCollectiveParallelLap,
                    requireStartMiddleParallelLap,
                    requireEndMiddleParallelLap,
                    allowStartPartialPerpendicularLap,
                    allowEndPartialPerpendicularLap,
                    requiredInsulationClearance,
                    fullLapCandidateSignatures,
                    partialLapCandidateSignatures,
                    cancellationToken,
                    useActualTerminalWidths,
                    0d,
                    0d);
            var widthBatches = new List<IList<Point3d>>();
            foreach (var widthPair in GetFixedTerminalLapSearchWidthPairs(routeRequest, routeGroupRequest))
            {
                if (cancellationToken.IsCancellationRequested) break;
                widthBatches.AddRange(BuildDirectionalCandidatesCoreForWidths(routeRequest,
                    routeGroupRequest,
                    allowStartCollectiveParallelLap,
                    allowEndCollectiveParallelLap,
                    requireStartMiddleParallelLap,
                    requireEndMiddleParallelLap,
                    allowStartPartialPerpendicularLap,
                    allowEndPartialPerpendicularLap,
                    requiredInsulationClearance,
                    fullLapCandidateSignatures,
                    partialLapCandidateSignatures,
                    cancellationToken,
                    useActualTerminalWidths,
                    widthPair[0],
                    widthPair[1]));
            }
            return widthBatches.GroupBy(GetSignature).Select(batch => batch.First()).ToList();
        }

        private static IReadOnlyList<IList<Point3d>> BuildDirectionalCandidatesCoreForWidths(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            bool allowStartCollectiveParallelLap,
            bool allowEndCollectiveParallelLap,
            bool requireStartMiddleParallelLap,
            bool requireEndMiddleParallelLap,
            bool allowStartPartialPerpendicularLap,
            bool allowEndPartialPerpendicularLap,
            double requiredInsulationClearance,
            ISet<string> fullLapCandidateSignatures,
            ISet<string> partialLapCandidateSignatures,
            CancellationToken cancellationToken,
            bool useActualTerminalWidths,
            double lockedStartWidth,
            double lockedEndWidth)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var busbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var startConstructionWidth = lockedStartWidth > GeometryTolerance ? lockedStartWidth : useActualTerminalWidths
                ? routeGroupRequest.GetStartLapBusbarWidth(routeRequest) : busbarWidth;
            var endConstructionWidth = lockedEndWidth > GeometryTolerance ? lockedEndWidth : useActualTerminalWidths
                ? routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest) : busbarWidth;
            var generatedSingleBusbarThickness = routeGroupRequest
                .GetBusbarThickness(routeRequest);
            var routeStackCount = routeGroupRequest.GetStackCount(routeRequest);
            var terminalBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    routeStackCount,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var startCollectivePerpendicularLapSpan = GetCollectivePerpendicularLapSpan(
                routeGroupRequest.StartTerminal,
                startConstructionWidth);
            var endCollectivePerpendicularLapSpan = GetCollectivePerpendicularLapSpan(
                routeGroupRequest.EndTerminal,
                endConstructionWidth);
            var startPerpendicularTerminalSegmentLength = Math.Max(
                routeRequest.MinimumSegmentLength,
                startCollectivePerpendicularLapSpan + terminalBendAllowance);
            var endPerpendicularTerminalSegmentLength = Math.Max(
                routeRequest.MinimumSegmentLength,
                endCollectivePerpendicularLapSpan + terminalBendAllowance);
            var widthSizedOverlapBoundaryReferencePoint =
                GetWidthSizedOverlapBoundaryReferencePoint(
                    routeGroupRequest,
                    startConstructionWidth,
                    endConstructionWidth);
            var startPerpendicularLapBendContinuationDistance =
                startCollectivePerpendicularLapSpan / 2d +
                    terminalBendAllowance;
            var endPerpendicularLapBendContinuationDistance =
                endCollectivePerpendicularLapSpan / 2d +
                    terminalBendAllowance;
            var startStraightDoublePerpendicularLapTerminalExtension =
                startCollectivePerpendicularLapSpan + startConstructionWidth / 2d;
            var endStraightDoublePerpendicularLapTerminalExtension =
                endCollectivePerpendicularLapSpan + endConstructionWidth / 2d;
            var startConversionTerminalSegmentLength =
                startCollectivePerpendicularLapSpan +
                terminalBendAllowance +
                routeRequest.MinimumSegmentLength;
            var endConversionTerminalSegmentLength =
                endCollectivePerpendicularLapSpan +
                terminalBendAllowance +
                routeRequest.MinimumSegmentLength;
            var preserveTerminalLapVariants =
                allowStartPartialPerpendicularLap ||
                allowEndPartialPerpendicularLap;
            var effectiveFullLapCandidateSignatures =
                fullLapCandidateSignatures ??
                (preserveTerminalLapVariants
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : null);
            var effectivePartialLapCandidateSignatures =
                partialLapCandidateSignatures ??
                (preserveTerminalLapVariants
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : null);
            var candidatePaths = new List<IList<Point3d>>();
            var candidatePathSignatures = new HashSet<string>(StringComparer.Ordinal);
            var startTerminalPoses =
                LinkageRouteTerminalPoseFactory
                    .BuildWithShortStartLapPolicy(
                routeGroupRequest.StartTerminal,
                routeGroupRequest.EndTerminal.RouteAnchor,
                true,
                startConstructionWidth,
                Math.Max(routeRequest.MinimumSegmentLength, startConstructionWidth + terminalBendAllowance),
                startPerpendicularTerminalSegmentLength,
                routeRequest.MinimumSegmentLength,
                null,
                allowStartCollectiveParallelLap,
                requireStartMiddleParallelLap,
                allowStartPartialPerpendicularLap,
                terminalBendAllowance,
                widthSizedOverlapBoundaryReferencePoint,
                routeGroupRequest.GetShortPhysicalStartLapLength(routeRequest, true),
                requireFixedTerminalEndLap: routeRequest.RequireFixedTerminalEndLap, minimumParallelLapLength: routeGroupRequest.GetPhysicalStartParallelLapLength(routeRequest, true));
            var endTerminalPoses =
                LinkageRouteTerminalPoseFactory
                    .BuildWithShortStartLapPolicy(
                routeGroupRequest.EndTerminal,
                routeGroupRequest.StartTerminal.RouteAnchor,
                false,
                endConstructionWidth,
                Math.Max(routeRequest.MinimumSegmentLength, endConstructionWidth + terminalBendAllowance),
                endPerpendicularTerminalSegmentLength,
                routeRequest.MinimumSegmentLength,
                null,
                allowEndCollectiveParallelLap,
                requireEndMiddleParallelLap,
                allowEndPartialPerpendicularLap,
                terminalBendAllowance,
                widthSizedOverlapBoundaryReferencePoint,
                routeGroupRequest.GetShortPhysicalStartLapLength(routeRequest, false),
                requireFixedTerminalEndLap: routeRequest.RequireFixedTerminalEndLap, minimumParallelLapLength: routeGroupRequest.GetPhysicalStartParallelLapLength(routeRequest, false));
            // A section-specific terminal lap may be narrower than the middle
            // skeleton. Keep every existing pose; only recover a terminal that
            // had no pose because the skeleton width did not fit its fixed bar.
            var actualStartLapWidth = routeGroupRequest
                .GetStartLapBusbarWidth(routeRequest);
            if (!routeRequest.RequireFixedTerminalEndLap && startTerminalPoses.Count == 0 &&
                actualStartLapWidth > GeometryTolerance &&
                actualStartLapWidth + GeometryTolerance < busbarWidth)
            {
                startCollectivePerpendicularLapSpan =
                    GetCollectivePerpendicularLapSpan(
                        routeGroupRequest.StartTerminal,
                        actualStartLapWidth);
                startPerpendicularTerminalSegmentLength = Math.Max(
                    routeRequest.MinimumSegmentLength,
                    startCollectivePerpendicularLapSpan + terminalBendAllowance);
                startPerpendicularLapBendContinuationDistance =
                    startCollectivePerpendicularLapSpan / 2d +
                    terminalBendAllowance;
                startStraightDoublePerpendicularLapTerminalExtension =
                    startCollectivePerpendicularLapSpan +
                    actualStartLapWidth / 2d;
                startConversionTerminalSegmentLength =
                    startCollectivePerpendicularLapSpan +
                    terminalBendAllowance + routeRequest.MinimumSegmentLength;
                startTerminalPoses = LinkageRouteTerminalPoseFactory
                    .BuildWithShortStartLapPolicy(
                        routeGroupRequest.StartTerminal,
                        routeGroupRequest.EndTerminal.RouteAnchor,
                        true,
                        actualStartLapWidth,
                        Math.Max(routeRequest.MinimumSegmentLength,
                            actualStartLapWidth + terminalBendAllowance),
                        startPerpendicularTerminalSegmentLength,
                        routeRequest.MinimumSegmentLength,
                        null,
                        allowStartCollectiveParallelLap,
                        requireStartMiddleParallelLap,
                        allowStartPartialPerpendicularLap,
                        terminalBendAllowance,
                        GetWidthSizedOverlapBoundaryReferencePoint(
                            routeGroupRequest, actualStartLapWidth,
                            busbarWidth),
                        routeGroupRequest.GetShortPhysicalStartLapLength(routeRequest, true),
                requireFixedTerminalEndLap: routeRequest.RequireFixedTerminalEndLap, minimumParallelLapLength: routeGroupRequest.GetPhysicalStartParallelLapLength(routeRequest, true));
            }

            var actualEndLapWidth = routeGroupRequest
                .GetCabinetTopLapBusbarWidth(routeRequest);
            if (!routeRequest.RequireFixedTerminalEndLap && endTerminalPoses.Count == 0 &&
                actualEndLapWidth > GeometryTolerance &&
                actualEndLapWidth + GeometryTolerance < busbarWidth)
            {
                endCollectivePerpendicularLapSpan =
                    GetCollectivePerpendicularLapSpan(
                        routeGroupRequest.EndTerminal,
                        actualEndLapWidth);
                endPerpendicularTerminalSegmentLength = Math.Max(
                    routeRequest.MinimumSegmentLength,
                    endCollectivePerpendicularLapSpan + terminalBendAllowance);
                endPerpendicularLapBendContinuationDistance =
                    endCollectivePerpendicularLapSpan / 2d +
                    terminalBendAllowance;
                endStraightDoublePerpendicularLapTerminalExtension =
                    endCollectivePerpendicularLapSpan +
                    actualEndLapWidth / 2d;
                endConversionTerminalSegmentLength =
                    endCollectivePerpendicularLapSpan +
                    terminalBendAllowance + routeRequest.MinimumSegmentLength;
                endTerminalPoses = LinkageRouteTerminalPoseFactory
                    .BuildWithShortStartLapPolicy(
                        routeGroupRequest.EndTerminal,
                        routeGroupRequest.StartTerminal.RouteAnchor,
                        false,
                        actualEndLapWidth,
                        Math.Max(routeRequest.MinimumSegmentLength,
                            actualEndLapWidth + terminalBendAllowance),
                        endPerpendicularTerminalSegmentLength,
                        routeRequest.MinimumSegmentLength,
                        null,
                        allowEndCollectiveParallelLap,
                        requireEndMiddleParallelLap,
                        allowEndPartialPerpendicularLap,
                        terminalBendAllowance,
                        GetWidthSizedOverlapBoundaryReferencePoint(
                            routeGroupRequest,
                            startTerminalPoses.Count == 0
                                ? actualStartLapWidth : busbarWidth,
                            actualEndLapWidth),
                        routeGroupRequest.GetShortPhysicalStartLapLength(routeRequest, false),
                requireFixedTerminalEndLap: routeRequest.RequireFixedTerminalEndLap, minimumParallelLapLength: routeGroupRequest.GetPhysicalStartParallelLapLength(routeRequest, false));
            }
            var startTerminalLayerOffsets = GetTerminalLayerOffsets(
                routeRequest,
                routeGroupRequest,
                routeGroupRequest.StartTerminal,
                routeStackCount,
                generatedSingleBusbarThickness,
                true);
            var endTerminalLayerOffsets = GetTerminalLayerOffsets(
                routeRequest,
                routeGroupRequest,
                routeGroupRequest.EndTerminal,
                routeStackCount,
                generatedSingleBusbarThickness,
                false);

            startTerminalPoses = WithHoleCenteredPoses(routeRequest,routeGroupRequest,true,startTerminalPoses);
            endTerminalPoses = WithHoleCenteredPoses(routeRequest,routeGroupRequest,false,endTerminalPoses);
            foreach (var startTerminalPose in startTerminalPoses)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Array.Empty<IList<Point3d>>();
                }

                var startPoseUsesPartialPerpendicularLap =
                    allowStartPartialPerpendicularLap &&
                    startTerminalPose.IsPerpendicularLap &&
                    startTerminalPose.PerpendicularLapCoverageSpan +
                        GeometryTolerance <
                    startCollectivePerpendicularLapSpan;
                foreach (var endTerminalPose in endTerminalPoses)
                {
                    var endPoseUsesPartialPerpendicularLap =
                        allowEndPartialPerpendicularLap &&
                        endTerminalPose.IsPerpendicularLap &&
                        endTerminalPose.PerpendicularLapCoverageSpan +
                            GeometryTolerance <
                        endCollectivePerpendicularLapSpan;
                    var usesPartialPerpendicularLap =
                        startPoseUsesPartialPerpendicularLap ||
                        endPoseUsesPartialPerpendicularLap;
                    foreach (var startLayerOffset in !startTerminalPose.IsPerpendicularLap &&
                        routeRequest.EnableSingleStartConnectionBar
                            ? GetTerminalLayerOffsets(routeRequest, routeGroupRequest,
                                routeGroupRequest.StartTerminal, routeStackCount,
                                generatedSingleBusbarThickness, true, true)
                            : startTerminalLayerOffsets)
                    {
                        foreach (var endLayerOffset in endTerminalLayerOffsets)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                return Array.Empty<IList<Point3d>>();
                            }

                            var startLapPoint = Translate(
                                startTerminalPose.LapEndpoint,
                                routeGroupRequest.StartTerminal.ThicknessDirection.Normalized,
                                startLayerOffset);
                            var endLapPoint = Translate(
                                endTerminalPose.LapEndpoint,
                                routeGroupRequest.EndTerminal.ThicknessDirection.Normalized,
                                endLayerOffset);
                            var startExitPoints = GetTerminalExtensionPoints(
                                startLapPoint,
                                endLapPoint,
                                startTerminalPose.RouteDirection,
                                startTerminalPose.RequiredTerminalSegmentLength,
                                startConversionTerminalSegmentLength,
                                startPerpendicularLapBendContinuationDistance,
                                startStraightDoublePerpendicularLapTerminalExtension,
                                true);
                            var endEntryPoints = GetTerminalExtensionPoints(
                                endLapPoint,
                                startLapPoint,
                                endTerminalPose.RouteDirection,
                                endTerminalPose.RequiredTerminalSegmentLength,
                                endConversionTerminalSegmentLength,
                                endPerpendicularLapBendContinuationDistance,
                                endStraightDoublePerpendicularLapTerminalExtension,
                                false);
                            foreach (var startExitPoint in startExitPoints)
                            {
                                foreach (var endEntryPoint in endEntryPoints)
                                {
                                    var directCandidateStartIndex = candidatePaths.Count;
                                    AddDirectConnectorCandidates(
                                        candidatePaths,
                                        startLapPoint,
                                        startExitPoint,
                                        endEntryPoint,
                                        endLapPoint,
                                        startTerminalPose.RouteDirection,
                                        endTerminalPose.RouteDirection,
                                        candidatePathSignatures);
                                    if (preserveTerminalLapVariants)
                                    {
                                        RecordTerminalLapVariantCandidateSignatures(
                                            candidatePaths,
                                            directCandidateStartIndex,
                                            usesPartialPerpendicularLap,
                                            effectiveFullLapCandidateSignatures,
                                            effectivePartialLapCandidateSignatures);
                                    }
                                    var hasMinimumLengthDirectCandidate = candidatePaths
                                        .Skip(directCandidateStartIndex)
                                        .Any(candidatePath =>
                                            HasMinimumStackedUShapeSegments(
                                                candidatePath,
                                                routeRequest,
                                                routeGroupRequest));
                                    if (!hasMinimumLengthDirectCandidate)
                                    {
                                        var detourCandidateStartIndex =
                                            candidatePaths.Count;
                                        AddDetourConnectorCandidates(
                                            candidatePaths,
                                            routeRequest,
                                            routeGroupRequest,
                                            startLapPoint,
                                            startExitPoint,
                                            endEntryPoint,
                                            endLapPoint,
                                            startTerminalPose.RouteDirection,
                                            endTerminalPose.RouteDirection,
                                            Math.Max(
                                                startTerminalPose.RequiredTerminalSegmentLength,
                                                endTerminalPose.RequiredTerminalSegmentLength),
                                            requiredInsulationClearance,
                                            candidatePathSignatures);
                                        if (preserveTerminalLapVariants)
                                        {
                                            RecordTerminalLapVariantCandidateSignatures(
                                                candidatePaths,
                                                detourCandidateStartIndex,
                                                usesPartialPerpendicularLap,
                                                effectiveFullLapCandidateSignatures,
                                                effectivePartialLapCandidateSignatures);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return SelectNearestCandidatesByShape(
                candidatePaths,
                routeRequest,
                routeGroupRequest,
                generatedSingleBusbarThickness,
                Math.Max(
                    256,
                    Math.Max(1, routeRequest.SearchOptions.MaximumCandidatesPerGroup) * 8),
                requireStartMiddleParallelLap,
                requireEndMiddleParallelLap,
                false,
                allowStartPartialPerpendicularLap ||
                    allowEndPartialPerpendicularLap,
                effectiveFullLapCandidateSignatures,
                effectivePartialLapCandidateSignatures,
                cancellationToken);
        }

        public static IReadOnlyList<IList<Point3d>> BuildExtendedCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            return BuildExtendedCandidates(
                routeRequest,
                routeGroupRequest,
                CancellationToken.None);
        }

        public static IReadOnlyList<IList<Point3d>> BuildExtendedCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            CancellationToken cancellationToken)
        {
            return BuildExtendedCandidates(
                routeRequest,
                routeGroupRequest,
                LinkageRouteTraversalDirection.Forward,
                cancellationToken);
        }

        internal static IReadOnlyList<IList<Point3d>> BuildExtendedCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            return BuildExtendedCandidatesCore(
                routeRequest,
                routeGroupRequest,
                traversalDirection,
                null,
                null,
                2,
                8,
                cancellationToken);
        }

        public static IReadOnlyList<IList<Point3d>> BuildClearanceDrivenCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IEnumerable<LinkageRouteSolid> clearanceSolids)
        {
            return BuildClearanceDrivenCandidates(
                routeRequest,
                routeGroupRequest,
                clearanceSolids,
                CancellationToken.None);
        }

        public static IReadOnlyList<IList<Point3d>> BuildClearanceDrivenCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IEnumerable<LinkageRouteSolid> clearanceSolids,
            CancellationToken cancellationToken)
        {
            return BuildClearanceDrivenCandidates(
                routeRequest,
                routeGroupRequest,
                clearanceSolids,
                LinkageRouteTraversalDirection.Forward,
                cancellationToken);
        }

        internal static IReadOnlyList<IList<Point3d>> BuildClearanceDrivenCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IEnumerable<LinkageRouteSolid> clearanceSolids,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            var clearanceSolidList = (clearanceSolids ??
                    Enumerable.Empty<LinkageRouteSolid>())
                .Where(clearanceSolid => clearanceSolid != null)
                .ToList();
            var traversalRouteGroupRequest = GetTraversalRouteGroup(
                routeGroupRequest,
                traversalDirection);
            var clearanceCriticalCoordinates = BuildClearanceCriticalCoordinates(
                routeRequest,
                traversalRouteGroupRequest,
                clearanceSolidList,
                routeGroupRequest.GetRequiredInsulationClearance(routeRequest),
                cancellationToken);
            return BuildExtendedCandidatesCore(
                routeRequest,
                routeGroupRequest,
                traversalDirection,
                clearanceCriticalCoordinates,
                clearanceSolidList,
                2,
                8,
                cancellationToken);
        }

        public static IReadOnlyList<IList<Point3d>>
            BuildClearanceDrivenCandidatesForSegmentCount(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                IEnumerable<LinkageRouteSolid> clearanceSolids,
                int segmentCount,
                CancellationToken cancellationToken)
        {
            return BuildClearanceDrivenCandidatesForSegmentCount(
                routeRequest,
                routeGroupRequest,
                clearanceSolids,
                segmentCount,
                LinkageRouteTraversalDirection.Forward,
                cancellationToken);
        }

        internal static IReadOnlyList<IList<Point3d>>
            BuildClearanceDrivenCandidatesForSegmentCount(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                IEnumerable<LinkageRouteSolid> clearanceSolids,
                int segmentCount,
                LinkageRouteTraversalDirection traversalDirection,
                CancellationToken cancellationToken)
        {
            if (segmentCount < 2 || segmentCount > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCount));
            }

            var clearanceSolidList = (clearanceSolids ??
                    Enumerable.Empty<LinkageRouteSolid>())
                .Where(clearanceSolid => clearanceSolid != null)
                .ToList();
            var traversalRouteGroupRequest = GetTraversalRouteGroup(
                routeGroupRequest,
                traversalDirection);
            var clearanceCriticalCoordinates = BuildClearanceCriticalCoordinates(
                routeRequest,
                traversalRouteGroupRequest,
                clearanceSolidList,
                routeGroupRequest.GetRequiredInsulationClearance(routeRequest),
                cancellationToken);
            return BuildExtendedCandidatesCore(
                routeRequest,
                routeGroupRequest,
                traversalDirection,
                clearanceCriticalCoordinates,
                clearanceSolidList,
                segmentCount,
                segmentCount,
                cancellationToken);
        }

        public static void VisitClearanceDrivenCandidatesForSegmentCount(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IEnumerable<LinkageRouteSolid> clearanceSolids,
            int segmentCount,
            Func<IList<Point3d>, bool> candidateVisitor,
            IDictionary<SegmentClearanceCacheKey, bool> segmentClearanceCache,
            CancellationToken cancellationToken)
        {
            VisitClearanceDrivenCandidatesForSegmentCount(
                routeRequest,
                routeGroupRequest,
                clearanceSolids,
                segmentCount,
                candidateVisitor,
                segmentClearanceCache,
                LinkageRouteTraversalDirection.Forward,
                cancellationToken);
        }

        internal static void VisitClearanceDrivenCandidatesForSegmentCount(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IEnumerable<LinkageRouteSolid> clearanceSolids,
            int segmentCount,
            Func<IList<Point3d>, bool> candidateVisitor,
            IDictionary<SegmentClearanceCacheKey, bool> segmentClearanceCache,
            LinkageRouteTraversalDirection traversalDirection,
            CancellationToken cancellationToken)
        {
            if (segmentCount < 2 || segmentCount > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentCount));
            }

            if (candidateVisitor == null)
            {
                throw new ArgumentNullException(nameof(candidateVisitor));
            }

            var clearanceSolidList = (clearanceSolids ??
                    Enumerable.Empty<LinkageRouteSolid>())
                .Where(clearanceSolid => clearanceSolid != null)
                .ToList();
            var traversalRouteGroupRequest = GetTraversalRouteGroup(
                routeGroupRequest,
                traversalDirection);
            var clearanceCriticalCoordinates = BuildClearanceCriticalCoordinates(
                routeRequest,
                traversalRouteGroupRequest,
                clearanceSolidList,
                routeGroupRequest.GetRequiredInsulationClearance(routeRequest),
                cancellationToken);
            BuildDirectionalExtendedCandidates(
                routeRequest,
                traversalRouteGroupRequest,
                clearanceCriticalCoordinates,
                clearanceSolidList,
                segmentCount,
                segmentCount,
                traversalCandidate => candidateVisitor(
                    NormalizeTraversalCandidate(
                        traversalCandidate,
                        traversalDirection)),
                segmentClearanceCache,
                IsDoubleStartCollectiveParallelLapAllowed(
                    routeRequest,
                    routeGroupRequest,
                    traversalDirection,
                    true),
                IsDoubleStartCollectiveParallelLapAllowed(
                    routeRequest,
                    routeGroupRequest,
                    traversalDirection,
                    false),
                IsSingleStartMiddleParallelLapRequired(
                    routeRequest,
                    routeGroupRequest,
                    traversalDirection,
                    true),
                IsSingleStartMiddleParallelLapRequired(
                    routeRequest,
                    routeGroupRequest,
                    traversalDirection,
                    false),
                IsPartialStartPerpendicularLapAllowed(
                    routeRequest,
                    routeGroupRequest,
                    traversalDirection,
                    true),
                IsPartialStartPerpendicularLapAllowed(
                    routeRequest,
                    routeGroupRequest,
                    traversalDirection,
                    false),
                routeGroupRequest.GetRequiredInsulationClearance(routeRequest),
                cancellationToken);
        }

        private static IReadOnlyList<IList<Point3d>> BuildExtendedCandidatesCore(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteTraversalDirection traversalDirection,
            IReadOnlyDictionary<int, IReadOnlyList<double>> clearanceCriticalCoordinates,
            IReadOnlyList<LinkageRouteSolid> clearanceSolids,
            int minimumSegmentCount,
            int maximumSegmentCount,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var traversalRouteGroupRequest = GetTraversalRouteGroup(
                physicalRouteGroupRequest,
                traversalDirection);
            var generatedSingleBusbarThickness = physicalRouteGroupRequest
                .GetBusbarThickness(routeRequest);
            var maximumCandidateCount = Math.Max(
                512,
                Math.Max(1, routeRequest.SearchOptions.MaximumCandidatesPerGroup) * 8);
            var traversalCandidates = BuildDirectionalExtendedCandidates(
                routeRequest,
                traversalRouteGroupRequest,
                clearanceCriticalCoordinates,
                clearanceSolids,
                minimumSegmentCount,
                maximumSegmentCount,
                null,
                null,
                IsDoubleStartCollectiveParallelLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    true),
                IsDoubleStartCollectiveParallelLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    false),
                IsSingleStartMiddleParallelLapRequired(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    true),
                IsSingleStartMiddleParallelLapRequired(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    false),
                IsPartialStartPerpendicularLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    true),
                IsPartialStartPerpendicularLapAllowed(
                    routeRequest,
                    physicalRouteGroupRequest,
                    traversalDirection,
                    false),
                physicalRouteGroupRequest.GetRequiredInsulationClearance(routeRequest),
                cancellationToken);
            return SelectDiverseCandidates(
                NormalizeTraversalCandidates(
                    traversalCandidates,
                    traversalDirection),
                routeRequest,
                physicalRouteGroupRequest,
                generatedSingleBusbarThickness,
                maximumCandidateCount,
                clearanceCriticalCoordinates != null,
                cancellationToken);
        }

        private static IReadOnlyList<IList<Point3d>> BuildDirectionalExtendedCandidates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyDictionary<int, IReadOnlyList<double>> clearanceCriticalCoordinates,
            IReadOnlyList<LinkageRouteSolid> clearanceSolids,
            int minimumSegmentCount,
            int maximumSegmentCount,
            Func<IList<Point3d>, bool> candidateVisitor,
            IDictionary<SegmentClearanceCacheKey, bool> sharedSegmentClearanceCache,
            bool allowStartCollectiveParallelLap,
            bool allowEndCollectiveParallelLap,
            bool requireStartMiddleParallelLap,
            bool requireEndMiddleParallelLap,
            bool allowStartPartialPerpendicularLap,
            bool allowEndPartialPerpendicularLap,
            double requiredInsulationClearance,
            CancellationToken cancellationToken)
        {
            if (routeRequest?.RequireFixedTerminalEndLap != true)
                return BuildDirectionalExtendedCandidatesForWidths(
                    routeRequest,
                    routeGroupRequest,
                    clearanceCriticalCoordinates,
                    clearanceSolids,
                    minimumSegmentCount,
                    maximumSegmentCount,
                    candidateVisitor,
                    sharedSegmentClearanceCache,
                    allowStartCollectiveParallelLap,
                    allowEndCollectiveParallelLap,
                    requireStartMiddleParallelLap,
                    requireEndMiddleParallelLap,
                    allowStartPartialPerpendicularLap,
                    allowEndPartialPerpendicularLap,
                    requiredInsulationClearance,
                    cancellationToken,
                    0d,
                    0d);
            var widthBatches = new List<IList<Point3d>>();
            foreach (var widthPair in GetFixedTerminalLapSearchWidthPairs(routeRequest, routeGroupRequest))
            {
                if (cancellationToken.IsCancellationRequested) break;
                widthBatches.AddRange(BuildDirectionalExtendedCandidatesForWidths(routeRequest,
                    routeGroupRequest,
                    clearanceCriticalCoordinates,
                    clearanceSolids,
                    minimumSegmentCount,
                    maximumSegmentCount,
                    candidateVisitor,
                    sharedSegmentClearanceCache,
                    allowStartCollectiveParallelLap,
                    allowEndCollectiveParallelLap,
                    requireStartMiddleParallelLap,
                    requireEndMiddleParallelLap,
                    allowStartPartialPerpendicularLap,
                    allowEndPartialPerpendicularLap,
                    requiredInsulationClearance,
                    cancellationToken,
                    widthPair[0],
                    widthPair[1]));
            }
            return widthBatches.GroupBy(GetSignature).Select(batch => batch.First()).ToList();
        }

        private static IReadOnlyList<IList<Point3d>> BuildDirectionalExtendedCandidatesForWidths(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyDictionary<int, IReadOnlyList<double>> clearanceCriticalCoordinates,
            IReadOnlyList<LinkageRouteSolid> clearanceSolids,
            int minimumSegmentCount,
            int maximumSegmentCount,
            Func<IList<Point3d>, bool> candidateVisitor,
            IDictionary<SegmentClearanceCacheKey, bool> sharedSegmentClearanceCache,
            bool allowStartCollectiveParallelLap,
            bool allowEndCollectiveParallelLap,
            bool requireStartMiddleParallelLap,
            bool requireEndMiddleParallelLap,
            bool allowStartPartialPerpendicularLap,
            bool allowEndPartialPerpendicularLap,
            double requiredInsulationClearance,
            CancellationToken cancellationToken,
            double lockedStartWidth,
            double lockedEndWidth)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var busbarWidth = routeGroupRequest.GetBusbarWidth(routeRequest);
            var startPoseWidth = lockedStartWidth > GeometryTolerance ? lockedStartWidth : busbarWidth;
            var endPoseWidth = lockedEndWidth > GeometryTolerance ? lockedEndWidth : busbarWidth;
            var generatedSingleBusbarThickness = routeGroupRequest
                .GetBusbarThickness(routeRequest);
            var routeStackCount = routeGroupRequest.GetStackCount(routeRequest);
            var terminalBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    routeStackCount,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var parallelTerminalSegmentLength = Math.Max(
                routeRequest.MinimumSegmentLength,
                busbarWidth + terminalBendAllowance);
            var startCollectivePerpendicularLapSpan = GetCollectivePerpendicularLapSpan(
                routeGroupRequest.StartTerminal,
                startPoseWidth);
            var endCollectivePerpendicularLapSpan = GetCollectivePerpendicularLapSpan(
                routeGroupRequest.EndTerminal,
                endPoseWidth);
            var startPerpendicularTerminalSegmentLength = Math.Max(
                routeRequest.MinimumSegmentLength,
                startCollectivePerpendicularLapSpan + terminalBendAllowance);
            var endPerpendicularTerminalSegmentLength = Math.Max(
                routeRequest.MinimumSegmentLength,
                endCollectivePerpendicularLapSpan + terminalBendAllowance);
            var widthSizedOverlapBoundaryReferencePoint =
                GetWidthSizedOverlapBoundaryReferencePoint(
                    routeGroupRequest,
                    busbarWidth,
                    busbarWidth);
            var startPerpendicularLapBendContinuationDistance =
                startCollectivePerpendicularLapSpan / 2d +
                    terminalBendAllowance;
            var endPerpendicularLapBendContinuationDistance =
                endCollectivePerpendicularLapSpan / 2d +
                    terminalBendAllowance;
            var generatedInternalLapBendContinuationDistance =
                busbarWidth / 2d + terminalBendAllowance;
            var startTerminalPoses = BuildExtendedTerminalPosesForRoute(
                routeGroupRequest.StartTerminal,
                routeGroupRequest.EndTerminal.RouteAnchor,
                true,
                startPoseWidth,
                Math.Max(parallelTerminalSegmentLength, startPoseWidth + terminalBendAllowance),
                startPerpendicularTerminalSegmentLength,
                routeRequest.MinimumSegmentLength,
                clearanceCriticalCoordinates,
                allowStartCollectiveParallelLap,
                requireStartMiddleParallelLap,
                allowStartPartialPerpendicularLap,
                terminalBendAllowance,
                widthSizedOverlapBoundaryReferencePoint,
                routeGroupRequest.GetShortPhysicalStartLapLength(routeRequest, true),
                requireFixedTerminalEndLap: routeRequest.RequireFixedTerminalEndLap, minimumParallelLapLength: routeGroupRequest.GetPhysicalStartParallelLapLength(routeRequest, true));
            var endTerminalPoses = BuildExtendedTerminalPosesForRoute(
                routeGroupRequest.EndTerminal,
                routeGroupRequest.StartTerminal.RouteAnchor,
                false,
                endPoseWidth,
                Math.Max(parallelTerminalSegmentLength, endPoseWidth + terminalBendAllowance),
                endPerpendicularTerminalSegmentLength,
                routeRequest.MinimumSegmentLength,
                clearanceCriticalCoordinates,
                allowEndCollectiveParallelLap,
                requireEndMiddleParallelLap,
                allowEndPartialPerpendicularLap,
                terminalBendAllowance,
                widthSizedOverlapBoundaryReferencePoint,
                routeGroupRequest.GetShortPhysicalStartLapLength(routeRequest, false),
                requireFixedTerminalEndLap: routeRequest.RequireFixedTerminalEndLap, minimumParallelLapLength: routeGroupRequest.GetPhysicalStartParallelLapLength(routeRequest, false));
            var startLayerOffsets = GetTerminalLayerOffsets(
                routeRequest,
                routeGroupRequest,
                routeGroupRequest.StartTerminal,
                routeStackCount,
                generatedSingleBusbarThickness,
                true);
            var endLayerOffsets = GetTerminalLayerOffsets(
                routeRequest,
                routeGroupRequest,
                routeGroupRequest.EndTerminal,
                routeStackCount,
                generatedSingleBusbarThickness,
                false);
            var extendedCandidates = new List<IList<Point3d>>();
            var extendedCandidateSignatures = new HashSet<string>(StringComparer.Ordinal);
            var segmentClearanceCache = sharedSegmentClearanceCache ??
                new Dictionary<SegmentClearanceCacheKey, bool>();
            Func<Point3d, Point3d, bool> segmentAcceptance =
                (segmentStart, segmentEnd) =>
                {
                    var segmentClearanceCacheKey = SegmentClearanceCacheKey.Create(
                        segmentStart,
                        segmentEnd);
                    if (segmentClearanceCache.TryGetValue(
                        segmentClearanceCacheKey,
                        out var cachedSegmentAcceptance))
                    {
                        return cachedSegmentAcceptance;
                    }

                    var evaluatedSegmentAcceptance =
                        IsEarlyClearanceSegmentAllowed(
                            segmentStart,
                            segmentEnd,
                            routeGroupRequest.GetClearanceGroupKey(),
                            clearanceSolids,
                            requiredInsulationClearance);
                    segmentClearanceCache[segmentClearanceCacheKey] =
                        evaluatedSegmentAcceptance;
                    return evaluatedSegmentAcceptance;
                };
            var extendedSearchWorkItems = new List<ExtendedSearchWorkItem>();
            startTerminalPoses = WithHoleCenteredPoses(routeRequest,routeGroupRequest,true,startTerminalPoses);
            endTerminalPoses = WithHoleCenteredPoses(routeRequest,routeGroupRequest,false,endTerminalPoses);
            foreach (var startTerminalPose in startTerminalPoses)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Array.Empty<IList<Point3d>>();
                }

                foreach (var endTerminalPose in endTerminalPoses)
                {
                    var axisSequences = EnumerateAxisSequences(
                        GetAxisIndex(startTerminalPose.RouteDirection),
                        GetAxisIndex(endTerminalPose.RouteDirection),
                        minimumSegmentCount,
                        maximumSegmentCount);
                    foreach (var startLayerOffset in !startTerminalPose.IsPerpendicularLap &&
                        routeRequest.EnableSingleStartConnectionBar
                            ? GetTerminalLayerOffsets(routeRequest, routeGroupRequest,
                                routeGroupRequest.StartTerminal, routeStackCount,
                                generatedSingleBusbarThickness, true, true)
                            : startLayerOffsets)
                    {
                        foreach (var endLayerOffset in endLayerOffsets)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                return Array.Empty<IList<Point3d>>();
                            }

                            var startLapPoint = Translate(
                                startTerminalPose.LapEndpoint,
                                routeGroupRequest.StartTerminal.ThicknessDirection.Normalized,
                                startLayerOffset);
                            var endLapPoint = Translate(
                                endTerminalPose.LapEndpoint,
                                routeGroupRequest.EndTerminal.ThicknessDirection.Normalized,
                                endLayerOffset);
                            var coordinateMargin = Math.Max(
                                startTerminalPose.RequiredTerminalSegmentLength,
                                endTerminalPose.RequiredTerminalSegmentLength);
                            var coordinateOptions = Enumerable.Range(0, 3)
                                .ToDictionary(
                                    coordinateIndex => coordinateIndex,
                                    coordinateIndex => GetExtendedCoordinateOptions(
                                        routeRequest,
                                        routeGroupRequest,
                                        startLapPoint,
                                        endLapPoint,
                                        coordinateIndex,
                                        coordinateMargin,
                                        startPerpendicularLapBendContinuationDistance,
                                        endPerpendicularLapBendContinuationDistance,
                                        clearanceCriticalCoordinates == null
                                            ? 0d
                                            : generatedInternalLapBendContinuationDistance,
                                        requiredInsulationClearance,
                                        clearanceCriticalCoordinates != null &&
                                            clearanceCriticalCoordinates.TryGetValue(
                                                coordinateIndex,
                                                out var criticalCoordinates)
                                            ? criticalCoordinates
                                            : null));
                            foreach (var axisSequence in axisSequences)
                            {
                                extendedSearchWorkItems.Add(new ExtendedSearchWorkItem
                                {
                                    StartLapPoint = startLapPoint,
                                    EndLapPoint = endLapPoint,
                                    StartRouteDirection = startTerminalPose.RouteDirection,
                                    EndRouteDirection = endTerminalPose.RouteDirection,
                                    StartRequiredTerminalSegmentLength =
                                        startTerminalPose.RequiredTerminalSegmentLength,
                                    EndRequiredTerminalSegmentLength =
                                        endTerminalPose.RequiredTerminalSegmentLength,
                                    AxisSequence = axisSequence,
                                    CoordinateOptions = coordinateOptions,
                                    EstimatedRouteLength = startLapPoint.DistanceTo(endLapPoint) +
                                        startTerminalPose.RequiredTerminalSegmentLength +
                                        endTerminalPose.RequiredTerminalSegmentLength
                                });
                            }
                        }
                    }
                }
            }

            foreach (var extendedSearchWorkItem in extendedSearchWorkItems
                .OrderBy(workItem => workItem.AxisSequence.Length)
                .ThenBy(workItem => workItem.EstimatedRouteLength)
                .ThenBy(workItem => string.Join(string.Empty, workItem.AxisSequence),
                    StringComparer.Ordinal)
                .ThenBy(workItem => GetSignature(new[] { workItem.StartLapPoint }),
                    StringComparer.Ordinal)
                .ThenBy(workItem => GetSignature(new[] { workItem.EndLapPoint }),
                    StringComparer.Ordinal))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Array.Empty<IList<Point3d>>();
                }

                var sequenceCandidates = new List<IList<Point3d>>();
                BuildSequenceCandidates(
                    sequenceCandidates,
                    routeRequest,
                    routeGroupRequest,
                    extendedSearchWorkItem.StartLapPoint,
                    extendedSearchWorkItem.EndLapPoint,
                    extendedSearchWorkItem.StartRouteDirection,
                    extendedSearchWorkItem.EndRouteDirection,
                    extendedSearchWorkItem.AxisSequence,
                    extendedSearchWorkItem.CoordinateOptions,
                    routeRequest.MinimumSegmentLength,
                    GetStackedUShapeMinimumBaseSegmentLength(
                        routeRequest,
                        routeGroupRequest),
                    extendedSearchWorkItem.StartRequiredTerminalSegmentLength,
                    extendedSearchWorkItem.EndRequiredTerminalSegmentLength,
                    maximumCandidatesForSequence:
                        clearanceCriticalCoordinates == null ? 12 : 24,
                    maximumSearchNodeCount: routeRequest.SearchOptions
                        .MaximumSequenceSearchNodeCount,
                    knownCandidateSignatures: extendedCandidateSignatures,
                    segmentAcceptance: segmentAcceptance,
                    cancellationToken: cancellationToken);
                if (candidateVisitor == null)
                {
                    extendedCandidates.AddRange(sequenceCandidates);
                    continue;
                }

                foreach (var sequenceCandidate in sequenceCandidates)
                {
                    if (!candidateVisitor(sequenceCandidate))
                    {
                        return Array.Empty<IList<Point3d>>();
                    }
                }
            }

            if (candidateVisitor != null)
            {
                return Array.Empty<IList<Point3d>>();
            }

            return SelectDiverseCandidates(
                extendedCandidates,
                routeRequest,
                routeGroupRequest,
                generatedSingleBusbarThickness,
                Math.Max(
                    512,
                    Math.Max(1, routeRequest.SearchOptions.MaximumCandidatesPerGroup) * 8),
                clearanceCriticalCoordinates != null,
                cancellationToken);
        }

        private static IReadOnlyList<LinkageRouteTerminalPose>
            BuildExtendedTerminalPosesForRoute(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double parallelTerminalSegmentLength,
            double perpendicularTerminalSegmentLength,
            double partialPerpendicularTerminalSegmentLength,
            IReadOnlyDictionary<int, IReadOnlyList<double>>
                clearanceCriticalCoordinates,
            bool allowCollectiveParallelLap,
            bool requireMiddleParallelLap,
            bool allowPartialPerpendicularLap,
            double perpendicularTerminalBendAllowance,
            Point3d terminalBoundaryReferencePoint = null,
            double shortPhysicalStartLapLength = 0d,
                bool requireFixedTerminalEndLap = false, double minimumParallelLapLength = 0d)
        {
            var fixedWidthDirection = terminalAssembly?.Members
                .Select(terminalMember => terminalMember?.FixedBusbar?.WidthVector)
                .FirstOrDefault(widthDirection => widthDirection != null &&
                    widthDirection.Norm > GeometryTolerance);
            var fixedWidthAxisIndex = fixedWidthDirection == null
                ? -1 : GetAxisIndex(fixedWidthDirection);
            var terminalClearanceWidthCoordinates = fixedWidthAxisIndex >= 0 &&
                clearanceCriticalCoordinates != null &&
                clearanceCriticalCoordinates.TryGetValue(fixedWidthAxisIndex, out var widthCriticalCoordinates)
                ? widthCriticalCoordinates : null;
            Func<Point3d, IReadOnlyList<LinkageRouteTerminalPose>> resolveExtendedPoses = approachReference =>
                LinkageRouteTerminalPoseFactory.BuildWithShortStartLapPolicy(
                    terminalAssembly, approachReference, isStartTerminal, generatedBusbarWidth,
                    parallelTerminalSegmentLength, perpendicularTerminalSegmentLength,
                    partialPerpendicularTerminalSegmentLength, terminalClearanceWidthCoordinates,
                    allowCollectiveParallelLap, requireMiddleParallelLap, allowPartialPerpendicularLap,
                    perpendicularTerminalBendAllowance, terminalBoundaryReferencePoint,
                    shortPhysicalStartLapLength, requireFixedTerminalEndLap: requireFixedTerminalEndLap, minimumParallelLapLength: minimumParallelLapLength);
            var extendedTerminalPoses = resolveExtendedPoses(oppositeTerminalAnchor).ToList();
            // The opposite terminal's projection chooses the preferred approach,
            // but it does not forbid approaching the same legal lap from the
            // other width edge after an obstacle detour. Resolve that edge with
            // the production pose policy instead of reversing an endpoint.
            foreach (var preferredPerpendicularPose in extendedTerminalPoses
                .Where(terminalPose => terminalPose.IsPerpendicularLap).ToArray())
            {
                var oppositeApproachReference = GeometRiMethod.TranslateAlongDirection(
                    preferredPerpendicularPose.LapEndpoint, preferredPerpendicularPose.RouteDirection,
                    (isStartTerminal ? -1d : 1d) *
                        Math.Max(generatedBusbarWidth, preferredPerpendicularPose.PerpendicularLapCoverageSpan));
                foreach (var oppositePerpendicularPose in resolveExtendedPoses(oppositeApproachReference))
                    if (oppositePerpendicularPose.IsPerpendicularLap &&
                        oppositePerpendicularPose.RouteDirection.Normalized.Dot(
                            preferredPerpendicularPose.RouteDirection.Normalized) < -1d + GeometryTolerance &&
                        !extendedTerminalPoses.Any(existingPose =>
                            existingPose.LapEndpoint.DistanceTo(oppositePerpendicularPose.LapEndpoint) <= GeometryTolerance &&
                            Math.Abs(existingPose.RequiredTerminalSegmentLength -
                                oppositePerpendicularPose.RequiredTerminalSegmentLength) <= GeometryTolerance &&
                            Math.Abs(existingPose.PerpendicularLapCoverageSpan -
                                oppositePerpendicularPose.PerpendicularLapCoverageSpan) <= GeometryTolerance &&
                            existingPose.RouteDirection.Normalized.Dot(oppositePerpendicularPose.RouteDirection.Normalized) >
                                1d - GeometryTolerance))
                        extendedTerminalPoses.Add(oppositePerpendicularPose);
            }
            return extendedTerminalPoses;
        }

        internal static Point3d GetWidthSizedOverlapBoundaryReferencePoint(
            LinkageRouteGroupRequest routeGroupRequest,
            double startGeneratedBusbarWidth,
            double endGeneratedBusbarWidth)
        {
            return LinkageRouteTerminalPoseFactory
                .TryBuildWidthSizedLengthOverlapPosePair(
                    routeGroupRequest?.StartTerminal,
                    routeGroupRequest?.EndTerminal,
                    startGeneratedBusbarWidth,
                    endGeneratedBusbarWidth,
                    startGeneratedBusbarWidth,
                    endGeneratedBusbarWidth,
                    false,
                    0d,
                    0d,
                    out var widthSizedOverlapPosePair)
                ? widthSizedOverlapPosePair.SharedLapCenterPoint
                : null;
        }

        public static IList<Point3d> CreateTerminalPlaneCriticalCandidate(
            IList<Point3d> sourceAxisCenterline,
            double? startTerminalCriticalSegmentLength,
            double? endTerminalCriticalSegmentLength,
            double minimumSegmentLength)
        {
            if (sourceAxisCenterline == null || sourceAxisCenterline.Count < 3)
            {
                return null;
            }

            var terminalPlaneCriticalCenterline = sourceAxisCenterline
                .Select(Copy)
                .ToList();
            var hasAdjustedTerminalPlane = false;
            if (startTerminalCriticalSegmentLength.HasValue)
            {
                hasAdjustedTerminalPlane |= TryShortenTerminalPlane(
                    terminalPlaneCriticalCenterline,
                    startTerminalCriticalSegmentLength.Value,
                    true);
            }

            if (endTerminalCriticalSegmentLength.HasValue)
            {
                hasAdjustedTerminalPlane |= TryShortenTerminalPlane(
                    terminalPlaneCriticalCenterline,
                    endTerminalCriticalSegmentLength.Value,
                    false);
            }

            return hasAdjustedTerminalPlane &&
                HasMinimumSegments(terminalPlaneCriticalCenterline, minimumSegmentLength)
                ? terminalPlaneCriticalCenterline
                : null;
        }

        public static IList<Point3d> CreateTerminalPlaneExtensionCandidate(
            IList<Point3d> sourceAxisCenterline,
            double startTerminalAdditionalLength,
            double endTerminalAdditionalLength,
            double minimumSegmentLength)
        {
            if (sourceAxisCenterline == null || sourceAxisCenterline.Count < 3)
            {
                return null;
            }

            var terminalPlaneExtendedCenterline = sourceAxisCenterline
                .Select(Copy)
                .ToList();
            var hasExtendedTerminalPlane = false;
            if (startTerminalAdditionalLength > GeometryTolerance)
            {
                hasExtendedTerminalPlane |= TryExtendTerminalPlane(
                    terminalPlaneExtendedCenterline,
                    startTerminalAdditionalLength,
                    true);
            }

            if (endTerminalAdditionalLength > GeometryTolerance)
            {
                hasExtendedTerminalPlane |= TryExtendTerminalPlane(
                    terminalPlaneExtendedCenterline,
                    endTerminalAdditionalLength,
                    false);
            }

            return hasExtendedTerminalPlane &&
                HasMinimumSegments(
                    terminalPlaneExtendedCenterline,
                    minimumSegmentLength)
                ? terminalPlaneExtendedCenterline
                : null;
        }

        private static bool TryExtendTerminalPlane(
            IList<Point3d> terminalPlaneCenterline,
            double terminalAdditionalLength,
            bool adjustStartTerminal)
        {
            if (terminalAdditionalLength <= GeometryTolerance)
            {
                return false;
            }

            var terminalPointIndex = adjustStartTerminal
                ? 0
                : terminalPlaneCenterline.Count - 1;
            var adjacentTerminalPointIndex = adjustStartTerminal
                ? 1
                : terminalPlaneCenterline.Count - 2;
            var terminalPoint = terminalPlaneCenterline[terminalPointIndex];
            var adjacentTerminalPoint = terminalPlaneCenterline[
                adjacentTerminalPointIndex];
            var terminalSegmentVector = new Vector3d(
                terminalPoint,
                adjacentTerminalPoint);
            if (terminalSegmentVector.Norm <= GeometryTolerance)
            {
                return false;
            }

            var terminalSegmentAxisIndex = GetAxisIndex(terminalSegmentVector);
            var sourceTerminalPlaneCoordinate = GetCoordinate(
                adjacentTerminalPoint,
                terminalSegmentAxisIndex);
            var extendedTerminalPlanePoint = Translate(
                terminalPoint,
                terminalSegmentVector.Normalized,
                terminalSegmentVector.Norm + terminalAdditionalLength);
            var extendedTerminalPlaneCoordinate = GetCoordinate(
                extendedTerminalPlanePoint,
                terminalSegmentAxisIndex);
            if (adjustStartTerminal)
            {
                for (var terminalPlanePointIndex = 1;
                    terminalPlanePointIndex < terminalPlaneCenterline.Count &&
                    Math.Abs(GetCoordinate(
                        terminalPlaneCenterline[terminalPlanePointIndex],
                        terminalSegmentAxisIndex) - sourceTerminalPlaneCoordinate) <=
                    GeometryTolerance;
                    terminalPlanePointIndex++)
                {
                    terminalPlaneCenterline[terminalPlanePointIndex] = WithCoordinate(
                        terminalPlaneCenterline[terminalPlanePointIndex],
                        terminalSegmentAxisIndex,
                        extendedTerminalPlaneCoordinate);
                }
            }
            else
            {
                for (var terminalPlanePointIndex = terminalPlaneCenterline.Count - 2;
                    terminalPlanePointIndex >= 0 &&
                    Math.Abs(GetCoordinate(
                        terminalPlaneCenterline[terminalPlanePointIndex],
                        terminalSegmentAxisIndex) - sourceTerminalPlaneCoordinate) <=
                    GeometryTolerance;
                    terminalPlanePointIndex--)
                {
                    terminalPlaneCenterline[terminalPlanePointIndex] = WithCoordinate(
                        terminalPlaneCenterline[terminalPlanePointIndex],
                        terminalSegmentAxisIndex,
                        extendedTerminalPlaneCoordinate);
                }
            }

            return true;
        }

        private static bool TryShortenTerminalPlane(
            IList<Point3d> terminalPlaneCenterline,
            double terminalCriticalSegmentLength,
            bool adjustStartTerminal)
        {
            if (terminalCriticalSegmentLength <= GeometryTolerance)
            {
                return false;
            }

            var terminalPointIndex = adjustStartTerminal
                ? 0
                : terminalPlaneCenterline.Count - 1;
            var adjacentTerminalPointIndex = adjustStartTerminal
                ? 1
                : terminalPlaneCenterline.Count - 2;
            var terminalPoint = terminalPlaneCenterline[terminalPointIndex];
            var adjacentTerminalPoint = terminalPlaneCenterline[adjacentTerminalPointIndex];
            var terminalSegmentVector = new Vector3d(terminalPoint, adjacentTerminalPoint);
            if (terminalSegmentVector.Norm <= terminalCriticalSegmentLength + GeometryTolerance)
            {
                return false;
            }

            var terminalSegmentAxisIndex = GetAxisIndex(terminalSegmentVector);
            var sourceTerminalPlaneCoordinate = GetCoordinate(
                adjacentTerminalPoint,
                terminalSegmentAxisIndex);
            var criticalTerminalPlanePoint = Translate(
                terminalPoint,
                terminalSegmentVector.Normalized,
                terminalCriticalSegmentLength);
            var criticalTerminalPlaneCoordinate = GetCoordinate(
                criticalTerminalPlanePoint,
                terminalSegmentAxisIndex);

            if (adjustStartTerminal)
            {
                for (var terminalPlanePointIndex = 1;
                    terminalPlanePointIndex < terminalPlaneCenterline.Count &&
                    Math.Abs(GetCoordinate(
                        terminalPlaneCenterline[terminalPlanePointIndex],
                        terminalSegmentAxisIndex) - sourceTerminalPlaneCoordinate) <=
                    GeometryTolerance;
                    terminalPlanePointIndex++)
                {
                    terminalPlaneCenterline[terminalPlanePointIndex] = WithCoordinate(
                        terminalPlaneCenterline[terminalPlanePointIndex],
                        terminalSegmentAxisIndex,
                        criticalTerminalPlaneCoordinate);
                }
            }
            else
            {
                for (var terminalPlanePointIndex = terminalPlaneCenterline.Count - 2;
                    terminalPlanePointIndex >= 0 &&
                    Math.Abs(GetCoordinate(
                        terminalPlaneCenterline[terminalPlanePointIndex],
                        terminalSegmentAxisIndex) - sourceTerminalPlaneCoordinate) <=
                    GeometryTolerance;
                    terminalPlanePointIndex--)
                {
                    terminalPlaneCenterline[terminalPlanePointIndex] = WithCoordinate(
                        terminalPlaneCenterline[terminalPlanePointIndex],
                        terminalSegmentAxisIndex,
                        criticalTerminalPlaneCoordinate);
                }
            }

            return true;
        }

        private static LinkageRouteGroupRequest GetTraversalRouteGroup(
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteTraversalDirection traversalDirection)
        {
            return traversalDirection == LinkageRouteTraversalDirection.Reverse
                ? CreateReversedTraversalGroup(physicalRouteGroupRequest)
                : physicalRouteGroupRequest;
        }

        private static bool IsDoubleStartCollectiveParallelLapAllowed(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteTraversalDirection traversalDirection,
            bool isTraversalStartTerminal)
        {
            if (routeRequest?.AllowDoubleStartMiddleLap != true ||
                physicalRouteGroupRequest?.StartTerminal?.Members?.Count < 2)
            {
                return false;
            }

            return traversalDirection == LinkageRouteTraversalDirection.Forward
                ? isTraversalStartTerminal
                : !isTraversalStartTerminal;
        }

        private static bool IsSingleStartMiddleParallelLapRequired(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteTraversalDirection traversalDirection,
            bool isTraversalStartTerminal)
        {
            if (routeRequest?.RequireSingleStartMiddleLap != true ||
                physicalRouteGroupRequest?.StartTerminal?.Members?.Count != 1)
            {
                return false;
            }

            // 该开关只属于物理始端。反向搜索交换了构造视角，因此规则应随
            // 物理始端移动到遍历终端，不能误套到物理柜顶。
            return traversalDirection == LinkageRouteTraversalDirection.Forward
                ? isTraversalStartTerminal
                : !isTraversalStartTerminal;
        }

        private static bool IsPartialStartPerpendicularLapAllowed(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest physicalRouteGroupRequest,
            LinkageRouteTraversalDirection traversalDirection,
            bool isTraversalStartTerminal)
        {
            if (routeRequest?.AllowPartialStartPerpendicularLap != true ||
                physicalRouteGroupRequest?.StartTerminal?.Members == null ||
                physicalRouteGroupRequest.StartTerminal.Members.Count == 0)
            {
                return false;
            }

            // 与单始端居中规则相同，此开关只属于物理始端。反向搜索时
            // 物理始端位于遍历终端，不能把短搭接误用到物理柜顶。
            return traversalDirection == LinkageRouteTraversalDirection.Forward
                ? isTraversalStartTerminal
                : !isTraversalStartTerminal;
        }

        private static IEnumerable<IList<Point3d>> NormalizeTraversalCandidates(
            IEnumerable<IList<Point3d>> traversalCandidates,
            LinkageRouteTraversalDirection traversalDirection)
        {
            return (traversalCandidates ?? Enumerable.Empty<IList<Point3d>>())
                .Select(traversalCandidate => NormalizeTraversalCandidate(
                    traversalCandidate,
                    traversalDirection));
        }

        private static IList<Point3d> NormalizeTraversalCandidate(
            IList<Point3d> traversalCandidate,
            LinkageRouteTraversalDirection traversalDirection)
        {
            return traversalDirection == LinkageRouteTraversalDirection.Reverse
                ? ReverseTraversalPath(traversalCandidate)
                : traversalCandidate.Select(Copy).ToList();
        }

        // 反向批次仅交换骨架构造视角。返回候选前必须恢复物理点序，且后续
        // 转换、搭接、奇偶、绝缘和叠排仍一律使用原始物理请求复核。
        private static LinkageRouteGroupRequest CreateReversedTraversalGroup(
            LinkageRouteGroupRequest sourceRouteGroupRequest)
        {
            return new LinkageRouteGroupRequest
            {
                GroupKey = sourceRouteGroupRequest.GroupKey,
                Phase = sourceRouteGroupRequest.Phase,
                DisplayName = sourceRouteGroupRequest.DisplayName,
                StartTerminal = sourceRouteGroupRequest.EndTerminal,
                PathSearchIsReversed = !sourceRouteGroupRequest.PathSearchIsReversed,
                PathSearchPhysicalStartLapWidth = sourceRouteGroupRequest.PathSearchPhysicalStartLapWidth,
                EndTerminal = sourceRouteGroupRequest.StartTerminal,
                BusbarWidth = sourceRouteGroupRequest.BusbarWidth,
                BusbarThickness = sourceRouteGroupRequest.BusbarThickness,
                StackCount = sourceRouteGroupRequest.StackCount,
                StartLapBusbarWidth =
                    sourceRouteGroupRequest.CabinetTopLapBusbarWidth,
                StartLapBusbarThickness =
                    sourceRouteGroupRequest.CabinetTopLapBusbarThickness,
                StartLapStackCount =
                    sourceRouteGroupRequest.CabinetTopLapStackCount,
                CabinetTopLapBusbarWidth =
                    sourceRouteGroupRequest.StartLapBusbarWidth,
                CabinetTopLapBusbarThickness =
                    sourceRouteGroupRequest.StartLapBusbarThickness,
                CabinetTopLapStackCount =
                    sourceRouteGroupRequest.StartLapStackCount,
                StackStartSpace = sourceRouteGroupRequest.StackEndSpace,
                StackMiddleSpace = sourceRouteGroupRequest.StackMiddleSpace,
                StackEndSpace = sourceRouteGroupRequest.StackStartSpace,
                PathSearchActualStartLapBusbarWidth =
                    sourceRouteGroupRequest.PathSearchActualCabinetTopLapBusbarWidth,
                PathSearchActualCabinetTopLapBusbarWidth =
                    sourceRouteGroupRequest.PathSearchActualStartLapBusbarWidth,
                StackMoveVector = sourceRouteGroupRequest.StackMoveVector == null
                    ? null
                    : new Vector3d(
                        sourceRouteGroupRequest.StackMoveVector.X,
                        sourceRouteGroupRequest.StackMoveVector.Y,
                        sourceRouteGroupRequest.StackMoveVector.Z),
                StackReverse = sourceRouteGroupRequest.StackReverse
            };
        }

        // 与 CreateReversedTraversalGroup 配套，把反向遍历结果恢复为物理始端→柜顶。
        private static IList<Point3d> ReverseTraversalPath(IList<Point3d> traversalPath)
        {
            return traversalPath
                .Reverse()
                .Select(Copy)
                .ToList();
        }

        private static IReadOnlyList<IList<Point3d>> SelectDiverseCandidates(
            IEnumerable<IList<Point3d>> candidatePaths,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            double generatedSingleBusbarThickness,
            int? maximumCandidateCountOverride,
            bool prioritizeInternalLapReserve,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var maximumCandidateCount = maximumCandidateCountOverride ?? Math.Max(
                1, routeRequest.SearchOptions.MaximumCandidatesPerGroup);
            var generatedInternalLapContinuationLength =
                routeGroupRequest.GetBusbarWidth(routeRequest) / 2d +
                LinkageRouteStackGeometry.GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    routeGroupRequest.GetStackCount(routeRequest),
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var orderedTopologyGroups = candidatePaths
                .Where(candidatePath =>
                {
                    return !cancellationToken.IsCancellationRequested;
                })
                .GroupBy(candidatePath => GetCandidateDiversitySignature(
                    candidatePath,
                    routeGroupRequest,
                    generatedInternalLapContinuationLength))
                .Select(topologyGroup => topologyGroup
                    .OrderBy(candidatePath =>
                        HasMinimumStackedUShapeSegments(
                            candidatePath,
                            routeRequest,
                            routeGroupRequest) ? 0 : 1)
                    .ThenBy(candidatePath => GetRawTerminalLayerError(
                        candidatePath,
                        routeRequest,
                        routeGroupRequest,
                        generatedSingleBusbarThickness))
                    .ThenBy(candidatePath =>
                        GetEstimatedPostConversionCenterlineLength(
                            candidatePath,
                            routeRequest,
                            routeGroupRequest))
                    .ToList())
                .OrderBy(topologyCandidates => topologyCandidates[0].Count)
                .ThenBy(topologyCandidates =>
                    GetEstimatedPostConversionCenterlineLength(
                        topologyCandidates[0],
                        routeRequest,
                        routeGroupRequest))
                .ToList();
            var terminalPlacementCandidateSequences = orderedTopologyGroups
                .GroupBy(topologyCandidates => GetTerminalPlacementSignature(
                    topologyCandidates[0]))
                .Select(terminalPlacementGroup =>
                {
                    var placementTopologyGroups = terminalPlacementGroup
                        .OrderBy(topologyCandidates => topologyCandidates[0].Count)
                        .ThenBy(topologyCandidates =>
                            GetEstimatedPostConversionCenterlineLength(
                                topologyCandidates[0],
                                routeRequest,
                                routeGroupRequest))
                        .ToList();
                    var placementCandidateSequence = new List<IList<Point3d>>();
                    for (var topologyCandidateRoundIndex = 0;
                        ;
                        topologyCandidateRoundIndex++)
                    {
                        var addedPlacementCandidate = false;
                        foreach (var placementTopologyCandidates in placementTopologyGroups)
                        {
                            if (topologyCandidateRoundIndex >= placementTopologyCandidates.Count)
                            {
                                continue;
                            }

                            placementCandidateSequence.Add(
                                placementTopologyCandidates[topologyCandidateRoundIndex]);
                            addedPlacementCandidate = true;
                        }

                        if (!addedPlacementCandidate)
                        {
                            break;
                        }
                    }

                    return placementCandidateSequence;
                })
                .OrderBy(placementCandidates => placementCandidates[0].Count)
                .ThenBy(placementCandidates =>
                    GetEstimatedPostConversionCenterlineLength(
                        placementCandidates[0],
                        routeRequest,
                        routeGroupRequest))
                .ToList();
            var selectedCandidates = new List<IList<Point3d>>();
            for (var roundIndex = 0;
                selectedCandidates.Count < maximumCandidateCount;
                roundIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Array.Empty<IList<Point3d>>();
                }

                var addedInRound = false;
                foreach (var placementCandidates in terminalPlacementCandidateSequences)
                {
                    if (roundIndex >= placementCandidates.Count)
                    {
                        continue;
                    }

                    selectedCandidates.Add(placementCandidates[roundIndex]);
                    addedInRound = true;
                    if (selectedCandidates.Count >= maximumCandidateCount)
                    {
                        break;
                    }
                }

                if (!addedInRound)
                {
                    break;
                }
            }

            if (!prioritizeInternalLapReserve)
            {
                return selectedCandidates;
            }

            var internalReserveCandidateSequence = orderedTopologyGroups
                .SelectMany(topologyCandidates => topologyCandidates)
                .Where(candidatePath => HasInternalLapContinuationReserve(
                    candidatePath,
                    generatedInternalLapContinuationLength))
                .GroupBy(candidatePath =>
                    GetTerminalPlacementSignature(candidatePath) + "|" +
                    GetSignedAxisShapeSignature(candidatePath),
                    StringComparer.Ordinal)
                .Select(reserveCandidateGroup => reserveCandidateGroup
                    .OrderBy(candidatePath => candidatePath.Count)
                    .ThenBy(candidatePath =>
                        GetEstimatedPostConversionCenterlineLength(
                            candidatePath,
                            routeRequest,
                            routeGroupRequest))
                    .ThenBy(GetSignature, StringComparer.Ordinal)
                    .First())
                .OrderBy(candidatePath => candidatePath.Count)
                .ThenBy(candidatePath =>
                    GetEstimatedPostConversionCenterlineLength(
                        candidatePath,
                        routeRequest,
                        routeGroupRequest))
                .ThenBy(GetSignature, StringComparer.Ordinal)
                .ToList();
            if (internalReserveCandidateSequence.Count == 0)
            {
                return selectedCandidates;
            }

            // Keep one normal shortest/diverse lane and one manufacturability-
            // critical lane. The latter prevents longer repeated-axis paths that
            // satisfy width-plus-bend continuation reserve from being hidden
            // behind hundreds of shorter but non-convertible coordinate variants.
            var mergedCandidates = new List<IList<Point3d>>();
            var mergedCandidateSignatures = new HashSet<string>(StringComparer.Ordinal);
            var normalCandidateIndex = 0;
            var reserveCandidateIndex = 0;
            while (mergedCandidates.Count < maximumCandidateCount &&
                (normalCandidateIndex < selectedCandidates.Count ||
                 reserveCandidateIndex < internalReserveCandidateSequence.Count))
            {
                AddNextUniqueCandidate(
                    selectedCandidates,
                    ref normalCandidateIndex,
                    mergedCandidates,
                    mergedCandidateSignatures,
                    maximumCandidateCount);
                AddNextUniqueCandidate(
                    internalReserveCandidateSequence,
                    ref reserveCandidateIndex,
                    mergedCandidates,
                    mergedCandidateSignatures,
                    maximumCandidateCount);
            }

            return mergedCandidates;
        }

        private static bool HasInternalLapContinuationReserve(
            IList<Point3d> candidatePath,
            double generatedInternalLapContinuationLength)
        {
            return candidatePath != null &&
                candidatePath.Count >= 4 &&
                Enumerable.Range(1, candidatePath.Count - 3)
                    .All(segmentIndex => candidatePath[segmentIndex]
                        .DistanceTo(candidatePath[segmentIndex + 1]) +
                        GeometryTolerance >= generatedInternalLapContinuationLength);
        }

        private static void AddNextUniqueCandidate(
            IReadOnlyList<IList<Point3d>> sourceCandidates,
            ref int sourceCandidateIndex,
            ICollection<IList<Point3d>> destinationCandidates,
            ISet<string> destinationCandidateSignatures,
            int maximumCandidateCount)
        {
            while (sourceCandidateIndex < sourceCandidates.Count &&
                destinationCandidates.Count < maximumCandidateCount)
            {
                var sourceCandidate = sourceCandidates[sourceCandidateIndex++];
                if (!destinationCandidateSignatures.Add(GetSignature(sourceCandidate)))
                {
                    continue;
                }

                destinationCandidates.Add(sourceCandidate);
                return;
            }
        }

        private static void RecordTerminalLapVariantCandidateSignatures(
            IReadOnlyList<IList<Point3d>> candidatePaths,
            int firstNewCandidateIndex,
            bool usesPartialPerpendicularLap,
            ISet<string> fullLapCandidateSignatures,
            ISet<string> partialLapCandidateSignatures)
        {
            var destinationSignatures = usesPartialPerpendicularLap
                ? partialLapCandidateSignatures
                : fullLapCandidateSignatures;
            if (destinationSignatures == null || candidatePaths == null)
            {
                return;
            }

            for (var candidateIndex = Math.Max(0, firstNewCandidateIndex);
                candidateIndex < candidatePaths.Count;
                candidateIndex++)
            {
                destinationSignatures.Add(GetSignature(
                    candidatePaths[candidateIndex]));
            }
        }

        private static IReadOnlyList<IList<Point3d>>
            SelectCandidatesWithTerminalLapVariantReserve(
            IEnumerable<IList<Point3d>> orderedCandidatePaths,
            int maximumCandidateCount,
            ISet<string> fullLapCandidateSignatures,
            ISet<string> partialLapCandidateSignatures)
        {
            var uniqueOrderedCandidatePaths = (orderedCandidatePaths ??
                    Enumerable.Empty<IList<Point3d>>())
                .Where(candidatePath => candidatePath != null)
                .Select(candidatePath => new
                {
                    CandidatePath = candidatePath,
                    Signature = GetSignature(candidatePath)
                })
                .GroupBy(candidatePath => candidatePath.Signature,
                    StringComparer.Ordinal)
                .Select(candidatePathGroup => candidatePathGroup.First())
                .ToList();
            var boundedMaximumCandidateCount = Math.Max(
                1,
                maximumCandidateCount);
            if (fullLapCandidateSignatures == null ||
                partialLapCandidateSignatures == null)
            {
                return uniqueOrderedCandidatePaths
                    .Take(boundedMaximumCandidateCount)
                    .Select(candidatePath => candidatePath.CandidatePath)
                    .ToList();
            }

            var requiredFullLapCandidate = uniqueOrderedCandidatePaths
                .FirstOrDefault(candidatePath => fullLapCandidateSignatures
                    .Contains(candidatePath.Signature));
            var requiredPartialLapCandidate = uniqueOrderedCandidatePaths
                .FirstOrDefault(candidatePath =>
                {
                    var candidateSignature = candidatePath.Signature;
                    return partialLapCandidateSignatures.Contains(
                            candidateSignature) &&
                        !fullLapCandidateSignatures.Contains(candidateSignature);
                });
            if (requiredFullLapCandidate == null ||
                requiredPartialLapCandidate == null)
            {
                return uniqueOrderedCandidatePaths
                    .Take(boundedMaximumCandidateCount)
                    .Select(candidatePath => candidatePath.CandidatePath)
                    .ToList();
            }

            // 短搭接是附加枚举。候选上限只有 1 时仍必须为原满搭接保留
            // 一席，并同时保留一条真实短搭接，避免更短候选把回退路径挤掉。
            var effectiveMaximumCandidateCount = Math.Max(
                2,
                boundedMaximumCandidateCount);
            var requiredCandidateSignatures = new HashSet<string>(
                StringComparer.Ordinal)
            {
                requiredFullLapCandidate.Signature,
                requiredPartialLapCandidate.Signature
            };
            var selectedCandidatePaths = new[]
            {
                requiredFullLapCandidate,
                requiredPartialLapCandidate
            }.ToList();
            foreach (var orderedCandidatePath in uniqueOrderedCandidatePaths)
            {
                if (selectedCandidatePaths.Count >=
                        effectiveMaximumCandidateCount ||
                    !requiredCandidateSignatures.Add(
                        orderedCandidatePath.Signature))
                {
                    continue;
                }

                selectedCandidatePaths.Add(orderedCandidatePath);
            }

            var candidateOrderBySignature = uniqueOrderedCandidatePaths
                .Select((candidatePath, candidateIndex) => new
                {
                    candidatePath.Signature,
                    Index = candidateIndex
                })
                .ToDictionary(
                    candidateOrder => candidateOrder.Signature,
                    candidateOrder => candidateOrder.Index,
                    StringComparer.Ordinal);
            return selectedCandidatePaths
                .OrderBy(candidatePath => candidateOrderBySignature[
                    candidatePath.Signature])
                .Select(candidatePath => candidatePath.CandidatePath)
                .ToList();
        }

        /// <summary>
        /// 基础搜索只保留每种有向 XYZ 形状中离终端真实厚面最近、总长最短的一套。
        /// 快速正向批次另保留每个轴向临界坐标的最小值/最大值少量边界样本，
        /// 这些样本仍沿同一完整实体校验链处理；反向批次和扩展批次不在此处
        /// 扩张候选预算。单始端水平居中例外保留中心与可转换偏心落位，避免
        /// 尚未执行居中转换就删除可行拓扑；其他同形状落位仍只由后续绝缘
        /// 临界修复按需派生，避免在无干涉场景先做位置笛卡尔积。
        /// </summary>
        private static IReadOnlyList<IList<Point3d>> SelectNearestCandidatesByShape(
            IEnumerable<IList<Point3d>> candidatePaths,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            double generatedSingleBusbarThickness,
            int maximumCandidateCount,
            bool preserveStartHorizontalProposalPlacements,
            bool preserveEndHorizontalProposalPlacements,
            bool preserveForwardBoundaryCriticalCandidates,
            bool preservePartialPerpendicularLapAlternatives,
            ISet<string> fullLapCandidateSignatures,
            ISet<string> partialLapCandidateSignatures,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Array.Empty<IList<Point3d>>();
            }

            if (HasPreferredPerpendicularLapReferences(
                    routeGroupRequest?.StartTerminal) ||
                HasPreferredPerpendicularLapReferences(
                    routeGroupRequest?.EndTerminal))
            {
                return SelectMaterializedTerminalLapCandidatesByPlacement(
                    candidatePaths,
                    routeRequest,
                    routeGroupRequest,
                    generatedSingleBusbarThickness,
                    maximumCandidateCount,
                    fullLapCandidateSignatures,
                    partialLapCandidateSignatures,
                    cancellationToken);
            }

            var generatedInternalLapContinuationLength =
                routeGroupRequest.GetBusbarWidth(routeRequest) / 2d +
                LinkageRouteStackGeometry.GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    routeGroupRequest.GetStackCount(routeRequest),
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var rankedShapeCandidates = candidatePaths
                .Where(candidatePath =>
                {
                    return !cancellationToken.IsCancellationRequested &&
                        candidatePath != null && candidatePath.Count >= 2;
                })
                .Select(candidatePath =>
                {
                    var signedAxisShapeSignature =
                        GetSignedAxisShapeSignature(candidatePath);
                    return new
                    {
                        CandidatePath = candidatePath,
                        GroupSignature =
                            preservePartialPerpendicularLapAlternatives
                                ? GetCandidateDiversitySignature(
                                    candidatePath,
                                    routeGroupRequest,
                                    generatedInternalLapContinuationLength)
                                : preserveStartHorizontalProposalPlacements ||
                                    preserveEndHorizontalProposalPlacements
                                    ? GetTerminalCenteringProposalSignature(
                                        candidatePath,
                                        routeGroupRequest,
                                        signedAxisShapeSignature,
                                        preserveStartHorizontalProposalPlacements,
                                        preserveEndHorizontalProposalPlacements)
                                    : signedAxisShapeSignature,
                        SignedAxisShapeSignature = signedAxisShapeSignature,
                        MinimumSegmentPenalty = HasMinimumStackedUShapeSegments(
                            candidatePath,
                            routeRequest,
                            routeGroupRequest) ? 0 : 1,
                        TerminalLayerError = GetRawTerminalLayerError(
                            candidatePath,
                            routeRequest,
                            routeGroupRequest,
                            generatedSingleBusbarThickness),
                        EstimatedLength =
                            GetEstimatedPostConversionCenterlineLength(
                                candidatePath,
                                routeRequest,
                                routeGroupRequest),
                        Signature = GetSignature(candidatePath)
                    };
                })
                .ToList();
            var orderedShapeCandidates = rankedShapeCandidates
                .GroupBy(candidateRanking => candidateRanking.GroupSignature,
                    StringComparer.Ordinal)
                .Select(shapeCandidateGroup => shapeCandidateGroup
                    .OrderBy(candidateRanking =>
                        candidateRanking.MinimumSegmentPenalty)
                    .ThenBy(candidateRanking =>
                        candidateRanking.TerminalLayerError)
                    .ThenBy(candidateRanking =>
                        candidateRanking.EstimatedLength)
                    .ThenBy(candidateRanking => candidateRanking.Signature,
                        StringComparer.Ordinal)
                    .First())
                .OrderBy(candidateRanking =>
                    candidateRanking.CandidatePath.Count)
                .ThenBy(candidateRanking => candidateRanking.EstimatedLength)
                .ThenBy(candidateRanking =>
                    candidateRanking.SignedAxisShapeSignature,
                    StringComparer.Ordinal)
                .Select(candidateRanking => candidateRanking.CandidatePath)
                .ToList();
            var boundaryAwareOrderedShapeCandidates =
                preserveForwardBoundaryCriticalCandidates
                    ? PrependBoundaryCriticalCandidates(
                        orderedShapeCandidates,
                        rankedShapeCandidates.Select(candidateRanking =>
                            candidateRanking.CandidatePath),
                        maximumCandidateCount)
                    : orderedShapeCandidates;
            if (!preservePartialPerpendicularLapAlternatives)
            {
                return boundaryAwareOrderedShapeCandidates
                    .Take(Math.Max(1, maximumCandidateCount))
                    .ToList();
            }

            return SelectCandidatesWithTerminalLapVariantReserve(
                boundaryAwareOrderedShapeCandidates,
                maximumCandidateCount,
                fullLapCandidateSignatures,
                partialLapCandidateSignatures);
        }

        private const int FastForwardBoundaryCriticalCandidateCount = 6;

        private static IReadOnlyList<IList<Point3d>>
            PrependBoundaryCriticalCandidates(
                IReadOnlyList<IList<Point3d>> orderedShapeCandidates,
                IEnumerable<IList<Point3d>> sourceCandidatePaths,
                int maximumCandidateCount)
        {
            var boundedMaximumCandidateCount = Math.Max(
                1,
                maximumCandidateCount);
            var uniqueSourceCandidatePaths = (sourceCandidatePaths ??
                    Enumerable.Empty<IList<Point3d>>())
                .Where(candidatePath => candidatePath != null &&
                    candidatePath.Count >= 2)
                .GroupBy(GetSignature, StringComparer.Ordinal)
                .Select(candidatePathGroup => candidatePathGroup.First())
                .ToList();
            var boundaryCandidatePaths = new List<IList<Point3d>>();
            var boundaryCandidateSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            for (var coordinateAxisIndex = 0;
                coordinateAxisIndex < 3;
                coordinateAxisIndex++)
            {
                var orderedBoundaryCandidates = uniqueSourceCandidatePaths
                    .Select(candidatePath => new
                    {
                        CandidatePath = candidatePath,
                        MinimumCoordinate = GetInteriorCoordinateExtreme(
                            candidatePath,
                            coordinateAxisIndex,
                            false),
                        MaximumCoordinate = GetInteriorCoordinateExtreme(
                            candidatePath,
                            coordinateAxisIndex,
                            true),
                        Signature = GetSignature(candidatePath)
                    })
                    .Where(candidatePath =>
                        !double.IsNaN(candidatePath.MinimumCoordinate) &&
                        !double.IsNaN(candidatePath.MaximumCoordinate))
                    .ToList();
                foreach (var boundaryCandidate in orderedBoundaryCandidates
                    .OrderBy(candidatePath => candidatePath.MinimumCoordinate)
                    .ThenBy(candidatePath => candidatePath.CandidatePath.Count)
                    .ThenBy(candidatePath => candidatePath.Signature,
                        StringComparer.Ordinal)
                    .Take(1)
                    .Concat(orderedBoundaryCandidates
                        .OrderByDescending(candidatePath =>
                            candidatePath.MaximumCoordinate)
                        .ThenBy(candidatePath => candidatePath.CandidatePath.Count)
                        .ThenBy(candidatePath => candidatePath.Signature,
                            StringComparer.Ordinal)
                        .Take(1)))
                {
                    if (boundaryCandidatePaths.Count >= Math.Min(
                            FastForwardBoundaryCriticalCandidateCount,
                            boundedMaximumCandidateCount) ||
                        !boundaryCandidateSignatures.Add(
                            boundaryCandidate.Signature))
                    {
                        break;
                    }

                    boundaryCandidatePaths.Add(boundaryCandidate.CandidatePath);
                }
            }

            var selectedCandidatePaths = new List<IList<Point3d>>();
            var selectedCandidateSignatures = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var boundaryCandidatePath in boundaryCandidatePaths
                .Concat(orderedShapeCandidates ??
                    Array.Empty<IList<Point3d>>()))
            {
                if (selectedCandidatePaths.Count >= boundedMaximumCandidateCount ||
                    !selectedCandidateSignatures.Add(
                        GetSignature(boundaryCandidatePath)))
                {
                    continue;
                }

                selectedCandidatePaths.Add(boundaryCandidatePath);
            }

            return selectedCandidatePaths;
        }

        private static double GetInteriorCoordinateExtreme(
            IList<Point3d> candidatePath,
            int coordinateAxisIndex,
            bool takeMaximum)
        {
            if (candidatePath == null || candidatePath.Count == 0)
            {
                return double.NaN;
            }

            var firstInteriorPointIndex = candidatePath.Count > 2 ? 1 : 0;
            var lastInteriorPointIndex = candidatePath.Count > 2
                ? candidatePath.Count - 2
                : candidatePath.Count - 1;
            var coordinateValues = Enumerable.Range(
                    firstInteriorPointIndex,
                    lastInteriorPointIndex - firstInteriorPointIndex + 1)
                .Select(pointIndex => GetCoordinate(
                    candidatePath[pointIndex],
                    coordinateAxisIndex))
                .Where(coordinateValue => !double.IsNaN(coordinateValue) &&
                    !double.IsInfinity(coordinateValue))
                .ToList();
            if (coordinateValues.Count == 0)
            {
                return double.NaN;
            }

            return takeMaximum
                ? coordinateValues.Max()
                : coordinateValues.Min();
        }

        private static IReadOnlyList<IList<Point3d>>
            SelectMaterializedTerminalLapCandidatesByPlacement(
            IEnumerable<IList<Point3d>> candidatePaths,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            double generatedSingleBusbarThickness,
            int maximumCandidateCount,
            ISet<string> fullLapCandidateSignatures,
            ISet<string> partialLapCandidateSignatures,
            CancellationToken cancellationToken)
        {
            var materializedTerminalCandidateSequences = candidatePaths
                .Where(candidatePath =>
                    !cancellationToken.IsCancellationRequested &&
                    candidatePath != null && candidatePath.Count >= 2)
                .GroupBy(GetTerminalPlacementSignature, StringComparer.Ordinal)
                .Select(terminalPlacementCandidateGroup =>
                    terminalPlacementCandidateGroup
                        .GroupBy(
                            GetSignedAxisShapeSignature,
                            StringComparer.Ordinal)
                        .Select(shapeCandidateGroup => shapeCandidateGroup
                            .OrderBy(candidatePath =>
                                HasMinimumStackedUShapeSegments(
                                    candidatePath,
                                    routeRequest,
                                    routeGroupRequest) ? 0 : 1)
                            .ThenBy(candidatePath => GetRawTerminalLayerError(
                                candidatePath,
                                routeRequest,
                                routeGroupRequest,
                                generatedSingleBusbarThickness))
                            .ThenBy(candidatePath =>
                                GetEstimatedPostConversionCenterlineLength(
                                    candidatePath,
                                    routeRequest,
                                    routeGroupRequest))
                            .ThenBy(GetSignature, StringComparer.Ordinal)
                            .First())
                        .OrderBy(candidatePath => candidatePath.Count)
                        .ThenBy(candidatePath =>
                            GetEstimatedPostConversionCenterlineLength(
                                candidatePath,
                                routeRequest,
                                routeGroupRequest))
                        .ThenBy(
                            GetSignedAxisShapeSignature,
                            StringComparer.Ordinal)
                        .ToList())
                .Where(materializedTerminalCandidateSequence =>
                    materializedTerminalCandidateSequence.Count > 0)
                .OrderBy(materializedTerminalCandidateSequence =>
                    GetEstimatedPostConversionCenterlineLength(
                        materializedTerminalCandidateSequence[0],
                        routeRequest,
                        routeGroupRequest))
                .ThenBy(materializedTerminalCandidateSequence =>
                    GetTerminalPlacementSignature(
                        materializedTerminalCandidateSequence[0]),
                    StringComparer.Ordinal)
                .ToList();
            var selectedMaterializedTerminalCandidates =
                new List<IList<Point3d>>();
            var selectedMaterializedTerminalCandidateSignatures =
                new HashSet<string>(StringComparer.Ordinal);
            if (fullLapCandidateSignatures != null &&
                partialLapCandidateSignatures != null)
            {
                var reservedTerminalLapVariantCandidates =
                    SelectCandidatesWithTerminalLapVariantReserve(
                        materializedTerminalCandidateSequences.SelectMany(
                            candidateSequence => candidateSequence),
                        1,
                        fullLapCandidateSignatures,
                        partialLapCandidateSignatures);
                if (reservedTerminalLapVariantCandidates.Count >= 2)
                {
                    foreach (var reservedTerminalLapVariantCandidate in
                        reservedTerminalLapVariantCandidates)
                    {
                        selectedMaterializedTerminalCandidates.Add(
                            reservedTerminalLapVariantCandidate);
                        selectedMaterializedTerminalCandidateSignatures.Add(
                            GetSignature(
                                reservedTerminalLapVariantCandidate));
                    }
                }
            }

            var materializedTerminalCandidateIndices =
                new int[materializedTerminalCandidateSequences.Count];
            var boundedMaximumCandidateCount = Math.Max(
                Math.Max(1, maximumCandidateCount),
                selectedMaterializedTerminalCandidates.Count);
            var addedCandidateInRound = true;
            while (!cancellationToken.IsCancellationRequested &&
                selectedMaterializedTerminalCandidates.Count <
                    boundedMaximumCandidateCount &&
                addedCandidateInRound)
            {
                addedCandidateInRound = false;
                for (var terminalPlacementIndex = 0;
                    terminalPlacementIndex <
                        materializedTerminalCandidateSequences.Count &&
                    selectedMaterializedTerminalCandidates.Count <
                        boundedMaximumCandidateCount;
                    terminalPlacementIndex++)
                {
                    var materializedTerminalCandidateSequence =
                        materializedTerminalCandidateSequences[
                            terminalPlacementIndex];
                    while (materializedTerminalCandidateIndices[
                            terminalPlacementIndex] <
                        materializedTerminalCandidateSequence.Count)
                    {
                        var materializedTerminalCandidate =
                            materializedTerminalCandidateSequence[
                                materializedTerminalCandidateIndices[
                                    terminalPlacementIndex]++];
                        if (!selectedMaterializedTerminalCandidateSignatures.Add(
                                GetSignature(materializedTerminalCandidate)))
                        {
                            continue;
                        }

                        selectedMaterializedTerminalCandidates.Add(
                            materializedTerminalCandidate);
                        addedCandidateInRound = true;
                        break;
                    }
                }
            }

            return selectedMaterializedTerminalCandidates;
        }

        private static IReadOnlyList<IList<Point3d>>
            SelectWidthOverlapCandidatesByPlacement(
                IEnumerable<IList<Point3d>> widthOverlapCandidatePaths,
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                double generatedSingleBusbarThickness,
                int maximumCandidateCount,
                ISet<string> fullLapCandidateSignatures,
                ISet<string> partialLapCandidateSignatures,
                CancellationToken cancellationToken)
        {
            var widthOverlapPlacementCandidateSequences =
                (widthOverlapCandidatePaths ??
                    Enumerable.Empty<IList<Point3d>>())
                .Where(widthOverlapCandidatePath =>
                    !cancellationToken.IsCancellationRequested &&
                    widthOverlapCandidatePath != null &&
                    widthOverlapCandidatePath.Count >= 2)
                .GroupBy(
                    GetTerminalPlacementSignature,
                    StringComparer.Ordinal)
                .Select(widthOverlapPlacementGroup =>
                    widthOverlapPlacementGroup
                        .OrderBy(widthOverlapCandidatePath =>
                            HasMinimumStackedUShapeSegments(
                                widthOverlapCandidatePath,
                                routeRequest,
                                routeGroupRequest) ? 0 : 1)
                        .ThenBy(widthOverlapCandidatePath =>
                            GetRawTerminalLayerError(
                                widthOverlapCandidatePath,
                                routeRequest,
                                routeGroupRequest,
                                generatedSingleBusbarThickness))
                        .ThenBy(widthOverlapCandidatePath =>
                            GetEstimatedPostConversionCenterlineLength(
                                widthOverlapCandidatePath,
                                routeRequest,
                                routeGroupRequest))
                        .ThenBy(GetSignature, StringComparer.Ordinal)
                        .ToList())
                .Where(widthOverlapPlacementCandidateSequence =>
                    widthOverlapPlacementCandidateSequence.Count > 0)
                .OrderBy(widthOverlapPlacementCandidateSequence =>
                    GetRawTerminalLayerError(
                        widthOverlapPlacementCandidateSequence[0],
                        routeRequest,
                        routeGroupRequest,
                        generatedSingleBusbarThickness))
                .ThenBy(widthOverlapPlacementCandidateSequence =>
                    GetEstimatedPostConversionCenterlineLength(
                        widthOverlapPlacementCandidateSequence[0],
                        routeRequest,
                        routeGroupRequest))
                .ThenBy(widthOverlapPlacementCandidateSequence =>
                    GetTerminalPlacementSignature(
                        widthOverlapPlacementCandidateSequence[0]),
                    StringComparer.Ordinal)
                .ToList();
            var selectedWidthOverlapCandidates =
                new List<IList<Point3d>>();
            var selectedWidthOverlapCandidateSignatures =
                new HashSet<string>(StringComparer.Ordinal);
            if (fullLapCandidateSignatures != null &&
                partialLapCandidateSignatures != null)
            {
                var reservedTerminalLapVariantCandidates =
                    SelectCandidatesWithTerminalLapVariantReserve(
                        widthOverlapPlacementCandidateSequences.SelectMany(
                            candidateSequence => candidateSequence),
                        1,
                        fullLapCandidateSignatures,
                        partialLapCandidateSignatures);
                if (reservedTerminalLapVariantCandidates.Count >= 2)
                {
                    foreach (var reservedTerminalLapVariantCandidate in
                        reservedTerminalLapVariantCandidates)
                    {
                        selectedWidthOverlapCandidates.Add(
                            reservedTerminalLapVariantCandidate);
                        selectedWidthOverlapCandidateSignatures.Add(
                            GetSignature(
                                reservedTerminalLapVariantCandidate));
                    }
                }
            }

            var widthOverlapPlacementCandidateIndices =
                new int[widthOverlapPlacementCandidateSequences.Count];
            var boundedWidthOverlapCandidateCount = Math.Max(
                Math.Max(1, maximumCandidateCount),
                selectedWidthOverlapCandidates.Count);
            var addedWidthOverlapCandidateInRound = true;
            while (!cancellationToken.IsCancellationRequested &&
                selectedWidthOverlapCandidates.Count <
                    boundedWidthOverlapCandidateCount &&
                addedWidthOverlapCandidateInRound)
            {
                addedWidthOverlapCandidateInRound = false;
                for (var widthOverlapPlacementIndex = 0;
                    widthOverlapPlacementIndex <
                        widthOverlapPlacementCandidateSequences.Count &&
                    selectedWidthOverlapCandidates.Count <
                        boundedWidthOverlapCandidateCount;
                    widthOverlapPlacementIndex++)
                {
                    var widthOverlapPlacementCandidateSequence =
                        widthOverlapPlacementCandidateSequences[
                            widthOverlapPlacementIndex];
                    while (widthOverlapPlacementCandidateIndices[
                            widthOverlapPlacementIndex] <
                        widthOverlapPlacementCandidateSequence.Count)
                    {
                        var selectedWidthOverlapCandidate =
                            widthOverlapPlacementCandidateSequence[
                                widthOverlapPlacementCandidateIndices[
                                    widthOverlapPlacementIndex]++];
                        if (!selectedWidthOverlapCandidateSignatures.Add(
                                GetSignature(selectedWidthOverlapCandidate)))
                        {
                            continue;
                        }

                        selectedWidthOverlapCandidates.Add(
                            selectedWidthOverlapCandidate);
                        addedWidthOverlapCandidateInRound = true;
                        break;
                    }
                }
            }

            return selectedWidthOverlapCandidates;
        }

        private static bool HasPreferredPerpendicularLapReferences(
            LinkageTerminalAssembly terminalAssembly)
        {
            return terminalAssembly?.PreferredPerpendicularLapReferencePoints !=
                    null &&
                terminalAssembly.PreferredPerpendicularLapReferencePoints.Count >
                    0;
        }

        private static string GetTerminalPlacementSignature(IList<Point3d> candidatePath)
        {
            var startDirection = new Vector3d(candidatePath[0], candidatePath[1]).Normalized;
            var endDirection = new Vector3d(
                candidatePath[candidatePath.Count - 2],
                candidatePath[candidatePath.Count - 1]).Normalized;
            var startEndpoint = candidatePath[0];
            var endEndpoint = candidatePath[candidatePath.Count - 1];
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "S={0:F6},{1:F6},{2:F6}|SD={3:F3},{4:F3},{5:F3}|E={6:F6},{7:F6},{8:F6}|ED={9:F3},{10:F3},{11:F3}",
                startEndpoint.X,
                startEndpoint.Y,
                startEndpoint.Z,
                startDirection.X,
                startDirection.Y,
                startDirection.Z,
                endEndpoint.X,
                endEndpoint.Y,
                endEndpoint.Z,
                endDirection.X,
                endDirection.Y,
                endDirection.Z);
        }

        private static IReadOnlyList<int[]> EnumerateAxisSequences(
            int startAxisIndex,
            int endAxisIndex,
            int minimumSegmentCount,
            int maximumSegmentCount)
        {
            var axisSequences = new List<int[]>();
            for (var segmentCount = Math.Max(2, minimumSegmentCount);
                segmentCount <= maximumSegmentCount;
                segmentCount++)
            {
                var sequence = new int[segmentCount];
                sequence[0] = startAxisIndex;
                sequence[segmentCount - 1] = endAxisIndex;
                EnumerateAxisSequenceCore(sequence, 1, axisSequences);
            }

            return axisSequences
                .OrderBy(axisSequence => axisSequence.Length)
                .ThenBy(axisSequence => string.Join(string.Empty, axisSequence))
                .ToList();
        }

        private static void EnumerateAxisSequenceCore(
            int[] axisSequence,
            int axisPosition,
            ICollection<int[]> axisSequences)
        {
            if (axisPosition >= axisSequence.Length - 1)
            {
                if (axisSequence.Length == 1 ||
                    axisSequence[axisSequence.Length - 2] != axisSequence[axisSequence.Length - 1])
                {
                    axisSequences.Add(axisSequence.ToArray());
                }

                return;
            }

            for (var axisIndex = 0; axisIndex < 3; axisIndex++)
            {
                if (axisIndex == axisSequence[axisPosition - 1])
                {
                    continue;
                }

                axisSequence[axisPosition] = axisIndex;
                EnumerateAxisSequenceCore(axisSequence, axisPosition + 1, axisSequences);
            }
        }

        private static IReadOnlyList<double> GetExtendedCoordinateOptions(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            Point3d startLapPoint,
            Point3d endLapPoint,
            int coordinateIndex,
            double terminalExtensionLength,
            double startPerpendicularLapBendContinuationDistance,
            double endPerpendicularLapBendContinuationDistance,
            double generatedInternalLapBendContinuationDistance,
            double requiredInsulationClearance,
            IEnumerable<double> clearanceCriticalCoordinates = null)
        {
            var startLapCoordinate = GetCoordinate(startLapPoint, coordinateIndex);
            var endLapCoordinate = GetCoordinate(endLapPoint, coordinateIndex);
            return new[]
                {
                    endLapCoordinate,
                    endLapCoordinate - endPerpendicularLapBendContinuationDistance,
                    endLapCoordinate + endPerpendicularLapBendContinuationDistance
                }
                .Concat(generatedInternalLapBendContinuationDistance > GeometryTolerance
                    ? new[]
                    {
                        endLapCoordinate - generatedInternalLapBendContinuationDistance,
                        endLapCoordinate + generatedInternalLapBendContinuationDistance
                    }
                    : Enumerable.Empty<double>())
                .Concat(clearanceCriticalCoordinates ?? Enumerable.Empty<double>())
                // 临界坐标只覆盖障碍物边界，不能替代整体绕行坐标。此前
                // 只要传入了非空字典就完全跳过 GetDetourCoordinates，导致
                // “从所有固定排下方/上方横向绕行”的拓扑在有任一临界坐标时
                // 永远没有机会进入候选池。保留两侧整体绕行坐标；最终候选
                // 仍会经过包围盒、实体和相间净距终检，且坐标集合在末尾去重，
                // 不会改变已有临界位置的优先级。
                .Concat(GetDetourCoordinates(
                    routeRequest,
                    routeGroupRequest,
                    coordinateIndex,
                    terminalExtensionLength,
                    requiredInsulationClearance))
                .Concat(new[]
                {
                    startLapCoordinate,
                    startLapCoordinate - startPerpendicularLapBendContinuationDistance,
                    startLapCoordinate + startPerpendicularLapBendContinuationDistance
                })
                .Concat(generatedInternalLapBendContinuationDistance > GeometryTolerance
                    ? new[]
                    {
                        startLapCoordinate - generatedInternalLapBendContinuationDistance,
                        startLapCoordinate + generatedInternalLapBendContinuationDistance
                    }
                    : Enumerable.Empty<double>())
                .GroupBy(coordinate => Math.Round(coordinate, 6))
                .Select(coordinateGroup => coordinateGroup.First())
                .ToList();
        }

        private static IReadOnlyDictionary<int, IReadOnlyList<double>>
            BuildClearanceCriticalCoordinates(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                IEnumerable<LinkageRouteSolid> clearanceSolids,
                double requiredInsulationClearance,
                CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new Dictionary<int, IReadOnlyList<double>>();
            }

            var generatedHalfExtents = GetGeneratedContainmentHalfExtents(
                routeRequest,
                routeGroupRequest);
            var coordinateMidpoints = new[]
            {
                (routeGroupRequest.StartTerminal.RouteAnchor.X +
                    routeGroupRequest.EndTerminal.RouteAnchor.X) / 2d,
                (routeGroupRequest.StartTerminal.RouteAnchor.Y +
                    routeGroupRequest.EndTerminal.RouteAnchor.Y) / 2d,
                (routeGroupRequest.StartTerminal.RouteAnchor.Z +
                    routeGroupRequest.EndTerminal.RouteAnchor.Z) / 2d
            };
            var coordinatesByAxis = Enumerable.Range(0, 3)
                .ToDictionary(axisIndex => axisIndex, axisIndex => new List<double>());
            if (routeRequest.UseExplicitContainmentBox)
            {
                for (var axisIndex = 0; axisIndex < 3; axisIndex++)
                {
                    coordinatesByAxis[axisIndex].AddRange(
                        GetExplicitContainmentWallCoordinates(
                            routeRequest,
                            routeGroupRequest,
                            axisIndex));
                }
            }

            foreach (var clearanceSolid in clearanceSolids ??
                Enumerable.Empty<LinkageRouteSolid>())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new Dictionary<int, IReadOnlyList<double>>();
                }

                var isSameClearanceGroupSolid = clearanceSolid != null &&
                    !string.IsNullOrWhiteSpace(
                        clearanceSolid.ClearanceGroupKey) &&
                    string.Equals(
                        clearanceSolid.ClearanceGroupKey,
                        routeGroupRequest.GetClearanceGroupKey(),
                        StringComparison.OrdinalIgnoreCase);
                var isInputObstacle = clearanceSolid?.SolidRole ==
                    LinkageRouteSolidRole.InputObstacle;
                if (clearanceSolid == null ||
                    (isSameClearanceGroupSolid &&
                     !clearanceSolid.IsForbiddenTerminalRegion &&
                     !isInputObstacle))
                {
                    // ClearanceGroupKey 表示电气相序，不是独立逻辑组身份。
                    // 同相正体积穿入必须等实际铜排成型后再按 GroupKey 复核，
                    // 避免把本组始末端的合法搭接在骨架阶段提前淘汰。
                    continue;
                }

                var solidVertices = clearanceSolid.GetVertices();
                var solidRequiredInsulationClearance =
                    clearanceSolid.IsForbiddenTerminalRegion ||
                    isSameClearanceGroupSolid ||
                    isInputObstacle
                        ? 0d
                        : requiredInsulationClearance;
                for (var axisIndex = 0; axisIndex < 3; axisIndex++)
                {
                    var solidMinimumCoordinate = solidVertices.Min(vertex =>
                        GetCoordinate(vertex, axisIndex));
                    var solidMaximumCoordinate = solidVertices.Max(vertex =>
                        GetCoordinate(vertex, axisIndex));
                    foreach (var generatedHalfExtent in generatedHalfExtents)
                    {
                        var requiredCenterlineMargin = solidRequiredInsulationClearance +
                            generatedHalfExtent;
                        coordinatesByAxis[axisIndex].Add(
                            solidMinimumCoordinate - requiredCenterlineMargin);
                        coordinatesByAxis[axisIndex].Add(
                            solidMaximumCoordinate + requiredCenterlineMargin);
                    }
                }
            }

            return coordinatesByAxis.ToDictionary(
                coordinatePair => coordinatePair.Key,
                coordinatePair =>
                {
                    var orderedDistinctCriticalCoordinates = coordinatePair.Value
                        .Where(coordinate => IsInsideContainmentCoordinate(
                            routeRequest,
                            coordinatePair.Key,
                            coordinate))
                        .GroupBy(coordinate => Math.Round(coordinate, 6))
                        .Select(coordinateGroup => coordinateGroup.First())
                        .OrderBy(coordinate => coordinate)
                        .ToList();
                    return OrderClearanceCriticalCoordinatesForCoverage(
                        orderedDistinctCriticalCoordinates,
                        coordinateMidpoints[coordinatePair.Key]);
                });
        }

        private static IReadOnlyList<double>
            GetGeneratedContainmentHalfExtents(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest)
        {
            var generatedSingleBusbarThickness = routeGroupRequest
                .GetBusbarThickness(routeRequest);
            var generatedStackEnvelopeThickness = LinkageRouteStackGeometry
                .GetStackEnvelopeThickness(
                    generatedSingleBusbarThickness,
                    routeGroupRequest.GetStackCount(routeRequest),
                    routeGroupRequest.StackMiddleSpace);
            var generatedStackBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    generatedSingleBusbarThickness,
                    routeGroupRequest.GetStackCount(routeRequest),
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            return new[]
                {
                    generatedSingleBusbarThickness / 2d,
                    generatedStackEnvelopeThickness / 2d,
                    routeGroupRequest.GetBusbarWidth(routeRequest) / 2d,
                    routeGroupRequest.GetStartLapBusbarWidth(routeRequest) / 2d,
                    routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest) / 2d,
                    generatedStackBendAllowance
                }
                .Where(halfExtent => halfExtent > GeometryTolerance)
                .Distinct()
                .ToList();
        }

        // Bounded proposal only. A failed containment skeleton can have two
        // neighboring interior planes straddling the usable wall plane. Merge
        // them on one axis, retain both physical terminal anchors/directions,
        // and let the ordinary candidate factory validate all actual solids.
        internal static IReadOnlyList<IList<Point3d>> BuildExplicitWallAlignedSiblings(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> sourceSkeleton)
        {
            var wallAlignedSiblings = new List<IList<Point3d>>();
            if (routeRequest?.UseExplicitContainmentBox != true ||
                routeRequest.ContainmentMinimum == null ||
                routeRequest.ContainmentMaximum == null ||
                routeGroupRequest == null || sourceSkeleton == null ||
                sourceSkeleton.Count < 4 || !IsAxisAligned(sourceSkeleton))
                return wallAlignedSiblings;
            var maximumActualWidth = Math.Max(routeGroupRequest.GetBusbarWidth(routeRequest),
                Math.Max(routeGroupRequest.GetStartLapBusbarWidth(routeRequest),
                    routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest)));
            var wallHalfWidths = new[] { maximumActualWidth / 2d,
                routeGroupRequest.GetBusbarWidth(routeRequest) / 2d }.Distinct().ToList();
            var initialDirection = new Vector3d(sourceSkeleton[0], sourceSkeleton[1]);
            var finalDirection = new Vector3d(sourceSkeleton[sourceSkeleton.Count-2],
                sourceSkeleton[sourceSkeleton.Count-1]);
            var knownWallSignatures = new HashSet<string>(StringComparer.Ordinal) {
                GetSignature(sourceSkeleton) };
            for (var wallAxisIndex = 0; wallAxisIndex < 3; wallAxisIndex++)
            foreach (var wallHalfWidth in wallHalfWidths)
            foreach (var wallCoordinate in new[] {
                GetCoordinate(routeRequest.ContainmentMinimum, wallAxisIndex) + wallHalfWidth,
                GetCoordinate(routeRequest.ContainmentMaximum, wallAxisIndex) - wallHalfWidth })
            {
                if (wallHalfWidth <= GeometryTolerance ||
                    !IsInsideContainmentCoordinate(routeRequest, wallAxisIndex, wallCoordinate))
                    continue;
                var wallSibling = sourceSkeleton.Select(Copy).ToList();
                for (var interiorIndex = 1; interiorIndex < wallSibling.Count-1; interiorIndex++)
                    wallSibling[interiorIndex] = WithCoordinate(
                        wallSibling[interiorIndex], wallAxisIndex, wallCoordinate);
                var normalizedWallSibling = Normalize(wallSibling);
                if (!HasMinimumSegments(normalizedWallSibling, routeRequest.MinimumSegmentLength))
                    continue;
                AddCandidate(wallAlignedSiblings, normalizedWallSibling,
                    initialDirection, finalDirection, knownWallSignatures);
            }
            return wallAlignedSiblings;
        }

        internal static IReadOnlyList<IList<Point3d>> BuildCriticalPlaneAlignedSiblings(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> sourceSkeleton,
            IReadOnlyList<LinkageRouteSolid> fixedTerminalAndObstacleSolids,
            CancellationToken cancellationToken)
        {
            var planeSiblings = new List<IList<Point3d>>();
            if (routeRequest == null || routeGroupRequest == null || sourceSkeleton == null ||
                sourceSkeleton.Count < 4 || !IsAxisAligned(sourceSkeleton) || cancellationToken.IsCancellationRequested)
                return planeSiblings;
            var criticalCoordinates = BuildClearanceCriticalCoordinates(routeRequest, routeGroupRequest,
                fixedTerminalAndObstacleSolids, routeGroupRequest.GetRequiredInsulationClearance(routeRequest), cancellationToken);
            var initialDirection = new Vector3d(sourceSkeleton[0], sourceSkeleton[1]);
            var finalDirection = new Vector3d(sourceSkeleton[sourceSkeleton.Count-2], sourceSkeleton.Last());
            var signatures = new HashSet<string>(StringComparer.Ordinal) { GetSignature(sourceSkeleton) };
            for (var planeAxis = 0; planeAxis < 3; planeAxis++)
            {
                if (!criticalCoordinates.TryGetValue(planeAxis, out var coordinates)) continue;
                for (var runStart = 1; runStart < sourceSkeleton.Count-1; runStart++)
                {
                    if (cancellationToken.IsCancellationRequested) return planeSiblings;
                    var originalCoordinate = GetCoordinate(sourceSkeleton[runStart], planeAxis);
                    var runEnd = runStart;
                    while (runEnd+1 < sourceSkeleton.Count-1 && Math.Abs(
                        GetCoordinate(sourceSkeleton[runEnd+1], planeAxis)-originalCoordinate) <= GeometryTolerance)
                        runEnd++;
                    if (runEnd == runStart) continue;
                    // A terminal-adjacent segment normal to another axis is
                    // locked. Moving it would turn that segment diagonal.
                    if (Math.Abs(GetCoordinate(sourceSkeleton[runStart-1], planeAxis)-originalCoordinate) > GeometryTolerance &&
                        Math.Abs(GetCoordinate(sourceSkeleton[runEnd+1], planeAxis)-originalCoordinate) > GeometryTolerance)
                    {
                        foreach (var direction in new[] { -1d, 1d })
                        foreach (var criticalCoordinate in coordinates.Where(value =>
                            (value-originalCoordinate)*direction > GeometryTolerance)
                            .OrderBy(value => Math.Abs(value-originalCoordinate)).Take(8))
                        {
                            if (cancellationToken.IsCancellationRequested) return planeSiblings;
                            var sibling = sourceSkeleton.Select(Copy).ToList();
                            for (var pointIndex = runStart; pointIndex <= runEnd; pointIndex++)
                                sibling[pointIndex] = WithCoordinate(sibling[pointIndex], planeAxis, criticalCoordinate);
                            var normalizedSibling = Normalize(sibling);
                            if (HasMinimumSegments(normalizedSibling, routeRequest.MinimumSegmentLength))
                                AddCandidate(planeSiblings, normalizedSibling, initialDirection, finalDirection, signatures);
                        }
                    }
                    runStart = runEnd;
                }
            }
            return planeSiblings;
        }

        private static IEnumerable<double>
            GetExplicitContainmentWallCoordinates(
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                int coordinateIndex)
        {
            if (routeRequest?.UseExplicitContainmentBox != true ||
                routeRequest.ContainmentMinimum == null ||
                routeRequest.ContainmentMaximum == null)
            {
                yield break;
            }

            var minimumCoordinate = GetCoordinate(
                routeRequest.ContainmentMinimum,
                coordinateIndex);
            var maximumCoordinate = GetCoordinate(
                routeRequest.ContainmentMaximum,
                coordinateIndex);
            foreach (var generatedHalfExtent in
                GetGeneratedContainmentHalfExtents(
                    routeRequest,
                    routeGroupRequest))
            {
                var minimumWallCenterlineCoordinate =
                    minimumCoordinate + generatedHalfExtent;
                var maximumWallCenterlineCoordinate =
                    maximumCoordinate - generatedHalfExtent;
                if (minimumWallCenterlineCoordinate <=
                    maximumWallCenterlineCoordinate + GeometryTolerance)
                {
                    yield return minimumWallCenterlineCoordinate;
                    yield return maximumWallCenterlineCoordinate;
                }
            }
        }

        private static IReadOnlyList<double>
            OrderClearanceCriticalCoordinatesForCoverage(
            IReadOnlyList<double> orderedDistinctCriticalCoordinates,
            double routeCoordinateMidpoint)
        {
            if (orderedDistinctCriticalCoordinates == null ||
                orderedDistinctCriticalCoordinates.Count <= 2)
            {
                return orderedDistinctCriticalCoordinates ?? Array.Empty<double>();
            }

            var clearanceCoordinateCoverageOrder = new List<double>();
            var clearanceCoordinateCoverageKeys = new HashSet<double>();
            Action<double> addCoverageCoordinate = clearanceCoordinate =>
            {
                var roundedClearanceCoordinate = Math.Round(
                    clearanceCoordinate,
                    6);
                if (clearanceCoordinateCoverageKeys.Add(roundedClearanceCoordinate))
                {
                    clearanceCoordinateCoverageOrder.Add(clearanceCoordinate);
                }
            };

            // 每个轴的临界坐标前段必须同时覆盖内部短路和两侧整体绕行。
            // 否则单轴序的有限候选预算会被靠近中点的坐标耗尽，位于实体
            // 集合最小值或最大值外侧的可行通道永远无法进入候选池。
            addCoverageCoordinate(orderedDistinctCriticalCoordinates
                .OrderBy(clearanceCoordinate => Math.Abs(
                    clearanceCoordinate - routeCoordinateMidpoint))
                .First());
            addCoverageCoordinate(orderedDistinctCriticalCoordinates.First());
            addCoverageCoordinate(orderedDistinctCriticalCoordinates.Last());

            foreach (var remainingClearanceCoordinate in
                orderedDistinctCriticalCoordinates.OrderBy(clearanceCoordinate =>
                    Math.Abs(clearanceCoordinate - routeCoordinateMidpoint)))
            {
                addCoverageCoordinate(remainingClearanceCoordinate);
            }

            return clearanceCoordinateCoverageOrder;
        }

        private static bool IsInsideContainmentCoordinate(
            LinkageMultiGroupRouteRequest routeRequest,
            int coordinateIndex,
            double coordinate)
        {
            if (routeRequest.ContainmentMinimum != null &&
                coordinate < GetCoordinate(routeRequest.ContainmentMinimum, coordinateIndex) -
                GeometryTolerance)
            {
                return false;
            }

            return routeRequest.ContainmentMaximum == null ||
                coordinate <= GetCoordinate(
                    routeRequest.ContainmentMaximum, coordinateIndex) + GeometryTolerance;
        }

        private static void BuildSequenceCandidates(
            ICollection<IList<Point3d>> destinationCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            Point3d startLapPoint,
            Point3d endLapPoint,
            Vector3d requiredStartDirection,
            Vector3d requiredEndDirection,
            IReadOnlyList<int> axisSequence,
            IReadOnlyDictionary<int, IReadOnlyList<double>> coordinateOptions,
            double minimumSegmentLength,
            double stackedUShapeMinimumBaseSegmentLength,
            double startTerminalSegmentLength,
            double endTerminalSegmentLength,
            int maximumCandidatesForSequence,
            int maximumSearchNodeCount,
            ISet<string> knownCandidateSignatures,
            Func<Point3d, Point3d, bool> segmentAcceptance,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var sequenceCandidates = new List<IList<Point3d>>();
            var workingPoints = new List<Point3d> { Copy(startLapPoint) };
            var sequenceSearchVisitedNodeCount = 0;
            var laterSameAxisSegmentIndexesBySegment = axisSequence
                .Select((axisIndex, axisSequenceIndex) =>
                    (IReadOnlyList<int>)axisSequence
                        .Select((laterAxisIndex, laterAxisSequenceIndex) => new
                        {
                            AxisIndex = laterAxisIndex,
                            AxisSequenceIndex = laterAxisSequenceIndex
                        })
                        .Where(axisEntry =>
                            axisEntry.AxisSequenceIndex > axisSequenceIndex &&
                            axisEntry.AxisIndex == axisIndex)
                        .Select(axisEntry => axisEntry.AxisSequenceIndex)
                        .ToList())
                .ToList();
            BuildSequenceCandidateCore(
                sequenceCandidates,
                routeRequest,
                routeGroupRequest,
                workingPoints,
                endLapPoint,
                requiredStartDirection,
                requiredEndDirection,
                axisSequence,
                laterSameAxisSegmentIndexesBySegment,
                coordinateOptions,
                minimumSegmentLength,
                stackedUShapeMinimumBaseSegmentLength,
                startTerminalSegmentLength,
                endTerminalSegmentLength,
                0,
                maximumCandidatesForSequence,
                maximumSearchNodeCount,
                ref sequenceSearchVisitedNodeCount,
                segmentAcceptance,
                cancellationToken);
            foreach (var sequenceCandidate in sequenceCandidates.OrderBy(GetLength))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                AddCandidate(
                    destinationCandidates,
                    sequenceCandidate,
                    requiredStartDirection,
                    requiredEndDirection,
                    knownCandidateSignatures);
            }
        }

        private static void BuildSequenceCandidateCore(
            ICollection<IList<Point3d>> sequenceCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IList<Point3d> workingPoints,
            Point3d endLapPoint,
            Vector3d requiredStartDirection,
            Vector3d requiredEndDirection,
            IReadOnlyList<int> axisSequence,
            IReadOnlyList<IReadOnlyList<int>>
                laterSameAxisSegmentIndexesBySegment,
            IReadOnlyDictionary<int, IReadOnlyList<double>> coordinateOptions,
            double minimumSegmentLength,
            double stackedUShapeMinimumBaseSegmentLength,
            double startTerminalSegmentLength,
            double endTerminalSegmentLength,
            int segmentIndex,
            int maximumCandidatesForSequence,
            int maximumSearchNodeCount,
            ref int sequenceSearchVisitedNodeCount,
            Func<Point3d, Point3d, bool> segmentAcceptance,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested ||
                sequenceSearchVisitedNodeCount >= maximumSearchNodeCount)
            {
                return;
            }

            sequenceSearchVisitedNodeCount++;

            if (sequenceCandidates.Count >= maximumCandidatesForSequence)
            {
                return;
            }

            var currentPoint = workingPoints[workingPoints.Count - 1];
            var currentAxisIndex = axisSequence[segmentIndex];
            var isLastSegment = segmentIndex == axisSequence.Count - 1;
            if (isLastSegment)
            {
                if (!CoordinatesMatchExceptAxis(currentPoint, endLapPoint, currentAxisIndex))
                {
                    return;
                }

                var lastSegmentEnd = Copy(endLapPoint);
                var lastSegmentDirection = new Vector3d(currentPoint, lastSegmentEnd);
                if (lastSegmentDirection.Norm +
                        LinkageRouteGeometryTolerances.PointCoincidence <
                        endTerminalSegmentLength ||
                    lastSegmentDirection.Normalized.Dot(
                        requiredEndDirection.Normalized) <
                        1d - LinkageRouteGeometryTolerances.AssemblyDirection)
                {
                    return;
                }

                if (WouldNewSegmentSelfIntersect(workingPoints, lastSegmentEnd) ||
                    (segmentAcceptance != null &&
                     !segmentAcceptance(currentPoint, lastSegmentEnd)))
                {
                    return;
                }

                var completedPath = workingPoints.Select(Copy).ToList();
                AddPoint(completedPath, lastSegmentEnd);
                if (!TryAdjustActualWidthInternalStraightSections(
                        completedPath,
                        routeRequest,
                        routeGroupRequest,
                        out var adjustedCompletedPath) ||
                    !HasMinimumStackedUShapeSegments(
                        adjustedCompletedPath,
                        routeRequest,
                        routeGroupRequest))
                {
                    return;
                }

                sequenceCandidates.Add(adjustedCompletedPath);
                return;
            }

            var laterSameAxisSegmentIndexes =
                laterSameAxisSegmentIndexesBySegment[segmentIndex];
            var axisAppearsLater = laterSameAxisSegmentIndexes.Count > 0;
            var nextCoordinateValues = (axisAppearsLater
                    ? coordinateOptions[currentAxisIndex]
                    : new[] { GetCoordinate(endLapPoint, currentAxisIndex) })
                .Where(nextCoordinateValue => IsPotentialSequenceCoordinate(
                    currentPoint,
                    endLapPoint,
                    requiredStartDirection,
                    currentAxisIndex,
                    nextCoordinateValue,
                    segmentIndex,
                    laterSameAxisSegmentIndexes,
                    axisSequence.Count,
                    minimumSegmentLength,
                    startTerminalSegmentLength,
                    endTerminalSegmentLength))
                .ToList();
            var remainingCandidateCapacity =
                maximumCandidatesForSequence - sequenceCandidates.Count;
            var maximumCandidatesPerCoordinateBranch = axisAppearsLater &&
                nextCoordinateValues.Count > 1
                ? Math.Max(
                    1,
                    (int)Math.Ceiling(
                        remainingCandidateCapacity /
                        (double)nextCoordinateValues.Count))
                : remainingCandidateCapacity;
            foreach (var nextCoordinateValue in nextCoordinateValues)
            {
                var nextPoint = WithCoordinate(
                    currentPoint, currentAxisIndex, nextCoordinateValue);
                var segmentDirection = new Vector3d(currentPoint, nextPoint);
                var requiredSegmentLength = segmentIndex == 0
                    ? startTerminalSegmentLength
                    : minimumSegmentLength;
                if (segmentDirection.Norm +
                        LinkageRouteGeometryTolerances.PointCoincidence <
                    requiredSegmentLength)
                {
                    continue;
                }

                if (segmentIndex == 0 &&
                    segmentDirection.Normalized.Dot(requiredStartDirection.Normalized) <
                        1d - LinkageRouteGeometryTolerances.AssemblyDirection)
                {
                    continue;
                }

                if (WouldNewSegmentSelfIntersect(workingPoints, nextPoint) ||
                    (segmentAcceptance != null &&
                     !segmentAcceptance(currentPoint, nextPoint)))
                {
                    continue;
                }

                workingPoints.Add(nextPoint);
                // A repeated axis represents a real detour choice. Give every
                // viable coordinate branch a bounded share before one branch can
                // consume the whole per-shape budget with downstream variants.
                // This keeps obstacle extremes and terminal-bend coordinates in
                // the same search without expanding their full Cartesian product.
                var coordinateBranchCandidateLimit = axisAppearsLater
                    ? Math.Min(
                        maximumCandidatesForSequence,
                        sequenceCandidates.Count +
                            maximumCandidatesPerCoordinateBranch)
                    : maximumCandidatesForSequence;
                BuildSequenceCandidateCore(
                    sequenceCandidates,
                    routeRequest,
                    routeGroupRequest,
                    workingPoints,
                    endLapPoint,
                    requiredStartDirection,
                    requiredEndDirection,
                    axisSequence,
                    laterSameAxisSegmentIndexesBySegment,
                    coordinateOptions,
                    minimumSegmentLength,
                    stackedUShapeMinimumBaseSegmentLength,
                    startTerminalSegmentLength,
                    endTerminalSegmentLength,
                    segmentIndex + 1,
                    coordinateBranchCandidateLimit,
                    maximumSearchNodeCount,
                    ref sequenceSearchVisitedNodeCount,
                    segmentAcceptance,
                    cancellationToken);
                workingPoints.RemoveAt(workingPoints.Count - 1);
                if (sequenceCandidates.Count >= maximumCandidatesForSequence)
                {
                    break;
                }
            }
        }

        private static bool IsPotentialSequenceCoordinate(
            Point3d currentPoint,
            Point3d endLapPoint,
            Vector3d requiredStartDirection,
            int currentAxisIndex,
            double nextCoordinateValue,
            int segmentIndex,
            IReadOnlyCollection<int> laterSameAxisSegmentIndexes,
            int axisSequenceCount,
            double minimumSegmentLength,
            double startTerminalSegmentLength,
            double endTerminalSegmentLength)
        {
            var currentCoordinate = GetCoordinate(currentPoint, currentAxisIndex);
            var currentSegmentLength = Math.Abs(nextCoordinateValue - currentCoordinate);
            var requiredCurrentSegmentLength = segmentIndex == 0
                ? startTerminalSegmentLength
                : minimumSegmentLength;
            if (currentSegmentLength +
                    LinkageRouteGeometryTolerances.PointCoincidence <
                requiredCurrentSegmentLength)
            {
                return false;
            }

            if (segmentIndex == 0)
            {
                var signedAxisDirection = nextCoordinateValue >= currentCoordinate
                    ? 1d
                    : -1d;
                var requiredDirectionCoordinate = currentAxisIndex == 0
                    ? requiredStartDirection.Normalized.X
                    : currentAxisIndex == 1
                        ? requiredStartDirection.Normalized.Y
                        : requiredStartDirection.Normalized.Z;
                if (signedAxisDirection * requiredDirectionCoordinate < 1d - 1e-6)
                {
                    return false;
                }
            }

            if (laterSameAxisSegmentIndexes.Count != 1)
            {
                return true;
            }

            var finalSameAxisSegmentIndex = laterSameAxisSegmentIndexes.First();
            var requiredReturnSegmentLength =
                finalSameAxisSegmentIndex == axisSequenceCount - 1
                    ? endTerminalSegmentLength
                    : minimumSegmentLength;
            return Math.Abs(nextCoordinateValue -
                    GetCoordinate(endLapPoint, currentAxisIndex)) +
                LinkageRouteGeometryTolerances.PointCoincidence >=
                requiredReturnSegmentLength;
        }

        private static bool CoordinatesMatchExceptAxis(
            Point3d firstPoint,
            Point3d secondPoint,
            int ignoredCoordinateIndex)
        {
            for (var coordinateIndex = 0; coordinateIndex < 3; coordinateIndex++)
            {
                if (coordinateIndex == ignoredCoordinateIndex)
                {
                    continue;
                }

                if (Math.Abs(GetCoordinate(firstPoint, coordinateIndex) -
                    GetCoordinate(secondPoint, coordinateIndex)) >
                    LinkageRouteGeometryTolerances.PointCoincidence)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool WouldNewSegmentSelfIntersect(
            IList<Point3d> existingPathPoints,
            Point3d newSegmentEnd)
        {
            if (existingPathPoints == null || existingPathPoints.Count < 3 ||
                newSegmentEnd == null)
            {
                return false;
            }

            var newSegmentStart = existingPathPoints[existingPathPoints.Count - 1];
            for (var existingSegmentIndex = 0;
                existingSegmentIndex < existingPathPoints.Count - 2;
                existingSegmentIndex++)
            {
                if (LinkageRouteSolidDistance.GetSegmentMinimumDistance(
                        existingPathPoints[existingSegmentIndex],
                        existingPathPoints[existingSegmentIndex + 1],
                        newSegmentStart,
                        newSegmentEnd) <= GeometryTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEarlyClearanceSegmentAllowed(
            Point3d segmentStart,
            Point3d segmentEnd,
            string currentClearanceGroupKey,
            IEnumerable<LinkageRouteSolid> clearanceSolids,
            double requiredClearance)
        {
            if (clearanceSolids == null)
            {
                return true;
            }

            foreach (var clearanceSolid in clearanceSolids)
            {
                var isSameClearanceGroupSolid = clearanceSolid != null &&
                    !string.IsNullOrWhiteSpace(
                        clearanceSolid.ClearanceGroupKey) &&
                    string.Equals(
                        clearanceSolid.ClearanceGroupKey,
                        currentClearanceGroupKey,
                        StringComparison.OrdinalIgnoreCase);
                var isInputObstacle = clearanceSolid?.SolidRole ==
                    LinkageRouteSolidRole.InputObstacle;
                if (clearanceSolid == null ||
                    (isSameClearanceGroupSolid &&
                     !clearanceSolid.IsForbiddenTerminalRegion &&
                     !isInputObstacle))
                {
                    // ClearanceGroupKey 表示电气相序，不是独立逻辑组身份。
                    // 同相正体积穿入必须等实际铜排成型后再按 GroupKey 复核，
                    // 避免把本组始末端的合法搭接在骨架阶段提前淘汰。
                    continue;
                }

                // 整体禁区只禁止最终铜排实体正体积进入。中心线位于边界上、
                // 或离边界不足项目绝缘距离时都不能在这里被提前淘汰。
                if (clearanceSolid.IsForbiddenTerminalRegion)
                {
                    continue;
                }

                if (isInputObstacle)
                {
                    // 输入障碍物只限制路径实体进入，不要求额外绝缘间距。
                    // 中心线已经穿入时可安全提前剪枝；宽厚包络擦入由完整
                    // 候选实体的正体积相交门禁继续复核。
                    if (LinkageRouteSolidDistance.GetSegmentMinimumDistance(
                            segmentStart,
                            segmentEnd,
                            clearanceSolid) <= GeometryTolerance)
                    {
                        return false;
                    }

                    continue;
                }

                if (requiredClearance <= GeometryTolerance)
                {
                    continue;
                }

                // 这里只使用中心线到真实有向障碍实体的距离做必然失败剪枝。
                // 铜排实体包含中心线，因此中心线本身已不足绝缘时，最终铜排一定失败；
                // 反之仍交给候选构造阶段的完整有向长方体距离校验。
                var axisAlignedDistanceLowerBound = LinkageRouteSolidDistance
                    .GetSegmentAxisAlignedBoundingBoxDistanceLowerBound(
                        segmentStart,
                        segmentEnd,
                        clearanceSolid);
                if (axisAlignedDistanceLowerBound + GeometryTolerance >=
                    requiredClearance)
                {
                    continue;
                }

                if (LinkageRouteSolidDistance.GetSegmentMinimumDistance(
                    segmentStart,
                    segmentEnd,
                    clearanceSolid) + GeometryTolerance < requiredClearance)
                {
                    return false;
                }
            }

            return true;
        }

        public readonly struct SegmentClearanceCacheKey :
            IEquatable<SegmentClearanceCacheKey>
        {
            private SegmentClearanceCacheKey(
                long firstX,
                long firstY,
                long firstZ,
                long secondX,
                long secondY,
                long secondZ)
            {
                FirstX = firstX;
                FirstY = firstY;
                FirstZ = firstZ;
                SecondX = secondX;
                SecondY = secondY;
                SecondZ = secondZ;
            }

            private long FirstX { get; }
            private long FirstY { get; }
            private long FirstZ { get; }
            private long SecondX { get; }
            private long SecondY { get; }
            private long SecondZ { get; }

            public static SegmentClearanceCacheKey Create(
                Point3d firstEndpoint,
                Point3d secondEndpoint)
            {
                var firstX = GetCoordinateBits(firstEndpoint.X);
                var firstY = GetCoordinateBits(firstEndpoint.Y);
                var firstZ = GetCoordinateBits(firstEndpoint.Z);
                var secondX = GetCoordinateBits(secondEndpoint.X);
                var secondY = GetCoordinateBits(secondEndpoint.Y);
                var secondZ = GetCoordinateBits(secondEndpoint.Z);
                if (CompareEndpointCoordinates(firstEndpoint, secondEndpoint) > 0)
                {
                    return new SegmentClearanceCacheKey(
                        secondX,
                        secondY,
                        secondZ,
                        firstX,
                        firstY,
                        firstZ);
                }

                return new SegmentClearanceCacheKey(
                    firstX,
                    firstY,
                    firstZ,
                    secondX,
                    secondY,
                    secondZ);
            }

            public bool Equals(SegmentClearanceCacheKey otherKey)
            {
                return FirstX == otherKey.FirstX &&
                    FirstY == otherKey.FirstY &&
                    FirstZ == otherKey.FirstZ &&
                    SecondX == otherKey.SecondX &&
                    SecondY == otherKey.SecondY &&
                    SecondZ == otherKey.SecondZ;
            }

            public override bool Equals(object comparedObject)
            {
                return comparedObject is SegmentClearanceCacheKey otherKey &&
                    Equals(otherKey);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = FirstX.GetHashCode();
                    hashCode = hashCode * 397 ^ FirstY.GetHashCode();
                    hashCode = hashCode * 397 ^ FirstZ.GetHashCode();
                    hashCode = hashCode * 397 ^ SecondX.GetHashCode();
                    hashCode = hashCode * 397 ^ SecondY.GetHashCode();
                    return hashCode * 397 ^ SecondZ.GetHashCode();
                }
            }

            private static long GetCoordinateBits(double coordinate)
            {
                return BitConverter.DoubleToInt64Bits(
                    coordinate == 0d ? 0d : coordinate);
            }

            private static int CompareEndpointCoordinates(
                Point3d firstEndpoint,
                Point3d secondEndpoint)
            {
                var xComparison = firstEndpoint.X.CompareTo(secondEndpoint.X);
                if (xComparison != 0)
                {
                    return xComparison;
                }

                var yComparison = firstEndpoint.Y.CompareTo(secondEndpoint.Y);
                return yComparison != 0
                    ? yComparison
                    : firstEndpoint.Z.CompareTo(secondEndpoint.Z);
            }
        }

        private static int GetAxisIndex(Vector3d axisDirection)
        {
            var normalizedDirection = axisDirection.Normalized;
            var absoluteX = Math.Abs(normalizedDirection.X);
            var absoluteY = Math.Abs(normalizedDirection.Y);
            var absoluteZ = Math.Abs(normalizedDirection.Z);
            if (absoluteX >= absoluteY && absoluteX >= absoluteZ) return 0;
            return absoluteY >= absoluteZ ? 1 : 2;
        }

        private static double GetRawTerminalLayerError(
            IList<Point3d> candidatePath,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            double generatedSingleBusbarThickness)
        {
            var generatedStackCount = routeGroupRequest.GetStackCount(routeRequest);
            var startTerminalError = GetTerminalLayerError(
                routeRequest,
                routeGroupRequest,
                routeGroupRequest.StartTerminal,
                candidatePath[0],
                generatedStackCount,
                generatedSingleBusbarThickness,
                true);
            var endTerminalError = GetTerminalLayerError(
                routeRequest,
                routeGroupRequest,
                routeGroupRequest.EndTerminal,
                candidatePath[candidatePath.Count - 1],
                generatedStackCount,
                generatedSingleBusbarThickness,
                false);
            return startTerminalError + endTerminalError;
        }

        private static double GetTerminalLayerError(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageTerminalAssembly terminalAssembly,
            Point3d generatedBaseEndpoint,
            int generatedStackCount,
            double generatedSingleBusbarThickness,
            bool useStartTerminal)
        {
            if (!LinkageRouteStackPlacementSolver.TryMeasureBaseCenterOffset(
                    terminalAssembly,
                    generatedBaseEndpoint,
                    out var measuredBaseCenterOffset))
            {
                return double.MaxValue / 4d;
            }

            var terminalAdditionalSpace = useStartTerminal
                ? routeGroupRequest.StackStartSpace
                : routeGroupRequest.StackEndSpace;
            var terminalPlacements =
                LinkageRouteStackPlacementSolver.ResolveTerminalPlacements(
                    terminalAssembly,
                    generatedStackCount,
                    generatedSingleBusbarThickness,
                    terminalAdditionalSpace,
                    routeGroupRequest.StackMiddleSpace,
                    false,
                    useStartTerminal);
            if (terminalPlacements.Count == 0)
            {
                return double.MaxValue / 4d;
            }

            return terminalPlacements.Min(stackPlacement => Math.Abs(
                stackPlacement.BaseCenterOffset - measuredBaseCenterOffset));
        }

        private static string GetSignedAxisShapeSignature(IList<Point3d> candidatePath)
        {
            var signedAxisNames = new List<string>();
            for (var pointIndex = 0;
                pointIndex < candidatePath.Count - 1;
                pointIndex++)
            {
                var segmentDirection = new Vector3d(
                    candidatePath[pointIndex],
                    candidatePath[pointIndex + 1]).Normalized;
                var signedAxisName = Math.Abs(segmentDirection.X) >= 1d - 1e-6
                    ? (segmentDirection.X >= 0d ? "+X" : "-X")
                    : Math.Abs(segmentDirection.Y) >= 1d - 1e-6
                        ? (segmentDirection.Y >= 0d ? "+Y" : "-Y")
                        : segmentDirection.Z >= 0d ? "+Z" : "-Z";
                if (signedAxisNames.Count == 0 ||
                    !string.Equals(
                        signedAxisNames[signedAxisNames.Count - 1],
                        signedAxisName,
                        StringComparison.Ordinal))
                {
                    signedAxisNames.Add(signedAxisName);
                }
            }

            return string.Join("-", signedAxisNames);
        }

        private static string GetCandidateDiversitySignature(
            IList<Point3d> candidatePath,
            LinkageRouteGroupRequest routeGroupRequest,
            double generatedInternalLapContinuationLength)
        {
            var startTerminalLayerOffset = new Vector3d(
                routeGroupRequest.StartTerminal.RouteAnchor,
                candidatePath[0]).Dot(
                    routeGroupRequest.StartTerminal.ThicknessDirection.Normalized);
            var endTerminalLayerOffset = new Vector3d(
                routeGroupRequest.EndTerminal.RouteAnchor,
                candidatePath[candidatePath.Count - 1]).Dot(
                    routeGroupRequest.EndTerminal.ThicknessDirection.Normalized);
            var startTerminalWidthCoordinate = GetTerminalWidthCoordinate(
                candidatePath[0],
                routeGroupRequest.StartTerminal);
            var endTerminalWidthCoordinate = GetTerminalWidthCoordinate(
                candidatePath[candidatePath.Count - 1],
                routeGroupRequest.EndTerminal);
            var startTerminalSegmentLength = candidatePath[0]
                .DistanceTo(candidatePath[1]);
            var endTerminalSegmentLength = candidatePath[candidatePath.Count - 2]
                .DistanceTo(candidatePath[candidatePath.Count - 1]);
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}|IR={1}|SO={2:F6}|EO={3:F6}|SW={4:F6}|EW={5:F6}|SS={6:F6}|ES={7:F6}",
                GetSignedAxisShapeSignature(candidatePath),
                GetInternalLapReserveSignature(
                    candidatePath,
                    generatedInternalLapContinuationLength),
                startTerminalLayerOffset,
                endTerminalLayerOffset,
                startTerminalWidthCoordinate,
                endTerminalWidthCoordinate,
                startTerminalSegmentLength,
                endTerminalSegmentLength);
        }

        private static string GetInternalLapReserveSignature(
            IList<Point3d> candidatePath,
            double generatedInternalLapContinuationLength)
        {
            if (candidatePath == null || candidatePath.Count < 4)
            {
                return "NONE";
            }

            return string.Join(string.Empty, Enumerable.Range(
                    1,
                    candidatePath.Count - 3)
                .Select(segmentIndex => candidatePath[segmentIndex]
                    .DistanceTo(candidatePath[segmentIndex + 1]) + GeometryTolerance >=
                    generatedInternalLapContinuationLength
                        ? "L"
                        : "S"));
        }

        private static double GetTerminalWidthCoordinate(
            Point3d terminalPathEndpoint,
            LinkageTerminalAssembly terminalAssembly)
        {
            var representativeWidthDirection = terminalAssembly?.Members == null
                ? null
                : terminalAssembly.Members
                    .Select(terminalMember => terminalMember?.FixedBusbar?.WidthVector)
                    .FirstOrDefault(widthDirection => widthDirection != null &&
                        widthDirection.Norm > GeometryTolerance);
            if (terminalPathEndpoint == null || terminalAssembly?.RouteAnchor == null ||
                representativeWidthDirection == null)
            {
                return 0d;
            }

            return new Vector3d(
                    terminalAssembly.RouteAnchor,
                    terminalPathEndpoint)
                .Dot(representativeWidthDirection.Normalized);
        }

        private static string GetTerminalCenteringProposalSignature(
            IList<Point3d> candidatePath,
            LinkageRouteGroupRequest routeGroupRequest,
            string signedAxisShapeSignature,
            bool includeStartTerminalWidthCoordinate,
            bool includeEndTerminalWidthCoordinate)
        {
            var startTerminalWidthSignature = includeStartTerminalWidthCoordinate
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "|SW={0:F6}",
                    GetTerminalWidthCoordinate(
                        candidatePath[0],
                        routeGroupRequest.StartTerminal))
                : string.Empty;
            var endTerminalWidthSignature = includeEndTerminalWidthCoordinate
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "|EW={0:F6}",
                    GetTerminalWidthCoordinate(
                        candidatePath[candidatePath.Count - 1],
                        routeGroupRequest.EndTerminal))
                : string.Empty;
            return signedAxisShapeSignature +
                startTerminalWidthSignature +
                endTerminalWidthSignature;
        }

        private static IReadOnlyList<Point3d> GetTerminalExtensionPoints(
            Point3d terminalLapPoint,
            Point3d oppositeLapPoint,
            Vector3d terminalLengthDirection,
            double minimumExtensionLength,
            double conversionExtensionLength,
            double perpendicularLapBendContinuationDistance,
            double straightDoublePerpendicularLapTerminalExtension,
            bool extendsAlongDirection)
        {
            var extensionSign = extendsAlongDirection ? 1d : -1d;
            var extensionDirection = new Vector3d(
                terminalLengthDirection.X * extensionSign,
                terminalLengthDirection.Y * extensionSign,
                terminalLengthDirection.Z * extensionSign);
            var extensionLengths = new List<double>
            {
                minimumExtensionLength,
                Math.Max(minimumExtensionLength, conversionExtensionLength)
            };
            var projectedOppositeDistance = new Vector3d(
                terminalLapPoint, oppositeLapPoint).Dot(extensionDirection.Normalized);
            if (projectedOppositeDistance + GeometryTolerance >= minimumExtensionLength)
            {
                extensionLengths.Add(projectedOppositeDistance);
            }

            var nearerPerpendicularLapExtension =
                projectedOppositeDistance - perpendicularLapBendContinuationDistance;
            if (nearerPerpendicularLapExtension + GeometryTolerance >= minimumExtensionLength)
            {
                extensionLengths.Add(nearerPerpendicularLapExtension);
            }

            var fartherPerpendicularLapExtension =
                projectedOppositeDistance + perpendicularLapBendContinuationDistance;
            if (fartherPerpendicularLapExtension + GeometryTolerance >= minimumExtensionLength)
            {
                extensionLengths.Add(fartherPerpendicularLapExtension);
            }

            if (straightDoublePerpendicularLapTerminalExtension + GeometryTolerance >=
                minimumExtensionLength)
            {
                extensionLengths.Add(straightDoublePerpendicularLapTerminalExtension);
            }

            return extensionLengths
                .Distinct()
                .Select(extensionLength => Translate(
                    terminalLapPoint, extensionDirection, extensionLength))
                .ToList();
        }

        private static void AddDirectConnectorCandidates(
            ICollection<IList<Point3d>> candidatePaths,
            Point3d startLapPoint,
            Point3d startExitPoint,
            Point3d endEntryPoint,
            Point3d endLapPoint,
            Vector3d requiredStartDirection,
            Vector3d requiredEndDirection,
            ISet<string> knownCandidateSignatures)
        {
            foreach (var connectorCoordinateOrder in ConnectorCoordinateOrders)
            {
                var connectorPoints = BuildOrderedAxisPath(
                    startExitPoint, endEntryPoint, connectorCoordinateOrder);
                var completePath = new List<Point3d>
                {
                    Copy(startLapPoint),
                    Copy(startExitPoint)
                };
                foreach (var connectorPoint in connectorPoints.Skip(1))
                {
                    AddPoint(completePath, connectorPoint);
                }

                AddPoint(completePath, endLapPoint);
                AddCandidate(
                    candidatePaths,
                    completePath,
                    requiredStartDirection,
                    requiredEndDirection,
                    knownCandidateSignatures);
            }
        }

        /// <summary>
        /// 当公共搭接区两端位于同一平面、首末出排方向同轴同向时，一根
        /// 平弯排的最短中心线族为“首方向-横向-末方向”。横向转折可在
        /// 两端最小直段限定的闭区间内移动而不增加总长。保留区间两端及
        /// 中点，既覆盖最短落位，也给多逻辑组组合留下错开转折的选择。
        /// </summary>
        private static void AddShortestCoplanarParallelConnectorCandidates(
            ICollection<IList<Point3d>> destinationCandidatePaths,
            Point3d startLapPoint,
            Point3d endLapPoint,
            Vector3d requiredStartDirection,
            Vector3d requiredEndDirection,
            double requiredStartSegmentLength,
            double requiredEndSegmentLength,
            ISet<string> knownCandidateSignatures)
        {
            if (destinationCandidatePaths == null ||
                startLapPoint == null ||
                endLapPoint == null ||
                requiredStartDirection == null ||
                requiredEndDirection == null ||
                requiredStartDirection.Norm <= GeometryTolerance ||
                requiredEndDirection.Norm <= GeometryTolerance)
            {
                return;
            }

            var normalizedStartDirection = requiredStartDirection.Normalized;
            var normalizedEndDirection = requiredEndDirection.Normalized;
            if (normalizedStartDirection.Dot(normalizedEndDirection) <
                1d - 1e-6d)
            {
                return;
            }

            var longitudinalAxisIndex = GetAxisIndex(normalizedStartDirection);
            if (GetAxisIndex(normalizedEndDirection) != longitudinalAxisIndex)
            {
                return;
            }

            var longitudinalAxisSign = GetCoordinate(
                new Point3d(
                    normalizedStartDirection.X,
                    normalizedStartDirection.Y,
                    normalizedStartDirection.Z),
                longitudinalAxisIndex) >= 0d
                    ? 1d
                    : -1d;
            var startLongitudinalCoordinate = GetCoordinate(
                    startLapPoint,
                    longitudinalAxisIndex) *
                longitudinalAxisSign;
            var endLongitudinalCoordinate = GetCoordinate(
                    endLapPoint,
                    longitudinalAxisIndex) *
                longitudinalAxisSign;
            var minimumTurnLongitudinalCoordinate =
                startLongitudinalCoordinate +
                Math.Max(0d, requiredStartSegmentLength);
            var maximumTurnLongitudinalCoordinate =
                endLongitudinalCoordinate -
                Math.Max(0d, requiredEndSegmentLength);
            if (minimumTurnLongitudinalCoordinate >
                maximumTurnLongitudinalCoordinate + GeometryTolerance)
            {
                return;
            }

            var transverseAxisIndices = Enumerable.Range(0, 3)
                .Where(axisIndex =>
                    axisIndex != longitudinalAxisIndex &&
                    Math.Abs(
                        GetCoordinate(startLapPoint, axisIndex) -
                        GetCoordinate(endLapPoint, axisIndex)) >
                            GeometryTolerance)
                .ToList();
            if (transverseAxisIndices.Count == 0)
            {
                AddCandidate(
                    destinationCandidatePaths,
                    new[] { startLapPoint, endLapPoint },
                    requiredStartDirection,
                    requiredEndDirection,
                    knownCandidateSignatures);
                return;
            }

            if (transverseAxisIndices.Count != 1)
            {
                return;
            }

            var turnLongitudinalCoordinates = new[]
            {
                minimumTurnLongitudinalCoordinate,
                (minimumTurnLongitudinalCoordinate +
                    maximumTurnLongitudinalCoordinate) / 2d,
                maximumTurnLongitudinalCoordinate
            };
            foreach (var turnLongitudinalCoordinate in
                turnLongitudinalCoordinates
                    .GroupBy(coordinate => Math.Round(coordinate, 6))
                    .Select(coordinateGroup => coordinateGroup.First()))
            {
                var physicalTurnCoordinate =
                    turnLongitudinalCoordinate * longitudinalAxisSign;
                var startTurnPoint = WithCoordinate(
                    startLapPoint,
                    longitudinalAxisIndex,
                    physicalTurnCoordinate);
                var endTurnPoint = WithCoordinate(
                    endLapPoint,
                    longitudinalAxisIndex,
                    physicalTurnCoordinate);
                AddCandidate(
                    destinationCandidatePaths,
                    new[]
                    {
                        startLapPoint,
                        startTurnPoint,
                        endTurnPoint,
                        endLapPoint
                    },
                    requiredStartDirection,
                    requiredEndDirection,
                    knownCandidateSignatures);
            }
        }

        private static void AddDetourConnectorCandidates(
            ICollection<IList<Point3d>> candidatePaths,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            Point3d startLapPoint,
            Point3d startExitPoint,
            Point3d endEntryPoint,
            Point3d endLapPoint,
            Vector3d requiredStartDirection,
            Vector3d requiredEndDirection,
            double terminalExtensionLength,
            double requiredInsulationClearance,
            ISet<string> knownCandidateSignatures)
        {
            for (var detourAxisIndex = 0; detourAxisIndex < 3; detourAxisIndex++)
            {
                foreach (var detourCoordinate in GetDetourCoordinates(
                    routeRequest,
                    routeGroupRequest,
                    detourAxisIndex,
                    terminalExtensionLength,
                    requiredInsulationClearance))
                {
                    var detourStartPoint = WithCoordinate(
                        startExitPoint, detourAxisIndex, detourCoordinate);
                    var detourEndPoint = WithCoordinate(
                        endEntryPoint, detourAxisIndex, detourCoordinate);
                    foreach (var connectorCoordinateOrder in ConnectorCoordinateOrders)
                    {
                        var connectorPoints = BuildOrderedAxisPath(
                            detourStartPoint, detourEndPoint, connectorCoordinateOrder);
                        var completePath = new List<Point3d>
                        {
                            Copy(startLapPoint),
                            Copy(startExitPoint)
                        };
                        AddPoint(completePath, detourStartPoint);
                        foreach (var connectorPoint in connectorPoints.Skip(1))
                        {
                            AddPoint(completePath, connectorPoint);
                        }

                        AddPoint(completePath, endEntryPoint);
                        AddPoint(completePath, endLapPoint);
                        AddCandidate(
                            candidatePaths,
                            completePath,
                            requiredStartDirection,
                            requiredEndDirection,
                            knownCandidateSignatures);
                    }
                }
            }
        }

        private static IList<double> GetDetourCoordinates(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int coordinateIndex,
            double terminalExtensionLength,
            double requiredInsulationClearance)
        {
            var geometryCoordinates = new List<double>();
            foreach (var routeGroup in routeRequest.Groups)
            {
                if (routeGroup?.StartTerminal?.RouteAnchor != null)
                {
                    geometryCoordinates.Add(GetCoordinate(
                        routeGroup.StartTerminal.RouteAnchor, coordinateIndex));
                }

                if (routeGroup?.EndTerminal?.RouteAnchor != null)
                {
                    geometryCoordinates.Add(GetCoordinate(
                        routeGroup.EndTerminal.RouteAnchor, coordinateIndex));
                }
            }

            foreach (var routeObstacle in routeRequest.Obstacles ?? new List<LinkageRouteObstacle>())
            {
                if (routeObstacle?.Minimum != null)
                {
                    geometryCoordinates.Add(GetCoordinate(routeObstacle.Minimum, coordinateIndex));
                }

                if (routeObstacle?.Maximum != null)
                {
                    geometryCoordinates.Add(GetCoordinate(routeObstacle.Maximum, coordinateIndex));
                }
            }

            if (geometryCoordinates.Count == 0)
            {
                return new List<double>();
            }

            var generatedSingleBusbarThickness = routeGroupRequest.GetBusbarThickness(routeRequest);
            var stackEnvelopeThickness = LinkageRouteStackGeometry.GetStackEnvelopeThickness(
                generatedSingleBusbarThickness,
                routeGroupRequest.GetStackCount(routeRequest),
                routeGroupRequest.StackMiddleSpace);
            var detourMargin = Math.Max(
                terminalExtensionLength,
                requiredInsulationClearance +
                stackEnvelopeThickness / 2d +
                routeGroupRequest.GetBusbarWidth(routeRequest) / 2d +
                routeRequest.GetEffectiveBendAllowance() +
                routeRequest.MinimumSegmentLength);
            var detourCoordinates = new List<double>
            {
                geometryCoordinates.Min() - detourMargin,
                geometryCoordinates.Max() + detourMargin
            };
            if (routeRequest.UseExplicitContainmentBox)
            {
                detourCoordinates.AddRange(
                    GetExplicitContainmentWallCoordinates(
                        routeRequest,
                        routeGroupRequest,
                        coordinateIndex));
            }

            if (routeRequest.ContainmentMinimum != null)
            {
                var minimumCoordinate = GetCoordinate(
                    routeRequest.ContainmentMinimum, coordinateIndex);
                detourCoordinates = detourCoordinates
                    .Where(detourCoordinate => detourCoordinate >= minimumCoordinate)
                    .ToList();
            }

            if (routeRequest.ContainmentMaximum != null)
            {
                var maximumCoordinate = GetCoordinate(
                    routeRequest.ContainmentMaximum, coordinateIndex);
                detourCoordinates = detourCoordinates
                    .Where(detourCoordinate => detourCoordinate <= maximumCoordinate)
                    .ToList();
            }

            return detourCoordinates.Distinct().ToList();
        }

        private static double[] GetTerminalLayerOffsets(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageTerminalAssembly terminalAssembly,
            int stackCount,
            double generatedSingleBusbarThickness,
            bool useStartTerminal,
            bool useHorizontalConnectionPlacements = false)
        {
            var terminalAdditionalSpace = useStartTerminal
                ? routeGroupRequest.StackStartSpace
                : routeGroupRequest.StackEndSpace;
            var terminalPlacements = LinkageRouteStackPlacementSolver.ResolveTerminalPlacements(
                    terminalAssembly,
                    stackCount,
                    generatedSingleBusbarThickness,
                    terminalAdditionalSpace,
                    routeGroupRequest.StackMiddleSpace,
                    false,
                    useStartTerminal);
            if (useStartTerminal && useHorizontalConnectionPlacements)
                terminalPlacements = LinkageRouteTerminalAccessoryBuilder.PrepareSingleStartHorizontalConnectionPlacements(
                    terminalPlacements, routeRequest, routeGroupRequest, generatedSingleBusbarThickness);
            return terminalPlacements
                .Select(stackPlacement => stackPlacement.BaseCenterOffset)
                .Distinct()
                .OrderBy(stackBaseCenterOffset => Math.Abs(stackBaseCenterOffset))
                .ThenBy(stackBaseCenterOffset => stackBaseCenterOffset)
                .ToArray();
        }

        private static double GetCollectivePerpendicularLapSpan(
            LinkageTerminalAssembly terminalAssembly,
            double fallbackWidth)
        {
            return LinkageRouteTerminalPoseFactory.GetCollectivePerpendicularLapSpan(
                terminalAssembly,
                fallbackWidth);
        }

        private static List<Point3d> BuildOrderedAxisPath(
            Point3d startPoint,
            Point3d endPoint,
            IReadOnlyList<int> coordinateOrder)
        {
            var axisPath = new List<Point3d> { Copy(startPoint) };
            var currentPoint = Copy(startPoint);
            foreach (var coordinateIndex in coordinateOrder)
            {
                var nextPoint = WithCoordinate(
                    currentPoint,
                    coordinateIndex,
                    GetCoordinate(endPoint, coordinateIndex));
                AddPoint(axisPath, nextPoint);
                currentPoint = nextPoint;
            }

            AddPoint(axisPath, endPoint);
            return Normalize(axisPath);
        }

        private static void AddCandidate(
            ICollection<IList<Point3d>> candidatePaths,
            IEnumerable<Point3d> candidatePoints,
            Vector3d requiredStartDirection,
            Vector3d requiredEndDirection,
            ISet<string> knownCandidateSignatures = null)
        {
            var normalizedPath = Normalize(candidatePoints);
            if (normalizedPath.Count < 2 ||
                !IsAxisAligned(normalizedPath) ||
                HasImmediateReversal(normalizedPath) ||
                !HasTerminalDirections(
                    normalizedPath, requiredStartDirection, requiredEndDirection))
            {
                return;
            }

            var candidateSignature = GetSignature(normalizedPath);
            if (knownCandidateSignatures != null)
            {
                if (knownCandidateSignatures.Add(candidateSignature))
                {
                    candidatePaths.Add(normalizedPath);
                }

                return;
            }

            if (candidatePaths.All(existingPath =>
                GetSignature(existingPath) != candidateSignature))
            {
                candidatePaths.Add(normalizedPath);
            }
        }

        private static List<Point3d> Normalize(IEnumerable<Point3d> sourcePoints)
        {
            var normalizedPoints = new List<Point3d>();
            foreach (var sourcePoint in sourcePoints)
            {
                AddPoint(normalizedPoints, sourcePoint);
                while (normalizedPoints.Count >= 3)
                {
                    var firstDirection = new Vector3d(
                        normalizedPoints[normalizedPoints.Count - 3],
                        normalizedPoints[normalizedPoints.Count - 2]);
                    var secondDirection = new Vector3d(
                        normalizedPoints[normalizedPoints.Count - 2],
                        normalizedPoints[normalizedPoints.Count - 1]);
                    if (firstDirection.Norm > GeometryTolerance &&
                        secondDirection.Norm > GeometryTolerance &&
                        firstDirection.Normalized.Cross(secondDirection.Normalized).Norm <= GeometryTolerance &&
                        firstDirection.Normalized.Dot(secondDirection.Normalized) > 0d)
                    {
                        normalizedPoints.RemoveAt(normalizedPoints.Count - 2);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return normalizedPoints;
        }

        private static bool HasTerminalDirections(
            IList<Point3d> points,
            Vector3d requiredStartDirection,
            Vector3d requiredEndDirection)
        {
            var firstDirection = new Vector3d(points[0], points[1]).Normalized;
            var lastDirection = new Vector3d(
                points[points.Count - 2], points[points.Count - 1]).Normalized;
            return firstDirection.Dot(requiredStartDirection.Normalized) >= 1d - 1e-6 &&
                lastDirection.Dot(requiredEndDirection.Normalized) >= 1d - 1e-6;
        }

        private static bool IsAxisAligned(IList<Point3d> points)
        {
            for (var pointIndex = 0; pointIndex < points.Count - 1; pointIndex++)
            {
                var segmentDirection = new Vector3d(points[pointIndex], points[pointIndex + 1]);
                if (segmentDirection.Norm <= GeometryTolerance)
                {
                    return false;
                }

                var normalizedDirection = segmentDirection.Normalized;
                var alignedCoordinateCount =
                    (Math.Abs(normalizedDirection.X) >= 1d - 1e-6 ? 1 : 0) +
                    (Math.Abs(normalizedDirection.Y) >= 1d - 1e-6 ? 1 : 0) +
                    (Math.Abs(normalizedDirection.Z) >= 1d - 1e-6 ? 1 : 0);
                if (alignedCoordinateCount != 1)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasImmediateReversal(IList<Point3d> points)
        {
            for (var pointIndex = 1; pointIndex < points.Count - 1; pointIndex++)
            {
                var incomingDirection = new Vector3d(
                    points[pointIndex - 1], points[pointIndex]).Normalized;
                var outgoingDirection = new Vector3d(
                    points[pointIndex], points[pointIndex + 1]).Normalized;
                if (incomingDirection.Dot(outgoingDirection) <= -1d + 1e-6)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasMinimumSegments(
            IList<Point3d> points,
            double minimumSegmentLength)
        {
            return points.Zip(points.Skip(1), (firstPoint, secondPoint) =>
                    firstPoint.DistanceTo(secondPoint))
                .All(segmentLength => segmentLength +
                    LinkageRouteGeometryTolerances.PointCoincidence >=
                    minimumSegmentLength);
        }

        private static bool HasMinimumStackedUShapeSegments(
            IList<Point3d> points,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            return HasMinimumStackedUShapeSegments(
                points,
                routeRequest.MinimumSegmentLength,
                GetStackedUShapeMinimumBaseSegmentLength(
                    routeRequest,
                    routeGroupRequest),
                GetUShapeBendTangentSegmentLength(
                    routeRequest,
                    routeGroupRequest),
                GetUShapeMinimumMiddleDistance(
                    routeRequest,
                    routeGroupRequest));
        }

        private static IReadOnlyList<IList<Point3d>>
            AdjustActualWidthInternalStraightSectionCandidates(
            IEnumerable<IList<Point3d>> sourceCandidates,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (sourceCandidates == null)
            {
                return Array.Empty<IList<Point3d>>();
            }

            var adjustedCandidates = new List<IList<Point3d>>();
            foreach (var sourceCandidate in sourceCandidates)
            {
                if (TryAdjustActualWidthInternalStraightSections(
                        sourceCandidate,
                        routeRequest,
                        routeGroupRequest,
                        out var adjustedCandidate))
                {
                    adjustedCandidates.Add(adjustedCandidate);
                }
            }

            return adjustedCandidates;
        }

        /// <summary>
        /// 对异规格三段以上路径中的短直 I 做局部延长，不再淘汰它再去
        /// 选择一条更长的绕行路径。直 I 没有折弯半径补偿，源路径只表示
        /// 两侧搭接范围去掉各半个排宽后的中心线距离，因此目标长度为：
        /// 两端实际排宽之和 / 2。实体化阶段再按真实规格恢复两侧搭接面。
        /// </summary>
        private static bool TryAdjustActualWidthInternalStraightSections(
            IList<Point3d> sourcePoints,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out IList<Point3d> adjustedPoints)
        {
            adjustedPoints = sourcePoints == null
                ? null
                : sourcePoints.Select(Copy).ToList();
            if (sourcePoints == null)
            {
                return false;
            }

            if (routeRequest == null ||
                routeGroupRequest?.StartTerminal == null)
            {
                return true;
            }

            var actualStartLapWidth = routeGroupRequest
                .PathSearchActualStartLapBusbarWidth;
            if (actualStartLapWidth <= GeometryTolerance)
            {
                actualStartLapWidth = routeGroupRequest.GetStartLapBusbarWidth(
                    routeRequest);
            }

            var actualCabinetTopLapWidth = routeGroupRequest
                .PathSearchActualCabinetTopLapBusbarWidth;
            if (actualCabinetTopLapWidth <= GeometryTolerance)
            {
                actualCabinetTopLapWidth = routeGroupRequest
                    .GetCabinetTopLapBusbarWidth(routeRequest);
            }

            if (Math.Abs(actualStartLapWidth - actualCabinetTopLapWidth) <=
                GeometryTolerance)
            {
                return true;
            }

            var transitionPointIndices = LinkageRouteCenterlineConverter
                .GetActualPlanarTransitionPointIndices(
                    adjustedPoints,
                    routeGroupRequest.StartTerminal.ThicknessDirection);
            var physicalSectionCount = transitionPointIndices.Count + 1;
            if (physicalSectionCount < 3)
            {
                return true;
            }

            var genericSectionWidth = routeGroupRequest.GetBusbarWidth(
                routeRequest);
            if (genericSectionWidth <= GeometryTolerance)
            {
                return true;
            }

            for (var physicalSectionIndex = 1;
                physicalSectionIndex < physicalSectionCount - 1;
                physicalSectionIndex++)
            {
                var sectionStartPointIndex = physicalSectionIndex == 1
                    ? transitionPointIndices[0]
                    : transitionPointIndices[physicalSectionIndex - 1];
                var sectionEndPointIndex = transitionPointIndices[
                    physicalSectionIndex];
                // 只有两个实际转折点之间恰好一根源线段时，才是需要按
                // 两端实际排宽补足的短直 I；已有转折的中间段保持原路径。
                if (sectionEndPointIndex - sectionStartPointIndex != 1)
                {
                    continue;
                }

                var precedingSectionWidth = physicalSectionIndex == 1
                    ? actualStartLapWidth
                    : genericSectionWidth;
                var followingSectionWidth = physicalSectionIndex ==
                    physicalSectionCount - 2
                    ? actualCabinetTopLapWidth
                    : genericSectionWidth;
                if (precedingSectionWidth <= GeometryTolerance ||
                    followingSectionWidth <= GeometryTolerance)
                {
                    continue;
                }

                var requiredSectionRawLength = Math.Max(
                    routeRequest.MinimumSegmentLength,
                    (precedingSectionWidth + followingSectionWidth) / 2d);
                var rawSectionLength = adjustedPoints[sectionStartPointIndex]
                    .DistanceTo(adjustedPoints[sectionEndPointIndex]);
                if (rawSectionLength + GeometryTolerance >=
                    requiredSectionRawLength)
                {
                    continue;
                }

                var sectionDirection = new Vector3d(
                    adjustedPoints[sectionStartPointIndex],
                    adjustedPoints[sectionEndPointIndex]);
                if (sectionDirection.Norm <= GeometryTolerance)
                {
                    return false;
                }

                sectionDirection = sectionDirection.Normalized;
                var extensionDistance = requiredSectionRawLength -
                    rawSectionLength;
                var extendedStartPoint = Translate(
                    adjustedPoints[sectionStartPointIndex],
                    sectionDirection,
                    -extensionDistance / 2d);
                var extendedEndPoint = Translate(
                    adjustedPoints[sectionEndPointIndex],
                    sectionDirection,
                    extensionDistance / 2d);
                var trialPoints = adjustedPoints.Select(Copy).ToList();
                trialPoints[sectionStartPointIndex] = extendedStartPoint;
                trialPoints[sectionEndPointIndex] = extendedEndPoint;
                var trialHasMinimumSegments = HasMinimumSegments(
                    trialPoints,
                    routeRequest.MinimumSegmentLength);
                var trialIsAxisAligned = IsAxisAligned(trialPoints);
                var trialHasImmediateReversal = HasImmediateReversal(trialPoints);
                if (!trialHasMinimumSegments ||
                    !trialIsAxisAligned ||
                    trialHasImmediateReversal)
                {
                    return false;
                }

                adjustedPoints = trialPoints;
            }

            return true;
        }

        private static bool HasMinimumStackedUShapeSegments(
            IList<Point3d> points,
            double minimumSegmentLength,
            double stackedUShapeMinimumBaseSegmentLength)
        {
            return HasMinimumStackedUShapeSegments(
                points,
                minimumSegmentLength,
                stackedUShapeMinimumBaseSegmentLength,
                0d,
                0d);
        }

        private static bool HasMinimumStackedUShapeSegments(
            IList<Point3d> points,
            double minimumSegmentLength,
            double stackedUShapeMinimumBaseSegmentLength,
            double uShapeBendTangentSegmentLength,
            double uShapeMinimumMiddleDistance)
        {
            if (points == null ||
                !HasMinimumSegments(points, minimumSegmentLength))
            {
                return false;
            }

            if (points.Count < 4 ||
                (stackedUShapeMinimumBaseSegmentLength <=
                     minimumSegmentLength + GeometryTolerance &&
                 uShapeBendTangentSegmentLength <=
                     minimumSegmentLength + GeometryTolerance &&
                 uShapeMinimumMiddleDistance <= GeometryTolerance))
            {
                return true;
            }

            for (var segmentIndex = 1;
                segmentIndex < points.Count - 2;
                segmentIndex++)
            {
                var previousSegmentDirection = new Vector3d(
                    points[segmentIndex - 1],
                    points[segmentIndex]);
                var currentSegmentDirection = new Vector3d(
                    points[segmentIndex],
                    points[segmentIndex + 1]);
                var followingSegmentDirection = new Vector3d(
                    points[segmentIndex + 1],
                    points[segmentIndex + 2]);
                if (previousSegmentDirection.Norm <= GeometryTolerance ||
                    currentSegmentDirection.Norm <= GeometryTolerance ||
                    followingSegmentDirection.Norm <= GeometryTolerance)
                {
                    return false;
                }

                var firstTurnNormal = previousSegmentDirection.Normalized.Cross(
                    currentSegmentDirection.Normalized);
                var secondTurnNormal = currentSegmentDirection.Normalized.Cross(
                    followingSegmentDirection.Normalized);
                if (firstTurnNormal.Norm <= GeometryTolerance ||
                    secondTurnNormal.Norm <= GeometryTolerance ||
                    firstTurnNormal.Normalized.Dot(
                        secondTurnNormal.Normalized) < 1d - 1e-6d)
                {
                    continue;
                }

                if (uShapeBendTangentSegmentLength > GeometryTolerance &&
                    (previousSegmentDirection.Norm + GeometryTolerance <
                         uShapeBendTangentSegmentLength ||
                     currentSegmentDirection.Norm + GeometryTolerance <
                         uShapeBendTangentSegmentLength ||
                     followingSegmentDirection.Norm + GeometryTolerance <
                         uShapeBendTangentSegmentLength))
                {
                    return false;
                }

                // 同一平面内连续两次同向 90° 转折就是 U 型回程段。
                // 最内侧预览面在两个转角各损失半个叠排总包络厚度，
                // 因而基准段必须使用叠排专用最短长度。
                if (currentSegmentDirection.Norm + GeometryTolerance <
                    stackedUShapeMinimumBaseSegmentLength)
                {
                    return false;
                }

                // 中间跨距严格大于 4×(R+t)，圆角扣除两端各一个
                // 折弯切线后，保留下来的直线段才会严格大于两个折弯单位。
                if (uShapeMinimumMiddleDistance > GeometryTolerance &&
                    currentSegmentDirection.Norm <=
                        uShapeMinimumMiddleDistance + GeometryTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static double GetStackedUShapeMinimumBaseSegmentLength(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (routeRequest == null || routeGroupRequest == null)
            {
                return 0d;
            }

            return LinkageRouteStackGeometry
                .GetUShapeReturnMinimumBaseSegmentLength(
                    routeRequest.MinimumSegmentLength,
                    routeGroupRequest.GetBusbarThickness(routeRequest),
                    routeGroupRequest.GetStackCount(routeRequest),
                    routeGroupRequest.StackMiddleSpace);
        }

        private static double GetUShapeBendTangentSegmentLength(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (routeRequest == null || routeGroupRequest == null)
            {
                return 0d;
            }

            var busbarThickness = routeGroupRequest.GetBusbarThickness(
                routeRequest);
            if (busbarThickness <= GeometryTolerance ||
                routeRequest.BendingRadius <= GeometryTolerance)
            {
                return 0d;
            }

            // 90° U 型转角的切线长度为 (R+t)/tan(45°) = R+t。
            return routeRequest.BendingRadius + busbarThickness;
        }

        private static double GetUShapeMinimumMiddleDistance(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (routeRequest == null || routeGroupRequest == null)
            {
                return 0d;
            }

            var busbarThickness = routeGroupRequest.GetBusbarThickness(
                routeRequest);
            if (busbarThickness <= GeometryTolerance ||
                routeRequest.BendingRadius <= GeometryTolerance)
            {
                return 0d;
            }

            return LinkageRouteStackGeometry.GetUShapeMinimumMiddleDistance(
                busbarThickness,
                routeRequest.BendingRadius);
        }

        private static void AddPoint(ICollection<Point3d> points, Point3d sourcePoint)
        {
            if (sourcePoint == null)
            {
                return;
            }

            var pointList = points as IList<Point3d>;
            if (pointList != null && pointList.Count > 0 &&
                pointList[pointList.Count - 1].DistanceTo(sourcePoint) <= GeometryTolerance)
            {
                return;
            }

            points.Add(Copy(sourcePoint));
        }

        private static string GetSignature(IEnumerable<Point3d> points)
        {
            return string.Join("|", points.Select(point => string.Format(
                CultureInfo.InvariantCulture,
                "{0:F4},{1:F4},{2:F4}",
                point.X,
                point.Y,
                point.Z)));
        }

        private static double GetLength(IList<Point3d> points)
        {
            return points.Zip(points.Skip(1), (firstPoint, secondPoint) =>
                firstPoint.DistanceTo(secondPoint)).Sum();
        }

        /// <summary>
        /// XYZ 骨架尚未拆成实际铜排时的排序长度。每个真实跨平面转角在
        /// Planar 转换后会形成一个垂直搭接接口，并在两根排上各增加 PK/2，
        /// 因此这里提前计入一个完整生成排宽，避免裸骨架长度低估该方案。
        /// </summary>
        private static double GetEstimatedPostConversionCenterlineLength(
            IList<Point3d> centerlinePoints,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            var rawCenterlineLength = GetLength(centerlinePoints);
            if (centerlinePoints == null || centerlinePoints.Count < 3 ||
                routeRequest == null || routeGroupRequest?.StartTerminal == null)
            {
                return rawCenterlineLength;
            }

            var planarTransitionCount =
                LinkageRouteCenterlineConverter.CountActualPlanarTransitions(
                    centerlinePoints,
                    routeGroupRequest.StartTerminal.ThicknessDirection);
            return rawCenterlineLength +
                planarTransitionCount * routeGroupRequest.GetBusbarWidth(routeRequest);
        }

        private static Point3d WithCoordinate(
            Point3d point,
            int coordinateIndex,
            double coordinateValue)
        {
            if (coordinateIndex == 0) return new Point3d(coordinateValue, point.Y, point.Z);
            if (coordinateIndex == 1) return new Point3d(point.X, coordinateValue, point.Z);
            return new Point3d(point.X, point.Y, coordinateValue);
        }

        private static double GetCoordinate(Point3d point, int coordinateIndex)
        {
            if (coordinateIndex == 0) return point.X;
            return coordinateIndex == 1 ? point.Y : point.Z;
        }

        private static Point3d Translate(Point3d point, Vector3d direction, double distance)
        {
            return new Point3d(
                point.X + direction.X * distance,
                point.Y + direction.Y * distance,
                point.Z + direction.Z * distance);
        }

        private static Point3d Copy(Point3d point)
        {
            return new Point3d(point.X, point.Y, point.Z);
        }

        private sealed class ExtendedSearchWorkItem
        {
            public Point3d StartLapPoint { get; set; }
            public Point3d EndLapPoint { get; set; }
            public Vector3d StartRouteDirection { get; set; }
            public Vector3d EndRouteDirection { get; set; }
            public double StartRequiredTerminalSegmentLength { get; set; }
            public double EndRequiredTerminalSegmentLength { get; set; }
            public int[] AxisSequence { get; set; }
            public IReadOnlyDictionary<int, IReadOnlyList<double>> CoordinateOptions
            {
                get;
                set;
            }
            public double EstimatedRouteLength { get; set; }
        }
    }
}
