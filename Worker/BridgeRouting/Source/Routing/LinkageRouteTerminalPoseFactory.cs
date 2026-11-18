using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static RouteLinkBar.Algorithm.Geometry.GeometRiMethod;

namespace RouteLinkBar.Algorithm.Routing
{
    internal sealed class LinkageRouteTerminalPose
    {
        public Point3d LapEndpoint { get; set; }
        public Vector3d RouteDirection { get; set; }
        public double RequiredTerminalSegmentLength { get; set; }
        public double PerpendicularLapCoverageSpan { get; set; }
        public bool IsPerpendicularLap { get; set; }
    }

    internal sealed class LinkageRouteTerminalOverlapPosePair
    {
        public LinkageRouteTerminalPose StartTerminalPose { get; set; }
        public LinkageRouteTerminalPose EndTerminalPose { get; set; }
        public Point3d SharedLapCenterPoint { get; set; }
        public Vector3d SharedLengthDirection { get; set; }
        public Point3d StartOverlapFacingBoundaryPoint { get; set; }
        public Point3d EndOverlapFacingBoundaryPoint { get; set; }
    }

    internal static class LinkageRouteTerminalPoseFactory
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.ExactPointCoincidence;

        private sealed class TerminalMemberPoseGeometry
        {
            public PlanarBendBusbarSetting FixedSetting { get; set; }
            public Point3d SelectedBoundaryPoint { get; set; }
            public Point3d AdjacentInteriorPoint { get; set; }
            public Vector3d InwardLengthDirection { get; set; }
            public Vector3d FixedWidthDirection { get; set; }
            public bool HasGeneratedWidthLengthReserve { get; set; }
        }

        private sealed class TerminalMemberWidthInterval
        {
            public double Minimum { get; set; }
            public double Maximum { get; set; }
        }

        public static IReadOnlyList<LinkageRouteTerminalPose> Build(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double parallelTerminalSegmentLength,
            double perpendicularTerminalSegmentLength,
                bool requireFixedTerminalEndLap = false)
        {
            return BuildCore(
                terminalAssembly,
                oppositeTerminalAnchor,
                isStartTerminal,
                generatedBusbarWidth,
                parallelTerminalSegmentLength,
                perpendicularTerminalSegmentLength,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap);
        }

        public static IReadOnlyList<LinkageRouteTerminalPose> BuildWithDirectLapReferences(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double parallelTerminalSegmentLength,
            double perpendicularTerminalSegmentLength,
            IEnumerable<Point3d> directLapReferencePoints,
                bool requireFixedTerminalEndLap = false)
        {
            // 兼容旧调试入口：当前规范要求直接参考点不得恢复混合宽度位置网格，
            // 精确垂直参考统一由 terminalAssembly 的首选参考点表达。
            // 保留参数仅避免破坏既有反射调用，并显式确认其不参与姿态枚举。
            _ = directLapReferencePoints;
            return BuildCore(
                terminalAssembly,
                oppositeTerminalAnchor,
                isStartTerminal,
                generatedBusbarWidth,
                parallelTerminalSegmentLength,
                perpendicularTerminalSegmentLength,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap);
        }

        public static IReadOnlyList<LinkageRouteTerminalPose> BuildWithClearanceWidthCoordinates(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double parallelTerminalSegmentLength,
            double perpendicularTerminalSegmentLength,
            IEnumerable<double> clearanceWidthCoordinates,
                bool requireFixedTerminalEndLap = false)
        {
            return BuildCore(
                terminalAssembly,
                oppositeTerminalAnchor,
                isStartTerminal,
                generatedBusbarWidth,
                parallelTerminalSegmentLength,
                perpendicularTerminalSegmentLength,
                clearanceWidthCoordinates,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap);
        }

        internal static IReadOnlyList<LinkageRouteTerminalPose> BuildForRouteTerminal(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double parallelTerminalSegmentLength,
            double perpendicularTerminalSegmentLength,
            IEnumerable<double> clearanceWidthCoordinates,
            bool allowCollectiveParallelLap,
            bool requireMiddleParallelLap,
            bool allowPartialPerpendicularLap,
            double perpendicularTerminalBendAllowance,
            Point3d terminalBoundaryReferencePoint = null,
                bool requireFixedTerminalEndLap = false)
        {
            return BuildCore(
                terminalAssembly,
                oppositeTerminalAnchor,
                isStartTerminal,
                generatedBusbarWidth,
                parallelTerminalSegmentLength,
                perpendicularTerminalSegmentLength,
                clearanceWidthCoordinates,
                allowCollectiveParallelLap,
                requireMiddleParallelLap,
                allowPartialPerpendicularLap,
                perpendicularTerminalBendAllowance,
                terminalBoundaryReferencePoint,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap);
        }

        /// <summary>
        /// “允许垂直短搭接”只增加短搭接姿态，不能替换原来的满搭接姿态。
        /// 满搭接始终先加入，开启选项后再补充一组短搭接并按几何去重。
        /// </summary>
        internal static IReadOnlyList<LinkageRouteTerminalPose>
            BuildForRouteTerminalWithOptionalPartialPerpendicularLap(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double parallelTerminalSegmentLength,
            double fullPerpendicularTerminalSegmentLength,
            double partialPerpendicularTerminalSegmentLength,
            IEnumerable<double> clearanceWidthCoordinates,
            bool allowCollectiveParallelLap,
            bool requireMiddleParallelLap,
            bool allowPartialPerpendicularLap,
            double perpendicularTerminalBendAllowance,
            Point3d terminalBoundaryReferencePoint = null,
                bool requireFixedTerminalEndLap = false, double minimumParallelLapLength = 0d)
        {
            var terminalPoseVariants = BuildCore(
                    terminalAssembly,
                    oppositeTerminalAnchor,
                    isStartTerminal,
                    generatedBusbarWidth,
                    parallelTerminalSegmentLength,
                    fullPerpendicularTerminalSegmentLength,
                    clearanceWidthCoordinates,
                    allowCollectiveParallelLap,
                    requireMiddleParallelLap,
                    false,
                    perpendicularTerminalBendAllowance,
                    terminalBoundaryReferencePoint,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap, minimumParallelLapLength: minimumParallelLapLength)
                .ToList();
            if (allowPartialPerpendicularLap)
            {
                terminalPoseVariants.AddRange(BuildCore(
                    terminalAssembly,
                    oppositeTerminalAnchor,
                    isStartTerminal,
                    generatedBusbarWidth,
                    parallelTerminalSegmentLength,
                    partialPerpendicularTerminalSegmentLength,
                    clearanceWidthCoordinates,
                    allowCollectiveParallelLap,
                    requireMiddleParallelLap,
                    true,
                    perpendicularTerminalBendAllowance,
                    terminalBoundaryReferencePoint,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap, minimumParallelLapLength: minimumParallelLapLength));
            }

            return terminalPoseVariants
                .GroupBy(GetSignature, StringComparer.Ordinal)
                .Select(terminalPoseGroup => terminalPoseGroup.First())
                .ToList();
        }

        internal static IReadOnlyList<LinkageRouteTerminalPose> BuildWithShortStartLapPolicy(
            LinkageTerminalAssembly terminalAssembly, Point3d oppositeTerminalAnchor,
            bool isStartTerminal, double generatedBusbarWidth, double parallelTerminalSegmentLength,
            double fullPerpendicularTerminalSegmentLength, double partialPerpendicularTerminalSegmentLength,
            IEnumerable<double> clearanceWidthCoordinates, bool allowCollectiveParallelLap,
            bool requireMiddleParallelLap, bool allowPartialPerpendicularLap,
            double perpendicularTerminalBendAllowance, Point3d terminalBoundaryReferencePoint,
            double shortPhysicalStartLapLength,
                bool requireFixedTerminalEndLap = false, double minimumParallelLapLength = 0d)
        {
            if (double.IsNaN(shortPhysicalStartLapLength)) return Array.Empty<LinkageRouteTerminalPose>();
            if (shortPhysicalStartLapLength <= GeometryTolerance)
                return BuildForRouteTerminalWithOptionalPartialPerpendicularLap(terminalAssembly,
                    oppositeTerminalAnchor, isStartTerminal, generatedBusbarWidth, parallelTerminalSegmentLength,
                    fullPerpendicularTerminalSegmentLength, partialPerpendicularTerminalSegmentLength,
                    clearanceWidthCoordinates, allowCollectiveParallelLap, requireMiddleParallelLap,
                    allowPartialPerpendicularLap, perpendicularTerminalBendAllowance, terminalBoundaryReferencePoint,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap, minimumParallelLapLength: minimumParallelLapLength);
            return BuildCore(terminalAssembly, oppositeTerminalAnchor, isStartTerminal, generatedBusbarWidth,
                parallelTerminalSegmentLength, fullPerpendicularTerminalSegmentLength, clearanceWidthCoordinates,
                allowCollectiveParallelLap, requireMiddleParallelLap, false, perpendicularTerminalBendAllowance,
                terminalBoundaryReferencePoint, shortPhysicalStartLapLength,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap, minimumParallelLapLength: minimumParallelLapLength);
        }

        private static IReadOnlyList<LinkageRouteTerminalPose> BuildCore(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double parallelTerminalSegmentLength,
            double perpendicularTerminalSegmentLength,
            IEnumerable<double> clearanceWidthCoordinates = null,
            bool allowCollectiveParallelLap = false,
            bool requireMiddleParallelLap = false,
            bool allowPartialPerpendicularLap = false,
            double perpendicularTerminalBendAllowance = 0d,
            Point3d terminalBoundaryReferencePoint = null,
            double shortPhysicalStartLapLength = 0d,
                bool requireFixedTerminalEndLap = false, double minimumParallelLapLength = 0d)
        {
            requireFixedTerminalEndLap = requireFixedTerminalEndLap &&
                terminalAssembly?.IsMaterializedRoutingBridge != true;
            var terminalPoses = new List<LinkageRouteTerminalPose>();
            var terminalMemberPoseGeometries = BuildTerminalMemberPoseGeometries(
                terminalAssembly,
                requireFixedTerminalEndLap ? oppositeTerminalAnchor : terminalBoundaryReferencePoint ?? oppositeTerminalAnchor,
                generatedBusbarWidth,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap);
            if (terminalMemberPoseGeometries.Count == 0)
            {
                return new List<LinkageRouteTerminalPose>();
            }

            foreach (var terminalMemberPoseGeometry in terminalMemberPoseGeometries)
            {
                AddParallelTerminalPoses(
                    terminalPoses,
                    terminalMemberPoseGeometry,
                    oppositeTerminalAnchor,
                    isStartTerminal,
                    generatedBusbarWidth,
                    parallelTerminalSegmentLength,
                    clearanceWidthCoordinates,
                    false,
                    requireMiddleParallelLap,
                    shortPhysicalStartLapLength, minimumParallelLapLength);
            }

            if (allowCollectiveParallelLap &&
                LinkageRouteDoubleStartLapGeometry.TryCreateCollectiveFixedBusbar(
                    terminalAssembly,
                    out var collectiveFixedBusbar) &&
                TryResolveCollectiveForbiddenExitDirection(
                    terminalAssembly,
                    out var collectiveForbiddenExitDirection))
            {
                var collectiveTerminalAssembly = new LinkageTerminalAssembly
                {
                    RouteAnchor = terminalAssembly.RouteAnchor,
                    LengthDirection = terminalAssembly.LengthDirection,
                    ThicknessDirection = terminalAssembly.ThicknessDirection,
                    Members = new List<LinkageTerminalMember>
                    {
                        new LinkageTerminalMember
                        {
                            MemberKey = "DOUBLE_START_COLLECTIVE_LAP",
                            FixedBusbar = collectiveFixedBusbar,
                            ForbiddenHorizontalExitDirection =
                                collectiveForbiddenExitDirection
                        }
                    }
                };
                var collectiveTerminalPoseGeometries =
                    BuildTerminalMemberPoseGeometries(
                        collectiveTerminalAssembly,
                        requireFixedTerminalEndLap ? oppositeTerminalAnchor : terminalBoundaryReferencePoint ?? oppositeTerminalAnchor,
                        generatedBusbarWidth,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap);
                foreach (var collectiveTerminalPoseGeometry in
                    collectiveTerminalPoseGeometries)
                {
                    if (requireFixedTerminalEndLap)
                    {
                        // 集合端只合并排宽跨度；排长允许区间仍取逐根固定端窗口的交集。
                        var collectiveOrigin = collectiveTerminalPoseGeometry.SelectedBoundaryPoint;
                        var collectiveAxis = collectiveTerminalPoseGeometry.InwardLengthDirection;
                        var commonMinimum = terminalMemberPoseGeometries.Max(memberGeometry =>
                            new Vector3d(collectiveOrigin, memberGeometry.SelectedBoundaryPoint).Dot(collectiveAxis));
                        var commonMaximum = terminalMemberPoseGeometries.Min(memberGeometry =>
                            new Vector3d(collectiveOrigin, memberGeometry.AdjacentInteriorPoint).Dot(collectiveAxis));
                        var requiredOverlap = minimumParallelLapLength > GeometryTolerance ? minimumParallelLapLength
                            : shortPhysicalStartLapLength > GeometryTolerance
                                ? Math.Min(generatedBusbarWidth, shortPhysicalStartLapLength) : generatedBusbarWidth;
                        if (commonMaximum - commonMinimum + GeometryTolerance < requiredOverlap) continue;
                        collectiveTerminalPoseGeometry.SelectedBoundaryPoint = TranslateAlongDirection(
                            collectiveOrigin, collectiveAxis, commonMinimum);
                        collectiveTerminalPoseGeometry.AdjacentInteriorPoint = TranslateAlongDirection(
                            collectiveOrigin, collectiveAxis, commonMaximum);
                    }
                    AddParallelTerminalPoses(
                        terminalPoses,
                        collectiveTerminalPoseGeometry,
                        oppositeTerminalAnchor,
                        isStartTerminal,
                        generatedBusbarWidth,
                        parallelTerminalSegmentLength,
                        clearanceWidthCoordinates,
                        true,
                        false,
                        shortPhysicalStartLapLength, minimumParallelLapLength);
                }
            }

            if (shortPhysicalStartLapLength > GeometryTolerance)
                return terminalPoses.GroupBy(GetSignature, StringComparer.Ordinal).Select(poses => poses.First()).ToList();

            var preferredPerpendicularLapReferencePoints = terminalAssembly
                .PreferredPerpendicularLapReferencePoints;
            if (preferredPerpendicularLapReferencePoints == null ||
                preferredPerpendicularLapReferencePoints.Count == 0)
            {
                if (TryBuildCollectivePerpendicularTerminalPose(
                        terminalMemberPoseGeometries,
                        oppositeTerminalAnchor,
                        isStartTerminal,
                        generatedBusbarWidth,
                        perpendicularTerminalSegmentLength,
                        null,
                        allowPartialPerpendicularLap,
                        perpendicularTerminalBendAllowance,
                        out var collectivePerpendicularTerminalPose))
                {
                    terminalPoses.Add(collectivePerpendicularTerminalPose);
                }
            }
            else
            {
                foreach (var preferredPerpendicularLapReferencePoint in
                    preferredPerpendicularLapReferencePoints
                        .Where(referencePoint => referencePoint != null)
                        .GroupBy(
                            referencePoint => string.Format(
                                CultureInfo.InvariantCulture,
                                "{0:F6},{1:F6},{2:F6}",
                                referencePoint.X,
                                referencePoint.Y,
                                referencePoint.Z),
                            StringComparer.Ordinal)
                        .Select(referencePointGroup =>
                            referencePointGroup.First()))
                {
                    if (TryBuildCollectivePerpendicularTerminalPose(
                            terminalMemberPoseGeometries,
                            oppositeTerminalAnchor,
                            isStartTerminal,
                            generatedBusbarWidth,
                            perpendicularTerminalSegmentLength,
                            preferredPerpendicularLapReferencePoint,
                            allowPartialPerpendicularLap,
                            perpendicularTerminalBendAllowance,
                            out var preferredPerpendicularTerminalPose))
                    {
                        terminalPoses.Add(preferredPerpendicularTerminalPose);
                    }
                }
            }

            if (terminalAssembly.CabinetBridgeAssembly != null)
            {
                var bridgeSetting = terminalAssembly.Members[0].FixedBusbar;
                var widthAxis = bridgeSetting.WidthVector.Normalized;
                var edgeOffset = Math.Max(0d, (bridgeSetting.BusBarWidth-generatedBusbarWidth)/2d);
                terminalPoses = terminalPoses.OrderBy(pose => pose.IsPerpendicularLap ? 1 : 0)
                    .ThenBy(pose => Math.Abs(Math.Abs(new Vector3d(bridgeSetting.PointList[0], pose.LapEndpoint)
                        .Dot(widthAxis))-edgeOffset))
                    .ThenBy(pose => pose.LapEndpoint.DistanceTo(oppositeTerminalAnchor)).ToList();
            }
            return terminalPoses
                .GroupBy(terminalPose => GetSignature(terminalPose), StringComparer.Ordinal)
                .Select(terminalPoseGroup => terminalPoseGroup.First())
                .ToList();
        }

        internal static bool TryBuildWidthSizedLengthOverlapPosePair(
            LinkageTerminalAssembly startTerminalAssembly,
            LinkageTerminalAssembly endTerminalAssembly,
            double startGeneratedBusbarWidth,
            double endGeneratedBusbarWidth,
            double startPerpendicularTerminalSegmentLength,
            double endPerpendicularTerminalSegmentLength,
            bool allowPartialStartPerpendicularLap,
            double startPerpendicularTerminalBendAllowance,
            double endPerpendicularTerminalBendAllowance,
            out LinkageRouteTerminalOverlapPosePair terminalOverlapPosePair,
                bool requireFixedTerminalEndLap = false)
        {
            terminalOverlapPosePair = null;
            if (startTerminalAssembly == null ||
                endTerminalAssembly == null ||
                startGeneratedBusbarWidth <= GeometryTolerance ||
                endGeneratedBusbarWidth <= GeometryTolerance ||
                !TryGetCollectiveStraightLengthInterval(
                    startTerminalAssembly,
                    null,
                    out var sharedLengthDirection,
                    out var startLengthMinimum,
                    out var startLengthMaximum) ||
                !TryGetCollectiveStraightLengthInterval(
                    endTerminalAssembly,
                    sharedLengthDirection,
                    out _,
                    out var endLengthMinimum,
                    out var endLengthMaximum))
            {
                return false;
            }

            var sharedLengthMinimum = Math.Max(
                startLengthMinimum,
                endLengthMinimum);
            var sharedLengthMaximum = Math.Min(
                startLengthMaximum,
                endLengthMaximum);
            var sharedLengthSpan = sharedLengthMaximum - sharedLengthMinimum;
            var requiredSharedLengthSpan = Math.Max(
                startGeneratedBusbarWidth,
                endGeneratedBusbarWidth);
            var widthComparisonTolerance = Math.Max(
                1e-6d,
                requiredSharedLengthSpan * 1e-6d);
            if (sharedLengthSpan <= GeometryTolerance ||
                Math.Abs(sharedLengthSpan - requiredSharedLengthSpan) >
                    widthComparisonTolerance)
            {
                return false;
            }

            var startRepresentativeFixedSetting = startTerminalAssembly.Members
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .FirstOrDefault(IsUsableFixedSetting);
            if (startRepresentativeFixedSetting == null)
            {
                return false;
            }

            var sharedLengthCenterCoordinate =
                (sharedLengthMinimum + sharedLengthMaximum) / 2d;
            var sharedLapCenterReferencePoint =
                startRepresentativeFixedSetting.PointList[0];
            var sharedLapCenterPoint = TranslateAlongDirection(
                sharedLapCenterReferencePoint,
                sharedLengthDirection,
                sharedLengthCenterCoordinate - GetLengthCoordinate(
                    sharedLapCenterReferencePoint,
                    sharedLengthDirection));
            var startTerminalMemberPoseGeometries =
                BuildTerminalMemberPoseGeometries(
                    startTerminalAssembly,
                    requireFixedTerminalEndLap ? endTerminalAssembly.RouteAnchor : sharedLapCenterPoint,
                    startGeneratedBusbarWidth,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap);
            var endTerminalMemberPoseGeometries =
                BuildTerminalMemberPoseGeometries(
                    endTerminalAssembly,
                    requireFixedTerminalEndLap ? startTerminalAssembly.RouteAnchor : sharedLapCenterPoint,
                    endGeneratedBusbarWidth,
                requireFixedTerminalEndLap: requireFixedTerminalEndLap);
            if (!TryBuildCollectivePerpendicularTerminalPose(
                    startTerminalMemberPoseGeometries,
                    endTerminalAssembly.RouteAnchor,
                    true,
                    startGeneratedBusbarWidth,
                    startPerpendicularTerminalSegmentLength,
                    sharedLapCenterPoint,
                    allowPartialStartPerpendicularLap,
                    startPerpendicularTerminalBendAllowance,
                    out var startTerminalPose) ||
                !TryBuildCollectivePerpendicularTerminalPose(
                    endTerminalMemberPoseGeometries,
                    startTerminalAssembly.RouteAnchor,
                    false,
                    endGeneratedBusbarWidth,
                    endPerpendicularTerminalSegmentLength,
                    sharedLapCenterPoint,
                    false,
                    endPerpendicularTerminalBendAllowance,
                    out var endTerminalPose) ||
                Math.Abs(GetLengthCoordinate(
                        startTerminalPose.LapEndpoint,
                        sharedLengthDirection) - sharedLengthCenterCoordinate) >
                    widthComparisonTolerance ||
                Math.Abs(GetLengthCoordinate(
                        endTerminalPose.LapEndpoint,
                        sharedLengthDirection) - sharedLengthCenterCoordinate) >
                    widthComparisonTolerance)
            {
                return false;
            }

            terminalOverlapPosePair = new LinkageRouteTerminalOverlapPosePair
            {
                StartTerminalPose = startTerminalPose,
                EndTerminalPose = endTerminalPose,
                SharedLapCenterPoint = sharedLapCenterPoint,
                SharedLengthDirection = sharedLengthDirection,
                StartOverlapFacingBoundaryPoint =
                    startTerminalMemberPoseGeometries[0].SelectedBoundaryPoint,
                EndOverlapFacingBoundaryPoint =
                    endTerminalMemberPoseGeometries[0].SelectedBoundaryPoint
            };
            return true;
        }

        internal static double GetCollectivePerpendicularLapSpan(
            LinkageTerminalAssembly terminalAssembly,
            double fallbackBusbarWidth)
        {
            var usableFixedSettings = (terminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(IsUsableFixedSetting)
                .ToList();
            if (usableFixedSettings.Count == 0)
            {
                return fallbackBusbarWidth;
            }

            var collectiveWidthReferencePoint = usableFixedSettings[0].PointList[0];
            var collectiveWidthReferenceDirection = usableFixedSettings[0]
                .WidthVector.Normalized;
            var collectiveWidthMinimum = double.PositiveInfinity;
            var collectiveWidthMaximum = double.NegativeInfinity;
            foreach (var usableFixedSetting in usableFixedSettings)
            {
                var currentFixedWidthDirection = usableFixedSetting.WidthVector.Normalized;
                if (currentFixedWidthDirection.Cross(
                        collectiveWidthReferenceDirection).Norm > GeometryTolerance)
                {
                    return usableFixedSettings
                        .Select(fixedSetting => fixedSetting.BusBarWidth)
                        .Where(fixedWidth => fixedWidth > GeometryTolerance)
                        .DefaultIfEmpty(fallbackBusbarWidth)
                        .Max();
                }

                var currentFixedCenterCoordinate = new Vector3d(
                        collectiveWidthReferencePoint,
                        usableFixedSetting.PointList[0])
                    .Dot(collectiveWidthReferenceDirection);
                var currentFixedHalfWidth = usableFixedSetting.BusBarWidth / 2d;
                collectiveWidthMinimum = Math.Min(
                    collectiveWidthMinimum,
                    currentFixedCenterCoordinate - currentFixedHalfWidth);
                collectiveWidthMaximum = Math.Max(
                    collectiveWidthMaximum,
                    currentFixedCenterCoordinate + currentFixedHalfWidth);
            }

            var collectivePerpendicularLapSpan =
                collectiveWidthMaximum - collectiveWidthMinimum;
            return collectivePerpendicularLapSpan > GeometryTolerance
                ? collectivePerpendicularLapSpan
                : fallbackBusbarWidth;
        }

        /// <summary>实际选中搭接直段的共同排长范围；不使用展开长度或包围盒。
        /// 多成员必须逐根有效且方向一致，没有共同正长度时不派生缩宽。</summary>
        internal static bool TryGetCommonContactSegmentLength(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            out double commonContactLength)
        {
            return TryGetCommonContactSegmentLength(terminalAssembly, oppositeTerminalAnchor,
                out commonContactLength, out _);
        }

        internal static bool TryGetCommonContactSegmentLength(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            out double commonContactLength, out Vector3d commonContactDirection)
        {
            commonContactLength = 0d;
            commonContactDirection = null;
            if (terminalAssembly?.Members == null || terminalAssembly.Members.Count == 0 ||
                terminalAssembly.Members.Any(member => !IsUsableFixedSetting(member?.FixedBusbar)))
                return false;
            var contactGeometries = new List<TerminalMemberPoseGeometry>();
            foreach (var contactMember in terminalAssembly.Members)
            {
                var memberGeometries = BuildTerminalMemberPoseGeometries(new LinkageTerminalAssembly
                {
                    Members = new List<LinkageTerminalMember> { contactMember },
                    ThicknessDirection = terminalAssembly.ThicknessDirection
                }, oppositeTerminalAnchor, 0d);
                // 不能沿用姿态枚举“跳过无效成员”的语义来证明共同覆盖。
                if (memberGeometries.Count != 1) return false;
                contactGeometries.Add(memberGeometries[0]);
            }
            var contactAxis = contactGeometries[0].InwardLengthDirection.Normalized;
            commonContactDirection = contactAxis;
            var contactMinimum = double.NegativeInfinity;
            var contactMaximum = double.PositiveInfinity;
            foreach (var contactGeometry in contactGeometries)
            {
                if (contactAxis.Cross(contactGeometry.InwardLengthDirection).Norm > GeometryTolerance)
                    return false;
                var firstCoordinate = GetLengthCoordinate(contactGeometry.SelectedBoundaryPoint, contactAxis);
                var lastCoordinate = GetLengthCoordinate(contactGeometry.AdjacentInteriorPoint, contactAxis);
                contactMinimum = Math.Max(contactMinimum, Math.Min(firstCoordinate, lastCoordinate));
                contactMaximum = Math.Min(contactMaximum, Math.Max(firstCoordinate, lastCoordinate));
            }
            commonContactLength = contactMaximum - contactMinimum;
            return !double.IsNaN(commonContactLength) && !double.IsInfinity(commonContactLength) &&
                commonContactLength > GeometryTolerance;
        }

        internal static bool TryResolveSingleMemberLapBoundary(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            double generatedBusbarWidth,
            out Point3d selectedBoundaryPoint,
            out Vector3d inwardLengthDirection,
            out Vector3d fixedWidthDirection)
        {
            selectedBoundaryPoint = null;
            inwardLengthDirection = null;
            fixedWidthDirection = null;
            if (terminalAssembly?.Members?.Count != 1)
            {
                return false;
            }

            var singleMemberPoseGeometries = BuildTerminalMemberPoseGeometries(
                terminalAssembly,
                oppositeTerminalAnchor,
                generatedBusbarWidth);
            if (singleMemberPoseGeometries.Count != 1)
            {
                return false;
            }

            var singleMemberPoseGeometry = singleMemberPoseGeometries[0];
            selectedBoundaryPoint = new Point3d(
                singleMemberPoseGeometry.SelectedBoundaryPoint.X,
                singleMemberPoseGeometry.SelectedBoundaryPoint.Y,
                singleMemberPoseGeometry.SelectedBoundaryPoint.Z);
            inwardLengthDirection = Copy(
                singleMemberPoseGeometry.InwardLengthDirection);
            fixedWidthDirection = Copy(
                singleMemberPoseGeometry.FixedWidthDirection);
            return selectedBoundaryPoint != null &&
                inwardLengthDirection != null &&
                inwardLengthDirection.Norm > GeometryTolerance &&
                fixedWidthDirection != null &&
                fixedWidthDirection.Norm > GeometryTolerance;
        }

        private static bool TryResolveCollectiveForbiddenExitDirection(
            LinkageTerminalAssembly terminalAssembly,
            out Vector3d collectiveForbiddenExitDirection)
        {
            collectiveForbiddenExitDirection = null;
            var configuredDirections = (terminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
                .Select(terminalMember =>
                    terminalMember?.ForbiddenHorizontalExitDirection)
                .Where(configuredDirection => configuredDirection != null &&
                    configuredDirection.Norm > GeometryTolerance)
                .Select(configuredDirection => configuredDirection.Normalized)
                .ToList();
            if (configuredDirections.Count == 0)
            {
                return true;
            }

            var referenceDirection = configuredDirections[0];
            if (configuredDirections.Skip(1).Any(configuredDirection =>
                    configuredDirection.Dot(referenceDirection) <
                        1d - LinkageRouteGeometryTolerances.AssemblyDirection))
            {
                return false;
            }

            var averagedDirection = new Vector3d(
                configuredDirections.Average(direction => direction.X),
                configuredDirections.Average(direction => direction.Y),
                configuredDirections.Average(direction => direction.Z));
            if (averagedDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            collectiveForbiddenExitDirection = averagedDirection.Normalized;
            return true;
        }

        private static IReadOnlyList<TerminalMemberPoseGeometry>
            BuildTerminalMemberPoseGeometries(
            LinkageTerminalAssembly terminalAssembly,
            Point3d oppositeTerminalAnchor,
            double generatedBusbarWidth,
                bool requireFixedTerminalEndLap = false)
        {
            requireFixedTerminalEndLap = requireFixedTerminalEndLap &&
                terminalAssembly?.IsMaterializedRoutingBridge != true;
            var representativeFixedSetting = (terminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .FirstOrDefault(IsUsableFixedSetting);
            if (representativeFixedSetting == null ||
                terminalAssembly.ThicknessDirection == null ||
                terminalAssembly.ThicknessDirection.Norm <= GeometryTolerance)
            {
                return Array.Empty<TerminalMemberPoseGeometry>();
            }

            var terminalReferenceCenterPlanePoint =
                representativeFixedSetting.PointList[0];
            var normalizedTerminalThicknessDirection = terminalAssembly
                .ThicknessDirection.Normalized;
            var terminalMemberPoseGeometries = new List<TerminalMemberPoseGeometry>();
            foreach (var terminalMember in terminalAssembly.Members)
            {
                var currentFixedSetting = terminalMember?.FixedBusbar;
                if (!IsUsableFixedSetting(currentFixedSetting))
                {
                    continue;
                }

                var currentForbiddenExitDirection =
                    terminalMember.ForbiddenHorizontalExitDirection;
                var selectedFixedPointIndex = currentForbiddenExitDirection == null ||
                    currentForbiddenExitDirection.Norm <= GeometryTolerance
                    ? FindNearestFixedPointIndex(
                        currentFixedSetting.PointList,
                        oppositeTerminalAnchor)
                    : FindAllowedExitFixedPointIndex(
                        currentFixedSetting.PointList,
                        currentForbiddenExitDirection);
                if (selectedFixedPointIndex < 0)
                {
                    continue;
                }

                var adjacentFixedPointIndex = FindLapSegmentAdjacentPointIndex(
                    currentFixedSetting.PointList,
                    selectedFixedPointIndex,
                    oppositeTerminalAnchor);
                if (adjacentFixedPointIndex < 0)
                {
                    continue;
                }

                var projectedSelectedBoundaryPoint = ProjectToPlane(
                    currentFixedSetting.PointList[selectedFixedPointIndex],
                    terminalReferenceCenterPlanePoint,
                    normalizedTerminalThicknessDirection);
                var projectedAdjacentInteriorPoint = ProjectToPlane(
                    currentFixedSetting.PointList[adjacentFixedPointIndex],
                    terminalReferenceCenterPlanePoint,
                    normalizedTerminalThicknessDirection);
                var projectedBoundarySegmentDirection = new Vector3d(
                    projectedSelectedBoundaryPoint,
                    projectedAdjacentInteriorPoint);
                if (projectedBoundarySegmentDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                if (requireFixedTerminalEndLap && generatedBusbarWidth > GeometryTolerance)
                    projectedAdjacentInteriorPoint = TranslateAlongDirection(
                        projectedSelectedBoundaryPoint, projectedBoundarySegmentDirection.Normalized,
                        Math.Min(projectedBoundarySegmentDirection.Norm, generatedBusbarWidth));

                terminalMemberPoseGeometries.Add(new TerminalMemberPoseGeometry
                {
                    FixedSetting = currentFixedSetting,
                    SelectedBoundaryPoint = projectedSelectedBoundaryPoint,
                    AdjacentInteriorPoint = projectedAdjacentInteriorPoint,
                    InwardLengthDirection = projectedBoundarySegmentDirection.Normalized,
                    FixedWidthDirection = currentFixedSetting.WidthVector.Normalized,
                    HasGeneratedWidthLengthReserve =
                        projectedBoundarySegmentDirection.Norm + GeometryTolerance >=
                        generatedBusbarWidth
                });
            }

            return terminalMemberPoseGeometries
                .GroupBy(GetTerminalMemberPoseGeometrySignature, StringComparer.Ordinal)
                .Select(terminalMemberPoseGeometryGroup =>
                    terminalMemberPoseGeometryGroup.First())
                .ToList();
        }

        private static void AddParallelTerminalPoses(
            ICollection<LinkageRouteTerminalPose> destinationTerminalPoses,
            TerminalMemberPoseGeometry terminalMemberPoseGeometry,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double parallelTerminalSegmentLength,
            IEnumerable<double> clearanceWidthCoordinates,
            bool includeCollectiveWidthCoverageOffsets,
            bool requireMiddleWidthOffset,
            double shortPhysicalStartLapLength = 0d, double minimumParallelLapLength = 0d)
        {
            var requiredParallelOverlap = minimumParallelLapLength > GeometryTolerance ? minimumParallelLapLength
                : shortPhysicalStartLapLength > GeometryTolerance
                    ? Math.Min(generatedBusbarWidth, shortPhysicalStartLapLength) : generatedBusbarWidth;
            if (terminalMemberPoseGeometry.SelectedBoundaryPoint.DistanceTo(
                    terminalMemberPoseGeometry.AdjacentInteriorPoint) + GeometryTolerance < requiredParallelOverlap)
            {
                return;
            }

            var physicalParallelExitDirection = Scale(
                terminalMemberPoseGeometry.InwardLengthDirection,
                -1d);
            if (!IsAxisAligned(physicalParallelExitDirection))
            {
                return;
            }

            var routeDirection = isStartTerminal
                ? Copy(physicalParallelExitDirection)
                : Scale(physicalParallelExitDirection, -1d);
            var memberContactCenter = TranslateAlongDirection(
                terminalMemberPoseGeometry.SelectedBoundaryPoint,
                terminalMemberPoseGeometry.InwardLengthDirection,
                requiredParallelOverlap / 2d);
            var memberParallelWidthOffsets = BuildParallelWidthOffsets(
                memberContactCenter,
                oppositeTerminalAnchor,
                terminalMemberPoseGeometry.FixedWidthDirection,
                terminalMemberPoseGeometry.FixedSetting.BusBarWidth,
                generatedBusbarWidth,
                clearanceWidthCoordinates,
                includeCollectiveWidthCoverageOffsets,
                requireMiddleWidthOffset);
            foreach (var memberParallelWidthOffset in memberParallelWidthOffsets)
            {
                var offsetMemberContactCenter = TranslateAlongDirection(
                    memberContactCenter,
                    terminalMemberPoseGeometry.FixedWidthDirection,
                    memberParallelWidthOffset);
                destinationTerminalPoses.Add(new LinkageRouteTerminalPose
                {
                    LapEndpoint = TranslateAlongDirection(
                        offsetMemberContactCenter,
                        routeDirection,
                        (isStartTerminal ? -1d : 1d) *
                            requiredParallelOverlap / 2d),
                    RouteDirection = routeDirection,
                    RequiredTerminalSegmentLength = Math.Max(parallelTerminalSegmentLength,
                        requiredParallelOverlap + Math.Max(0d, parallelTerminalSegmentLength - generatedBusbarWidth)),
                    IsPerpendicularLap = false
                });
            }
        }

        private static bool TryBuildCollectivePerpendicularTerminalPose(
            IReadOnlyList<TerminalMemberPoseGeometry> terminalMemberPoseGeometries,
            Point3d oppositeTerminalAnchor,
            bool isStartTerminal,
            double generatedBusbarWidth,
            double perpendicularTerminalSegmentLength,
            Point3d preferredContactReferencePoint,
            bool allowPartialPerpendicularLap,
            double perpendicularTerminalBendAllowance,
            out LinkageRouteTerminalPose collectivePerpendicularTerminalPose)
        {
            collectivePerpendicularTerminalPose = null;
            if (terminalMemberPoseGeometries == null ||
                terminalMemberPoseGeometries.Count == 0)
            {
                return false;
            }

            var referenceMemberPoseGeometry = terminalMemberPoseGeometries[0];
            var collectiveReferencePoint =
                referenceMemberPoseGeometry.SelectedBoundaryPoint;
            var collectiveInwardLengthDirection =
                referenceMemberPoseGeometry.InwardLengthDirection.Normalized;
            var collectiveFixedWidthDirection =
                referenceMemberPoseGeometry.FixedWidthDirection.Normalized;
            if (!IsAxisAligned(collectiveFixedWidthDirection))
            {
                return false;
            }

            var commonContactLengthMinimum = double.NegativeInfinity;
            var commonContactLengthMaximum = double.PositiveInfinity;
            var collectiveFixedWidthMinimum = double.PositiveInfinity;
            var collectiveFixedWidthMaximum = double.NegativeInfinity;
            var terminalMemberWidthIntervals =
                new List<TerminalMemberWidthInterval>();
            foreach (var terminalMemberPoseGeometry in terminalMemberPoseGeometries)
            {
                if (!terminalMemberPoseGeometry.HasGeneratedWidthLengthReserve ||
                    terminalMemberPoseGeometry.InwardLengthDirection.Dot(
                        collectiveInwardLengthDirection) < 1d - 1e-6d ||
                    terminalMemberPoseGeometry.FixedWidthDirection.Cross(
                        collectiveFixedWidthDirection).Norm > 1e-6d)
                {
                    return false;
                }

                var selectedLengthCoordinate = new Vector3d(
                        collectiveReferencePoint,
                        terminalMemberPoseGeometry.SelectedBoundaryPoint)
                    .Dot(collectiveInwardLengthDirection);
                var adjacentLengthCoordinate = new Vector3d(
                        collectiveReferencePoint,
                        terminalMemberPoseGeometry.AdjacentInteriorPoint)
                    .Dot(collectiveInwardLengthDirection);
                var memberContactLengthMinimum = Math.Min(
                        selectedLengthCoordinate,
                        adjacentLengthCoordinate) +
                    generatedBusbarWidth / 2d;
                var memberContactLengthMaximum = Math.Max(
                        selectedLengthCoordinate,
                        adjacentLengthCoordinate) -
                    generatedBusbarWidth / 2d;
                commonContactLengthMinimum = Math.Max(
                    commonContactLengthMinimum,
                    memberContactLengthMinimum);
                commonContactLengthMaximum = Math.Min(
                    commonContactLengthMaximum,
                    memberContactLengthMaximum);

                var memberWidthCenterCoordinate = new Vector3d(
                        collectiveReferencePoint,
                        terminalMemberPoseGeometry.SelectedBoundaryPoint)
                    .Dot(collectiveFixedWidthDirection);
                var memberFixedHalfWidth =
                    terminalMemberPoseGeometry.FixedSetting.BusBarWidth / 2d;
                collectiveFixedWidthMinimum = Math.Min(
                    collectiveFixedWidthMinimum,
                    memberWidthCenterCoordinate - memberFixedHalfWidth);
                collectiveFixedWidthMaximum = Math.Max(
                    collectiveFixedWidthMaximum,
                    memberWidthCenterCoordinate + memberFixedHalfWidth);
                terminalMemberWidthIntervals.Add(new TerminalMemberWidthInterval
                {
                    Minimum = memberWidthCenterCoordinate - memberFixedHalfWidth,
                    Maximum = memberWidthCenterCoordinate + memberFixedHalfWidth
                });
            }

            if (commonContactLengthMinimum >
                    commonContactLengthMaximum + GeometryTolerance ||
                collectiveFixedWidthMinimum >
                    collectiveFixedWidthMaximum - GeometryTolerance)
            {
                return false;
            }

            // 垂直搭接不受“单始端水平居中”开关影响。普通固定端始终从
            // 已解析的允许搭接端头向内保留半个生成排宽；只有已经实体化的
            // 双始端桥或排长重叠拓扑显式提供参考点时，才使用该参考位置。
            var selectedContactLengthCoordinate = Math.Max(
                commonContactLengthMinimum,
                Math.Min(
                    commonContactLengthMaximum,
                    preferredContactReferencePoint == null
                        ? generatedBusbarWidth / 2d
                        : new Vector3d(
                                collectiveReferencePoint,
                                preferredContactReferencePoint)
                            .Dot(collectiveInwardLengthDirection)));
            var collectiveFixedWidthCenterCoordinate =
                (collectiveFixedWidthMinimum + collectiveFixedWidthMaximum) / 2d;
            var collectiveLapCenter = TranslateAlongDirection(
                TranslateAlongDirection(
                    collectiveReferencePoint,
                    collectiveInwardLengthDirection,
                    selectedContactLengthCoordinate),
                collectiveFixedWidthDirection,
                collectiveFixedWidthCenterCoordinate);
            var lapCenterToOppositeDirection = oppositeTerminalAnchor == null
                ? null
                : new Vector3d(collectiveLapCenter, oppositeTerminalAnchor);
            var oppositeWidthProjection = lapCenterToOppositeDirection == null
                ? 0d
                : lapCenterToOppositeDirection.Dot(
                    collectiveFixedWidthDirection);
            var physicalPerpendicularExitDirection =
                oppositeWidthProjection <
                    -LinkageRouteGeometryTolerances.TerminalFace
                    ? Scale(collectiveFixedWidthDirection, -1d)
                    : Copy(collectiveFixedWidthDirection);
            var exitDirectionUsesPositiveWidthSide =
                physicalPerpendicularExitDirection.Dot(
                    collectiveFixedWidthDirection) > 0d;
            var collectiveLapEndpointWidthCoordinate =
                exitDirectionUsesPositiveWidthSide
                    ? collectiveFixedWidthMinimum
                    : collectiveFixedWidthMaximum;
            var actualPerpendicularLapCoverageSpan =
                collectiveFixedWidthMaximum - collectiveFixedWidthMinimum;
            if (allowPartialPerpendicularLap)
            {
                // 始端短搭接从离后续路径最远的那根固定排开始，只在该根
                // 始端内保留一个生成排排宽；随后沿出排方向到达其余始端，
                // 并分别只预留一个生成排排宽。这样单始端不会满跨固定排，
                // 双始端又仍真实搭接两根，而不会把近侧始端默认整宽跨满。
                var farthestTerminalMemberWidthInterval =
                    exitDirectionUsesPositiveWidthSide
                        ? terminalMemberWidthIntervals
                            .OrderBy(widthInterval => widthInterval.Maximum)
                            .ThenBy(widthInterval => widthInterval.Minimum)
                            .First()
                        : terminalMemberWidthIntervals
                            .OrderByDescending(widthInterval => widthInterval.Minimum)
                            .ThenByDescending(widthInterval => widthInterval.Maximum)
                            .First();
                var farthestTerminalMemberWidth =
                    farthestTerminalMemberWidthInterval.Maximum -
                    farthestTerminalMemberWidthInterval.Minimum;
                var retainedPartialLapWidth = Math.Min(
                    generatedBusbarWidth,
                    farthestTerminalMemberWidth);
                collectiveLapEndpointWidthCoordinate =
                    exitDirectionUsesPositiveWidthSide
                        ? farthestTerminalMemberWidthInterval.Maximum -
                            retainedPartialLapWidth
                        : farthestTerminalMemberWidthInterval.Minimum +
                            retainedPartialLapWidth;
                if (!TryGetMinimumPartialPerpendicularLapCoverageSpan(
                        terminalMemberWidthIntervals,
                        collectiveLapEndpointWidthCoordinate,
                        exitDirectionUsesPositiveWidthSide,
                        generatedBusbarWidth,
                        out actualPerpendicularLapCoverageSpan))
                {
                    return false;
                }
            }
            var collectiveLapEndpoint = TranslateAlongDirection(
                TranslateAlongDirection(
                    collectiveReferencePoint,
                    collectiveInwardLengthDirection,
                    selectedContactLengthCoordinate),
                collectiveFixedWidthDirection,
                collectiveLapEndpointWidthCoordinate);
            var collectivePerpendicularLapSpan =
                collectiveFixedWidthMaximum - collectiveFixedWidthMinimum;
            collectivePerpendicularTerminalPose = new LinkageRouteTerminalPose
            {
                LapEndpoint = collectiveLapEndpoint,
                RouteDirection = isStartTerminal
                    ? physicalPerpendicularExitDirection
                    : Scale(physicalPerpendicularExitDirection, -1d),
                RequiredTerminalSegmentLength = Math.Max(
                    perpendicularTerminalSegmentLength,
                    actualPerpendicularLapCoverageSpan +
                        Math.Max(0d, perpendicularTerminalBendAllowance)),
                PerpendicularLapCoverageSpan =
                    actualPerpendicularLapCoverageSpan > GeometryTolerance
                        ? actualPerpendicularLapCoverageSpan
                        : collectivePerpendicularLapSpan,
                IsPerpendicularLap = true
            };
            return true;
        }

        private static bool TryGetMinimumPartialPerpendicularLapCoverageSpan(
            IEnumerable<TerminalMemberWidthInterval> terminalMemberWidthIntervals,
            double generatedLapEndpointWidthCoordinate,
            bool exitDirectionUsesPositiveWidthSide,
            double generatedBusbarWidth,
            out double minimumPartialLapCoverageSpan)
        {
            minimumPartialLapCoverageSpan = 0d;
            if (terminalMemberWidthIntervals == null ||
                generatedBusbarWidth <= GeometryTolerance)
            {
                return false;
            }

            var resolvedTerminalMemberCount = 0;
            foreach (var terminalMemberWidthInterval in
                terminalMemberWidthIntervals)
            {
                if (terminalMemberWidthInterval == null)
                {
                    return false;
                }

                var terminalMemberWidth =
                    terminalMemberWidthInterval.Maximum -
                    terminalMemberWidthInterval.Minimum;
                if (terminalMemberWidth <= GeometryTolerance)
                {
                    return false;
                }

                var requiredMemberOverlapWidth = Math.Min(
                    generatedBusbarWidth,
                    terminalMemberWidth);
                double requiredMemberCoverageSpan;
                if (exitDirectionUsesPositiveWidthSide)
                {
                    var memberOverlapStartCoordinate = Math.Max(
                        generatedLapEndpointWidthCoordinate,
                        terminalMemberWidthInterval.Minimum);
                    var memberOverlapEndCoordinate =
                        memberOverlapStartCoordinate +
                        requiredMemberOverlapWidth;
                    if (memberOverlapEndCoordinate >
                        terminalMemberWidthInterval.Maximum +
                            GeometryTolerance)
                    {
                        return false;
                    }

                    requiredMemberCoverageSpan =
                        memberOverlapEndCoordinate -
                        generatedLapEndpointWidthCoordinate;
                }
                else
                {
                    var memberOverlapEndCoordinate = Math.Min(
                        generatedLapEndpointWidthCoordinate,
                        terminalMemberWidthInterval.Maximum);
                    var memberOverlapStartCoordinate =
                        memberOverlapEndCoordinate -
                        requiredMemberOverlapWidth;
                    if (memberOverlapStartCoordinate <
                        terminalMemberWidthInterval.Minimum -
                            GeometryTolerance)
                    {
                        return false;
                    }

                    requiredMemberCoverageSpan =
                        generatedLapEndpointWidthCoordinate -
                        memberOverlapStartCoordinate;
                }

                minimumPartialLapCoverageSpan = Math.Max(
                    minimumPartialLapCoverageSpan,
                    requiredMemberCoverageSpan);
                resolvedTerminalMemberCount++;
            }

            return resolvedTerminalMemberCount > 0 &&
                minimumPartialLapCoverageSpan > GeometryTolerance;
        }

        private static bool TryGetCollectiveStraightLengthInterval(
            LinkageTerminalAssembly terminalAssembly,
            Vector3d requiredParallelLengthDirection,
            out Vector3d resolvedLengthDirection,
            out double collectiveLengthMinimum,
            out double collectiveLengthMaximum)
        {
            resolvedLengthDirection = null;
            collectiveLengthMinimum = double.NegativeInfinity;
            collectiveLengthMaximum = double.PositiveInfinity;
            var usableFixedSettings = (terminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(IsUsableFixedSetting)
                .ToList();
            if (usableFixedSettings.Count == 0)
            {
                return false;
            }

            var referenceFixedLengthDirection = FindFirstSegmentDirection(
                usableFixedSettings[0]);
            if (referenceFixedLengthDirection == null ||
                !IsAxisAligned(referenceFixedLengthDirection) ||
                (requiredParallelLengthDirection != null &&
                 Math.Abs(referenceFixedLengthDirection.Dot(
                     requiredParallelLengthDirection.Normalized)) < 1d - 1e-6d))
            {
                return false;
            }

            resolvedLengthDirection = requiredParallelLengthDirection == null
                ? referenceFixedLengthDirection.Normalized
                : requiredParallelLengthDirection.Normalized;
            var normalizedCollectiveLengthDirection =
                resolvedLengthDirection.Normalized;
            foreach (var usableFixedSetting in usableFixedSettings)
            {
                if (!HasOnlyParallelLengthSegments(
                        usableFixedSetting,
                        normalizedCollectiveLengthDirection))
                {
                    return false;
                }

                var memberLengthCoordinates = usableFixedSetting.PointList
                    .Select(fixedPoint => GetLengthCoordinate(
                        fixedPoint,
                        normalizedCollectiveLengthDirection))
                    .ToList();
                var memberLengthMinimum = memberLengthCoordinates.Min();
                var memberLengthMaximum = memberLengthCoordinates.Max();
                if (memberLengthMaximum - memberLengthMinimum <= GeometryTolerance)
                {
                    return false;
                }

                collectiveLengthMinimum = Math.Max(
                    collectiveLengthMinimum,
                    memberLengthMinimum);
                collectiveLengthMaximum = Math.Min(
                    collectiveLengthMaximum,
                    memberLengthMaximum);
            }

            return collectiveLengthMaximum - collectiveLengthMinimum >
                GeometryTolerance;
        }

        private static Vector3d FindFirstSegmentDirection(
            PlanarBendBusbarSetting fixedSetting)
        {
            if (fixedSetting?.PointList == null)
            {
                return null;
            }

            for (var fixedPointIndex = 0;
                fixedPointIndex + 1 < fixedSetting.PointList.Count;
                fixedPointIndex++)
            {
                var fixedSegmentDirection = new Vector3d(
                    fixedSetting.PointList[fixedPointIndex],
                    fixedSetting.PointList[fixedPointIndex + 1]);
                if (fixedSegmentDirection.Norm > GeometryTolerance)
                {
                    return fixedSegmentDirection.Normalized;
                }
            }

            return null;
        }

        private static bool HasOnlyParallelLengthSegments(
            PlanarBendBusbarSetting fixedSetting,
            Vector3d referenceLengthDirection)
        {
            var hasUsableLengthSegment = false;
            for (var fixedPointIndex = 0;
                fixedPointIndex + 1 < fixedSetting.PointList.Count;
                fixedPointIndex++)
            {
                var fixedSegmentDirection = new Vector3d(
                    fixedSetting.PointList[fixedPointIndex],
                    fixedSetting.PointList[fixedPointIndex + 1]);
                if (fixedSegmentDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                hasUsableLengthSegment = true;
                if (Math.Abs(fixedSegmentDirection.Normalized.Dot(
                        referenceLengthDirection.Normalized)) < 1d - 1e-6d)
                {
                    return false;
                }
            }

            return hasUsableLengthSegment;
        }

        private static double GetLengthCoordinate(
            Point3d sourcePoint,
            Vector3d normalizedLengthDirection)
        {
            var normalizedDirection = normalizedLengthDirection.Normalized;
            return sourcePoint.X * normalizedDirection.X +
                sourcePoint.Y * normalizedDirection.Y +
                sourcePoint.Z * normalizedDirection.Z;
        }

        private static string GetTerminalMemberPoseGeometrySignature(
            TerminalMemberPoseGeometry terminalMemberPoseGeometry)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:F6},{1:F6},{2:F6}|{3:F6},{4:F6},{5:F6}|{6:F3}",
                terminalMemberPoseGeometry.SelectedBoundaryPoint.X,
                terminalMemberPoseGeometry.SelectedBoundaryPoint.Y,
                terminalMemberPoseGeometry.SelectedBoundaryPoint.Z,
                terminalMemberPoseGeometry.AdjacentInteriorPoint.X,
                terminalMemberPoseGeometry.AdjacentInteriorPoint.Y,
                terminalMemberPoseGeometry.AdjacentInteriorPoint.Z,
                terminalMemberPoseGeometry.FixedSetting.BusBarWidth);
        }

        private static IReadOnlyList<double> BuildParallelWidthOffsets(
            Point3d contactCenter,
            Point3d oppositeTerminalAnchor,
            Vector3d fixedWidthDirection,
            double fixedTerminalBusbarWidth,
            double generatedBusbarWidth,
            IEnumerable<double> clearanceWidthCoordinates,
            bool includeCollectiveWidthCoverageOffsets,
            bool requireMiddleWidthOffset)
        {
            var maximumAbsoluteWidthOffset =
                (fixedTerminalBusbarWidth - generatedBusbarWidth) / 2d;
            if (!includeCollectiveWidthCoverageOffsets &&
                maximumAbsoluteWidthOffset < -GeometryTolerance)
            {
                return new List<double>();
            }

            maximumAbsoluteWidthOffset = Math.Max(0d, maximumAbsoluteWidthOffset);
            var projectedOppositeWidthOffset = oppositeTerminalAnchor == null
                ? 0d
                : new Vector3d(contactCenter, oppositeTerminalAnchor)
                    .Dot(fixedWidthDirection.Normalized);
            var nearestContainedWidthOffset = includeCollectiveWidthCoverageOffsets
                ? projectedOppositeWidthOffset
                : Math.Max(
                    -maximumAbsoluteWidthOffset,
                    Math.Min(maximumAbsoluteWidthOffset, projectedOppositeWidthOffset));
            var selectedWidthOffsets = requireMiddleWidthOffset
                ? new List<double> { 0d }
                : new List<double> { nearestContainedWidthOffset };
            if (requireMiddleWidthOffset)
            {
                // “单始端水平居中”约束的是最终实体，不是路径提案。除中心
                // 姿态外仍保留原来朝对侧端点和绝缘临界面生成的水平提案；
                // 候选转换阶段会把其始端刚性前缀拉回中心，并由相邻段吸收
                // 位移。这样不会误删可转换方案，也不影响独立生成的垂直姿态。
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    nearestContainedWidthOffset);
            }
            if (includeCollectiveWidthCoverageOffsets)
            {
                // 双始端开启连续搭接后，合并范围不应只贡献“离柜顶最近”的
                // 一个姿态。柜顶投影落在整体范围外时，该姿态通常与其中一根
                // 真实始端的姿态重合，导致中间空白范围实际上没有进入搜索。
                AddUniqueWidthOffset(selectedWidthOffsets, 0d);
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    -maximumAbsoluteWidthOffset);
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    maximumAbsoluteWidthOffset);

                // 水平双始端的有效域由正常路径搜索范围决定，不再由固定端
                // 两根始端的宽度差裁切。没有障碍临界坐标的快速层也要保留
                // 整体范围两侧和对侧投影附近的关键位置；这些距离全部由
                // 实际固定端/生成排宽度推导，不使用项目特例坐标。
                var collectiveSearchHalfSpan =
                    (fixedTerminalBusbarWidth + generatedBusbarWidth) / 2d;
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    projectedOppositeWidthOffset - collectiveSearchHalfSpan);
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    projectedOppositeWidthOffset + collectiveSearchHalfSpan);
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    -collectiveSearchHalfSpan);
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    collectiveSearchHalfSpan);
            }

            var normalizedFixedWidthDirection = fixedWidthDirection.Normalized;
            var fixedWidthAxisIndex = GetDominantAxisIndex(normalizedFixedWidthDirection);
            var fixedWidthAxisComponent = GetVectorCoordinate(
                normalizedFixedWidthDirection,
                fixedWidthAxisIndex);
            if (Math.Abs(fixedWidthAxisComponent) <= GeometryTolerance)
            {
                return selectedWidthOffsets;
            }

            var contactCenterAxisCoordinate = GetPointCoordinate(
                contactCenter,
                fixedWidthAxisIndex);
            var containedClearanceOffsets = (clearanceWidthCoordinates ??
                    Enumerable.Empty<double>())
                .Select(clearanceWidthCoordinate =>
                    (clearanceWidthCoordinate - contactCenterAxisCoordinate) /
                    fixedWidthAxisComponent)
                .Select(clearanceWidthOffset =>
                    includeCollectiveWidthCoverageOffsets
                        ? clearanceWidthOffset
                        : Math.Max(
                            -maximumAbsoluteWidthOffset,
                            Math.Min(
                                maximumAbsoluteWidthOffset,
                                clearanceWidthOffset)))
                .GroupBy(clearanceWidthOffset => Math.Round(clearanceWidthOffset, 6))
                .Select(clearanceOffsetGroup => clearanceOffsetGroup.First())
                .OrderBy(clearanceWidthOffset => clearanceWidthOffset)
                .ToList();
            if (containedClearanceOffsets.Count > 0)
            {
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    containedClearanceOffsets[0]);
                AddUniqueWidthOffset(
                    selectedWidthOffsets,
                    containedClearanceOffsets[containedClearanceOffsets.Count - 1]);
            }

            return selectedWidthOffsets;
        }

        private static void AddUniqueWidthOffset(
            ICollection<double> selectedWidthOffsets,
            double candidateWidthOffset)
        {
            if (selectedWidthOffsets.Any(existingWidthOffset =>
                    Math.Abs(existingWidthOffset - candidateWidthOffset) <=
                    GeometryTolerance))
            {
                return;
            }

            selectedWidthOffsets.Add(candidateWidthOffset);
        }

        private static int GetDominantAxisIndex(Vector3d direction)
        {
            if (Math.Abs(direction.X) >= Math.Abs(direction.Y) &&
                Math.Abs(direction.X) >= Math.Abs(direction.Z))
            {
                return 0;
            }

            return Math.Abs(direction.Y) >= Math.Abs(direction.Z) ? 1 : 2;
        }

        private static double GetVectorCoordinate(Vector3d direction, int coordinateIndex)
        {
            if (coordinateIndex == 0) return direction.X;
            return coordinateIndex == 1 ? direction.Y : direction.Z;
        }

        private static double GetPointCoordinate(Point3d point, int coordinateIndex)
        {
            if (coordinateIndex == 0) return point.X;
            return coordinateIndex == 1 ? point.Y : point.Z;
        }

        internal static int FindAllowedExitFixedPointIndex(
            IList<Point3d> fixedCenterlinePoints,
            Vector3d forbiddenExitDirection)
        {
            if (fixedCenterlinePoints == null || fixedCenterlinePoints.Count < 2 ||
                forbiddenExitDirection == null || forbiddenExitDirection.Norm <= GeometryTolerance)
            {
                return -1;
            }

            var allowedExitDirection = Scale(forbiddenExitDirection.Normalized, -1d);
            var firstOutwardDirection = new Vector3d(
                fixedCenterlinePoints[1],
                fixedCenterlinePoints[0]).Normalized;
            var lastPointIndex = fixedCenterlinePoints.Count - 1;
            var lastOutwardDirection = new Vector3d(
                fixedCenterlinePoints[lastPointIndex - 1],
                fixedCenterlinePoints[lastPointIndex]).Normalized;
            var firstDirectionScore = firstOutwardDirection.Dot(allowedExitDirection);
            var lastDirectionScore = lastOutwardDirection.Dot(allowedExitDirection);
            if (Math.Max(firstDirectionScore, lastDirectionScore) < 1d - 1e-6d)
            {
                return -1;
            }

            return firstDirectionScore >= lastDirectionScore ? 0 : lastPointIndex;
        }

        internal static int FindNearestFixedPointIndex(
            IList<Point3d> fixedCenterlinePoints,
            Point3d oppositeTerminalAnchor)
        {
            if (oppositeTerminalAnchor == null)
            {
                return 0;
            }

            var nearestFixedPointIndex = 0;
            var nearestFixedPointDistance = fixedCenterlinePoints[0].DistanceTo(oppositeTerminalAnchor);
            for (var fixedPointIndex = 1;
                fixedPointIndex < fixedCenterlinePoints.Count;
                fixedPointIndex++)
            {
                var fixedPointDistance = fixedCenterlinePoints[fixedPointIndex]
                    .DistanceTo(oppositeTerminalAnchor);
                if (fixedPointDistance + GeometryTolerance >= nearestFixedPointDistance)
                {
                    continue;
                }

                nearestFixedPointIndex = fixedPointIndex;
                nearestFixedPointDistance = fixedPointDistance;
            }

            return nearestFixedPointIndex;
        }

        internal static int FindLapSegmentAdjacentPointIndex(
            IList<Point3d> fixedCenterlinePoints,
            int nearestFixedPointIndex,
            Point3d oppositeTerminalAnchor)
        {
            var adjacentFixedPointIndices = new List<int>();
            if (nearestFixedPointIndex > 0)
            {
                adjacentFixedPointIndices.Add(nearestFixedPointIndex - 1);
            }

            if (nearestFixedPointIndex + 1 < fixedCenterlinePoints.Count)
            {
                adjacentFixedPointIndices.Add(nearestFixedPointIndex + 1);
            }

            if (adjacentFixedPointIndices.Count <= 1 || oppositeTerminalAnchor == null)
            {
                return adjacentFixedPointIndices.Count == 0
                    ? -1
                    : adjacentFixedPointIndices[0];
            }

            var nearestFixedPoint = fixedCenterlinePoints[nearestFixedPointIndex];
            var nearestPointToOppositeDirection = new Vector3d(
                nearestFixedPoint,
                oppositeTerminalAnchor).Normalized;
            return adjacentFixedPointIndices
                .OrderByDescending(adjacentFixedPointIndex =>
                {
                    var fixedSegmentInwardDirection = new Vector3d(
                        nearestFixedPoint,
                        fixedCenterlinePoints[adjacentFixedPointIndex]).Normalized;
                    return Scale(fixedSegmentInwardDirection, -1d)
                        .Dot(nearestPointToOppositeDirection);
                })
                .First();
        }

        private static bool IsUsableFixedSetting(PlanarBendBusbarSetting fixedSetting)
        {
            return fixedSetting?.PointList != null &&
                fixedSetting.PointList.Count >= 2 &&
                fixedSetting.BusBarWidth > GeometryTolerance &&
                fixedSetting.WidthVector != null &&
                fixedSetting.WidthVector.Norm > GeometryTolerance;
        }

        private static bool IsAxisAligned(Vector3d direction)
        {
            var normalizedDirection = direction.Normalized;
            var alignedAxisCount =
                (Math.Abs(normalizedDirection.X) >= 1d - 1e-6 ? 1 : 0) +
                (Math.Abs(normalizedDirection.Y) >= 1d - 1e-6 ? 1 : 0) +
                (Math.Abs(normalizedDirection.Z) >= 1d - 1e-6 ? 1 : 0);
            return alignedAxisCount == 1;
        }

        private static string GetSignature(LinkageRouteTerminalPose terminalPose)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:F6},{1:F6},{2:F6}|{3:F3},{4:F3},{5:F3}|" +
                "LEN={6:F6}|SPAN={7:F6}|PERP={8}",
                terminalPose.LapEndpoint.X,
                terminalPose.LapEndpoint.Y,
                terminalPose.LapEndpoint.Z,
                terminalPose.RouteDirection.X,
                terminalPose.RouteDirection.Y,
                terminalPose.RouteDirection.Z,
                terminalPose.RequiredTerminalSegmentLength,
                terminalPose.PerpendicularLapCoverageSpan,
                terminalPose.IsPerpendicularLap ? 1 : 0);
        }

        private static Point3d ProjectToPlane(
            Point3d sourcePoint,
            Point3d planeReferencePoint,
            Vector3d planeNormalDirection)
        {
            var normalizedPlaneNormalDirection = planeNormalDirection.Normalized;
            var signedPlaneDistance = new Vector3d(
                    planeReferencePoint,
                    sourcePoint)
                .Dot(normalizedPlaneNormalDirection);
            return TranslateAlongDirection(
                sourcePoint,
                normalizedPlaneNormalDirection,
                -signedPlaneDistance);
        }
    }
}
