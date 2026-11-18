using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.Geometry;
using static RouteLinkBar.Algorithm.Geometry.GeometRiMethod;

namespace RouteLinkBar.Algorithm.Routing
{
    public sealed class LinkageRouteConversionContext
    {
        public double StartMinimumExtension { get; set; }
        public double EndMinimumExtension { get; set; }
        public double StartMaximumExtension { get; set; }
        public double EndMaximumExtension { get; set; }
        public double MinimumAdaptiveStep { get; set; } = 0.5d;
        public IList<double> StartCriticalExtensions { get; set; } = new List<double>();
        public IList<double> EndCriticalExtensions { get; set; } = new List<double>();
        public bool UseTerminalTrimRule { get; set; }
        public double GeneratedBusbarWidth { get; set; }
        public double StartMatingBusbarWidth { get; set; }
        public double EndMatingBusbarWidth { get; set; }
        public double StackBendAllowance { get; set; }
        /// <summary>始端垂直搭接排按自身厚度与叠排数量计算的折弯留量。</summary>
        public double StartStackBendAllowance { get; set; }
        /// <summary>柜顶垂直搭接排按自身厚度与叠排数量计算的折弯留量。</summary>
        public double EndStackBendAllowance { get; set; }
        /// <summary>不同规格内部垂直搭接从交点到折弯点的最不利最小距离。</summary>
        public double InternalLapMinimumRetainedLength { get; set; }
        public double MinimumSegmentLength { get; set; }
        /// <summary>
        /// 完整中心线首段的实际排厚方向。生产入口使用它预判跨平面拆排点，
        /// 从而只在某一保留腿确实成为双搭接 I 排时精确增加最小留量。
        /// </summary>
        public Vector3d InitialThicknessDirection { get; set; }
        /// <summary>
        /// 已经落入其他实体干涉或绝缘范围、必须由当前斜化区间实际移动的
        /// 原始骨架点下标。区间锚点不会移动，因此候选区间必须严格包住这些点。
        /// </summary>
        public IList<int> ClearanceViolatedAxisPointIndices { get; set; } =
            new List<int>();
        /// <summary>
        /// 当前骨架需要避让的固定端、障碍物和已选中间排实体。仅用于把
        /// 回折区间的反向内部搭接腿放到最近障碍临界位置，不做步长扫描。
        /// </summary>
        public IList<LinkageRouteSolid> ClearanceSolids { get; set; } =
            new List<LinkageRouteSolid>();
        public string CurrentClearanceGroupKey { get; set; }
        public double RequiredClearance { get; set; }
        /// <summary>
        /// 默认关闭。开启后恢复备用的多位置参数网格和多区段局部转换枚举；
        /// 生产候选默认只对每个连续区间在最小合法留量位置转换一次。
        /// </summary>
        public bool EnableAlternativeConversionEnumeration { get; set; }
        /// <summary>
        /// 开启后当前请求只允许全局 XYZ 轴向排，转换入口不得生成 N/M 斜排。
        /// </summary>
        public bool RequireAxisAlignedRoutes { get; set; }
        /// <summary>专用整路径 N/M 转换最多保留的候选数量。</summary>
        public int MaximumRestrictedWholePathCandidateCount { get; set; } = 4;
    }

    public sealed class LinkageRouteConversionCandidate
    {
        public IList<Point3d> Centerline { get; set; } = new List<Point3d>();
        public bool IsConverted { get; set; }
        public double StartExtension { get; set; }
        public double EndExtension { get; set; }
        public string TopologySignature { get; set; }
        public int ConversionStartPointIndex { get; set; } = -1;
        public int ConversionEndPointIndex { get; set; } = -1;
        public int ConvertedStartAnchorPointIndex { get; set; } = -1;
        public int ConvertedEndAnchorPointIndex { get; set; } = -1;
        public bool ReversesStartInternalLap { get; set; }
        public bool ReversesEndInternalLap { get; set; }

        public static IReadOnlyList<LinkageRouteConversionCandidate> CreateContinuousCandidates(
            IList<Point3d> axisCenterline,
            LinkageRouteConversionContext conversionContext)
        {
            if (axisCenterline == null || axisCenterline.Count < 2)
            {
                throw new ArgumentException("中心线路径至少需要两个点。", nameof(axisCenterline));
            }

            var convertedCandidates = new List<LinkageRouteConversionCandidate>
            {
                new LinkageRouteConversionCandidate
                {
                    Centerline = CopyPoints(axisCenterline),
                    IsConverted = false,
                    TopologySignature = "XYZ"
                }
            };

            if (axisCenterline.Count < 3 || conversionContext == null ||
                conversionContext.RequireAxisAlignedRoutes)
            {
                return convertedCandidates;
            }

            if (conversionContext.UseTerminalTrimRule)
            {
                // 生产入口不再用 L、N_V、U 等母型限制转换。任意至少跨过
                // 一个中间点的连续区间都生成一次最小合法留量斜连候选；
                // 每个候选只转换一个区间，并统一交给锐角、自交、搭接、
                // 绝缘和实际长度校验。备用开关仍保留多位置临界枚举。
                var trimmedSubpathCandidates =
                    (conversionContext.EnableAlternativeConversionEnumeration
                        ? CreateTrimmedSubpathCandidates(
                            axisCenterline,
                            conversionContext,
                            false,
                            false,
                            true)
                        : CreateTrimmedSubpathCandidates(
                            axisCenterline,
                            conversionContext,
                            false,
                            true,
                            false))
                    .OrderBy(conversionCandidate => GetCenterlineLength(
                        conversionCandidate.Centerline))
                    .ThenBy(conversionCandidate =>
                        conversionCandidate.Centerline.Count)
                    .ThenBy(conversionCandidate =>
                        conversionCandidate.TopologySignature,
                        StringComparer.Ordinal)
                    .ToList();
                convertedCandidates.AddRange(trimmedSubpathCandidates);
                return convertedCandidates;
            }

            var startDirection = new Vector3d(axisCenterline[0], axisCenterline[1]);
            var endDirection = new Vector3d(axisCenterline[axisCenterline.Count - 1], axisCenterline[axisCenterline.Count - 2]);
            if (startDirection.Norm < 1e-8 || endDirection.Norm < 1e-8)
            {
                return convertedCandidates;
            }

            var startExtensions = conversionContext.EnableAlternativeConversionEnumeration
                ? GetSearchValues(
                    conversionContext.StartMinimumExtension,
                    conversionContext.StartMaximumExtension,
                    conversionContext.StartCriticalExtensions)
                : GetExtremeSearchValue(
                    conversionContext.StartMinimumExtension,
                    conversionContext.StartMaximumExtension);
            var endExtensions = conversionContext.EnableAlternativeConversionEnumeration
                ? GetSearchValues(
                    conversionContext.EndMinimumExtension,
                    conversionContext.EndMaximumExtension,
                    conversionContext.EndCriticalExtensions)
                : GetExtremeSearchValue(
                    conversionContext.EndMinimumExtension,
                    conversionContext.EndMaximumExtension);

            foreach (var startExtension in startExtensions)
            {
                foreach (var endExtension in endExtensions)
                {
                    var startAnchor = axisCenterline[0];
                    var endAnchor = axisCenterline[axisCenterline.Count - 1];
                    var normalizedStartDirection = startDirection.Normalized;
                    var normalizedEndDirection = endDirection.Normalized;
                    var firstBendPoint = Translate(startAnchor, normalizedStartDirection, startExtension);
                    var secondBendPoint = Translate(endAnchor, normalizedEndDirection, endExtension);
                    if (firstBendPoint.DistanceTo(secondBendPoint) < 1e-6)
                    {
                        continue;
                    }

                    convertedCandidates.Add(new LinkageRouteConversionCandidate
                    {
                        Centerline = new List<Point3d>
                        {
                            Copy(startAnchor),
                            firstBendPoint,
                            secondBendPoint,
                            Copy(endAnchor)
                        },
                        IsConverted = true,
                        StartExtension = startExtension,
                        EndExtension = endExtension,
                        TopologySignature = AreParallel(startDirection, endDirection) ? "N" : "M",
                        ConversionStartPointIndex = 0,
                        ConversionEndPointIndex = axisCenterline.Count - 1,
                        ConvertedStartAnchorPointIndex = 0,
                        ConvertedEndAnchorPointIndex = 3
                    });
                }
            }

            return convertedCandidates;
        }

        /// <summary>
        /// 为“始端与柜顶排长方向恰好重叠一个排宽”的专用直搭骨架，
        /// 只枚举完整首末区间的有限 N/M 单排转换。候选位置仅包含
        /// 最小合法留量以及原直搭骨架的首、末安全腿，不展开局部区间、
        /// 障碍临界网格或直角回退，避免影响普通候选链的首解速度。
        /// </summary>
        internal static IReadOnlyList<LinkageRouteConversionCandidate>
            CreateRestrictedWholePathCandidates(
                IList<Point3d> overlapAxisCenterline,
                LinkageRouteConversionContext sourceConversionContext,
                double originalStartLegLength,
                double originalEndLegLength)
        {
            if (overlapAxisCenterline == null ||
                overlapAxisCenterline.Count < 3 ||
                sourceConversionContext == null ||
                sourceConversionContext.RequireAxisAlignedRoutes)
            {
                return Array.Empty<LinkageRouteConversionCandidate>();
            }

            var restrictedWholePathConversionContext =
                new LinkageRouteConversionContext
                {
                    StartMinimumExtension =
                        sourceConversionContext.StartMinimumExtension,
                    EndMinimumExtension =
                        sourceConversionContext.EndMinimumExtension,
                    StartMaximumExtension =
                        sourceConversionContext.StartMaximumExtension,
                    EndMaximumExtension =
                        sourceConversionContext.EndMaximumExtension,
                    MinimumAdaptiveStep =
                        sourceConversionContext.MinimumAdaptiveStep,
                    StartCriticalExtensions = new[]
                    {
                        Math.Max(0d, originalStartLegLength)
                    },
                    EndCriticalExtensions = new[]
                    {
                        Math.Max(0d, originalEndLegLength)
                    },
                    UseTerminalTrimRule = true,
                    GeneratedBusbarWidth =
                        sourceConversionContext.GeneratedBusbarWidth,
                    StartMatingBusbarWidth =
                        sourceConversionContext.StartMatingBusbarWidth,
                    EndMatingBusbarWidth =
                        sourceConversionContext.EndMatingBusbarWidth,
                    StackBendAllowance =
                        sourceConversionContext.StackBendAllowance,
                    StartStackBendAllowance =
                        sourceConversionContext.StartStackBendAllowance,
                    EndStackBendAllowance =
                        sourceConversionContext.EndStackBendAllowance,
                    InternalLapMinimumRetainedLength =
                        sourceConversionContext.InternalLapMinimumRetainedLength,
                    MinimumSegmentLength =
                        sourceConversionContext.MinimumSegmentLength,
                    InitialThicknessDirection =
                        sourceConversionContext.InitialThicknessDirection,
                    ClearanceViolatedAxisPointIndices =
                        sourceConversionContext.ClearanceViolatedAxisPointIndices?
                            .ToList() ?? new List<int>(),
                    ClearanceSolids =
                        sourceConversionContext.ClearanceSolids?.ToList() ??
                            new List<LinkageRouteSolid>(),
                    CurrentClearanceGroupKey =
                        sourceConversionContext.CurrentClearanceGroupKey,
                    RequiredClearance =
                        sourceConversionContext.RequiredClearance,
                    EnableAlternativeConversionEnumeration = false,
                    RequireAxisAlignedRoutes =
                        sourceConversionContext.RequireAxisAlignedRoutes,
                    MaximumRestrictedWholePathCandidateCount =
                        sourceConversionContext
                            .MaximumRestrictedWholePathCandidateCount
                };

            return CreateTrimmedSubpathCandidates(
                    overlapAxisCenterline,
                    restrictedWholePathConversionContext,
                    true,
                    false,
                    false)
                .Where(restrictedCandidate =>
                    restrictedCandidate.IsConverted &&
                    restrictedCandidate.ConversionStartPointIndex == 0 &&
                    restrictedCandidate.ConversionEndPointIndex ==
                        overlapAxisCenterline.Count - 1 &&
                    (string.Equals(
                         restrictedCandidate.TopologySignature,
                         "N",
                         StringComparison.Ordinal) ||
                     string.Equals(
                         restrictedCandidate.TopologySignature,
                         "M",
                         StringComparison.Ordinal)))
                .GroupBy(
                    restrictedCandidate =>
                        CreatePointSignature(restrictedCandidate.Centerline),
                    StringComparer.Ordinal)
                .Select(restrictedCandidateGroup =>
                    restrictedCandidateGroup.First())
                .OrderBy(restrictedCandidate =>
                    GetCenterlineLength(restrictedCandidate.Centerline))
                .ThenBy(restrictedCandidate =>
                    restrictedCandidate.StartExtension +
                    restrictedCandidate.EndExtension)
                .ThenBy(restrictedCandidate =>
                    CreatePointSignature(restrictedCandidate.Centerline),
                    StringComparer.Ordinal)
                .Take(Math.Max(
                    1,
                    sourceConversionContext
                        .MaximumRestrictedWholePathCandidateCount))
                .ToList();
        }

        private static IEnumerable<LinkageRouteConversionCandidate> CreateTrimmedSubpathCandidates(
            IList<Point3d> axisCenterline,
            LinkageRouteConversionContext conversionContext,
            bool onlyWholePath,
            bool onlyMinimumReserve,
            bool allowRightAngleFallback)
        {
            var convertedCandidateSignatures = new HashSet<string>(StringComparer.Ordinal);
            var originalPlanarTransitionPointIndices = new HashSet<int>(
                LinkageRouteCenterlineConverter.GetActualPlanarTransitionPointIndices(
                    axisCenterline,
                    conversionContext.InitialThicknessDirection));
            var sectionStartPointIndices = onlyWholePath
                ? new[] { 0 }
                : Enumerable.Range(0, axisCenterline.Count - 2);
            foreach (var sectionStartPointIndex in sectionStartPointIndices)
            {
                var firstSectionEndPointIndex = onlyWholePath
                    ? axisCenterline.Count - 1
                    : sectionStartPointIndex + 2;
                for (var sectionEndPointIndex = firstSectionEndPointIndex;
                    sectionEndPointIndex < axisCenterline.Count;
                    sectionEndPointIndex++)
                {
                    if (!MovesEveryClearanceViolatedAxisPoint(
                            sectionStartPointIndex,
                            sectionEndPointIndex,
                            axisCenterline.Count,
                            conversionContext.ClearanceViolatedAxisPointIndices))
                    {
                        continue;
                    }

                    var startAnchor = axisCenterline[sectionStartPointIndex];
                    var startInnerPoint = axisCenterline[sectionStartPointIndex + 1];
                    var endAnchor = axisCenterline[sectionEndPointIndex];
                    var endInnerPoint = axisCenterline[sectionEndPointIndex - 1];
                    var startDirection = new Vector3d(startAnchor, startInnerPoint);
                    var endDirection = new Vector3d(endAnchor, endInnerPoint);
                    if (startDirection.Norm < 1e-8 || endDirection.Norm < 1e-8)
                    {
                        continue;
                    }

                    foreach (var sectionEndpointDirections in
                        BuildSlantedSectionEndpointDirectionCandidates(
                            axisCenterline.Count,
                            sectionStartPointIndex,
                            sectionEndPointIndex,
                            startDirection,
                            endDirection,
                            originalPlanarTransitionPointIndices))
                    {
                        var startAnchorIsTerminalEndpoint =
                            sectionStartPointIndex == 0;
                        var endAnchorIsTerminalEndpoint =
                            sectionEndPointIndex == axisCenterline.Count - 1;
                        var startMatingWidth = startAnchorIsTerminalEndpoint
                            ? conversionContext.StartMatingBusbarWidth
                            : conversionContext.GeneratedBusbarWidth;
                        var endMatingWidth = endAnchorIsTerminalEndpoint
                            ? conversionContext.EndMatingBusbarWidth
                            : conversionContext.GeneratedBusbarWidth;
                        var retainedStartLength = startAnchorIsTerminalEndpoint
                            ? startMatingWidth + ResolveTerminalStackBendAllowance(
                                conversionContext.StartStackBendAllowance,
                                conversionContext.StackBendAllowance)
                            : ResolveInternalLapMinimumRetainedLength(
                                conversionContext);
                        var retainedEndLength = endAnchorIsTerminalEndpoint
                            ? endMatingWidth + ResolveTerminalStackBendAllowance(
                                conversionContext.EndStackBendAllowance,
                                conversionContext.StackBendAllowance)
                            : ResolveInternalLapMinimumRetainedLength(
                                conversionContext);
                        var actualStartLegLength = Math.Max(
                            retainedStartLength,
                            conversionContext.MinimumSegmentLength);
                        var actualEndLegLength = Math.Max(
                            retainedEndLength,
                            conversionContext.MinimumSegmentLength);
                        if (sectionEndpointDirections.StartInwardDirection.Norm +
                                1e-8 < actualStartLegLength ||
                            sectionEndpointDirections.EndInwardDirection.Norm +
                                1e-8 < actualEndLegLength)
                        {
                            continue;
                        }

                        var trimmedExtensionPairs = GetTrimmedExtensionPairs(
                                actualStartLegLength,
                                sectionEndpointDirections.StartInwardDirection.Norm,
                                actualEndLegLength,
                                sectionEndpointDirections.EndInwardDirection.Norm,
                                startAnchorIsTerminalEndpoint
                                    ? conversionContext.StartCriticalExtensions
                                    : sectionEndpointDirections.ReversesStartInternalLap
                                        ? BuildInternalLapObstacleCriticalExtensions(
                                            startAnchor,
                                            sectionEndpointDirections
                                                .StartInwardDirection,
                                            actualStartLegLength,
                                            sectionEndpointDirections
                                                .StartInwardDirection.Norm,
                                            conversionContext)
                                        : null,
                                endAnchorIsTerminalEndpoint
                                    ? conversionContext.EndCriticalExtensions
                                    : sectionEndpointDirections.ReversesEndInternalLap
                                        ? BuildInternalLapObstacleCriticalExtensions(
                                            endAnchor,
                                            sectionEndpointDirections
                                                .EndInwardDirection,
                                            actualEndLegLength,
                                            sectionEndpointDirections
                                                .EndInwardDirection.Norm,
                                            conversionContext)
                                        : null)
                            .AsEnumerable();
                        if (onlyMinimumReserve)
                        {
                            // 普通区间仍只有一个最小留量候选。回折区间翻转
                            // 内部搭接腿后，额外保留一个最近障碍边界临界值；
                            // 不恢复多位置网格或两端笛卡尔积。
                            var retainedExtensionPairCount =
                                sectionEndpointDirections.ReversesStartInternalLap ||
                                sectionEndpointDirections.ReversesEndInternalLap
                                    ? 2
                                    : 1;
                            trimmedExtensionPairs = trimmedExtensionPairs.Take(
                                retainedExtensionPairCount);
                        }

                        foreach (var extensionPair in trimmedExtensionPairs)
                        {
                            if (!TryCreateMinimumLapSafeDirectSection(
                                    axisCenterline,
                                    sectionStartPointIndex,
                                    sectionEndPointIndex,
                                    sectionEndpointDirections.StartInwardDirection,
                                    sectionEndpointDirections.EndInwardDirection,
                                    extensionPair.Item1,
                                    extensionPair.Item2,
                                    conversionContext,
                                    out var directSectionCenterline,
                                    out var selectedStartLegLength,
                                    out var selectedEndLegLength))
                            {
                                continue;
                            }

                            foreach (var sectionShapeCandidate in
                                CreateManufacturableSectionShapeCandidates(
                                    directSectionCenterline,
                                    startAnchor,
                                    endAnchor,
                                    selectedStartLegLength,
                                    selectedEndLegLength,
                                    conversionContext.MinimumSegmentLength,
                                    allowRightAngleFallback))
                            {
                                var convertedCenterline = CreateReplacedSectionCenterline(
                                    axisCenterline,
                                    sectionStartPointIndex,
                                    sectionEndPointIndex,
                                    sectionShapeCandidate.Item1,
                                    out var convertedStartAnchorPointIndex,
                                    out var convertedEndAnchorPointIndex);
                                var candidateSignature = CreatePointSignature(
                                    convertedCenterline);
                                if (!convertedCandidateSignatures.Add(candidateSignature))
                                {
                                    continue;
                                }

                                yield return new LinkageRouteConversionCandidate
                                {
                                    Centerline = convertedCenterline,
                                    IsConverted = true,
                                    StartExtension = selectedStartLegLength,
                                    EndExtension = selectedEndLegLength,
                                    TopologySignature = sectionShapeCandidate.Item2,
                                    ConversionStartPointIndex = sectionStartPointIndex,
                                    ConversionEndPointIndex = sectionEndPointIndex,
                                    ConvertedStartAnchorPointIndex =
                                        convertedStartAnchorPointIndex,
                                    ConvertedEndAnchorPointIndex =
                                        convertedEndAnchorPointIndex,
                                    ReversesStartInternalLap =
                                        sectionEndpointDirections
                                            .ReversesStartInternalLap,
                                    ReversesEndInternalLap =
                                        sectionEndpointDirections
                                            .ReversesEndInternalLap
                                };
                            }
                        }
                    }
                }
            }
        }

        private static double ResolveTerminalStackBendAllowance(
            double terminalStackBendAllowance,
            double fallbackStackBendAllowance)
        {
            return terminalStackBendAllowance > 0d
                ? terminalStackBendAllowance
                : fallbackStackBendAllowance;
        }

        private static double ResolveInternalLapMinimumRetainedLength(
            LinkageRouteConversionContext conversionContext)
        {
            return conversionContext.InternalLapMinimumRetainedLength > 0d
                ? conversionContext.InternalLapMinimumRetainedLength
                : conversionContext.GeneratedBusbarWidth / 2d +
                    conversionContext.StackBendAllowance;
        }

        private static IReadOnlyList<SlantedSectionEndpointDirections>
            BuildSlantedSectionEndpointDirectionCandidates(
                int axisPointCount,
                int sectionStartPointIndex,
                int sectionEndPointIndex,
                Vector3d originalStartInwardDirection,
                Vector3d originalEndInwardDirection,
                ISet<int> originalPlanarTransitionPointIndices)
        {
            var endpointDirectionCandidates =
                new List<SlantedSectionEndpointDirections>
                {
                    new SlantedSectionEndpointDirections
                    {
                        StartInwardDirection = originalStartInwardDirection,
                        EndInwardDirection = originalEndInwardDirection
                    }
                };
            if (!AreParallel(
                    originalStartInwardDirection,
                    originalEndInwardDirection) ||
                originalStartInwardDirection.Normalized.Dot(
                    originalEndInwardDirection.Normalized) <= 0d)
            {
                return endpointDirectionCandidates;
            }

            // 区间两端的向内向量同向，表示实际首、尾行进方向相反，即该
            // 连续区间存在 U 型回折。若锚点是跨平面拆排形成的内部搭接面，
            // 后一根排可以从搭接面的另一侧穿过锚点；锚点仍固定，只反转
            // 该端保留腿。固定始端和柜顶没有这种自由度，绝不反转。
            var startAnchorIsInternalPlanarLap = sectionStartPointIndex > 0 &&
                sectionStartPointIndex < axisPointCount - 1 &&
                originalPlanarTransitionPointIndices.Contains(
                    sectionStartPointIndex);
            if (startAnchorIsInternalPlanarLap)
            {
                endpointDirectionCandidates.Add(
                    new SlantedSectionEndpointDirections
                    {
                        StartInwardDirection = CreateOppositeDirection(
                            originalStartInwardDirection),
                        EndInwardDirection = originalEndInwardDirection,
                        ReversesStartInternalLap = true
                    });
            }

            var endAnchorIsInternalPlanarLap = sectionEndPointIndex > 0 &&
                sectionEndPointIndex < axisPointCount - 1 &&
                originalPlanarTransitionPointIndices.Contains(
                    sectionEndPointIndex);
            if (endAnchorIsInternalPlanarLap)
            {
                endpointDirectionCandidates.Add(
                    new SlantedSectionEndpointDirections
                    {
                        StartInwardDirection = originalStartInwardDirection,
                        EndInwardDirection = CreateOppositeDirection(
                            originalEndInwardDirection),
                        ReversesEndInternalLap = true
                    });
            }

            return endpointDirectionCandidates;
        }

        private static IReadOnlyList<double>
            BuildInternalLapObstacleCriticalExtensions(
                Point3d internalLapAnchor,
                Vector3d reversedInwardDirection,
                double minimumExtension,
                double maximumExtension,
                LinkageRouteConversionContext conversionContext)
        {
            if (internalLapAnchor == null || reversedInwardDirection == null ||
                reversedInwardDirection.Norm <= 1e-8 ||
                conversionContext == null)
            {
                return Array.Empty<double>();
            }

            var normalizedReversedInwardDirection =
                reversedInwardDirection.Normalized;
            var internalLapCriticalExtensions = new List<double>();
            foreach (var clearanceSolid in conversionContext.ClearanceSolids ??
                Enumerable.Empty<LinkageRouteSolid>())
            {
                var isCurrentGroupSolid = clearanceSolid != null &&
                    !string.IsNullOrWhiteSpace(
                        clearanceSolid.ClearanceGroupKey) &&
                    string.Equals(
                        clearanceSolid.ClearanceGroupKey,
                        conversionContext.CurrentClearanceGroupKey,
                        StringComparison.OrdinalIgnoreCase);
                var isInputObstacle = clearanceSolid?.SolidRole ==
                    LinkageRouteSolidRole.InputObstacle;
                if (clearanceSolid == null ||
                    (isCurrentGroupSolid &&
                     !clearanceSolid.IsForbiddenTerminalRegion &&
                     !isInputObstacle))
                {
                    continue;
                }

                var solidRequiredClearance =
                    clearanceSolid.IsForbiddenTerminalRegion ||
                    isCurrentGroupSolid ||
                    isInputObstacle
                    ? 0d
                    : Math.Max(0d, conversionContext.RequiredClearance);
                var criticalSafetyMargin = solidRequiredClearance +
                    conversionContext.GeneratedBusbarWidth / 2d;
                var projectedSolidCoordinates = clearanceSolid.GetVertices()
                    .Select(solidVertex => new Vector3d(
                        internalLapAnchor,
                        solidVertex).Dot(normalizedReversedInwardDirection))
                    .ToList();
                if (projectedSolidCoordinates.Count == 0)
                {
                    continue;
                }

                internalLapCriticalExtensions.Add(
                    projectedSolidCoordinates.Min() - criticalSafetyMargin);
                internalLapCriticalExtensions.Add(
                    projectedSolidCoordinates.Max() + criticalSafetyMargin);
            }

            return internalLapCriticalExtensions
                .Where(criticalExtension =>
                    criticalExtension >= minimumExtension - 1e-8 &&
                    criticalExtension <= maximumExtension + 1e-8)
                .Select(criticalExtension => Math.Round(criticalExtension, 6))
                .Distinct()
                .OrderBy(criticalExtension =>
                    Math.Abs(criticalExtension - minimumExtension))
                .Take(1)
                .ToList();
        }

        private static Vector3d CreateOppositeDirection(Vector3d sourceDirection)
        {
            return new Vector3d(
                -sourceDirection.X,
                -sourceDirection.Y,
                -sourceDirection.Z);
        }

        private sealed class SlantedSectionEndpointDirections
        {
            public Vector3d StartInwardDirection { get; set; }
            public Vector3d EndInwardDirection { get; set; }
            public bool ReversesStartInternalLap { get; set; }
            public bool ReversesEndInternalLap { get; set; }
        }

        private static bool MovesEveryClearanceViolatedAxisPoint(
            int sectionStartPointIndex,
            int sectionEndPointIndex,
            int axisPointCount,
            IEnumerable<int> clearanceViolatedAxisPointIndices)
        {
            foreach (var clearanceViolatedAxisPointIndex in
                clearanceViolatedAxisPointIndices ?? Enumerable.Empty<int>())
            {
                if (clearanceViolatedAxisPointIndex < 0 ||
                    clearanceViolatedAxisPointIndex >= axisPointCount)
                {
                    continue;
                }

                // 斜化区间的两个锚点以及区间外点均保持原坐标；只有严格位于
                // 两锚点之间的原始点才会被替换，因此才能修复既有点违规。
                if (clearanceViolatedAxisPointIndex <= sectionStartPointIndex ||
                    clearanceViolatedAxisPointIndex >= sectionEndPointIndex)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryCreateMinimumLapSafeDirectSection(
            IList<Point3d> axisCenterline,
            int sectionStartPointIndex,
            int sectionEndPointIndex,
            Vector3d startDirection,
            Vector3d endDirection,
            double requestedStartLegLength,
            double requestedEndLegLength,
            LinkageRouteConversionContext conversionContext,
            out IList<Point3d> directSectionCenterline,
            out double selectedStartLegLength,
            out double selectedEndLegLength)
        {
            directSectionCenterline = null;
            selectedStartLegLength = requestedStartLegLength;
            selectedEndLegLength = requestedEndLegLength;
            var startAnchor = axisCenterline[sectionStartPointIndex];
            var endAnchor = axisCenterline[sectionEndPointIndex];
            var startAnchorIsTerminalEndpoint = sectionStartPointIndex == 0;
            var endAnchorIsTerminalEndpoint =
                sectionEndPointIndex == axisCenterline.Count - 1;

            // 留量只允许向内增加。一次增加可能改变斜段方向，因此最多复核三次；
            // 不做步长或网格枚举，最终位置仍是当前拓扑的最小合法位置。
            for (var reserveAdjustmentPass = 0;
                reserveAdjustmentPass < 3;
                reserveAdjustmentPass++)
            {
                if (selectedStartLegLength > startDirection.Norm + 1e-8 ||
                    selectedEndLegLength > endDirection.Norm + 1e-8)
                {
                    return false;
                }

                var firstBendPoint = Translate(
                    startAnchor,
                    startDirection.Normalized,
                    selectedStartLegLength);
                var secondBendPoint = Translate(
                    endAnchor,
                    endDirection.Normalized,
                    selectedEndLegLength);
                if (firstBendPoint.DistanceTo(secondBendPoint) + 1e-8 <
                    conversionContext.MinimumSegmentLength)
                {
                    return false;
                }

                directSectionCenterline = new List<Point3d>
                {
                    Copy(startAnchor),
                    firstBendPoint,
                    secondBendPoint,
                    Copy(endAnchor)
                };
                if (conversionContext.InitialThicknessDirection == null ||
                    conversionContext.InitialThicknessDirection.Norm <= 1e-8 ||
                    conversionContext.GeneratedBusbarWidth <= 1e-8)
                {
                    return true;
                }

                var convertedCenterline = CreateReplacedSectionCenterline(
                    axisCenterline,
                    sectionStartPointIndex,
                    sectionEndPointIndex,
                    directSectionCenterline,
                    out _,
                    out _);
                var transitionPointIndices = new HashSet<int>(
                    LinkageRouteCenterlineConverter
                        .GetActualPlanarTransitionPointIndices(
                            convertedCenterline,
                            conversionContext.InitialThicknessDirection));
                var minimumStartLegLength = GetDoubleLapSegmentMinimumLength(
                    sectionStartPointIndex,
                    startAnchorIsTerminalEndpoint,
                    false,
                    transitionPointIndices,
                    conversionContext.GeneratedBusbarWidth);
                var minimumEndLegLength = GetDoubleLapSegmentMinimumLength(
                    sectionStartPointIndex + 2,
                    false,
                    endAnchorIsTerminalEndpoint,
                    transitionPointIndices,
                    conversionContext.GeneratedBusbarWidth);
                var adjustedStartLegLength = Math.Max(
                    selectedStartLegLength,
                    minimumStartLegLength);
                var adjustedEndLegLength = Math.Max(
                    selectedEndLegLength,
                    minimumEndLegLength);
                if (Math.Abs(adjustedStartLegLength - selectedStartLegLength) <= 1e-8 &&
                    Math.Abs(adjustedEndLegLength - selectedEndLegLength) <= 1e-8)
                {
                    return true;
                }

                selectedStartLegLength = adjustedStartLegLength;
                selectedEndLegLength = adjustedEndLegLength;
            }

            if (selectedStartLegLength > startDirection.Norm + 1e-8 ||
                selectedEndLegLength > endDirection.Norm + 1e-8)
            {
                return false;
            }

            var finalFirstBendPoint = Translate(
                startAnchor,
                startDirection.Normalized,
                selectedStartLegLength);
            var finalSecondBendPoint = Translate(
                endAnchor,
                endDirection.Normalized,
                selectedEndLegLength);
            if (finalFirstBendPoint.DistanceTo(finalSecondBendPoint) + 1e-8 <
                conversionContext.MinimumSegmentLength)
            {
                return false;
            }

            directSectionCenterline = new List<Point3d>
            {
                Copy(startAnchor),
                finalFirstBendPoint,
                finalSecondBendPoint,
                Copy(endAnchor)
            };
            return true;
        }

        private static double GetDoubleLapSegmentMinimumLength(
            int segmentStartPointIndex,
            bool segmentStartsAtTerminalLap,
            bool segmentEndsAtTerminalLap,
            ISet<int> transitionPointIndices,
            double generatedBusbarWidth)
        {
            var startsAtPlanarLap = transitionPointIndices.Contains(
                segmentStartPointIndex);
            var endsAtPlanarLap = transitionPointIndices.Contains(
                segmentStartPointIndex + 1);
            if (!(segmentStartsAtTerminalLap || startsAtPlanarLap) ||
                !(segmentEndsAtTerminalLap || endsAtPlanarLap))
            {
                return 0d;
            }

            // 跨平面拆排会在交点外延半个排宽；固定端搭接锚点不外延。
            // 反算原中心线长度，使同一根 I 排上的两个满搭接矩形恰好不重叠。
            var planarExtensionLength =
                (startsAtPlanarLap ? generatedBusbarWidth / 2d : 0d) +
                (endsAtPlanarLap ? generatedBusbarWidth / 2d : 0d);
            return Math.Max(
                0d,
                generatedBusbarWidth * 2d - planarExtensionLength);
        }

        private static IList<Point3d> CreateReplacedSectionCenterline(
            IList<Point3d> axisCenterline,
            int sectionStartPointIndex,
            int sectionEndPointIndex,
            IEnumerable<Point3d> replacementSectionPoints,
            out int convertedStartAnchorPointIndex,
            out int convertedEndAnchorPointIndex)
        {
            var replacedCenterline = new List<Point3d>();
            convertedStartAnchorPointIndex = -1;
            convertedEndAnchorPointIndex = -1;
            for (var prefixPointIndex = 0;
                prefixPointIndex < sectionStartPointIndex;
                prefixPointIndex++)
            {
                AddPoint(replacedCenterline, axisCenterline[prefixPointIndex]);
            }

            foreach (var replacementSectionPoint in replacementSectionPoints)
            {
                AddPoint(replacedCenterline, replacementSectionPoint);
                if (convertedStartAnchorPointIndex < 0)
                {
                    convertedStartAnchorPointIndex =
                        replacedCenterline.Count - 1;
                }

                convertedEndAnchorPointIndex = replacedCenterline.Count - 1;
            }

            for (var suffixPointIndex = sectionEndPointIndex + 1;
                suffixPointIndex < axisCenterline.Count;
                suffixPointIndex++)
            {
                AddPoint(replacedCenterline, axisCenterline[suffixPointIndex]);
            }

            return replacedCenterline;
        }

        private static IReadOnlyList<Tuple<double, double>> GetTrimmedExtensionPairs(
            double minimumStartExtension,
            double maximumStartExtension,
            double minimumEndExtension,
            double maximumEndExtension,
            IEnumerable<double> startCriticalExtensions,
            IEnumerable<double> endCriticalExtensions)
        {
            var startExtensions = GetCriticalTrimmedExtensions(
                    minimumStartExtension,
                    maximumStartExtension,
                    startCriticalExtensions)
                .ToList();
            var endExtensions = GetCriticalTrimmedExtensions(
                    minimumEndExtension,
                    maximumEndExtension,
                    endCriticalExtensions)
                .ToList();
            if (startExtensions.Count == 0 || endExtensions.Count == 0)
            {
                return Array.Empty<Tuple<double, double>>();
            }

            var extensionPairs = new List<Tuple<double, double>>();
            var extensionPairSignatures = new HashSet<string>(StringComparer.Ordinal);
            Action<double, double> addUniquePair = (startExtension, endExtension) =>
            {
                var extensionPairSignature = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:F6}|{1:F6}",
                    startExtension,
                    endExtension);
                if (extensionPairSignatures.Add(extensionPairSignature))
                {
                    extensionPairs.Add(Tuple.Create(startExtension, endExtension));
                }
            };

            // 默认只做满足搭接、折弯余量和最小段长的最短转换。
            addUniquePair(startExtensions[0], endExtensions[0]);

            // 障碍临界位置不做笛卡尔积：分别移动一端，再按距离最小顺序
            // 成对移动两端。这样保留障碍两侧极限通道，但不会形成网格爆炸。
            foreach (var startExtension in startExtensions.Skip(1))
            {
                addUniquePair(startExtension, endExtensions[0]);
            }

            foreach (var endExtension in endExtensions.Skip(1))
            {
                addUniquePair(startExtensions[0], endExtension);
            }

            var pairedCriticalExtensionCount = Math.Max(
                startExtensions.Count,
                endExtensions.Count);
            for (var criticalExtensionIndex = 1;
                criticalExtensionIndex < pairedCriticalExtensionCount;
                criticalExtensionIndex++)
            {
                addUniquePair(
                    startExtensions[Math.Min(
                        criticalExtensionIndex,
                        startExtensions.Count - 1)],
                    endExtensions[Math.Min(
                        criticalExtensionIndex,
                        endExtensions.Count - 1)]);
            }

            return extensionPairs;
        }

        private static IEnumerable<double> GetCriticalTrimmedExtensions(
            double minimumExtension,
            double maximumExtension,
            IEnumerable<double> criticalExtensions)
        {
            var normalizedMinimumExtension = Math.Max(0d, minimumExtension);
            var normalizedMaximumExtension = Math.Max(0d, maximumExtension);
            if (normalizedMaximumExtension + 1e-8 < normalizedMinimumExtension)
            {
                return Enumerable.Empty<double>();
            }

            return new[] { normalizedMinimumExtension }
                .Concat(criticalExtensions ?? Enumerable.Empty<double>())
                .Where(extensionValue =>
                    extensionValue >= normalizedMinimumExtension - 1e-8 &&
                    extensionValue <= normalizedMaximumExtension + 1e-8)
                .Select(extensionValue => Math.Round(extensionValue, 6))
                .Distinct()
                .OrderBy(extensionValue => extensionValue);
        }

        private static IReadOnlyList<Tuple<IList<Point3d>, string>>
            CreateManufacturableSectionShapeCandidates(
                IList<Point3d> directSectionCenterline,
                Point3d startAnchor,
                Point3d endAnchor,
            double selectedStartLegLength,
            double selectedEndLegLength,
            double minimumSegmentLength,
            bool allowRightAngleFallback)
        {
            var directSectionTopology = LinkageRouteBendGeometry
                .ClassifyFourPointShape(directSectionCenterline);
            var sectionShapeCandidates =
                new List<Tuple<IList<Point3d>, string>>();
            var firstTravelDirection = new Vector3d(
                directSectionCenterline[0], directSectionCenterline[1]);
            var lastTravelDirection = new Vector3d(
                directSectionCenterline[2], directSectionCenterline[3]);
            var replacementMiddleDirection = new Vector3d(
                directSectionCenterline[1], directSectionCenterline[2]);
            if (!IsGenuinelySlanted(replacementMiddleDirection))
            {
                return sectionShapeCandidates;
            }

            if (LinkageRouteBendGeometry.HasNoAcuteAngle(
                    directSectionCenterline,
                    out _,
                    out _))
            {
                sectionShapeCandidates.Add(Tuple.Create<IList<Point3d>, string>(
                    directSectionCenterline,
                    directSectionTopology));
            }
            else if (allowRightAngleFallback &&
                string.Equals(directSectionTopology, "U", StringComparison.Ordinal) &&
                LinkageRouteBendGeometry.TryCreateRightAngleU(
                        startAnchor,
                        endAnchor,
                        firstTravelDirection,
                        lastTravelDirection,
                        selectedStartLegLength,
                        selectedEndLegLength,
                        minimumSegmentLength,
                        out var normalizedUSectionCenterline))
            {
                sectionShapeCandidates.Add(Tuple.Create(
                    normalizedUSectionCenterline,
                    "U"));
            }
            else if (allowRightAngleFallback &&
                string.Equals(
                    directSectionTopology,
                    "M",
                    StringComparison.Ordinal) &&
                LinkageRouteBendGeometry.TryCreateRightAngleL(
                    startAnchor,
                    endAnchor,
                    firstTravelDirection,
                    lastTravelDirection,
                    selectedStartLegLength,
                    selectedEndLegLength,
                    minimumSegmentLength,
                    out var repairedLSectionCenterline))
            {
                sectionShapeCandidates.Add(Tuple.Create(
                    repairedLSectionCenterline,
                    "L"));
            }

            return sectionShapeCandidates;
        }

        private static bool IsGenuinelySlanted(Vector3d segmentDirection)
        {
            if (segmentDirection == null || segmentDirection.Norm <= 1e-8)
            {
                return false;
            }

            var normalizedSegmentDirection = segmentDirection.Normalized;
            return new[]
                {
                    Math.Abs(normalizedSegmentDirection.X),
                    Math.Abs(normalizedSegmentDirection.Y),
                    Math.Abs(normalizedSegmentDirection.Z)
                }
                .Count(componentMagnitude => componentMagnitude > 1e-6) >= 2;
        }

        private static string CreatePointSignature(IEnumerable<Point3d> centerlinePoints)
        {
            return string.Join("|", centerlinePoints.Select(point => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:F6},{1:F6},{2:F6}",
                point.X,
                point.Y,
                point.Z)));
        }

        private static double GetCenterlineLength(IList<Point3d> centerlinePoints)
        {
            if (centerlinePoints == null || centerlinePoints.Count < 2)
            {
                return double.PositiveInfinity;
            }

            return centerlinePoints
                .Zip(centerlinePoints.Skip(1),
                    (segmentStart, segmentEnd) =>
                        segmentStart.DistanceTo(segmentEnd))
                .Sum();
        }

        internal static LinkageRouteConversionCandidate CreateAtParameters(
            IList<Point3d> axisCenterline,
            double startExtension,
            double endExtension)
        {
            if (axisCenterline == null || axisCenterline.Count < 3)
            {
                return null;
            }

            var startDirection = new Vector3d(axisCenterline[0], axisCenterline[1]);
            var endDirection = new Vector3d(
                axisCenterline[axisCenterline.Count - 1],
                axisCenterline[axisCenterline.Count - 2]);
            if (startDirection.Norm < 1e-8 || endDirection.Norm < 1e-8)
            {
                return null;
            }

            var startAnchor = axisCenterline[0];
            var endAnchor = axisCenterline[axisCenterline.Count - 1];
            var firstBendPoint = Translate(startAnchor, startDirection.Normalized, startExtension);
            var secondBendPoint = Translate(endAnchor, endDirection.Normalized, endExtension);
            if (firstBendPoint.DistanceTo(secondBendPoint) < 1e-6)
            {
                return null;
            }

            return new LinkageRouteConversionCandidate
            {
                Centerline = new List<Point3d>
                {
                    Copy(startAnchor),
                    firstBendPoint,
                    secondBendPoint,
                    Copy(endAnchor)
                },
                IsConverted = true,
                StartExtension = startExtension,
                EndExtension = endExtension,
                TopologySignature = AreParallel(startDirection, endDirection) ? "N" : "M",
                ConversionStartPointIndex = 0,
                ConversionEndPointIndex = axisCenterline.Count - 1,
                ConvertedStartAnchorPointIndex = 0,
                ConvertedEndAnchorPointIndex = 3
            };
        }

        private static IEnumerable<double> GetSearchValues(
            double minimumExtension,
            double maximumExtension,
            IEnumerable<double> criticalExtensions)
        {
            var normalizedMinimumExtension = Math.Max(0d, minimumExtension);
            var normalizedMaximumExtension = Math.Max(0d, maximumExtension);
            if (normalizedMaximumExtension + 1e-8 < normalizedMinimumExtension)
            {
                return Enumerable.Empty<double>();
            }

            return new[] { normalizedMinimumExtension, normalizedMaximumExtension }
                .Concat(criticalExtensions ?? Enumerable.Empty<double>())
                .Where(extensionValue => extensionValue >= normalizedMinimumExtension - 1e-8 &&
                                         extensionValue <= normalizedMaximumExtension + 1e-8)
                .Select(extensionValue => Math.Round(extensionValue, 6))
                .Distinct()
                .OrderBy(extensionValue => extensionValue);
        }

        private static IEnumerable<double> GetExtremeSearchValue(
            double minimumExtension,
            double maximumExtension)
        {
            var normalizedMinimumExtension = Math.Max(0d, minimumExtension);
            var normalizedMaximumExtension = Math.Max(0d, maximumExtension);
            return normalizedMaximumExtension + 1e-8 < normalizedMinimumExtension
                ? Enumerable.Empty<double>()
                : new[] { Math.Round(normalizedMinimumExtension, 6) };
        }

        private static bool AreParallel(Vector3d firstDirection, Vector3d secondDirection)
        {
            return firstDirection.Normalized.Cross(secondDirection.Normalized).Norm < 1e-6;
        }

        private static void AddPoint(ICollection<Point3d> targetPoints, Point3d sourcePoint)
        {
            var targetPointList = targetPoints as IList<Point3d>;
            if (targetPointList != null && targetPointList.Count > 0 &&
                targetPointList[targetPointList.Count - 1].DistanceTo(sourcePoint) <= 1e-8)
            {
                return;
            }

            targetPoints.Add(Copy(sourcePoint));
        }

        private static List<Point3d> CopyPoints(IEnumerable<Point3d> sourcePoints)
        {
            return sourcePoints.Select(Copy).ToList();
        }
    }

    public sealed class LinkageRouteConversionEvaluation
    {
        public bool IsFeasible { get; set; }
        public double ViolationDepth { get; set; }
        public double MinimumClearance { get; set; }
        public double ActualCopperLength { get; set; }
        public string Diagnostic { get; set; }
    }

    public sealed class LinkageRouteConversionSearchResult
    {
        public LinkageRouteConversionCandidate Candidate { get; set; }
        public LinkageRouteConversionEvaluation Evaluation { get; set; }
        public int EvaluatedCandidateCount { get; set; }
    }

    public static class LinkageRouteContinuousConversionSearch
    {
        public static LinkageRouteConversionSearchResult FindBestCandidate(
            IList<Point3d> axisCenterline,
            LinkageRouteConversionContext conversionContext,
            Func<LinkageRouteConversionCandidate, LinkageRouteConversionEvaluation> candidateEvaluator)
        {
            if (conversionContext == null)
            {
                throw new ArgumentNullException(nameof(conversionContext));
            }

            if (candidateEvaluator == null)
            {
                throw new ArgumentNullException(nameof(candidateEvaluator));
            }

            if (conversionContext.RequireAxisAlignedRoutes)
            {
                return null;
            }

            LinkageRouteConversionSearchResult bestSearchResult = null;
            var evaluatedParameterKeys = new HashSet<string>(StringComparer.Ordinal);
            var evaluatedCandidateCount = 0;
            foreach (var conversionCandidate in LinkageRouteConversionCandidate
                .CreateContinuousCandidates(axisCenterline, conversionContext)
                .Where(candidate => candidate.IsConverted))
            {
                EvaluateCandidate(
                    conversionCandidate,
                    candidateEvaluator,
                    evaluatedParameterKeys,
                    ref evaluatedCandidateCount,
                    ref bestSearchResult);
            }

            if (bestSearchResult == null)
            {
                return null;
            }

            if (conversionContext.UseTerminalTrimRule)
            {
                bestSearchResult.EvaluatedCandidateCount = evaluatedCandidateCount;
                return bestSearchResult;
            }

            var startRange = Math.Max(0d,
                conversionContext.StartMaximumExtension - conversionContext.StartMinimumExtension);
            var endRange = Math.Max(0d,
                conversionContext.EndMaximumExtension - conversionContext.EndMinimumExtension);
            var adaptiveStep = Math.Max(startRange, endRange) / 2d;
            var minimumAdaptiveStep = Math.Max(1e-4d, conversionContext.MinimumAdaptiveStep);
            while (adaptiveStep + 1e-9 >= minimumAdaptiveStep)
            {
                var centerStartExtension = bestSearchResult.Candidate.StartExtension;
                var centerEndExtension = bestSearchResult.Candidate.EndExtension;
                foreach (var startDelta in new[] { -adaptiveStep, 0d, adaptiveStep })
                {
                    foreach (var endDelta in new[] { -adaptiveStep, 0d, adaptiveStep })
                    {
                        var startExtension = Clamp(
                            centerStartExtension + startDelta,
                            conversionContext.StartMinimumExtension,
                            conversionContext.StartMaximumExtension);
                        var endExtension = Clamp(
                            centerEndExtension + endDelta,
                            conversionContext.EndMinimumExtension,
                            conversionContext.EndMaximumExtension);
                        var adaptiveCandidate = LinkageRouteConversionCandidate.CreateAtParameters(
                            axisCenterline, startExtension, endExtension);
                        EvaluateCandidate(
                            adaptiveCandidate,
                            candidateEvaluator,
                            evaluatedParameterKeys,
                            ref evaluatedCandidateCount,
                            ref bestSearchResult);
                    }
                }

                adaptiveStep /= 2d;
            }

            bestSearchResult.EvaluatedCandidateCount = evaluatedCandidateCount;
            return bestSearchResult;
        }

        private static void EvaluateCandidate(
            LinkageRouteConversionCandidate conversionCandidate,
            Func<LinkageRouteConversionCandidate, LinkageRouteConversionEvaluation> candidateEvaluator,
            ISet<string> evaluatedParameterKeys,
            ref int evaluatedCandidateCount,
            ref LinkageRouteConversionSearchResult bestSearchResult)
        {
            if (conversionCandidate == null)
            {
                return;
            }

            var parameterKey = Math.Round(conversionCandidate.StartExtension, 6) + ":" +
                Math.Round(conversionCandidate.EndExtension, 6) + ":" +
                string.Join("|", conversionCandidate.Centerline.Select(centerlinePoint =>
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0:F6},{1:F6},{2:F6}",
                        centerlinePoint.X,
                        centerlinePoint.Y,
                        centerlinePoint.Z)));
            if (!evaluatedParameterKeys.Add(parameterKey))
            {
                return;
            }

            var conversionEvaluation = candidateEvaluator(conversionCandidate) ??
                new LinkageRouteConversionEvaluation
                {
                    IsFeasible = false,
                    ViolationDepth = double.PositiveInfinity,
                    ActualCopperLength = double.PositiveInfinity
                };
            evaluatedCandidateCount++;
            if (bestSearchResult == null || IsBetter(conversionEvaluation, bestSearchResult.Evaluation))
            {
                bestSearchResult = new LinkageRouteConversionSearchResult
                {
                    Candidate = conversionCandidate,
                    Evaluation = conversionEvaluation,
                    EvaluatedCandidateCount = evaluatedCandidateCount
                };
            }
        }

        private static bool IsBetter(
            LinkageRouteConversionEvaluation candidateEvaluation,
            LinkageRouteConversionEvaluation currentEvaluation)
        {
            if (candidateEvaluation.IsFeasible != currentEvaluation.IsFeasible)
            {
                return candidateEvaluation.IsFeasible;
            }

            if (Math.Abs(candidateEvaluation.ViolationDepth - currentEvaluation.ViolationDepth) > 1e-8)
            {
                return candidateEvaluation.ViolationDepth < currentEvaluation.ViolationDepth;
            }

            return candidateEvaluation.ActualCopperLength < currentEvaluation.ActualCopperLength;
        }

        private static double Clamp(double sourceValue, double minimumValue, double maximumValue)
        {
            return Math.Max(minimumValue, Math.Min(maximumValue, sourceValue));
        }
    }
}
