using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>叠排成员与固定端成员在一个端部厚度方向上的相邻关系。</summary>
    public sealed class LinkageRouteStackTerminalContact
    {
        public int GeneratedMemberIndex { get; set; }
        public int FixedMemberIndex { get; set; }
    }

    /// <summary>已确定的多始端水平连接排输出位置。</summary>
    public sealed class LinkageRouteTerminalBridgePlacement
    {
        public int GeneratedMemberIndex { get; set; }
        public int SideSign { get; set; }
        public double Thickness { get; set; }
    }

    /// <summary>
    /// 一个端部夹层候选。BaseCenterOffset 以端部第一个有效固定排中心面为零点，
    /// GeneratedMemberOffsets 是各成员相对该路径基准的端部偏移；普通层位
    /// 以叠排中心为零点，无垫块局部闭缝后偏移均值可以不为零。
    /// </summary>
    public sealed class LinkageRouteStackTerminalPlacement
    {
        public string Signature { get; set; }
        public double BaseCenterOffset { get; set; }
        public IList<double> GeneratedMemberOffsets { get; set; } = new List<double>();
        public IList<LinkageRouteStackTerminalContact> Contacts { get; set; } =
            new List<LinkageRouteStackTerminalContact>();
        /// <summary>
        /// 无垫块局部闭缝或双始端桥排插入时实际改变过端部层位的生成成员。
        /// 未列出的柜顶成员保留既有无垫块模式层位，不扩大闭缝范围。
        /// </summary>
        public IList<int> SpacerlessMovedGeneratedMemberIndices { get; set; } =
            new List<int>();
        /// <summary>
        /// 双始端水平搭接时，实际搭接到横向桥排的生成成员索引。
        /// null 表示当前端部不需要横向桥排。
        /// </summary>
        public int? DoubleStartHorizontalBridgeGeneratedMemberIndex { get; set; }
        /// <summary>横向桥排所在的固定排厚面方向，取值为 -1 或 +1。</summary>
        public int DoubleStartHorizontalBridgeSideSign { get; set; }
        /// <summary>横向桥排单根排厚。</summary>
        public double DoubleStartHorizontalBridgeThickness { get; set; }
        /// <summary>多始端水平搭接时两侧独立连接排的位置集合。</summary>
        public IList<LinkageRouteTerminalBridgePlacement> DoubleStartHorizontalBridgePlacements { get; set; } =
            new List<LinkageRouteTerminalBridgePlacement>();
    }

    /// <summary>
    /// 只负责叠排层位：数量少的一组连续插入数量多的一组相邻缝隙；数量相等时
    /// 生成两种交错次序。普通层位不可行时，紧邻固定层作为只读厚向
    /// 组件参与夹层求解。不会创建垫块，也不会参与 XYZ 路径拓扑枚举。
    /// </summary>
    public static class LinkageRouteStackPlacementSolver
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.PointCoincidence;
        private const double PlacementMatchingToleranceMultiplier = 10d;

        public static IReadOnlyList<LinkageRouteStackTerminalPlacement> ResolveTerminalPlacements(
            LinkageTerminalAssembly terminalAssembly,
            int generatedStackCount,
            double generatedSingleBusbarThickness,
            double terminalAdditionalSpace,
            double middleAdditionalSpace,
            bool useSpacerlessTerminalLap = false,
            bool closeFixedTerminalThicknessDifferenceGaps = true)
        {
            if (terminalAssembly == null)
            {
                throw new ArgumentNullException(nameof(terminalAssembly));
            }

            if (generatedStackCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generatedStackCount));
            }

            if (generatedSingleBusbarThickness <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(generatedSingleBusbarThickness));
            }

            if (terminalAdditionalSpace < 0d || middleAdditionalSpace < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(terminalAdditionalSpace),
                    "叠排端部和中间附加间距不能为负数。" );
            }

            if (!TryBuildFixedLayers(
                    terminalAssembly,
                    out var fixedLayerCollection,
                    out _))
            {
                return Array.Empty<LinkageRouteStackTerminalPlacement>();
            }

            var generatedMiddlePitch = generatedSingleBusbarThickness + Math.Max(
                generatedSingleBusbarThickness,
                middleAdditionalSpace);
            var resolvedPlacements = new List<LinkageRouteStackTerminalPlacement>();
            if (generatedStackCount < fixedLayerCollection.Count)
            {
                BuildFixedHostPlacements(
                    resolvedPlacements,
                    fixedLayerCollection,
                    generatedStackCount,
                    generatedSingleBusbarThickness,
                    terminalAdditionalSpace);
            }
            else if (generatedStackCount > fixedLayerCollection.Count)
            {
                BuildGeneratedHostPlacements(
                    resolvedPlacements,
                    fixedLayerCollection,
                    generatedStackCount,
                    generatedSingleBusbarThickness,
                    terminalAdditionalSpace,
                    generatedMiddlePitch);
            }
            else
            {
                BuildEqualCountPlacements(
                    resolvedPlacements,
                    fixedLayerCollection,
                    generatedSingleBusbarThickness,
                    terminalAdditionalSpace);
            }

            if (resolvedPlacements.Count == 0 && fixedLayerCollection.Count >= 2)
            {
                BuildCompactFixedLayerPlacements(
                    resolvedPlacements, fixedLayerCollection, generatedStackCount,
                    generatedSingleBusbarThickness, terminalAdditionalSpace,
                    generatedMiddlePitch);
            }

            var distinctResolvedPlacements = resolvedPlacements
                .GroupBy(stackPlacement => stackPlacement.Signature, StringComparer.Ordinal)
                .Select(stackPlacementGroup => stackPlacementGroup.First())
                .OrderBy(stackPlacement => Math.Abs(stackPlacement.BaseCenterOffset))
                .ThenBy(stackPlacement => stackPlacement.BaseCenterOffset)
                .ToList();
            if (!useSpacerlessTerminalLap)
            {
                return distinctResolvedPlacements;
            }

            return distinctResolvedPlacements
                .SelectMany(sourceTerminalPlacement =>
                    CreateLocallyPackedSpacerlessPlacements(
                        terminalAssembly,
                        fixedLayerCollection,
                        sourceTerminalPlacement,
                        generatedSingleBusbarThickness,
                        closeFixedTerminalThicknessDifferenceGaps))
                .GroupBy(locallyPackedTerminalPlacement =>
                    locallyPackedTerminalPlacement.Signature,
                    StringComparer.Ordinal)
                .Select(locallyPackedPlacementGroup =>
                    locallyPackedPlacementGroup.First())
                .OrderBy(locallyPackedTerminalPlacement => Math.Abs(
                    locallyPackedTerminalPlacement.BaseCenterOffset))
                .ThenBy(locallyPackedTerminalPlacement =>
                    locallyPackedTerminalPlacement.BaseCenterOffset)
                .ToList();
        }

        /// <summary>
        /// 在完整普通方案已经选定具体端部夹层后，只对该夹层创建无垫块
        /// 输出层位。该入口不重新枚举路径基准，也不会改变普通候选顺序。
        /// </summary>
        internal static bool TryCreateSpacerlessOutputPlacement(
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement ordinaryTerminalPlacement,
            double generatedSingleBusbarThickness,
            bool closeFixedTerminalThicknessDifferenceGaps,
            out LinkageRouteStackTerminalPlacement spacerlessOutputPlacement)
        {
            spacerlessOutputPlacement = null;
            if (terminalAssembly == null || ordinaryTerminalPlacement == null ||
                generatedSingleBusbarThickness <= 0d ||
                !TryBuildFixedLayers(
                    terminalAssembly,
                    out var orderedFixedTerminalLayers,
                    out _))
            {
                return false;
            }

            spacerlessOutputPlacement =
                CreateLocallyPackedSpacerlessPlacements(
                        terminalAssembly,
                        orderedFixedTerminalLayers,
                        ordinaryTerminalPlacement,
                        generatedSingleBusbarThickness,
                        closeFixedTerminalThicknessDifferenceGaps)
                    .FirstOrDefault();
            return spacerlessOutputPlacement != null;
        }

        private static IReadOnlyList<LinkageRouteStackTerminalPlacement>
            CreateLocallyPackedSpacerlessPlacements(
            LinkageTerminalAssembly terminalAssembly,
            IReadOnlyList<FixedLayer> orderedFixedTerminalLayers,
            LinkageRouteStackTerminalPlacement sourceTerminalPlacement,
            double generatedSingleBusbarThickness,
            bool closeFixedTerminalThicknessDifferenceGaps)
        {
            if (sourceTerminalPlacement?.GeneratedMemberOffsets == null ||
                sourceTerminalPlacement.GeneratedMemberOffsets.Count == 0)
            {
                return Array.Empty<LinkageRouteStackTerminalPlacement>();
            }

            var sourceGeneratedCenterCoordinates = sourceTerminalPlacement
                .GeneratedMemberOffsets
                .Select(generatedMemberOffset =>
                    sourceTerminalPlacement.BaseCenterOffset +
                    generatedMemberOffset)
                .ToArray();
            var packedGeneratedCenterCoordinateOptions = new List<double[]>
            {
                sourceGeneratedCenterCoordinates.ToArray()
            };
            var generatedMembersByRegion = Enumerable
                .Range(0, orderedFixedTerminalLayers.Count + 1)
                .ToDictionary(
                    terminalRegionIndex => terminalRegionIndex,
                    terminalRegionIndex => new List<int>());
            for (var generatedMemberIndex = 0;
                generatedMemberIndex < sourceGeneratedCenterCoordinates.Length;
                generatedMemberIndex++)
            {
                if (!TryResolveGeneratedTerminalRegion(
                        sourceGeneratedCenterCoordinates[generatedMemberIndex],
                        generatedSingleBusbarThickness,
                        orderedFixedTerminalLayers,
                        out var terminalRegionIndex))
                {
                    return Array.Empty<LinkageRouteStackTerminalPlacement>();
                }

                generatedMembersByRegion[terminalRegionIndex].Add(
                    generatedMemberIndex);
            }

            foreach (var generatedRegionEntry in generatedMembersByRegion)
            {
                var orderedGeneratedMemberIndices = generatedRegionEntry.Value
                    .OrderBy(generatedMemberIndex =>
                        sourceGeneratedCenterCoordinates[generatedMemberIndex])
                    .ToList();
                if (orderedGeneratedMemberIndices.Count == 0)
                {
                    continue;
                }

                var regionPlacementOptions = BuildPackedRegionPlacementOptions(
                    generatedRegionEntry.Key,
                    orderedGeneratedMemberIndices,
                    sourceGeneratedCenterCoordinates,
                    generatedSingleBusbarThickness,
                    orderedFixedTerminalLayers,
                    closeFixedTerminalThicknessDifferenceGaps);
                var scoredRegionPlacements = regionPlacementOptions
                    .Select(regionCenterCoordinates => new
                    {
                        CenterCoordinates = regionCenterCoordinates,
                        PreservedExactContactCount =
                            CountPreservedExactContacts(
                                terminalAssembly,
                                orderedFixedTerminalLayers,
                                sourceTerminalPlacement.Contacts,
                                orderedGeneratedMemberIndices,
                                regionCenterCoordinates,
                                generatedSingleBusbarThickness),
                        TotalDisplacement = orderedGeneratedMemberIndices
                            .Select((generatedMemberIndex, regionMemberIndex) =>
                                Math.Abs(
                                    regionCenterCoordinates[regionMemberIndex] -
                                    sourceGeneratedCenterCoordinates[
                                        generatedMemberIndex]))
                            .Sum(),
                        PreservedNearestFixedBoundaryDistance =
                            MeasurePreservedNearestFixedBoundaryDistance(
                                generatedRegionEntry.Key,
                                orderedGeneratedMemberIndices,
                                sourceGeneratedCenterCoordinates,
                                regionCenterCoordinates,
                                generatedSingleBusbarThickness,
                                orderedFixedTerminalLayers)
                    })
                    // 原普通夹排候选已经确定了实际夹排侧。先保留该接触，
                    // 再在同一接触拓扑内选择移动量最小的闭缝方式，不能因
                    // 全组位移更小就把成员改压到另一张固定端搭接面。
                    .OrderByDescending(regionPlacementOption =>
                        regionPlacementOption.PreservedExactContactCount)
                    .ThenBy(regionPlacementOption =>
                        regionPlacementOption.TotalDisplacement)
                    // 总位移相同的镜像候选必须保留靠本 region 最近固定端
                    // 边界的内层锚点；不能按绝对小坐标把下侧组件推离 fixed。
                    .ThenBy(regionPlacementOption =>
                        regionPlacementOption
                            .PreservedNearestFixedBoundaryDistance)
                    .ThenBy(regionPlacementOption =>
                        regionPlacementOption.CenterCoordinates[0])
                    .ToList();
                if (scoredRegionPlacements.Count == 0)
                {
                    return Array.Empty<LinkageRouteStackTerminalPlacement>();
                }

                // 只取最能保留原夹排接触、且移动总量最小的真实搭接面；
                // 完全等价时再按厚向较小坐标稳定决胜，避免为同一路径
                // 基准制造不可区分的重复 placement。
                var selectedRegionPlacement = scoredRegionPlacements[0];
                var nextPackedGeneratedCenterCoordinateOptions = new
                    List<double[]>();
                foreach (var packedGeneratedCenterCoordinates in
                    packedGeneratedCenterCoordinateOptions)
                {
                    var nextPackedGeneratedCenterCoordinates =
                        packedGeneratedCenterCoordinates.ToArray();
                    for (var regionMemberIndex = 0;
                        regionMemberIndex <
                            orderedGeneratedMemberIndices.Count;
                        regionMemberIndex++)
                    {
                        nextPackedGeneratedCenterCoordinates[
                            orderedGeneratedMemberIndices[
                                regionMemberIndex]] =
                            selectedRegionPlacement.CenterCoordinates[
                                regionMemberIndex];
                    }

                    nextPackedGeneratedCenterCoordinateOptions.Add(
                        nextPackedGeneratedCenterCoordinates);
                }

                packedGeneratedCenterCoordinateOptions =
                    nextPackedGeneratedCenterCoordinateOptions;
            }

            return packedGeneratedCenterCoordinateOptions
                .GroupBy(packedGeneratedCenterCoordinates => string.Join(
                    ",",
                    packedGeneratedCenterCoordinates.Select(
                        generatedCenterCoordinate =>
                            generatedCenterCoordinate.ToString(
                                "F6",
                                CultureInfo.InvariantCulture))),
                    StringComparer.Ordinal)
                .Select(packedCoordinateGroup => packedCoordinateGroup.First())
                .Select(packedGeneratedCenterCoordinates => new
                    LinkageRouteStackTerminalPlacement
                    {
                        Signature = sourceTerminalPlacement.Signature +
                            "|SPACERLESS-LOCAL:" + string.Join(
                                ",",
                                packedGeneratedCenterCoordinates.Select(
                                    generatedCenterCoordinate =>
                                        generatedCenterCoordinate.ToString(
                                            "F6",
                                            CultureInfo.InvariantCulture))),
                        // 无垫块是路径求出后的端段局部贴合；路径基准必须
                        // 保持普通夹排候选不动，只改各成员相对基准的端部层位。
                        BaseCenterOffset = sourceTerminalPlacement
                            .BaseCenterOffset,
                        GeneratedMemberOffsets =
                            packedGeneratedCenterCoordinates
                                .Select(generatedCenterCoordinate =>
                                    generatedCenterCoordinate -
                                    sourceTerminalPlacement.BaseCenterOffset)
                                .ToList(),
                        SpacerlessMovedGeneratedMemberIndices = Enumerable
                            .Range(
                                0,
                                packedGeneratedCenterCoordinates.Length)
                            .Where(generatedMemberIndex => Math.Abs(
                                packedGeneratedCenterCoordinates[
                                    generatedMemberIndex] -
                                sourceGeneratedCenterCoordinates[
                                    generatedMemberIndex]) >
                                    GeometryTolerance)
                            .ToList(),
                        DoubleStartHorizontalBridgeGeneratedMemberIndex =
                            sourceTerminalPlacement
                                .DoubleStartHorizontalBridgeGeneratedMemberIndex,
                        DoubleStartHorizontalBridgeSideSign =
                            sourceTerminalPlacement
                                .DoubleStartHorizontalBridgeSideSign,
                        DoubleStartHorizontalBridgeThickness =
                            sourceTerminalPlacement
                                .DoubleStartHorizontalBridgeThickness,
                        DoubleStartHorizontalBridgePlacements =
                            (sourceTerminalPlacement.DoubleStartHorizontalBridgePlacements ??
                                new List<LinkageRouteTerminalBridgePlacement>())
                            .Select(bridgePlacement => new LinkageRouteTerminalBridgePlacement
                            {
                                GeneratedMemberIndex = bridgePlacement.GeneratedMemberIndex,
                                SideSign = bridgePlacement.SideSign,
                                Thickness = bridgePlacement.Thickness
                            }).ToList(),
                        Contacts = BuildDirectTerminalContacts(
                            terminalAssembly,
                            orderedFixedTerminalLayers,
                            packedGeneratedCenterCoordinates,
                            generatedSingleBusbarThickness,
                            sourceTerminalPlacement.Contacts)
                    })
                .ToList();
        }

        private static double MeasurePreservedNearestFixedBoundaryDistance(
            int terminalRegionIndex,
            IReadOnlyList<int> orderedGeneratedMemberIndices,
            IReadOnlyList<double> sourceGeneratedCenterCoordinates,
            IReadOnlyList<double> candidateRegionCenterCoordinates,
            double generatedSingleBusbarThickness,
            IReadOnlyList<FixedLayer> orderedFixedTerminalLayers)
        {
            var hasLowerFixedBoundary = terminalRegionIndex > 0;
            var hasUpperFixedBoundary = terminalRegionIndex <
                orderedFixedTerminalLayers.Count;
            var lowerFixedBoundaryCoordinate = hasLowerFixedBoundary
                ? orderedFixedTerminalLayers[terminalRegionIndex - 1]
                    .CenterPosition +
                    orderedFixedTerminalLayers[terminalRegionIndex - 1]
                        .Thickness / 2d
                : double.NegativeInfinity;
            var upperFixedBoundaryCoordinate = hasUpperFixedBoundary
                ? orderedFixedTerminalLayers[terminalRegionIndex]
                    .CenterPosition -
                    orderedFixedTerminalLayers[terminalRegionIndex]
                        .Thickness / 2d
                : double.PositiveInfinity;
            var nearestFixedBoundaryDistance = double.PositiveInfinity;
            for (var regionMemberIndex = 0;
                regionMemberIndex < orderedGeneratedMemberIndices.Count;
                regionMemberIndex++)
            {
                var generatedMemberIndex = orderedGeneratedMemberIndices[
                    regionMemberIndex];
                var sourceGeneratedCenterCoordinate =
                    sourceGeneratedCenterCoordinates[generatedMemberIndex];
                if (Math.Abs(
                        candidateRegionCenterCoordinates[regionMemberIndex] -
                        sourceGeneratedCenterCoordinate) > GeometryTolerance)
                {
                    continue;
                }

                var sourceGeneratedMinimumCoordinate =
                    sourceGeneratedCenterCoordinate -
                    generatedSingleBusbarThickness / 2d;
                var sourceGeneratedMaximumCoordinate =
                    sourceGeneratedCenterCoordinate +
                    generatedSingleBusbarThickness / 2d;
                if (hasLowerFixedBoundary)
                {
                    nearestFixedBoundaryDistance = Math.Min(
                        nearestFixedBoundaryDistance,
                        Math.Abs(
                            sourceGeneratedMinimumCoordinate -
                            lowerFixedBoundaryCoordinate));
                }

                if (hasUpperFixedBoundary)
                {
                    nearestFixedBoundaryDistance = Math.Min(
                        nearestFixedBoundaryDistance,
                        Math.Abs(
                            upperFixedBoundaryCoordinate -
                            sourceGeneratedMaximumCoordinate));
                }
            }

            return nearestFixedBoundaryDistance;
        }

        private static bool TryResolveGeneratedTerminalRegion(
            double generatedCenterCoordinate,
            double generatedSingleBusbarThickness,
            IReadOnlyList<FixedLayer> orderedFixedTerminalLayers,
            out int terminalRegionIndex)
        {
            terminalRegionIndex = 0;
            var generatedMinimumCoordinate = generatedCenterCoordinate -
                generatedSingleBusbarThickness / 2d;
            var generatedMaximumCoordinate = generatedCenterCoordinate +
                generatedSingleBusbarThickness / 2d;
            foreach (var fixedTerminalLayer in orderedFixedTerminalLayers)
            {
                var fixedMinimumCoordinate = fixedTerminalLayer.CenterPosition -
                    fixedTerminalLayer.Thickness / 2d;
                var fixedMaximumCoordinate = fixedTerminalLayer.CenterPosition +
                    fixedTerminalLayer.Thickness / 2d;
                if (generatedMaximumCoordinate >
                        fixedMinimumCoordinate + GeometryTolerance &&
                    generatedMinimumCoordinate <
                        fixedMaximumCoordinate - GeometryTolerance)
                {
                    return false;
                }

                if (fixedMaximumCoordinate <=
                    generatedMinimumCoordinate + GeometryTolerance)
                {
                    terminalRegionIndex++;
                }
            }

            return true;
        }

        private static IReadOnlyList<IReadOnlyList<double>>
            BuildPackedRegionPlacementOptions(
            int terminalRegionIndex,
            IReadOnlyList<int> orderedGeneratedMemberIndices,
            IReadOnlyList<double> sourceGeneratedCenterCoordinates,
            double generatedSingleBusbarThickness,
            IReadOnlyList<FixedLayer> orderedFixedTerminalLayers,
            bool closeFixedTerminalThicknessDifferenceGaps)
        {
            var packedRegionPlacementOptions = new List<IReadOnlyList<double>>();
            var generatedMemberCount = orderedGeneratedMemberIndices.Count;
            var hasLowerFixedBoundary = terminalRegionIndex > 0;
            var hasUpperFixedBoundary = terminalRegionIndex <
                orderedFixedTerminalLayers.Count;
            var lowerFixedBoundaryCoordinate = hasLowerFixedBoundary
                ? orderedFixedTerminalLayers[terminalRegionIndex - 1]
                    .CenterPosition +
                    orderedFixedTerminalLayers[terminalRegionIndex - 1]
                    .Thickness / 2d
                : double.NegativeInfinity;
            var upperFixedBoundaryCoordinate = hasUpperFixedBoundary
                ? orderedFixedTerminalLayers[terminalRegionIndex]
                    .CenterPosition -
                    orderedFixedTerminalLayers[terminalRegionIndex]
                    .Thickness / 2d
                : double.PositiveInfinity;

            if (!closeFixedTerminalThicknessDifferenceGaps)
            {
                // 本分支保留既有无垫块模式的柜顶层位，只局部闭合原来
                // 会生成通用垫块的“生成排—生成排”空缝，因此以原成员
                // 为锚逐层贴合，不能把整个区域强行吸附到某一侧固定排。
                for (var anchorRegionMemberIndex = 0;
                    anchorRegionMemberIndex < generatedMemberCount;
                    anchorRegionMemberIndex++)
                {
                    var anchorGeneratedMemberIndex =
                        orderedGeneratedMemberIndices[anchorRegionMemberIndex];
                    var anchorGeneratedCenterCoordinate =
                        sourceGeneratedCenterCoordinates[
                            anchorGeneratedMemberIndex];
                    AddPackedRegionPlacementOption(
                        packedRegionPlacementOptions,
                        Enumerable.Range(0, generatedMemberCount)
                            .Select(regionMemberIndex =>
                                anchorGeneratedCenterCoordinate +
                                (regionMemberIndex - anchorRegionMemberIndex) *
                                generatedSingleBusbarThickness)
                            .ToList(),
                        lowerFixedBoundaryCoordinate,
                        upperFixedBoundaryCoordinate,
                        generatedSingleBusbarThickness);
                }

                return packedRegionPlacementOptions
                    .GroupBy(regionPlacement => string.Join(
                        ",",
                        regionPlacement.Select(regionCenterCoordinate =>
                            regionCenterCoordinate.ToString(
                                "F6",
                                CultureInfo.InvariantCulture))),
                        StringComparer.Ordinal)
                    .Select(regionPlacementGroup =>
                        regionPlacementGroup.First())
                    .ToList();
            }

            if (hasLowerFixedBoundary)
            {
                AddPackedRegionPlacementOption(
                    packedRegionPlacementOptions,
                    Enumerable.Range(0, generatedMemberCount)
                        .Select(regionMemberIndex =>
                            lowerFixedBoundaryCoordinate +
                            generatedSingleBusbarThickness / 2d +
                            regionMemberIndex * generatedSingleBusbarThickness)
                        .ToList(),
                    lowerFixedBoundaryCoordinate,
                    upperFixedBoundaryCoordinate,
                    generatedSingleBusbarThickness);
            }

            if (hasUpperFixedBoundary)
            {
                AddPackedRegionPlacementOption(
                    packedRegionPlacementOptions,
                    Enumerable.Range(0, generatedMemberCount)
                        .Select(regionMemberIndex =>
                            upperFixedBoundaryCoordinate -
                            generatedSingleBusbarThickness / 2d -
                            (generatedMemberCount - 1 - regionMemberIndex) *
                            generatedSingleBusbarThickness)
                        .ToList(),
                    lowerFixedBoundaryCoordinate,
                    upperFixedBoundaryCoordinate,
                    generatedSingleBusbarThickness);
            }

            return packedRegionPlacementOptions
                .GroupBy(regionPlacement => string.Join(
                    ",",
                    regionPlacement.Select(regionCenterCoordinate =>
                        regionCenterCoordinate.ToString(
                            "F6",
                            CultureInfo.InvariantCulture))),
                    StringComparer.Ordinal)
                .Select(regionPlacementGroup => regionPlacementGroup.First())
                .ToList();
        }

        private static void AddPackedRegionPlacementOption(
            ICollection<IReadOnlyList<double>> destinationPlacementOptions,
            IReadOnlyList<double> packedCenterCoordinates,
            double lowerFixedBoundaryCoordinate,
            double upperFixedBoundaryCoordinate,
            double generatedSingleBusbarThickness)
        {
            if (packedCenterCoordinates.Count == 0)
            {
                return;
            }

            var packedMinimumCoordinate = packedCenterCoordinates[0] -
                generatedSingleBusbarThickness / 2d;
            var packedMaximumCoordinate = packedCenterCoordinates[
                packedCenterCoordinates.Count - 1] +
                generatedSingleBusbarThickness / 2d;
            if (packedMinimumCoordinate <
                    lowerFixedBoundaryCoordinate - GeometryTolerance ||
                packedMaximumCoordinate >
                    upperFixedBoundaryCoordinate + GeometryTolerance)
            {
                return;
            }

            destinationPlacementOptions.Add(packedCenterCoordinates);
        }

        private static int CountPreservedExactContacts(
            LinkageTerminalAssembly terminalAssembly,
            IReadOnlyList<FixedLayer> orderedFixedTerminalLayers,
            IEnumerable<LinkageRouteStackTerminalContact> sourceTerminalContacts,
            IReadOnlyList<int> orderedGeneratedMemberIndices,
            IReadOnlyList<double> packedRegionCenterCoordinates,
            double generatedSingleBusbarThickness)
        {
            var packedRegionCenterByMemberIndex = orderedGeneratedMemberIndices
                .Select((generatedMemberIndex, regionMemberIndex) => new
                {
                    GeneratedMemberIndex = generatedMemberIndex,
                    CenterCoordinate = packedRegionCenterCoordinates[
                        regionMemberIndex]
                })
                .ToDictionary(
                    generatedMemberEntry =>
                        generatedMemberEntry.GeneratedMemberIndex,
                    generatedMemberEntry => generatedMemberEntry.CenterCoordinate);
            return (sourceTerminalContacts ??
                    Enumerable.Empty<LinkageRouteStackTerminalContact>())
                .Count(sourceTerminalContact =>
                    packedRegionCenterByMemberIndex.TryGetValue(
                        sourceTerminalContact.GeneratedMemberIndex,
                        out var packedGeneratedCenterCoordinate) &&
                    IsGeneratedMemberTouchingFixedMember(
                        terminalAssembly,
                        orderedFixedTerminalLayers,
                        packedGeneratedCenterCoordinate,
                        generatedSingleBusbarThickness,
                        sourceTerminalContact.FixedMemberIndex));
        }

        private static List<LinkageRouteStackTerminalContact>
            BuildDirectTerminalContacts(
            LinkageTerminalAssembly terminalAssembly,
            IReadOnlyList<FixedLayer> orderedFixedTerminalLayers,
            IReadOnlyList<double> generatedCenterCoordinates,
            double generatedSingleBusbarThickness,
            IEnumerable<LinkageRouteStackTerminalContact> sourceTerminalContacts)
        {
            var directTerminalContacts = new List<
                LinkageRouteStackTerminalContact>();
            for (var generatedMemberIndex = 0;
                generatedMemberIndex < generatedCenterCoordinates.Count;
                generatedMemberIndex++)
            {
                foreach (var fixedTerminalLayer in orderedFixedTerminalLayers)
                {
                    foreach (var fixedMemberIndex in
                        fixedTerminalLayer.FixedMemberIndices)
                    {
                        if (!IsGeneratedMemberTouchingFixedMember(
                                terminalAssembly,
                                orderedFixedTerminalLayers,
                                generatedCenterCoordinates[generatedMemberIndex],
                                generatedSingleBusbarThickness,
                                fixedMemberIndex))
                        {
                            continue;
                        }

                        directTerminalContacts.Add(
                            new LinkageRouteStackTerminalContact
                            {
                                GeneratedMemberIndex = generatedMemberIndex,
                                FixedMemberIndex = fixedMemberIndex
                            });
                    }
                }
            }

            var sourceContactKeys = new HashSet<string>(
                (sourceTerminalContacts ??
                    Enumerable.Empty<LinkageRouteStackTerminalContact>())
                .Select(sourceTerminalContact => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}",
                    sourceTerminalContact.GeneratedMemberIndex,
                    sourceTerminalContact.FixedMemberIndex)),
                StringComparer.Ordinal);
            var sourceContactGeneratedMemberIndices = new HashSet<int>(
                (sourceTerminalContacts ??
                    Enumerable.Empty<LinkageRouteStackTerminalContact>())
                .Select(sourceTerminalContact =>
                    sourceTerminalContact.GeneratedMemberIndex));
            return directTerminalContacts
                .GroupBy(terminalContact => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}",
                    terminalContact.GeneratedMemberIndex,
                    terminalContact.FixedMemberIndex))
                .Select(terminalContactGroup => terminalContactGroup.First())
                // 原普通夹排候选的真实接触成员优先，确保双始端桥排
                // 保留原正/负搭接侧；局部闭缝新增的接触随后补入。
                .OrderBy(terminalContact => sourceContactKeys.Contains(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}:{1}",
                        terminalContact.GeneratedMemberIndex,
                        terminalContact.FixedMemberIndex))
                    ? 0
                    : sourceContactGeneratedMemberIndices.Contains(
                        terminalContact.GeneratedMemberIndex)
                        ? 1
                        : 2)
                .ThenBy(terminalContact => terminalContact.GeneratedMemberIndex)
                .ThenBy(terminalContact => terminalContact.FixedMemberIndex)
                .ToList();
        }

        private static bool IsGeneratedMemberTouchingFixedMember(
            LinkageTerminalAssembly terminalAssembly,
            IReadOnlyList<FixedLayer> orderedFixedTerminalLayers,
            double generatedCenterCoordinate,
            double generatedSingleBusbarThickness,
            int fixedMemberIndex)
        {
            if (fixedMemberIndex < 0 ||
                fixedMemberIndex >= terminalAssembly.Members.Count)
            {
                return false;
            }

            var fixedMemberSetting = terminalAssembly.Members[
                fixedMemberIndex]?.FixedBusbar;
            if (fixedMemberSetting == null ||
                fixedMemberSetting.BusBarThickness <= GeometryTolerance)
            {
                return false;
            }

            var matchingFixedTerminalLayer = orderedFixedTerminalLayers
                .FirstOrDefault(fixedTerminalLayer =>
                    fixedTerminalLayer.FixedMemberIndices.Contains(
                        fixedMemberIndex));
            if (matchingFixedTerminalLayer == null)
            {
                return false;
            }

            var generatedMinimumCoordinate = generatedCenterCoordinate -
                generatedSingleBusbarThickness / 2d;
            var generatedMaximumCoordinate = generatedCenterCoordinate +
                generatedSingleBusbarThickness / 2d;
            var fixedMinimumCoordinate = matchingFixedTerminalLayer
                .CenterPosition - fixedMemberSetting.BusBarThickness / 2d;
            var fixedMaximumCoordinate = matchingFixedTerminalLayer
                .CenterPosition + fixedMemberSetting.BusBarThickness / 2d;
            return Math.Abs(
                    generatedMaximumCoordinate - fixedMinimumCoordinate) <=
                    GeometryTolerance ||
                Math.Abs(
                    generatedMinimumCoordinate - fixedMaximumCoordinate) <=
                    GeometryTolerance;
        }

        internal static bool TryMeasureBaseCenterOffset(
            LinkageTerminalAssembly terminalAssembly,
            Point3d generatedBaseEndpoint,
            out double measuredBaseCenterOffset)
        {
            measuredBaseCenterOffset = 0d;
            if (generatedBaseEndpoint == null ||
                !TryBuildFixedLayers(
                    terminalAssembly,
                    out _,
                    out var referenceFixedCenterPoint))
            {
                return false;
            }

            measuredBaseCenterOffset = new Vector3d(
                    referenceFixedCenterPoint,
                    generatedBaseEndpoint)
                .Dot(terminalAssembly.ThicknessDirection.Normalized);
            return true;
        }

        internal static LinkageRouteStackTerminalPlacement FindMatchingPlacement(
            IEnumerable<LinkageRouteStackTerminalPlacement> terminalPlacements,
            double measuredBaseCenterOffset)
        {
            var nearestPlacement = (terminalPlacements ??
                    Enumerable.Empty<LinkageRouteStackTerminalPlacement>())
                .OrderBy(stackPlacement => Math.Abs(
                    stackPlacement.BaseCenterOffset - measuredBaseCenterOffset))
                .FirstOrDefault();
            return nearestPlacement != null && Math.Abs(
                    nearestPlacement.BaseCenterOffset - measuredBaseCenterOffset) <=
                    GeometryTolerance * PlacementMatchingToleranceMultiplier
                ? nearestPlacement
                : null;
        }

        private static void BuildFixedHostPlacements(
            ICollection<LinkageRouteStackTerminalPlacement> destinationPlacements,
            IReadOnlyList<FixedLayer> orderedFixedLayers,
            int generatedStackCount,
            double generatedSingleBusbarThickness,
            double terminalAdditionalSpace)
        {
            // G 根生成排必须占用固定叠排的 G 个连续缝隙。这样 4 夹 2 只有
            // [0,1]、[1,2] 两种，不会产生跨过中间缝隙的 [0,2]。
            var firstGapMaximumIndex = orderedFixedLayers.Count - generatedStackCount - 1;
            for (var firstGapIndex = 0;
                firstGapIndex <= firstGapMaximumIndex;
                firstGapIndex++)
            {
                var generatedMemberPositionOptionSets =
                    new List<IReadOnlyList<GeneratedMemberPositionOption>>(
                        generatedStackCount);
                var placementIsValid = true;
                for (var generatedMemberIndex = 0;
                    generatedMemberIndex < generatedStackCount;
                    generatedMemberIndex++)
                {
                    var lowerFixedLayer = orderedFixedLayers[
                        firstGapIndex + generatedMemberIndex];
                    var upperFixedLayer = orderedFixedLayers[
                        firstGapIndex + generatedMemberIndex + 1];
                    var generatedMemberPositionOptions =
                        BuildBetweenFixedLayerOptions(
                            lowerFixedLayer,
                            upperFixedLayer,
                            generatedSingleBusbarThickness,
                            terminalAdditionalSpace);
                    if (generatedMemberPositionOptions.Count == 0)
                    {
                        placementIsValid = false;
                        break;
                    }

                    generatedMemberPositionOptionSets.Add(
                        generatedMemberPositionOptions);
                }

                if (placementIsValid)
                {
                    BuildIndependentGeneratedMemberPlacements(
                        destinationPlacements,
                        "FIXED-HOST-" + firstGapIndex,
                        generatedMemberPositionOptionSets,
                        0,
                        new List<double>(),
                        new List<LinkageRouteStackTerminalContact>());
                }
            }
        }

        private static void BuildGeneratedHostPlacements(
            ICollection<LinkageRouteStackTerminalPlacement> destinationPlacements,
            IReadOnlyList<FixedLayer> orderedFixedLayers,
            int generatedStackCount,
            double generatedSingleBusbarThickness,
            double terminalAdditionalSpace,
            double generatedMiddlePitch)
        {
            // M 根固定排占用生成叠排的 M 个连续缝隙，候选数为 G-M。
            var firstOccupiedGapMaximumIndex =
                generatedStackCount - orderedFixedLayers.Count - 1;
            for (var firstOccupiedGapIndex = 0;
                firstOccupiedGapIndex <= firstOccupiedGapMaximumIndex;
                firstOccupiedGapIndex++)
            {
                var generatedGapPositionOptionSets =
                    new List<IReadOnlyList<GeneratedGapPositionOption>>(
                        orderedFixedLayers.Count);
                for (var fixedLayerIndex = 0;
                    fixedLayerIndex < orderedFixedLayers.Count;
                    fixedLayerIndex++)
                {
                    generatedGapPositionOptionSets.Add(
                        BuildGeneratedGapOptions(
                            orderedFixedLayers[fixedLayerIndex],
                            generatedSingleBusbarThickness,
                            terminalAdditionalSpace,
                            generatedMiddlePitch));
                }

                BuildGeneratedHostPlacementSequences(
                    destinationPlacements,
                    "GENERATED-HOST-" + firstOccupiedGapIndex,
                    orderedFixedLayers,
                    generatedGapPositionOptionSets,
                    generatedStackCount,
                    firstOccupiedGapIndex,
                    generatedMiddlePitch,
                    0,
                    new List<double>(),
                    new List<LinkageRouteStackTerminalContact>());
            }
        }

        private static void BuildEqualCountPlacements(
            ICollection<LinkageRouteStackTerminalPlacement> destinationPlacements,
            IReadOnlyList<FixedLayer> orderedFixedLayers,
            double generatedSingleBusbarThickness,
            double terminalAdditionalSpace)
        {
            var generatedStackCount = orderedFixedLayers.Count;
            var generatedFirstPositionOptionSets =
                new List<IReadOnlyList<GeneratedMemberPositionOption>>(
                    generatedStackCount)
                {
                    new[]
                    {
                        CreateOutsideFixedLayerOption(
                            orderedFixedLayers[0],
                            generatedSingleBusbarThickness,
                            terminalAdditionalSpace,
                            false)
                    }
                };
            for (var generatedMemberIndex = 1;
                generatedMemberIndex < generatedStackCount;
                generatedMemberIndex++)
            {
                generatedFirstPositionOptionSets.Add(
                    BuildBetweenFixedLayerOptions(
                        orderedFixedLayers[generatedMemberIndex - 1],
                        orderedFixedLayers[generatedMemberIndex],
                        generatedSingleBusbarThickness,
                        terminalAdditionalSpace));
            }

            BuildIndependentGeneratedMemberPlacements(
                destinationPlacements,
                "EQUAL-GENERATED-FIRST",
                generatedFirstPositionOptionSets,
                0,
                new List<double>(),
                new List<LinkageRouteStackTerminalContact>());

            var fixedFirstPositionOptionSets =
                new List<IReadOnlyList<GeneratedMemberPositionOption>>(
                    generatedStackCount);
            for (var generatedMemberIndex = 0;
                generatedMemberIndex + 1 < generatedStackCount;
                generatedMemberIndex++)
            {
                fixedFirstPositionOptionSets.Add(
                    BuildBetweenFixedLayerOptions(
                        orderedFixedLayers[generatedMemberIndex],
                        orderedFixedLayers[generatedMemberIndex + 1],
                        generatedSingleBusbarThickness,
                        terminalAdditionalSpace));
            }

            fixedFirstPositionOptionSets.Add(new[]
            {
                CreateOutsideFixedLayerOption(
                    orderedFixedLayers[orderedFixedLayers.Count - 1],
                    generatedSingleBusbarThickness,
                    terminalAdditionalSpace,
                    true)
            });
            BuildIndependentGeneratedMemberPlacements(
                destinationPlacements,
                "EQUAL-FIXED-FIRST",
                fixedFirstPositionOptionSets,
                0,
                new List<double>(),
                new List<LinkageRouteStackTerminalContact>());
        }

        private static void BuildCompactFixedLayerPlacements(
            ICollection<LinkageRouteStackTerminalPlacement> destinationPlacements,
            IReadOnlyList<FixedLayer> orderedFixedLayers,
            int generatedStackCount,
            double generatedSingleBusbarThickness,
            double terminalAdditionalSpace,
            double generatedMiddlePitch)
        {
            // 小于生成排厚的固定夹缝不能插排。只在普通交错无候选时，
            // 将连续紧邻固定层作为只读厚向组件参加同一夹层求解；
            // 实际 Contacts 仍只指向组件外侧的原始固定排，不移动固定排。
            var compactFixedLayers = new List<FixedLayer>();
            foreach (var nextFixedLayer in orderedFixedLayers)
            {
                if (compactFixedLayers.Count == 0)
                {
                    compactFixedLayers.Add(nextFixedLayer);
                    continue;
                }

                var precedingFixedLayer = compactFixedLayers.Last();
                var precedingMaximum = precedingFixedLayer.CenterPosition +
                    precedingFixedLayer.Thickness / 2d;
                var nextMinimum = nextFixedLayer.CenterPosition -
                    nextFixedLayer.Thickness / 2d;
                var fixedGap = nextMinimum - precedingMaximum;
                if (fixedGap < -GeometryTolerance)
                {
                    // 原始固定铜排发生体积重叠时不能用合并组件掩盖输入问题。
                    return;
                }
                if (fixedGap + GeometryTolerance >= generatedSingleBusbarThickness)
                {
                    compactFixedLayers.Add(nextFixedLayer);
                    continue;
                }

                var compactMinimum = precedingFixedLayer.CenterPosition -
                    precedingFixedLayer.Thickness / 2d;
                var compactMaximum = nextFixedLayer.CenterPosition +
                    nextFixedLayer.Thickness / 2d;
                compactFixedLayers[compactFixedLayers.Count - 1] = new FixedLayer
                {
                    CenterPosition = (compactMinimum + compactMaximum) / 2d,
                    Thickness = compactMaximum - compactMinimum,
                    NegativeContactLayer = SelectContactLayer(
                        precedingFixedLayer, false),
                    PositiveContactLayer = SelectContactLayer(nextFixedLayer, true)
                };
            }
            if (compactFixedLayers.Count == orderedFixedLayers.Count)
            {
                return;
            }

            if (generatedStackCount < compactFixedLayers.Count)
            {
                BuildFixedHostPlacements(destinationPlacements, compactFixedLayers,
                    generatedStackCount, generatedSingleBusbarThickness,
                    terminalAdditionalSpace);
            }
            else if (generatedStackCount > compactFixedLayers.Count)
            {
                BuildGeneratedHostPlacements(destinationPlacements, compactFixedLayers,
                    generatedStackCount, generatedSingleBusbarThickness,
                    terminalAdditionalSpace, generatedMiddlePitch);
            }
            else
            {
                BuildEqualCountPlacements(destinationPlacements, compactFixedLayers,
                    generatedSingleBusbarThickness, terminalAdditionalSpace);
            }
            foreach (var compactPlacement in destinationPlacements)
            {
                compactPlacement.Signature = "COMPACT-FIXED-PACK|" +
                    compactPlacement.Signature;
            }
        }

        private static FixedLayer SelectContactLayer(
            FixedLayer fixedLayer, bool usePositiveSide)
        {
            return (usePositiveSide
                ? fixedLayer.PositiveContactLayer
                : fixedLayer.NegativeContactLayer) ?? fixedLayer;
        }

        private static IReadOnlyList<GeneratedMemberPositionOption>
            BuildBetweenFixedLayerOptions(
            FixedLayer lowerFixedLayer,
            FixedLayer upperFixedLayer,
            double generatedSingleBusbarThickness,
            double terminalAdditionalSpace)
        {
            var minimumGeneratedCenterPosition = lowerFixedLayer.CenterPosition +
                GetContactCenterDistance(
                    lowerFixedLayer.Thickness,
                    generatedSingleBusbarThickness,
                    terminalAdditionalSpace);
            var maximumGeneratedCenterPosition = upperFixedLayer.CenterPosition -
                GetContactCenterDistance(
                    upperFixedLayer.Thickness,
                    generatedSingleBusbarThickness,
                    terminalAdditionalSpace);
            if (minimumGeneratedCenterPosition >
                maximumGeneratedCenterPosition + GeometryTolerance)
            {
                return Array.Empty<GeneratedMemberPositionOption>();
            }

            if (Math.Abs(
                    minimumGeneratedCenterPosition -
                    maximumGeneratedCenterPosition) <= GeometryTolerance)
            {
                return new[]
                {
                    new GeneratedMemberPositionOption
                    {
                        CenterPosition =
                            (minimumGeneratedCenterPosition +
                                maximumGeneratedCenterPosition) / 2d,
                        ContactedFixedLayers = new[]
                        {
                            SelectContactLayer(lowerFixedLayer, true),
                            SelectContactLayer(upperFixedLayer, false)
                        }
                    }
                };
            }

            // 缝隙大于客体排厚时不再错误地放在中点并假定两面同时接触；
            // 与单排双面搭接一致，分别贴住缝隙的两张真实搭接面。
            return new[]
            {
                new GeneratedMemberPositionOption
                {
                    CenterPosition = minimumGeneratedCenterPosition,
                    ContactedFixedLayers = new[] { SelectContactLayer(lowerFixedLayer, true) }
                },
                new GeneratedMemberPositionOption
                {
                    CenterPosition = maximumGeneratedCenterPosition,
                    ContactedFixedLayers = new[] { SelectContactLayer(upperFixedLayer, false) }
                }
            };
        }

        private static GeneratedMemberPositionOption CreateOutsideFixedLayerOption(
            FixedLayer fixedLayer,
            double generatedSingleBusbarThickness,
            double terminalAdditionalSpace,
            bool usePositiveSide)
        {
            var contactCenterDistance = GetContactCenterDistance(
                fixedLayer.Thickness,
                generatedSingleBusbarThickness,
                terminalAdditionalSpace);
            return new GeneratedMemberPositionOption
            {
                CenterPosition = fixedLayer.CenterPosition +
                    (usePositiveSide ? contactCenterDistance : -contactCenterDistance),
                ContactedFixedLayers = new[] { SelectContactLayer(fixedLayer, usePositiveSide) }
            };
        }

        private static IReadOnlyList<GeneratedGapPositionOption>
            BuildGeneratedGapOptions(
                FixedLayer insertedFixedLayer,
                double generatedSingleBusbarThickness,
                double terminalAdditionalSpace,
                double generatedMiddlePitch)
        {
            var contactCenterDistance = GetContactCenterDistance(
                insertedFixedLayer.Thickness,
                generatedSingleBusbarThickness,
                terminalAdditionalSpace);
            var requiredClampedPitch = contactCenterDistance * 2d;
            if (generatedMiddlePitch > requiredClampedPitch + GeometryTolerance)
            {
                return new[]
                {
                    new GeneratedGapPositionOption
                    {
                        LowerGeneratedCenterPosition =
                            insertedFixedLayer.CenterPosition - contactCenterDistance,
                        UpperGeneratedCenterPosition =
                            insertedFixedLayer.CenterPosition - contactCenterDistance +
                                generatedMiddlePitch,
                        ContactedRelativeGeneratedMemberIndices = new[] { 0 }
                    },
                    new GeneratedGapPositionOption
                    {
                        LowerGeneratedCenterPosition =
                            insertedFixedLayer.CenterPosition + contactCenterDistance -
                                generatedMiddlePitch,
                        UpperGeneratedCenterPosition =
                            insertedFixedLayer.CenterPosition + contactCenterDistance,
                        ContactedRelativeGeneratedMemberIndices = new[] { 1 }
                    }
                };
            }

            // 客体排刚好填满默认缝隙时两面同时搭接；客体更厚时把当前端部
            // 缝隙撑到真实夹紧厚度，不能让两根生成排与固定排发生实体穿插。
            return new[]
            {
                new GeneratedGapPositionOption
                {
                    LowerGeneratedCenterPosition =
                        insertedFixedLayer.CenterPosition - contactCenterDistance,
                    UpperGeneratedCenterPosition =
                        insertedFixedLayer.CenterPosition + contactCenterDistance,
                    ContactedRelativeGeneratedMemberIndices = new[] { 0, 1 }
                }
            };
        }

        private static void BuildIndependentGeneratedMemberPlacements(
            ICollection<LinkageRouteStackTerminalPlacement> destinationPlacements,
            string placementLabel,
            IReadOnlyList<IReadOnlyList<GeneratedMemberPositionOption>>
                generatedMemberPositionOptionSets,
            int generatedMemberIndex,
            IList<double> selectedGeneratedCenterPositions,
            IList<LinkageRouteStackTerminalContact> selectedTerminalContacts)
        {
            if (generatedMemberIndex >= generatedMemberPositionOptionSets.Count)
            {
                destinationPlacements.Add(CreatePlacement(
                    placementLabel,
                    selectedGeneratedCenterPositions,
                    selectedTerminalContacts));
                return;
            }

            var currentMemberPositionOptions =
                generatedMemberPositionOptionSets[generatedMemberIndex];
            foreach (var currentMemberPositionOption in currentMemberPositionOptions)
            {
                var nextGeneratedCenterPositions =
                    selectedGeneratedCenterPositions.ToList();
                nextGeneratedCenterPositions.Add(
                    currentMemberPositionOption.CenterPosition);
                var nextTerminalContacts = selectedTerminalContacts.ToList();
                foreach (var contactedFixedLayer in
                    currentMemberPositionOption.ContactedFixedLayers)
                {
                    AddLayerContacts(
                        nextTerminalContacts,
                        generatedMemberIndex,
                        contactedFixedLayer);
                }

                BuildIndependentGeneratedMemberPlacements(
                    destinationPlacements,
                    placementLabel,
                    generatedMemberPositionOptionSets,
                    generatedMemberIndex + 1,
                    nextGeneratedCenterPositions,
                    nextTerminalContacts);
            }
        }

        private static void BuildGeneratedHostPlacementSequences(
            ICollection<LinkageRouteStackTerminalPlacement> destinationPlacements,
            string placementLabel,
            IReadOnlyList<FixedLayer> orderedFixedLayers,
            IReadOnlyList<IReadOnlyList<GeneratedGapPositionOption>>
                generatedGapPositionOptionSets,
            int generatedStackCount,
            int firstOccupiedGapIndex,
            double generatedMiddlePitch,
            int fixedLayerIndex,
            IList<double> occupiedGeneratedCenterPositions,
            IList<LinkageRouteStackTerminalContact> selectedTerminalContacts)
        {
            if (fixedLayerIndex >= generatedGapPositionOptionSets.Count)
            {
                var generatedCenterPositions = new double[generatedStackCount];
                for (var occupiedMemberOffset = 0;
                    occupiedMemberOffset < occupiedGeneratedCenterPositions.Count;
                    occupiedMemberOffset++)
                {
                    generatedCenterPositions[
                        firstOccupiedGapIndex + occupiedMemberOffset] =
                        occupiedGeneratedCenterPositions[occupiedMemberOffset];
                }

                for (var generatedMemberIndex = firstOccupiedGapIndex - 1;
                    generatedMemberIndex >= 0;
                    generatedMemberIndex--)
                {
                    generatedCenterPositions[generatedMemberIndex] =
                        generatedCenterPositions[generatedMemberIndex + 1] -
                        generatedMiddlePitch;
                }

                for (var generatedMemberIndex =
                        firstOccupiedGapIndex + occupiedGeneratedCenterPositions.Count;
                    generatedMemberIndex < generatedStackCount;
                    generatedMemberIndex++)
                {
                    generatedCenterPositions[generatedMemberIndex] =
                        generatedCenterPositions[generatedMemberIndex - 1] +
                        generatedMiddlePitch;
                }

                destinationPlacements.Add(CreatePlacement(
                    placementLabel,
                    generatedCenterPositions,
                    selectedTerminalContacts));
                return;
            }

            foreach (var generatedGapPositionOption in
                generatedGapPositionOptionSets[fixedLayerIndex])
            {
                var nextOccupiedGeneratedCenterPositions =
                    occupiedGeneratedCenterPositions.ToList();
                if (nextOccupiedGeneratedCenterPositions.Count == 0)
                {
                    nextOccupiedGeneratedCenterPositions.Add(
                        generatedGapPositionOption.LowerGeneratedCenterPosition);
                }
                else
                {
                    var precedingSharedGeneratedCenterPosition =
                        nextOccupiedGeneratedCenterPositions[
                            nextOccupiedGeneratedCenterPositions.Count - 1];
                    if (Math.Abs(
                            precedingSharedGeneratedCenterPosition -
                            generatedGapPositionOption.LowerGeneratedCenterPosition) >
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                    {
                        continue;
                    }

                    // 保留已选厚面上的精确坐标，不将两个允许误差相加。
                    // 另一接口的真实厚向误差仍须通过完整搭接及实体终检。
                    if (Math.Abs(precedingSharedGeneratedCenterPosition -
                            generatedGapPositionOption.LowerGeneratedCenterPosition) <=
                        GeometryTolerance)
                    {
                        nextOccupiedGeneratedCenterPositions[
                            nextOccupiedGeneratedCenterPositions.Count - 1] =
                            (precedingSharedGeneratedCenterPosition +
                                generatedGapPositionOption.LowerGeneratedCenterPosition) / 2d;
                    }
                }

                nextOccupiedGeneratedCenterPositions.Add(
                    generatedGapPositionOption.UpperGeneratedCenterPosition);
                var nextTerminalContacts = selectedTerminalContacts.ToList();
                foreach (var contactedRelativeGeneratedMemberIndex in
                    generatedGapPositionOption
                        .ContactedRelativeGeneratedMemberIndices)
                {
                    AddLayerContacts(
                        nextTerminalContacts,
                        firstOccupiedGapIndex + fixedLayerIndex +
                            contactedRelativeGeneratedMemberIndex,
                        SelectContactLayer(orderedFixedLayers[fixedLayerIndex],
                            contactedRelativeGeneratedMemberIndex != 0));
                }

                BuildGeneratedHostPlacementSequences(
                    destinationPlacements,
                    placementLabel,
                    orderedFixedLayers,
                    generatedGapPositionOptionSets,
                    generatedStackCount,
                    firstOccupiedGapIndex,
                    generatedMiddlePitch,
                    fixedLayerIndex + 1,
                    nextOccupiedGeneratedCenterPositions,
                    nextTerminalContacts);
            }
        }

        private static double GetContactCenterDistance(
            double fixedSingleBusbarThickness,
            double generatedSingleBusbarThickness,
            double terminalAdditionalSpace)
        {
            // terminalAdditionalSpace 是一个夹层内的总附加厚度，平均分配到
            // 被夹固定排的两侧；垫块实体后续另行生成。
            return (fixedSingleBusbarThickness + generatedSingleBusbarThickness +
                terminalAdditionalSpace) / 2d;
        }

        private static LinkageRouteStackTerminalPlacement CreatePlacement(
            string placementLabel,
            IEnumerable<double> generatedCenterPositions,
            IEnumerable<LinkageRouteStackTerminalContact> terminalContacts)
        {
            var orderedGeneratedCenterPositions = generatedCenterPositions.ToList();
            var generatedBaseCenterPosition = orderedGeneratedCenterPositions.Average();
            var generatedMemberOffsets = orderedGeneratedCenterPositions
                .Select(generatedCenterPosition =>
                    generatedCenterPosition - generatedBaseCenterPosition)
                .ToList();
            return new LinkageRouteStackTerminalPlacement
            {
                BaseCenterOffset = generatedBaseCenterPosition,
                GeneratedMemberOffsets = generatedMemberOffsets,
                Contacts = terminalContacts
                    .GroupBy(terminalContact => string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}:{1}",
                        terminalContact.GeneratedMemberIndex,
                        terminalContact.FixedMemberIndex))
                    .Select(terminalContactGroup => terminalContactGroup.First())
                    .OrderBy(terminalContact => terminalContact.GeneratedMemberIndex)
                    .ThenBy(terminalContact => terminalContact.FixedMemberIndex)
                    .ToList(),
                Signature = placementLabel + "|" + string.Join(
                    ",",
                    orderedGeneratedCenterPositions.Select(generatedCenterPosition =>
                        generatedCenterPosition.ToString("F6", CultureInfo.InvariantCulture)))
            };
        }

        private static void AddLayerContacts(
            ICollection<LinkageRouteStackTerminalContact> destinationContacts,
            int generatedMemberIndex,
            FixedLayer fixedLayer)
        {
            foreach (var fixedMemberIndex in fixedLayer.FixedMemberIndices)
            {
                destinationContacts.Add(new LinkageRouteStackTerminalContact
                {
                    GeneratedMemberIndex = generatedMemberIndex,
                    FixedMemberIndex = fixedMemberIndex
                });
            }
        }

        private static bool TryBuildFixedLayers(
            LinkageTerminalAssembly terminalAssembly,
            out List<FixedLayer> orderedFixedLayers,
            out Point3d referenceFixedCenterPoint)
        {
            orderedFixedLayers = new List<FixedLayer>();
            referenceFixedCenterPoint = null;
            if (terminalAssembly?.Members == null ||
                terminalAssembly.Members.Count == 0 ||
                terminalAssembly.ThicknessDirection == null ||
                terminalAssembly.ThicknessDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            var terminalThicknessDirection = terminalAssembly.ThicknessDirection.Normalized;
            var usableFixedMembers = terminalAssembly.Members
                .Select((terminalMember, fixedMemberIndex) => new
                {
                    TerminalMember = terminalMember,
                    FixedMemberIndex = fixedMemberIndex
                })
                .Where(fixedMemberEntry =>
                    fixedMemberEntry.TerminalMember?.FixedBusbar?.PointList != null &&
                    fixedMemberEntry.TerminalMember.FixedBusbar.PointList.Count >= 2 &&
                    fixedMemberEntry.TerminalMember.FixedBusbar.BusBarThickness >
                        GeometryTolerance)
                .ToList();
            if (usableFixedMembers.Count == 0)
            {
                return false;
            }

            var sourceReferenceFixedCenterPoint = usableFixedMembers[0]
                .TerminalMember.FixedBusbar.PointList[0];
            var fixedLayerReferenceCenterPoint = new Point3d(
                sourceReferenceFixedCenterPoint.X,
                sourceReferenceFixedCenterPoint.Y,
                sourceReferenceFixedCenterPoint.Z);
            referenceFixedCenterPoint = fixedLayerReferenceCenterPoint;
            var fixedMemberScalars = usableFixedMembers
                .Select(fixedMemberEntry => new FixedMemberScalar
                {
                    FixedMemberIndex = fixedMemberEntry.FixedMemberIndex,
                    CenterPosition = new Vector3d(
                            fixedLayerReferenceCenterPoint,
                            fixedMemberEntry.TerminalMember.FixedBusbar.PointList[0])
                        .Dot(terminalThicknessDirection),
                    Thickness = fixedMemberEntry.TerminalMember.FixedBusbar.BusBarThickness
                })
                .OrderBy(fixedMemberScalar => fixedMemberScalar.CenterPosition)
                .ToList();

            foreach (var fixedMemberScalar in fixedMemberScalars)
            {
                var matchingFixedLayer = orderedFixedLayers.FirstOrDefault(fixedLayer =>
                    Math.Abs(fixedLayer.CenterPosition - fixedMemberScalar.CenterPosition) <=
                    GeometryTolerance);
                if (matchingFixedLayer == null)
                {
                    matchingFixedLayer = new FixedLayer
                    {
                        CenterPosition = fixedMemberScalar.CenterPosition,
                        Thickness = fixedMemberScalar.Thickness
                    };
                    orderedFixedLayers.Add(matchingFixedLayer);
                }
                else
                {
                    matchingFixedLayer.Thickness = Math.Max(
                        matchingFixedLayer.Thickness,
                        fixedMemberScalar.Thickness);
                }

                matchingFixedLayer.FixedMemberIndices.Add(
                    fixedMemberScalar.FixedMemberIndex);
            }

            orderedFixedLayers = orderedFixedLayers
                .OrderBy(fixedLayer => fixedLayer.CenterPosition)
                .ToList();
            return orderedFixedLayers.Count > 0;
        }

        private sealed class GeneratedMemberPositionOption
        {
            public double CenterPosition { get; set; }
            public IReadOnlyList<FixedLayer> ContactedFixedLayers { get; set; } =
                Array.Empty<FixedLayer>();
        }

        private sealed class GeneratedGapPositionOption
        {
            public double LowerGeneratedCenterPosition { get; set; }
            public double UpperGeneratedCenterPosition { get; set; }
            public IReadOnlyList<int> ContactedRelativeGeneratedMemberIndices
                { get; set; } = Array.Empty<int>();
        }

        private sealed class FixedMemberScalar
        {
            public int FixedMemberIndex { get; set; }
            public double CenterPosition { get; set; }
            public double Thickness { get; set; }
        }

        private sealed class FixedLayer
        {
            public double CenterPosition { get; set; }
            public double Thickness { get; set; }
            public FixedLayer NegativeContactLayer { get; set; }
            public FixedLayer PositiveContactLayer { get; set; }
            public IList<int> FixedMemberIndices { get; } = new List<int>();
        }
    }
}
