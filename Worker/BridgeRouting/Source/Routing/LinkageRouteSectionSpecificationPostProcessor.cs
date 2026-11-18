using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// 在候选转换阶段把实际生成排构造成始端搭接排、中间排和柜顶搭接排
    /// 各自的规格；发布后的短搭接/无垫块处理也复用该重建器。
    /// </summary>
    internal static partial class LinkageRouteSectionSpecificationPostProcessor
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.PointCoincidence;

        internal sealed class ProcessingState
        {
            public ISet<int> InternalSupportBoundaryIndices { get; } =
                new HashSet<int>();
            internal IDictionary<int, IList<SectionMemberSetting>>
                SettingsBySectionIndex { get; } =
                    new Dictionary<int, IList<SectionMemberSetting>>();
            internal IList<HorizontalLapMergeResult> HorizontalLapMerges { get; } =
                new List<HorizontalLapMergeResult>();
        }

        internal sealed class SectionMemberSetting
        {
            public int MemberIndex { get; set; }
            public PlanarBendBusbarSetting Setting { get; set; }
        }

        private sealed class InternalSupportPair
        {
            public SectionMemberSetting Preceding { get; set; }
            public SectionMemberSetting Following { get; set; }
        }

        private sealed class InternalSupportLayerInterval
        {
            public SectionMemberSetting SectionMember { get; set; }
            public bool IsPrecedingSection { get; set; }
            public double MinimumCoordinate { get; set; }
            public double MaximumCoordinate { get; set; }
        }

        internal sealed class SectionSpecification
        {
            public string Label { get; set; }
            public double Width { get; set; }
            public double Thickness { get; set; }
            public int StackCount { get; set; }
            public double StartLapWidth { get; set; }
            public double EndLapWidth { get; set; }
        }

        private sealed class HorizontalLapMergeChain
        {
            public IList<int> PartIndices { get; } = new List<int>();
            public int OriginalSegmentStartIndex { get; set; } = -1;
            public int OriginalSegmentEndIndex { get; set; } = -1;
            public IList<Vector3d> DirectionSequence { get; } =
                new List<Vector3d>();
            public string StartRole { get; set; }
            public string EndRole { get; set; }
            public int EffectiveStackCount { get; set; }
            public bool IsContinuous { get; set; }
            public bool ContainsRealTurn { get; set; }
        }

        internal sealed class HorizontalLapMergeResult
        {
            internal IList<int> MergedPartIndices { get; } = new List<int>();
            internal IList<int> RemovedPartIndices { get; } = new List<int>();
            internal IList<int> RemovedBoundaryIndices { get; } = new List<int>();
            internal IList<LinkageStackedRouteMember> RebuiltStackMembers { get; } =
                new List<LinkageStackedRouteMember>();
            internal ISet<string> RemovedSupportBlockKeys { get; } =
                new HashSet<string>(StringComparer.Ordinal);
            internal IList<string> Diagnostics { get; } = new List<string>();
            internal SectionSpecification MergedSpecification { get; set; }
            internal PlanarBendBusbarSetting MergedCenterlinePart { get; set; }
        }

        internal static bool TryApply(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out ProcessingState processingState,
            out string failureReason,
            bool applySingleStackCabinetTopSpecification = false)
        {
            processingState = new ProcessingState();
            failureReason = null;
            if (conversionResult?.CenterlineParts == null ||
                conversionResult.CenterlineParts.Count == 0 ||
                conversionResult.StackMembers == null ||
                conversionResult.StackMembers.Count == 0 ||
                routeRequest == null || routeGroupRequest == null)
            {
                failureReason = "分段排规格后处理缺少普通方案实际铜排。";
                return false;
            }

            // 候选中心线已经按统一中间排规生成。异规格实体化只修正端部
            // 搭接区域和层位，不再把一条连续路径拆成额外的始端 I 段；
            // 真实平面转折仍由原始中心线保留。
            if (!TrySplitSingleCoplanarTerminalSections(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    out var terminalSectionSplitFailureReason))
            {
                // 端部异规格拆分属于输出几何增强，不能把原本已经通过搜索
                // 的候选淘汰。拆分失败时保留原物理段，并把原因写入诊断；
                // 后续仍按原有单段兼容规则继续生成。
                if (!string.IsNullOrWhiteSpace(terminalSectionSplitFailureReason))
                {
                    conversionResult.Diagnostics.Add(
                        terminalSectionSplitFailureReason);
                }
            }

            var mergedSpecificationsByPartIndex =
                new Dictionary<int, SectionSpecification>();
            IList<HorizontalLapMergeChain> horizontalLapMergeChains =
                new List<HorizontalLapMergeChain>();
            if (IsHorizontalLapMergePostProcessingEnabled() &&
                !TryBuildHorizontalLapMergeChains(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    out horizontalLapMergeChains,
                    out var horizontalLapMergeFailureReason))
            {
                // A post-processing merge is an output enhancement.  Keep the
                // already materialized geometry when a chain cannot be
                // reconstructed and retain a diagnostic instead of rejecting
                // an otherwise valid candidate.
                if (!string.IsNullOrWhiteSpace(horizontalLapMergeFailureReason))
                {
                    conversionResult.Diagnostics.Add(
                        horizontalLapMergeFailureReason);
                }
                horizontalLapMergeChains = new List<HorizontalLapMergeChain>();
            }

            foreach (var horizontalLapMergeChain in horizontalLapMergeChains
                .OrderByDescending(chain => chain.PartIndices.FirstOrDefault()))
            {
                HorizontalLapMergeResult horizontalLapMergeResult;
                string mergeFailureReason;
                bool mergeSucceeded;
                try
                {
                    mergeSucceeded = MergeHorizontalLapChain(
                        conversionResult,
                        routeRequest,
                        routeGroupRequest,
                        horizontalLapMergeChain,
                        out horizontalLapMergeResult,
                        out mergeFailureReason);
                }
                catch (Exception mergeException)
                {
                    mergeSucceeded = false;
                    horizontalLapMergeResult = null;
                    mergeFailureReason = "水平搭接合并异常，保留原实体：" +
                        mergeException.Message;
                }

                if (!mergeSucceeded)
                {
                    if (!string.IsNullOrWhiteSpace(mergeFailureReason))
                    {
                        conversionResult.Diagnostics.Add(mergeFailureReason);
                    }
                    continue;
                }

                processingState.HorizontalLapMerges.Add(horizontalLapMergeResult);
            }

            // A lower-index merge can shift a previously merged higher-index
            // part.  Resolve the specification map from the retained setting
            // reference after all removals so section indices cannot become
            // stale when multiple chains are merged in one pass.
            foreach (var horizontalLapMergeResult in
                processingState.HorizontalLapMerges)
            {
                if (horizontalLapMergeResult.MergedSpecification == null ||
                    horizontalLapMergeResult.MergedCenterlinePart == null)
                {
                    continue;
                }

                var currentMergedPartIndex = conversionResult.CenterlineParts
                    .IndexOf(horizontalLapMergeResult.MergedCenterlinePart);
                if (currentMergedPartIndex >= 0)
                {
                    mergedSpecificationsByPartIndex[currentMergedPartIndex] =
                        horizontalLapMergeResult.MergedSpecification;
                }
            }

            var centerlinePartCount = conversionResult.CenterlineParts.Count;
            var sectionSpecifications = Enumerable.Range(0, centerlinePartCount)
                .Select(partIndex => mergedSpecificationsByPartIndex.TryGetValue(
                        partIndex,
                        out var mergedSpecification)
                    ? mergedSpecification
                    : ResolveSectionSpecificationWithOutputMode(
                        routeRequest,
                        routeGroupRequest,
                        partIndex,
                        centerlinePartCount,
                        conversionResult.DoubleStartPhysicalBridgeSectionIndices
                            .Contains(partIndex),
                        applySingleStackCabinetTopSpecification))
                .ToList();
            var haveDifferentPhysicalSectionSpecifications =
                HaveDifferentPhysicalSectionSpecifications(
                    sectionSpecifications);
            for (var boundaryIndex = 0;
                boundaryIndex < sectionSpecifications.Count - 1;
                boundaryIndex++)
            {
                var precedingSpecification =
                    sectionSpecifications[boundaryIndex];
                var followingSpecification =
                    sectionSpecifications[boundaryIndex + 1];
                if (precedingSpecification.StackCount !=
                        followingSpecification.StackCount ||
                    Math.Abs(precedingSpecification.Thickness -
                             followingSpecification.Thickness) >
                        GeometryTolerance)
                {
                    processingState.InternalSupportBoundaryIndices.Add(
                        boundaryIndex);
                }
            }
            var ordinaryMembers = conversionResult.StackMembers
                .Where(stackMember => stackMember?.PlanarParts != null)
                .OrderBy(stackMember => stackMember.MemberIndex)
                .ToList();
            if (ordinaryMembers.Count == 0 || ordinaryMembers.Any(stackMember =>
                    stackMember.PlanarParts.Count != centerlinePartCount))
            {
                failureReason =
                    "分段排规格后处理要求普通方案每层都包含完整的平面拆排。";
                return false;
            }

            var templateMember = ordinaryMembers
                .OrderBy(stackMember => Math.Abs(stackMember.CenterlineOffset))
                .ThenBy(stackMember => stackMember.MemberIndex)
                .First();
            var templates = templateMember.PlanarParts.ToList();
            if (templates.Any(template => template?.PointList == null ||
                    template.PointList.Count < 2))
            {
                failureReason = "分段排规格后处理找不到完整的普通平面排模板。";
                return false;
            }
            if (!TryGetPartOffsetOrientationSigns(
                    templates,
                    out var partOffsetOrientationSigns,
                    out failureReason))
            {
                return false;
            }

            var startBoundaryCoordinates = new double[centerlinePartCount][];
            var endBoundaryCoordinates = new double[centerlinePartCount][];
            if (!TryResolveTerminalBoundaryCoordinates(
                    routeGroupRequest.StartTerminal,
                    sectionSpecifications[0],
                    routeGroupRequest.StackStartSpace,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.ShouldUseSpacerlessTerminalLap(true),
                    templates[0],
                    true,
                    partOffsetOrientationSigns[0],
                    out var startTerminalPlacement,
                    out startBoundaryCoordinates[0],
                    out failureReason))
            {
                return false;
            }

            if (((routeGroupRequest.StartTerminal?.Members?.Count >= 2 &&
                    routeRequest.AllowDoubleStartMiddleLap) ||
                 (routeGroupRequest.StartTerminal?.Members?.Count == 1 &&
                    routeRequest.EnableSingleStartConnectionBar)) &&
                !conversionResult.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(0))
            {
                var bridgeReferenceSetting = conversionResult.CenterlineParts[0]
                    .Copy();
                ApplySettingDimensions(
                    bridgeReferenceSetting,
                    sectionSpecifications[0],
                    null,
                    centerlinePartCount > 1
                        ? sectionSpecifications[1]
                        : null);
                if (!LinkageRouteTerminalAccessoryBuilder
                        .TryPrepareDoubleStartHorizontalBridgePlacement(
                            startTerminalPlacement,
                            bridgeReferenceSetting,
                            routeRequest,
                            routeGroupRequest,
                            true,
                            out startTerminalPlacement,
                            out failureReason))
                {
                    return false;
                }

                startBoundaryCoordinates[0] =
                    ConvertPlacementToBoundaryCoordinates(
                        routeGroupRequest.StartTerminal,
                        startTerminalPlacement,
                        templates[0],
                        true);
            }

            conversionResult.StartStackPlacement = startTerminalPlacement;

            if (!TryResolveTerminalBoundaryCoordinates(
                    routeGroupRequest.EndTerminal,
                    sectionSpecifications[centerlinePartCount - 1],
                    routeGroupRequest.StackEndSpace,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.ShouldUseSpacerlessTerminalLap(false),
                    templates[centerlinePartCount - 1],
                    false,
                    partOffsetOrientationSigns[centerlinePartCount - 1],
                    out var endTerminalPlacement,
                    out endBoundaryCoordinates[centerlinePartCount - 1],
                    out failureReason))
            {
                return false;
            }

            conversionResult.EndStackPlacement = endTerminalPlacement;

            for (var boundaryIndex = 0;
                boundaryIndex < centerlinePartCount - 1;
                boundaryIndex++)
            {
                if (!TryBuildInternalBoundaryCoordinates(
                        templates[boundaryIndex],
                        sectionSpecifications[boundaryIndex],
                        templates[boundaryIndex + 1],
                        sectionSpecifications[boundaryIndex + 1],
                        ordinaryMembers,
                        boundaryIndex,
                        partOffsetOrientationSigns[boundaryIndex],
                        out endBoundaryCoordinates[boundaryIndex],
                        out startBoundaryCoordinates[boundaryIndex + 1],
                        out failureReason))
                {
                    return false;
                }
            }

            var canPropagatePhysicalLayersFromStart =
                CanPropagatePhysicalLayersFromStart(
                    haveDifferentPhysicalSectionSpecifications,
                    templates);
            var lastFlexibleSectionIndex = templates.FindLastIndex(
                template => !IsStraightIBar(template));
            var forwardPropagationBoundaryLimit = lastFlexibleSectionIndex >= 0
                ? lastFlexibleSectionIndex
                : centerlinePartCount - 1;
            if (canPropagatePhysicalLayersFromStart)
            {
                // 柜顶段有折弯自由度时，从始端传播真实层位。I 排可以
                // 整体偏移，暂时脱开的另一端继续传给后续段；最后由柜顶
                // 非 I 排吸收残差，其固定端层位不动。不能在中间状态
                // 因刚性 I 排尚未与下一段闭合而提前淘汰。
                for (var boundaryIndex = 0;
                    boundaryIndex < forwardPropagationBoundaryLimit;
                    boundaryIndex++)
                {
                    var followingPartIndex = boundaryIndex + 1;
                    if (IsStraightIBar(templates[boundaryIndex]))
                    {
                        endBoundaryCoordinates[boundaryIndex] =
                            startBoundaryCoordinates[boundaryIndex].ToArray();
                    }

                    if (!TryAlignFollowingBoundaryToPrecedingCoordinates(
                            templates[boundaryIndex],
                            sectionSpecifications[boundaryIndex],
                            templates[followingPartIndex],
                            sectionSpecifications[followingPartIndex],
                            startBoundaryCoordinates[followingPartIndex],
                            endBoundaryCoordinates[boundaryIndex],
                            partOffsetOrientationSigns[boundaryIndex],
                            out startBoundaryCoordinates[followingPartIndex],
                            out failureReason))
                    {
                        return false;
                    }
                }
            }
            else
            {
                // 同规格方案保留既有柜顶向始端的层位传播，不让本次异规格
                // 优先级修正改变原有同规格候选的层序和几何。
                for (var boundaryIndex = centerlinePartCount - 2;
                    boundaryIndex >= 0;
                    boundaryIndex--)
                {
                    var followingPartIndex = boundaryIndex + 1;
                    if (IsStraightIBar(templates[boundaryIndex]) ||
                        IsStraightIBar(templates[followingPartIndex]))
                    {
                        // 直 I 排没有折弯自由度，不再把任一端的层位向相邻段
                        // 传播，也不由相邻段反向推动整根 I 排。
                        continue;
                    }

                    if (!TryAlignPrecedingBoundaryToFollowingCoordinates(
                            templates[boundaryIndex],
                            sectionSpecifications[boundaryIndex],
                            templates[followingPartIndex],
                            sectionSpecifications[followingPartIndex],
                            endBoundaryCoordinates[boundaryIndex],
                            startBoundaryCoordinates[followingPartIndex],
                            partOffsetOrientationSigns[boundaryIndex],
                            out endBoundaryCoordinates[boundaryIndex],
                            out failureReason))
                    {
                        return false;
                    }
                }
            }

            if (canPropagatePhysicalLayersFromStart && lastFlexibleSectionIndex >= 0)
            {
                // 柜顶侧连续 I 排反向联动到最后一根非 I 排；固定柜顶
                // 层位不动，非 I 排末腿吸收残差后统一校验。
                for (var boundaryIndex = centerlinePartCount - 2;
                    boundaryIndex >= lastFlexibleSectionIndex;
                    boundaryIndex--)
                {
                    var followingPartIndex = boundaryIndex + 1;
                    startBoundaryCoordinates[followingPartIndex] =
                        endBoundaryCoordinates[followingPartIndex].ToArray();
                    if (!TryAlignPrecedingBoundaryToFollowingCoordinates(
                            templates[boundaryIndex],
                            sectionSpecifications[boundaryIndex],
                            templates[followingPartIndex],
                            sectionSpecifications[followingPartIndex],
                            endBoundaryCoordinates[boundaryIndex],
                            startBoundaryCoordinates[followingPartIndex],
                            partOffsetOrientationSigns[boundaryIndex],
                            out endBoundaryCoordinates[boundaryIndex],
                            out failureReason))
                    {
                        return false;
                    }
                }
            }

            var maximumSectionStackCount = sectionSpecifications.Max(
                specification => specification.StackCount);
            var rebuiltMembers = Enumerable.Range(0, maximumSectionStackCount)
                .Select(memberIndex => new LinkageStackedRouteMember
                {
                    MemberIndex = memberIndex,
                    CenterlineOffset = 0d
                })
                .ToList();
            conversionResult.StartTerminalMemberIndices.Clear();
            conversionResult.EndTerminalMemberIndices.Clear();

            for (var partIndex = 0; partIndex < centerlinePartCount; partIndex++)
            {
                var sectionSpecification = sectionSpecifications[partIndex];
                var precedingThickness = ResolvePrecedingThickness(
                    routeGroupRequest,
                    sectionSpecifications,
                    partIndex);
                var followingThickness = ResolveFollowingThickness(
                    routeGroupRequest,
                    sectionSpecifications,
                    partIndex);
                var sectionCenteredOffsets = LinkageRouteStackGeometry
                    .GetCenteredMemberOffsets(
                        sectionSpecification.Thickness,
                        sectionSpecification.StackCount,
                        routeGroupRequest.StackMiddleSpace);
                processingState.SettingsBySectionIndex[partIndex] =
                    new List<SectionMemberSetting>();
                for (var localMemberIndex = 0;
                    localMemberIndex < sectionSpecification.StackCount;
                    localMemberIndex++)
                {
                    if (!TryCreateSectionMemberSetting(
                            templates[partIndex],
                            sectionSpecification,
                            partIndex > 0
                                ? sectionSpecifications[partIndex - 1]
                                : null,
                            partIndex + 1 < centerlinePartCount
                                ? sectionSpecifications[partIndex + 1]
                                : null,
                            startBoundaryCoordinates[partIndex][localMemberIndex],
                            endBoundaryCoordinates[partIndex][localMemberIndex],
                            (sectionCenteredOffsets[localMemberIndex] -
                                templateMember.CenterlineOffset) *
                                partOffsetOrientationSigns[partIndex],
                            precedingThickness,
                            followingThickness,
                            partIndex == 0 ||
                                (canPropagatePhysicalLayersFromStart &&
                                 partIndex < forwardPropagationBoundaryLimit),
                            partIndex == centerlinePartCount - 1 ||
                                (canPropagatePhysicalLayersFromStart &&
                                 lastFlexibleSectionIndex >= 0 &&
                                 partIndex > lastFlexibleSectionIndex),
                            out var rebuiltSetting,
                            out var rigidConflictAtStart,
                            out var rigidConflictAtEnd,
                            out failureReason))
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}第 {1} 层：{2}",
                            sectionSpecification.Label,
                            localMemberIndex + 1,
                            failureReason);
                        return false;
                    }

                    rebuiltMembers[localMemberIndex].PlanarParts.Add(
                        rebuiltSetting);
                    rebuiltSetting.PhysicalSectionIndex = partIndex;
                    rebuiltSetting.SpecificationSectionIndex = partIndex;
                    processingState.SettingsBySectionIndex[partIndex].Add(
                        new SectionMemberSetting
                        {
                            MemberIndex = localMemberIndex,
                            Setting = rebuiltSetting
                        });
                    if (partIndex == 0)
                    {
                        conversionResult.StartTerminalMemberIndices.Add(
                            localMemberIndex);
                    }

                    if (partIndex == centerlinePartCount - 1)
                    {
                        conversionResult.EndTerminalMemberIndices.Add(
                            localMemberIndex);
                    }
                    if (rigidConflictAtStart && partIndex > 0)
                    {
                        processingState.InternalSupportBoundaryIndices.Add(
                            partIndex - 1);
                    }

                    if (rigidConflictAtEnd &&
                        partIndex < centerlinePartCount - 1)
                    {
                        processingState.InternalSupportBoundaryIndices.Add(
                            partIndex);
                    }
                }

                ApplySettingDimensions(
                    conversionResult.CenterlineParts[partIndex],
                    sectionSpecification,
                    partIndex > 0
                        ? sectionSpecifications[partIndex - 1]
                        : null,
                    partIndex + 1 < centerlinePartCount
                        ? sectionSpecifications[partIndex + 1]
                        : null);
                if (conversionResult.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(partIndex))
                {
                    // The physical double-start bridge owns the first lap
                    // interface.  When it is also the terminal one-member
                    // section, the cabinet-top lap must use the bridge width
                    // rather than the cabinet-top default width.
                    conversionResult.CenterlineParts[partIndex].StartLapWidth =
                        sectionSpecification.Width;
                    conversionResult.CenterlineParts[partIndex].EndLapWidth =
                        sectionSpecification.Width;
                }
            }

            conversionResult.StackMembers.Clear();
            foreach (var rebuiltMember in rebuiltMembers.Where(stackMember =>
                stackMember.PlanarParts.Count > 0))
            {
                conversionResult.StackMembers.Add(rebuiltMember);
            }

            var startInternalHorizontalWidthAdjustmentCount =
                EnsureStartInternalHorizontalLapWidthPlacement(
                    processingState,
                    routeGroupRequest,
                    conversionResult.StartStackPlacement,
                    centerlinePartCount);
            var terminalWidthPlacementAdjustmentCount =
                EnsureTerminalParallelLapWidthPlacements(
                    processingState,
                    routeGroupRequest,
                    conversionResult.StartStackPlacement,
                    conversionResult.EndStackPlacement,
                    centerlinePartCount) +
                EnsureTerminalPerpendicularLapWidthPlacements(
                    processingState,
                    routeGroupRequest,
                    conversionResult.StartStackPlacement,
                    conversionResult.EndStackPlacement,
                    centerlinePartCount,
                    routeGroupRequest.GetBusbarWidth(routeRequest),
                    routeRequest.AllowPartialStartPerpendicularLap);
            EnsureTerminalParallelLapLengths(
                processingState,
                routeGroupRequest,
                conversionResult.StartStackPlacement,
                conversionResult.EndStackPlacement,
                centerlinePartCount);
            EnsureTerminalPerpendicularLapWidths(
                processingState,
                routeGroupRequest,
                conversionResult.StartStackPlacement,
                conversionResult.EndStackPlacement,
                centerlinePartCount,
                routeRequest.AllowPartialStartPerpendicularLap);
            // 始端搭接端头收口必须放在统一首段长度之后：垂直搭接时各成员
            // 折弯内端点相差一个柜顶叠排层位，取最长首段会把层位靠外的成员
            // 端头推到固定排端面之外。这里只回收超出量。
            if (processingState.SettingsBySectionIndex.TryGetValue(
                    0,
                    out var startSectionSettingsForEndpointClamp))
            {
                ClampStartPerpendicularLapEndpointsToFixedBoundary(
                    startSectionSettingsForEndpointClamp,
                    routeGroupRequest.StartTerminal,
                    conversionResult.StartStackPlacement,
                    conversionResult.Diagnostics);
                AlignTerminalLapEndpointsToPrimaryMember(
                    startSectionSettingsForEndpointClamp,
                    conversionResult.StartStackPlacement,
                    true);
            }

            if (processingState.SettingsBySectionIndex.TryGetValue(
                    centerlinePartCount - 1,
                    out var endSectionSettingsForEndpointAlignment))
            {
                AlignTerminalLapEndpointsToPrimaryMember(
                    endSectionSettingsForEndpointAlignment,
                    conversionResult.EndStackPlacement,
                    false);
            }

            EnsureInternalPerpendicularLapWidths(
                processingState,
                centerlinePartCount);
            EnsureTerminalPerpendicularLapBendReserves(
                processingState,
                sectionSpecifications,
                routeRequest,
                routeGroupRequest,
                conversionResult.StartStackPlacement,
                conversionResult.EndStackPlacement,
                routeRequest.AllowPartialStartPerpendicularLap);
            EnsureInternalPerpendicularLapBendReserves(
                processingState,
                sectionSpecifications,
                routeRequest,
                routeGroupRequest);
            if (!TryValidateInternalPerpendicularLapBendReserves(
                    processingState,
                    sectionSpecifications,
                    routeRequest,
                    routeGroupRequest,
                    out failureReason))
            {
                return false;
            }
            conversionResult.ActualCopperLength = conversionResult.StackMembers
                .SelectMany(stackMember => stackMember.PlanarParts)
                .Sum(planarPart => planarPart.GetDevelopedLength());
            conversionResult.Diagnostics.Add(
                "路径按统一中间排规生成，实体化阶段按始端、中间、柜顶实际排规修正搭接区域和位置。");
            if (terminalWidthPlacementAdjustmentCount > 0)
            {
                conversionResult.Diagnostics.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "异规格实体化后沿排宽方向调整了 {0} 个端部搭接排，使实际排宽完整落入固定排搭接范围。",
                    terminalWidthPlacementAdjustmentCount));
            }
            if (startInternalHorizontalWidthAdjustmentCount > 0)
            {
                conversionResult.Diagnostics.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "异规格实体化后沿排宽方向调整了 {0} 根始端搭接排；固定始端和多始端连接排位置保持不变。",
                    startInternalHorizontalWidthAdjustmentCount));
            }
            return true;
        }

        private static bool TryBuildHorizontalLapMergeChains(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out IList<HorizontalLapMergeChain> mergeChains,
            out string failureReason)
        {
            mergeChains = new List<HorizontalLapMergeChain>();
            failureReason = null;
            var snapshot = conversionResult?.OriginalCenterlineSnapshot;
            var centerlineParts = conversionResult?.CenterlineParts;
            if (snapshot == null || centerlineParts == null ||
                centerlineParts.Count < 2 || routeRequest == null ||
                routeGroupRequest == null)
            {
                return true;
            }

            var currentPartOriginalRanges = centerlineParts
                .Select(part => ResolveOriginalSegmentRange(part, snapshot))
                .ToList();
            var currentPartOriginalIndices = currentPartOriginalRanges
                .Select(range => ResolveOriginalPhysicalPartIndex(
                    range,
                    snapshot))
                .ToList();
            var partIndex = 0;
            while (partIndex + 1 < centerlineParts.Count)
            {
                if (conversionResult.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(partIndex) ||
                    conversionResult.DoubleStartPhysicalBridgeSectionIndices
                        .Contains(partIndex + 1) ||
                    !CanMergePhysicalSection(
                        centerlineParts[partIndex],
                        centerlineParts[partIndex + 1],
                        snapshot,
                        currentPartOriginalIndices[partIndex],
                        currentPartOriginalIndices[partIndex + 1]))
                {
                    partIndex++;
                    continue;
                }

                var chain = new HorizontalLapMergeChain();
                chain.PartIndices.Add(partIndex);
                chain.PartIndices.Add(partIndex + 1);
                var nextPartIndex = partIndex + 1;
                while (nextPartIndex + 1 < centerlineParts.Count &&
                    !conversionResult.DoubleStartPhysicalBridgeSectionIndices
                        .Contains(nextPartIndex) &&
                    !conversionResult.DoubleStartPhysicalBridgeSectionIndices
                        .Contains(nextPartIndex + 1) &&
                    CanMergePhysicalSection(
                        centerlineParts[nextPartIndex],
                        centerlineParts[nextPartIndex + 1],
                        snapshot,
                        currentPartOriginalIndices[nextPartIndex],
                        currentPartOriginalIndices[nextPartIndex + 1]))
                {
                    chain.PartIndices.Add(nextPartIndex + 1);
                    nextPartIndex++;
                }

                var chainRanges = chain.PartIndices
                    .Select(index => currentPartOriginalRanges[index])
                    .Where(range => range.Start >= 0 && range.End >= range.Start)
                    .ToList();
                chain.OriginalSegmentStartIndex = chainRanges.Count == 0
                    ? -1
                    : chainRanges.Min(range => range.Start);
                chain.OriginalSegmentEndIndex = chainRanges.Count == 0
                    ? -1
                    : chainRanges.Max(range => range.End);
                if (chain.OriginalSegmentStartIndex >= 0 &&
                    chain.OriginalSegmentEndIndex < snapshot.DirectionSequence.Count)
                {
                    for (var directionIndex = chain.OriginalSegmentStartIndex;
                        directionIndex <= chain.OriginalSegmentEndIndex;
                        directionIndex++)
                    {
                        chain.DirectionSequence.Add(snapshot.DirectionSequence[
                            directionIndex]);
                    }
                }

                chain.StartRole = chain.PartIndices[0] == 0
                    ? "Start"
                    : "Middle";
                chain.EndRole = chain.PartIndices[chain.PartIndices.Count - 1] ==
                        centerlineParts.Count - 1
                    ? "CabinetTop"
                    : "Middle";
                chain.EffectiveStackCount = ResolveEffectiveStackCount(
                    routeRequest,
                    routeGroupRequest,
                    chain.StartRole,
                    chain.EndRole);
                chain.ContainsRealTurn = chain.DirectionSequence
                    .Zip(chain.DirectionSequence.Skip(1),
                        (firstDirection, secondDirection) =>
                            Math.Abs(firstDirection.Normalized.Dot(
                                secondDirection.Normalized)) <
                            1d - GeometryTolerance)
                    .Any(hasTurn => hasTurn);
                chain.IsContinuous = !chain.ContainsRealTurn;
                if (chain.IsContinuous)
                {
                    mergeChains.Add(chain);
                }

                partIndex = nextPartIndex + 1;
            }

            return true;
        }

        private static bool IsHorizontalLapBoundary(
            PlanarBendBusbarSetting precedingPart,
            PlanarBendBusbarSetting followingPart)
        {
            if (precedingPart?.PointList == null ||
                followingPart?.PointList == null ||
                precedingPart.PointList.Count < 2 ||
                followingPart.PointList.Count < 2)
            {
                return false;
            }

            var precedingDirection = new Vector3d(
                precedingPart.PointList[precedingPart.PointList.Count - 2],
                precedingPart.PointList[precedingPart.PointList.Count - 1]);
            var followingDirection = new Vector3d(
                followingPart.PointList[0],
                followingPart.PointList[1]);
            if (precedingDirection.Norm <= GeometryTolerance ||
                followingDirection.Norm <= GeometryTolerance ||
                precedingDirection.Normalized.Dot(
                    followingDirection.Normalized) <
                    1d - GeometryTolerance)
            {
                return false;
            }

            var precedingBoundaryPoint = precedingPart.PointList[
                precedingPart.PointList.Count - 1];
            var followingBoundaryPoint = followingPart.PointList[0];
            if (precedingBoundaryPoint == null || followingBoundaryPoint == null ||
                precedingBoundaryPoint.DistanceTo(followingBoundaryPoint) >
                    GeometryTolerance)
            {
                return false;
            }

            if (precedingPart.WidthVector == null ||
                followingPart.WidthVector == null)
            {
                return false;
            }

            var precedingPlaneNormal = precedingDirection.Normalized.Cross(
                precedingPart.WidthVector.Normalized);
            var followingPlaneNormal = followingDirection.Normalized.Cross(
                followingPart.WidthVector.Normalized);
            if (precedingPlaneNormal == null || followingPlaneNormal == null ||
                precedingPlaneNormal.Norm <= GeometryTolerance ||
                followingPlaneNormal.Norm <= GeometryTolerance ||
                Math.Abs(precedingPlaneNormal.Normalized.Dot(
                    followingPlaneNormal.Normalized)) <
                    1d - GeometryTolerance)
            {
                return false;
            }

            var boundaryDelta = new Vector3d(
                precedingBoundaryPoint,
                followingBoundaryPoint);
            return Math.Abs(boundaryDelta.Dot(
                precedingPlaneNormal.Normalized)) <= GeometryTolerance;
        }

        private static bool CanMergePhysicalSection(
            PlanarBendBusbarSetting precedingPart,
            PlanarBendBusbarSetting followingPart,
            OriginalCenterlineSnapshot snapshot,
            int precedingOriginalPhysicalPartIndex,
            int followingOriginalPhysicalPartIndex)
        {
            if (!IsHorizontalLapBoundary(precedingPart, followingPart) ||
                precedingOriginalPhysicalPartIndex < 0 ||
                precedingOriginalPhysicalPartIndex !=
                    followingOriginalPhysicalPartIndex)
            {
                return false;
            }

            if (snapshot?.DirectionSequence == null ||
                snapshot.DirectionSequence.Count == 0)
            {
                return false;
            }

            var precedingDirection = new Vector3d(
                precedingPart.PointList[precedingPart.PointList.Count - 2],
                precedingPart.PointList[precedingPart.PointList.Count - 1]);
            var followingDirection = new Vector3d(
                followingPart.PointList[0],
                followingPart.PointList[1]);
            return snapshot.DirectionSequence.Any(originalDirection =>
                originalDirection != null &&
                originalDirection.Normalized.Dot(
                    precedingDirection.Normalized) >=
                    1d - GeometryTolerance &&
                originalDirection.Normalized.Dot(
                    followingDirection.Normalized) >=
                    1d - GeometryTolerance);
        }

        private static bool MergeHorizontalLapChain(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            HorizontalLapMergeChain mergeChain,
            out HorizontalLapMergeResult mergeResult,
            out string failureReason)
        {
            mergeResult = new HorizontalLapMergeResult();
            failureReason = null;
            if (conversionResult?.CenterlineParts == null ||
                conversionResult.StackMembers == null ||
                mergeChain?.PartIndices == null ||
                mergeChain.PartIndices.Count < 2)
            {
                failureReason = "水平搭接合并缺少有效的物理段链。";
                return false;
            }

            var orderedPartIndices = mergeChain.PartIndices
                .Distinct()
                .OrderBy(index => index)
                .ToList();
            if (orderedPartIndices.Count < 2 ||
                orderedPartIndices.Any(index => index < 0 ||
                    index >= conversionResult.CenterlineParts.Count) ||
                orderedPartIndices.Zip(orderedPartIndices.Skip(1),
                    (firstIndex, secondIndex) => secondIndex == firstIndex + 1)
                    .Any(isNotContiguous => !isNotContiguous))
            {
                failureReason = "水平搭接合并物理段链不连续，保留原实体。";
                return false;
            }

            var mergedCenterline = BuildMergedCenterlineFromOriginalSnapshot(
                conversionResult.OriginalCenterlineSnapshot,
                mergeChain);
            if (mergedCenterline == null || mergedCenterline.Count < 2)
            {
                failureReason = "水平搭接合并无法从原始方向链重建中心线，保留原实体。";
                return false;
            }

            var firstPart = conversionResult.CenterlineParts[orderedPartIndices[0]];
            var mergedSpecification = ResolveMergedHorizontalLapSpecification(
                routeRequest,
                routeGroupRequest,
                mergeChain);
            var mergedTemplate = firstPart.Copy();
            mergedTemplate.PointList = mergedCenterline.ToList();
            ApplySettingDimensions(mergedTemplate, mergedSpecification, null, null);
            mergedTemplate.StartLapWidth = mergedSpecification.Width;
            mergedTemplate.EndLapWidth = mergedSpecification.Width;

            if (!RebuildMergedStackMemberGeometry(
                    mergedTemplate,
                    mergedSpecification,
                    routeRequest,
                    routeGroupRequest,
                    orderedPartIndices[0],
                    out var rebuiltStackMembers,
                    out failureReason))
            {
                return false;
            }

            var oldCenterlinePartCount = conversionResult.CenterlineParts.Count;
            conversionResult.CenterlineParts[orderedPartIndices[0]] = mergedTemplate;
            for (var removeIndex = orderedPartIndices.Count - 1;
                removeIndex >= 1;
                removeIndex--)
            {
                conversionResult.CenterlineParts.RemoveAt(
                    orderedPartIndices[removeIndex]);
            }

            var removedBoundaryIndices = orderedPartIndices.Skip(1)
                .Select(index => index - 1)
                .ToHashSet();
            var removedSupportAccessories = conversionResult.Accessories
                .Where(accessory => accessory?.Kind ==
                    LinkageRouteAccessoryKind.SupportBlock &&
                    removedBoundaryIndices.Any(boundaryIndex =>
                        IsSupportBlockForBoundary(
                            accessory.SupportBlockKey,
                            boundaryIndex)))
                .ToList();
            foreach (var removedSupportAccessory in removedSupportAccessories)
            {
                conversionResult.Accessories.Remove(removedSupportAccessory);
                if (!string.IsNullOrWhiteSpace(
                        removedSupportAccessory.SupportBlockKey))
                {
                    mergeResult.RemovedSupportBlockKeys.Add(
                        removedSupportAccessory.SupportBlockKey);
                }
            }

            foreach (var stackMember in conversionResult.StackMembers)
            {
                if (stackMember?.PlanarParts == null)
                {
                    continue;
                }

                var rebuiltMember = rebuiltStackMembers.FirstOrDefault(
                    candidate => candidate.MemberIndex == stackMember.MemberIndex);
                var replacementSetting = rebuiltMember?.PlanarParts.FirstOrDefault();
                if (replacementSetting == null)
                {
                    var retainedMemberOffset = stackMember.CenterlineOffset;
                    var retainedSegmentOffsets = Enumerable.Repeat(
                            retainedMemberOffset,
                            mergedTemplate.PointList.Count - 1)
                        .ToList();
                    if (LinkageRouteStackedCenterlineOffsetter.TryOffset(
                            mergedTemplate,
                            retainedSegmentOffsets,
                            out var retainedMemberPoints,
                            out _))
                    {
                        replacementSetting = mergedTemplate.Copy();
                        replacementSetting.PointList = retainedMemberPoints;
                        replacementSetting.PhysicalSectionIndex =
                            orderedPartIndices[0];
                    }
                }
                var firstRetainedIndex = orderedPartIndices[0];
                var prefixParts = stackMember.PlanarParts
                    .Take(firstRetainedIndex)
                    .Select(part => part.Copy())
                    .ToList();
                var suffixParts = stackMember.PlanarParts
                    .Skip(orderedPartIndices[orderedPartIndices.Count - 1] + 1)
                    .Select(part => part.Copy())
                    .ToList();
                stackMember.PlanarParts.Clear();
                foreach (var prefixPart in prefixParts)
                {
                    stackMember.PlanarParts.Add(prefixPart);
                }

                if (replacementSetting != null)
                {
                    stackMember.PlanarParts.Add(replacementSetting.Copy());
                }

                foreach (var suffixPart in suffixParts)
                {
                    stackMember.PlanarParts.Add(suffixPart);
                }
            }

            for (var rebuiltPartIndex = 0;
                rebuiltPartIndex < conversionResult.CenterlineParts.Count;
                rebuiltPartIndex++)
            {
                foreach (var stackMember in conversionResult.StackMembers)
                {
                    if (stackMember?.PlanarParts == null ||
                        rebuiltPartIndex >= stackMember.PlanarParts.Count)
                    {
                        continue;
                    }

                    stackMember.PlanarParts[rebuiltPartIndex]
                        .PhysicalSectionIndex = rebuiltPartIndex;
                }
            }

            mergeResult.MergedPartIndices.Add(orderedPartIndices[0]);
            foreach (var removedPartIndex in orderedPartIndices.Skip(1))
            {
                mergeResult.RemovedPartIndices.Add(removedPartIndex);
            }
            foreach (var removedBoundaryIndex in removedBoundaryIndices)
            {
                mergeResult.RemovedBoundaryIndices.Add(removedBoundaryIndex);
            }
            foreach (var rebuiltStackMember in rebuiltStackMembers)
            {
                mergeResult.RebuiltStackMembers.Add(rebuiltStackMember);
            }
            mergeResult.MergedSpecification = mergedSpecification;
            mergeResult.MergedCenterlinePart = mergedTemplate;
            mergeResult.Diagnostics.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "水平搭接物理段链已按原始方向序列合并：{0}→{1}。",
                    orderedPartIndices.First() + 1,
                    orderedPartIndices.Last() + 1));
            conversionResult.Diagnostics.Add(mergeResult.Diagnostics.Last());
            return true;
        }

        private static bool IsSupportBlockForBoundary(
            string supportBlockKey,
            int boundaryIndex)
        {
            return !string.IsNullOrWhiteSpace(supportBlockKey) &&
                supportBlockKey.StartsWith(
                    "boundary:" + boundaryIndex + "|",
                    StringComparison.Ordinal);
        }

        private static IList<Point3d> BuildMergedCenterlineFromOriginalSnapshot(
            OriginalCenterlineSnapshot snapshot,
            HorizontalLapMergeChain mergeChain)
        {
            if (snapshot == null || mergeChain == null ||
                snapshot.SegmentStartPoints == null ||
                snapshot.SegmentEndPoints == null ||
                mergeChain.OriginalSegmentStartIndex < 0 ||
                mergeChain.OriginalSegmentEndIndex <
                    mergeChain.OriginalSegmentStartIndex ||
                mergeChain.OriginalSegmentStartIndex >=
                    snapshot.SegmentStartPoints.Count ||
                mergeChain.OriginalSegmentEndIndex >=
                    snapshot.SegmentEndPoints.Count)
            {
                return new List<Point3d>();
            }

            var mergedPoints = new List<Point3d>
            {
                new Point3d(
                    snapshot.SegmentStartPoints[mergeChain.OriginalSegmentStartIndex].X,
                    snapshot.SegmentStartPoints[mergeChain.OriginalSegmentStartIndex].Y,
                    snapshot.SegmentStartPoints[mergeChain.OriginalSegmentStartIndex].Z)
            };
            for (var segmentIndex = mergeChain.OriginalSegmentStartIndex;
                segmentIndex <= mergeChain.OriginalSegmentEndIndex;
                segmentIndex++)
            {
                var segmentEndPoint = snapshot.SegmentEndPoints[segmentIndex];
                if (segmentEndPoint != null)
                {
                    mergedPoints.Add(new Point3d(
                        segmentEndPoint.X,
                        segmentEndPoint.Y,
                        segmentEndPoint.Z));
                }
            }

            return CollapseConsecutiveDirectionSegments(mergedPoints);
        }

        private static IList<Point3d> CollapseConsecutiveDirectionSegments(
            IList<Point3d> sourcePoints)
        {
            var collapsedPoints = sourcePoints == null
                ? new List<Point3d>()
                : sourcePoints.Where(point => point != null)
                    .Select(point => new Point3d(point.X, point.Y, point.Z))
                    .ToList();
            if (collapsedPoints.Count < 3)
            {
                return collapsedPoints;
            }

            for (var pointIndex = collapsedPoints.Count - 2;
                pointIndex >= 1;
                pointIndex--)
            {
                var precedingDirection = new Vector3d(
                    collapsedPoints[pointIndex - 1],
                    collapsedPoints[pointIndex]);
                var followingDirection = new Vector3d(
                    collapsedPoints[pointIndex],
                    collapsedPoints[pointIndex + 1]);
                if (precedingDirection.Norm > GeometryTolerance &&
                    followingDirection.Norm > GeometryTolerance &&
                    precedingDirection.Normalized.Dot(
                        followingDirection.Normalized) >
                        1d - GeometryTolerance)
                {
                    collapsedPoints.RemoveAt(pointIndex);
                }
            }

            return collapsedPoints;
        }

        private static bool RebuildMergedStackMemberGeometry(
            PlanarBendBusbarSetting mergedTemplate,
            SectionSpecification mergedSpecification,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int physicalSectionIndex,
            out IList<LinkageStackedRouteMember> rebuiltStackMembers,
            out string failureReason)
        {
            rebuiltStackMembers = new List<LinkageStackedRouteMember>();
            failureReason = null;
            if (mergedTemplate?.PointList == null ||
                mergedTemplate.PointList.Count < 2 ||
                mergedSpecification == null || routeRequest == null ||
                routeGroupRequest == null || mergedSpecification.StackCount <= 0)
            {
                failureReason = "水平搭接合并无法重建叠排成员。";
                return false;
            }

            var memberOffsets = LinkageRouteStackGeometry
                .GetCenteredMemberOffsets(
                    mergedSpecification.Thickness,
                    mergedSpecification.StackCount,
                    routeGroupRequest.StackMiddleSpace)
                .ToList();
            for (var memberIndex = 0;
                memberIndex < memberOffsets.Count;
                memberIndex++)
            {
                var segmentOffsets = Enumerable.Repeat(
                        memberOffsets[memberIndex],
                        mergedTemplate.PointList.Count - 1)
                    .ToList();
                if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(
                        mergedTemplate,
                        segmentOffsets,
                        out var memberPoints,
                        out var memberFailureReason))
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "水平搭接合并重建第 {0} 层失败：{1}",
                        memberIndex + 1,
                        memberFailureReason);
                    return false;
                }

                var memberSetting = mergedTemplate.Copy();
                memberSetting.PointList = memberPoints;
                memberSetting.PhysicalSectionIndex = physicalSectionIndex;
                rebuiltStackMembers.Add(new LinkageStackedRouteMember
                {
                    MemberIndex = memberIndex,
                    CenterlineOffset = memberOffsets[memberIndex],
                    PlanarParts = new List<PlanarBendBusbarSetting>
                    {
                        memberSetting
                    }
                });
            }

            return true;
        }

        private static SectionSpecification ResolveMergedHorizontalLapSpecification(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            HorizontalLapMergeChain mergeChain)
        {
            var effectiveStackCount = Math.Max(
                1,
                mergeChain?.EffectiveStackCount ??
                    routeGroupRequest.GetStackCount(routeRequest));
            if (IsSingleStackMaterialization(routeRequest, routeGroupRequest))
            {
                effectiveStackCount = 1;
            }
            if (effectiveStackCount == 1)
            {
                return new SectionSpecification
                {
                    Label = "柜顶搭接排",
                    Width = routeGroupRequest.GetCabinetTopLapBusbarWidth(
                        routeRequest),
                    Thickness = routeGroupRequest
                        .GetCabinetTopLapBusbarThickness(routeRequest),
                    StackCount = routeGroupRequest.GetCabinetTopLapStackCount(
                        routeRequest)
                };
            }

            return new SectionSpecification
            {
                Label = "始端搭接排",
                Width = routeGroupRequest.GetStartLapBusbarWidth(routeRequest),
                Thickness = routeGroupRequest.GetStartLapBusbarThickness(
                    routeRequest),
                StackCount = routeGroupRequest.GetStartLapStackCount(
                    routeRequest)
            };
        }

        private static int ResolveEffectiveStackCount(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            string startRole,
            string endRole)
        {
            if (IsSingleStackMaterialization(routeRequest, routeGroupRequest))
            {
                return 1;
            }

            var stackCounts = new List<int>
            {
                routeGroupRequest.GetStackCount(routeRequest)
            };
            if (string.Equals(startRole, "Start", StringComparison.Ordinal))
            {
                stackCounts.Add(routeGroupRequest.GetStartLapStackCount(
                    routeRequest));
            }

            if (string.Equals(endRole, "CabinetTop", StringComparison.Ordinal))
            {
                stackCounts.Add(routeGroupRequest.GetCabinetTopLapStackCount(
                    routeRequest));
            }

            return Math.Max(1, stackCounts.Max());
        }

        private static OriginalSegmentRange ResolveOriginalSegmentRange(
            PlanarBendBusbarSetting currentPart,
            OriginalCenterlineSnapshot snapshot)
        {
            if (currentPart?.PointList == null ||
                currentPart.PointList.Count < 2 || snapshot == null ||
                snapshot.SegmentStartPoints == null ||
                snapshot.SegmentEndPoints == null ||
                snapshot.DirectionSequence == null)
            {
                return new OriginalSegmentRange();
            }

            var currentStartPoint = currentPart.PointList[0];
            var currentEndPoint = currentPart.PointList[
                currentPart.PointList.Count - 1];
            var currentDirection = new Vector3d(
                currentPart.PointList[0],
                currentPart.PointList[1]);
            if (currentDirection.Norm <= GeometryTolerance)
            {
                return new OriginalSegmentRange();
            }

            var matchingIndices = Enumerable.Range(0,
                    Math.Min(
                        snapshot.DirectionSequence.Count,
                        Math.Min(
                            snapshot.SegmentStartPoints.Count,
                            snapshot.SegmentEndPoints.Count)))
                .Where(segmentIndex =>
                {
                    var originalDirection = snapshot.DirectionSequence[segmentIndex];
                    if (originalDirection == null ||
                        Math.Abs(originalDirection.Normalized.Dot(
                            currentDirection.Normalized)) <
                            1d - GeometryTolerance)
                    {
                        return false;
                    }

                    var startDistance = DistanceToSegmentLine(
                        currentStartPoint,
                        snapshot.SegmentStartPoints[segmentIndex],
                        snapshot.SegmentEndPoints[segmentIndex]);
                    var endDistance = DistanceToSegmentLine(
                        currentEndPoint,
                        snapshot.SegmentStartPoints[segmentIndex],
                        snapshot.SegmentEndPoints[segmentIndex]);
                    return startDistance <= GeometryTolerance * 10d ||
                        endDistance <= GeometryTolerance * 10d;
                })
                .ToList();
            if (matchingIndices.Count == 0)
            {
                return new OriginalSegmentRange();
            }

            return new OriginalSegmentRange
            {
                Start = matchingIndices.Min(),
                End = matchingIndices.Max()
            };
        }

        private static int ResolveOriginalPhysicalPartIndex(
            OriginalSegmentRange segmentRange,
            OriginalCenterlineSnapshot snapshot)
        {
            if (snapshot?.OriginalPhysicalPartMap == null ||
                segmentRange.Start < 0 ||
                segmentRange.Start >= snapshot.OriginalPhysicalPartMap.Count)
            {
                return -1;
            }

            return snapshot.OriginalPhysicalPartMap[segmentRange.Start];
        }

        private static double DistanceToSegmentLine(
            Point3d point,
            Point3d segmentStart,
            Point3d segmentEnd)
        {
            if (point == null || segmentStart == null || segmentEnd == null)
            {
                return double.PositiveInfinity;
            }

            var segmentDirection = new Vector3d(segmentStart, segmentEnd);
            if (segmentDirection.Norm <= GeometryTolerance)
            {
                return point.DistanceTo(segmentStart);
            }

            var pointDirection = new Vector3d(segmentStart, point);
            var projectedPoint = new Point3d(
                segmentStart.X + segmentDirection.X *
                    segmentDirection.Dot(pointDirection) /
                    (segmentDirection.Norm * segmentDirection.Norm),
                segmentStart.Y + segmentDirection.Y *
                    segmentDirection.Dot(pointDirection) /
                    (segmentDirection.Norm * segmentDirection.Norm),
                segmentStart.Z + segmentDirection.Z *
                    segmentDirection.Dot(pointDirection) /
                    (segmentDirection.Norm * segmentDirection.Norm));
            return point.DistanceTo(projectedPoint);
        }

        internal static int CountInternalLapEndsInGaps(LinkageRouteCenterlineConversionResult conversion)
        {
            if (!TryCreateProcessingStateFromExistingGeometry(conversion, out var state, out _)) return 0;
            var count = 0;
            for (var boundary = 0; boundary + 1 < conversion.CenterlineParts.Count; boundary++)
            {
                if (conversion.DoubleStartPhysicalBridgeSectionIndices.Contains(boundary)) continue;
                var hosts = state.SettingsBySectionIndex[boundary];
                if (hosts.Count < 2 || !TryGetLocalThicknessDirection(hosts[0].Setting,
                        hosts[0].Setting.PointList.Count - 2, out var normal)) continue;
                normal = normal.Normalized;
                var orderedHosts = hosts.OrderBy(host => DotPoint(host.Setting.PointList.Last(), normal)).ToList();
                foreach (var guest in state.SettingsBySectionIndex[boundary + 1])
                {
                    var center = DotPoint(guest.Setting.PointList[0], normal);
                    var half = guest.Setting.BusBarThickness / 2d;
                    for (var gap = 0; gap + 1 < orderedHosts.Count; gap++)
                    {
                        var lower = orderedHosts[gap].Setting;
                        var upper = orderedHosts[gap + 1].Setting;
                        if (center - half >= DotPoint(lower.PointList.Last(), normal) + lower.BusBarThickness / 2d - GeometryTolerance &&
                            center + half <= DotPoint(upper.PointList.Last(), normal) - upper.BusBarThickness / 2d + GeometryTolerance)
                        { count++; break; }
                    }
                }
            }
            return count;
        }

        internal static int MoveInternalLapEndsIntoAvailableGaps(
            LinkageRouteCenterlineConversionResult conversionResult)
        {
            if (!TryCreateProcessingStateFromExistingGeometry(conversionResult,
                    out var gapState, out _)) return 0;
            var movedGapEndCount = 0;
            for (var gapBoundaryIndex = 0;
                gapBoundaryIndex + 1 < conversionResult.CenterlineParts.Count;
                gapBoundaryIndex++)
            {
                if (conversionResult.DoubleStartPhysicalBridgeSectionIndices.Contains(gapBoundaryIndex))
                    continue;
                var gapHosts = gapState.SettingsBySectionIndex[gapBoundaryIndex];
                var gapGuests = gapState.SettingsBySectionIndex[gapBoundaryIndex + 1];
                if (gapHosts.Count < 2 || !TryGetLocalThicknessDirection(
                    gapHosts[0].Setting, gapHosts[0].Setting.PointList.Count - 2,
                    out var gapThicknessDirection)) continue;
                gapThicknessDirection = gapThicknessDirection.Normalized;
                var orderedGapHosts = gapHosts.OrderBy(host => DotPoint(
                    host.Setting.PointList.Last(), gapThicknessDirection)).ToList();
                var hostPackMinimum = orderedGapHosts.Min(host => DotPoint(
                    host.Setting.PointList.Last(), gapThicknessDirection) - host.Setting.BusBarThickness / 2d);
                var hostPackMaximum = orderedGapHosts.Max(host => DotPoint(
                    host.Setting.PointList.Last(), gapThicknessDirection) + host.Setting.BusBarThickness / 2d);
                foreach (var gapGuest in gapGuests)
                {
                    var gapGuestSetting = gapGuest.Setting;
                    var gapGuestCenter = DotPoint(gapGuestSetting.PointList[0], gapThicknessDirection);
                    if (gapGuestCenter > hostPackMinimum && gapGuestCenter < hostPackMaximum) continue;
                    var gapGuestHalfThickness = gapGuestSetting.BusBarThickness / 2d;
                    var gapTargetCoordinates = new List<double>();
                    for (var gapIndex = 0; gapIndex + 1 < orderedGapHosts.Count; gapIndex++)
                    {
                        var gapLowerHost = orderedGapHosts[gapIndex].Setting;
                        var gapUpperHost = orderedGapHosts[gapIndex + 1].Setting;
                        var gapMinimumCenter = DotPoint(gapLowerHost.PointList.Last(), gapThicknessDirection) +
                            gapLowerHost.BusBarThickness / 2d + gapGuestHalfThickness;
                        var gapMaximumCenter = DotPoint(gapUpperHost.PointList.Last(), gapThicknessDirection) -
                            gapUpperHost.BusBarThickness / 2d - gapGuestHalfThickness;
                        if (gapMinimumCenter > gapMaximumCenter + GeometryTolerance) continue;
                        if (!TryCreateInternalSupportFootprint(gapLowerHost, gapGuestSetting,
                                gapThicknessDirection, out _) ||
                            !TryCreateInternalSupportFootprint(gapUpperHost, gapGuestSetting,
                                gapThicknessDirection, out _)) continue;
                        gapTargetCoordinates.Add(gapMinimumCenter);
                        gapTargetCoordinates.Add(gapMaximumCenter);
                    }
                    foreach (var gapTarget in gapTargetCoordinates.Distinct()
                        .OrderBy(coordinate => Math.Abs(coordinate - gapGuestCenter)))
                    {
                        var gapSlotAvailable = gapGuests.Where(other => other != gapGuest).All(other =>
                        {
                            var otherGapCenter = DotPoint(other.Setting.PointList[0], gapThicknessDirection);
                            return Math.Abs(gapTarget - otherGapCenter) + GeometryTolerance >=
                                    gapGuestHalfThickness + other.Setting.BusBarThickness / 2d &&
                                (gapGuestCenter - otherGapCenter) * (gapTarget - otherGapCenter) > 0d;
                        });
                        if (!gapSlotAvailable) continue;
                        var gapGuestMember = conversionResult.StackMembers.First(member =>
                            member.MemberIndex == gapGuest.MemberIndex);
                        var gapGuestPartIndex = gapGuestMember.PlanarParts.IndexOf(gapGuestSetting);
                        // 末端 I 排锁定固定端，不为内侧收拢移动它；连续 I 链
                        // 仅在后续存在非 I 排可吸收位移时才进入此优化。
                        if (!gapGuestMember.PlanarParts.Skip(gapGuestPartIndex)
                            .Any(part => part.PointList.Count > 2)) continue;
                        var gapOriginalPoints = gapGuestMember.PlanarParts.Select(part =>
                            part.PointList.Select(point => new Point3d(point.X, point.Y, point.Z)).ToList()).ToList();
                        if (LinkageRouteTerminalAccessoryBuilder.TryMoveStartTerminalLapChain(
                                conversionResult, gapGuestMember, gapGuestPartIndex,
                                gapThicknessDirection, gapTarget - gapGuestCenter, out _) &&
                            Math.Abs(DotPoint(gapGuestSetting.PointList[0], gapThicknessDirection) -
                                gapTarget) <= GeometryTolerance)
                        {
                            movedGapEndCount++;
                            break;
                        }
                        for (var gapRestoreIndex = 0; gapRestoreIndex < gapGuestMember.PlanarParts.Count; gapRestoreIndex++)
                            gapGuestMember.PlanarParts[gapRestoreIndex].PointList = gapOriginalPoints[gapRestoreIndex];
                    }
                }
            }
            return movedGapEndCount;
        }

        private sealed class OriginalSegmentRange
        {
            public int Start { get; set; } = -1;
            public int End { get; set; } = -1;
        }

        private static Point3d TranslatePoint(
            Point3d sourcePoint,
            Vector3d direction,
            double distance)
        {
            return new Point3d(
                sourcePoint.X + direction.X * distance,
                sourcePoint.Y + direction.Y * distance,
                sourcePoint.Z + direction.Z * distance);
        }

        /// <summary>
        /// 已按真实分段规格生成的候选在短搭接/无垫块输出处理后，只需按
        /// 当前实际铜排重新建立内部补差垫块索引。不能再次从通用中心线
        /// 重建整组铜排，否则会覆盖已通过候选校验的层位和折弯余量。
        /// </summary>
        internal static bool TryCreateProcessingStateFromExistingGeometry(
            LinkageRouteCenterlineConversionResult conversionResult,
            out ProcessingState processingState,
            out string failureReason)
        {
            processingState = new ProcessingState();
            failureReason = null;
            var physicalSectionCount = conversionResult?.CenterlineParts?.Count ?? 0;
            if (physicalSectionCount == 0 ||
                conversionResult.StackMembers == null ||
                conversionResult.StackMembers.Count == 0)
            {
                failureReason = "异规格输出后处理缺少已生成的实际分段铜排。";
                return false;
            }

            for (var sectionIndex = 0;
                sectionIndex < physicalSectionCount;
                sectionIndex++)
            {
                processingState.SettingsBySectionIndex[sectionIndex] =
                    new List<SectionMemberSetting>();
            }

            foreach (var stackMember in conversionResult.StackMembers)
            {
                if (stackMember?.PlanarParts == null)
                {
                    continue;
                }

                foreach (var planarPart in stackMember.PlanarParts.Where(
                    currentPart => currentPart != null))
                {
                    var physicalSectionIndex = planarPart.PhysicalSectionIndex;
                    if (physicalSectionIndex < 0 ||
                        physicalSectionIndex >= physicalSectionCount)
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "异规格输出后处理无法识别第 {0} 层铜排的物理分段。",
                            stackMember.MemberIndex + 1);
                        return false;
                    }

                    processingState.SettingsBySectionIndex[
                        physicalSectionIndex].Add(new SectionMemberSetting
                        {
                            MemberIndex = stackMember.MemberIndex,
                            Setting = planarPart
                        });
                }
            }

            if (processingState.SettingsBySectionIndex.Values.Any(
                    sectionSettings => sectionSettings.Count == 0))
            {
                failureReason = "异规格输出后处理检测到缺失的物理分段铜排。";
                return false;
            }

            // 每个内部搭接面都按当前实际间隙复核。已经直接贴合的配对会
            // 计算出零厚度而不生成垫块；真正的厚差/数量差间隙才会补齐。
            for (var boundaryIndex = 0;
                boundaryIndex < physicalSectionCount - 1;
                boundaryIndex++)
            {
                processingState.InternalSupportBoundaryIndices.Add(
                    boundaryIndex);
            }

            return true;
        }

        internal static bool TryValidateInternalPhysicalLapClosure(
            LinkageRouteCenterlineConversionResult conversionResult,
            bool allowFinalSupportBlocks,
            out bool requiresFinalSupportBlock,
            out string failureReason,
            double acceptanceTolerance = GeometryTolerance)
        {
            requiresFinalSupportBlock = false;
            failureReason = null;
            if (!TryCreateProcessingStateFromExistingGeometry(
                    conversionResult,
                    out var processingState,
                    out failureReason))
            {
                return false;
            }

            foreach (var boundaryIndex in processingState
                .InternalSupportBoundaryIndices.OrderBy(index => index))
            {
                // 多始端连接排与第一物理段由终端连接排闭环单独校验，且该
                // 接口明确禁止用垫块掩盖未贴合。
                if (conversionResult.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(boundaryIndex))
                {
                    continue;
                }

                var precedingSettings = processingState.SettingsBySectionIndex[
                    boundaryIndex];
                var followingSettings = processingState.SettingsBySectionIndex[
                    boundaryIndex + 1];
                if (!TryGetLocalThicknessDirection(
                        precedingSettings[0].Setting,
                        precedingSettings[0].Setting.PointList.Count - 2,
                        out var boundaryThicknessDirection))
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0} 个内部搭接面无法取得排厚方向。",
                        boundaryIndex + 1);
                    return false;
                }

                boundaryThicknessDirection =
                    boundaryThicknessDirection.Normalized;
                var lapPairs = new List<InternalSupportPair>();
                foreach (var precedingItem in precedingSettings)
                {
                    var precedingEndpoint = precedingItem.Setting.PointList[
                        precedingItem.Setting.PointList.Count - 1];
                    var nearestFollowingItem = followingSettings
                        .OrderBy(item => Math.Abs(new Vector3d(
                                precedingEndpoint,
                                item.Setting.PointList[0])
                            .Dot(boundaryThicknessDirection)))
                        .First();
                    AddUniqueInternalSupportPair(
                        lapPairs,
                        precedingItem,
                        nearestFollowingItem);
                }

                foreach (var followingItem in followingSettings)
                {
                    var followingEndpoint = followingItem.Setting.PointList[0];
                    var nearestPrecedingItem = precedingSettings
                        .OrderBy(item => Math.Abs(new Vector3d(
                                item.Setting.PointList[
                                    item.Setting.PointList.Count - 1],
                                followingEndpoint)
                            .Dot(boundaryThicknessDirection)))
                        .First();
                    AddUniqueInternalSupportPair(
                        lapPairs,
                        nearestPrecedingItem,
                        followingItem);
                }

                if (precedingSettings.Count != followingSettings.Count)
                {
                    AddAdjacentInternalSupportGapPairs(
                        lapPairs,
                        precedingSettings,
                        followingSettings,
                        boundaryThicknessDirection);
                }
                foreach (var lapPair in lapPairs)
                {
                    var precedingEndpoint = lapPair.Preceding.Setting.PointList[
                        lapPair.Preceding.Setting.PointList.Count - 1];
                    var followingEndpoint =
                        lapPair.Following.Setting.PointList[0];
                    var actualCenterDistance = Math.Abs(new Vector3d(
                            precedingEndpoint,
                            followingEndpoint)
                        .Dot(boundaryThicknessDirection));
                    var requiredCenterDistance =
                        (lapPair.Preceding.Setting.BusBarThickness +
                         lapPair.Following.Setting.BusBarThickness) / 2d;
                    var thicknessDifference = actualCenterDistance -
                        requiredCenterDistance;
                    if (thicknessDifference < -acceptanceTolerance)
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "第 {0} 个内部搭接面第 {1}→{2} 层仍有 {3:F3} mm 正体积重叠。",
                            boundaryIndex + 1,
                            lapPair.Preceding.MemberIndex + 1,
                            lapPair.Following.MemberIndex + 1,
                            -thicknessDifference);
                        return false;
                    }

                    // 最终闭合只接受有限实体端段的真实投影交集，不能使用
                    // 仅供构造提案的无限延长线回退矩形掩盖短腿未搭接。
                    if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                            lapPair.Preceding.Setting,
                            false,
                            lapPair.Following.Setting,
                            true,
                            Math.Min(lapPair.Preceding.Setting.BusBarWidth,
                                lapPair.Following.Setting.BusBarWidth),
                            out _))
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "第 {0} 个内部搭接面第 {1}→{2} 层没有有效搭接区域。",
                            boundaryIndex + 1,
                            lapPair.Preceding.MemberIndex + 1,
                            lapPair.Following.MemberIndex + 1);
                        return false;
                    }

                    if (thicknessDifference <= acceptanceTolerance)
                    {
                        continue;
                    }

                    var supportBlockKey = BuildSupportBlockKey(
                        boundaryIndex,
                        lapPair.Preceding.MemberIndex,
                        lapPair.Following.MemberIndex);
                    var hasFinalSupportBlock = allowFinalSupportBlocks &&
                        conversionResult.Accessories.Any(routeAccessory =>
                            routeAccessory?.Kind ==
                                LinkageRouteAccessoryKind.SupportBlock &&
                            string.Equals(
                                routeAccessory.SupportBlockKey,
                                supportBlockKey,
                                StringComparison.Ordinal));
                    var hasResolvedLapPath = hasFinalSupportBlock ||
                        HasResolvedInternalLapPath(
                            conversionResult,
                            boundaryIndex,
                            lapPair.Preceding,
                            precedingSettings,
                            followingSettings,
                            boundaryThicknessDirection,
                            allowFinalSupportBlocks);
                    if (!hasResolvedLapPath)
                    {
                        requiresFinalSupportBlock = true;
                        if (allowFinalSupportBlocks)
                        {
                            // 真实搭接面已经通过上面的 footprint 校验，
                            // 但最终垫块尚未在本阶段生成。先保留该厚向
                            // 余隙，交由 TryAddInternalSupportBlocks 统一
                            // 建立并在后续实体检查中复核；否则前一边界
                            // 为避开正体积重叠而产生的合法垫块余隙会被
                            // 错误地提前淘汰。
                            continue;
                        }
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "第 {0} 个内部搭接面第 {1}→{2} 层存在 {3:F3} mm 缝隙，需要继续偏移或最终补垫块。",
                            boundaryIndex + 1,
                            lapPair.Preceding.MemberIndex + 1,
                            lapPair.Following.MemberIndex + 1,
                            thicknessDifference);
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool HasResolvedInternalLapPath(
            LinkageRouteCenterlineConversionResult conversionResult,
            int boundaryIndex,
            SectionMemberSetting sourcePrecedingItem,
            IList<SectionMemberSetting> precedingSettings,
            IList<SectionMemberSetting> followingSettings,
            Vector3d boundaryThicknessDirection,
            bool allowFinalSupportBlocks)
        {
            var adjacencyByNodeKey = new Dictionary<string, HashSet<string>>(
                StringComparer.Ordinal);
            foreach (var precedingItem in precedingSettings)
            {
                adjacencyByNodeKey["P:" + precedingItem.MemberIndex] =
                    new HashSet<string>(StringComparer.Ordinal);
            }
            foreach (var followingItem in followingSettings)
            {
                adjacencyByNodeKey["F:" + followingItem.MemberIndex] =
                    new HashSet<string>(StringComparer.Ordinal);
            }

            Action<string, string> addUndirectedEdge = (firstKey, secondKey) =>
            {
                adjacencyByNodeKey[firstKey].Add(secondKey);
                adjacencyByNodeKey[secondKey].Add(firstKey);
            };
            Action<IList<SectionMemberSetting>, string, bool>
                addSameSectionContactEdges =
                    (sectionSettings, nodePrefix, useSectionStart) =>
                    {
                        for (var firstIndex = 0;
                            firstIndex < sectionSettings.Count;
                            firstIndex++)
                        {
                            for (var secondIndex = firstIndex + 1;
                                secondIndex < sectionSettings.Count;
                                secondIndex++)
                            {
                                var firstItem = sectionSettings[firstIndex];
                                var secondItem = sectionSettings[secondIndex];
                                var firstPoint = useSectionStart
                                    ? firstItem.Setting.PointList[0]
                                    : firstItem.Setting.PointList[
                                        firstItem.Setting.PointList.Count - 1];
                                var secondPoint = useSectionStart
                                    ? secondItem.Setting.PointList[0]
                                    : secondItem.Setting.PointList[
                                        secondItem.Setting.PointList.Count - 1];
                                var centerDistance = Math.Abs(new Vector3d(
                                        firstPoint,
                                        secondPoint)
                                    .Dot(boundaryThicknessDirection));
                                var requiredCenterDistance =
                                    (firstItem.Setting.BusBarThickness +
                                     secondItem.Setting.BusBarThickness) / 2d;
                                if (centerDistance <= requiredCenterDistance +
                                        GeometryTolerance)
                                {
                                    addUndirectedEdge(
                                        nodePrefix + firstItem.MemberIndex,
                                        nodePrefix + secondItem.MemberIndex);
                                }
                            }
                        }
                    };
            addSameSectionContactEdges(precedingSettings, "P:", false);
            addSameSectionContactEdges(followingSettings, "F:", true);

            foreach (var precedingItem in precedingSettings)
            {
                var precedingEndpoint = precedingItem.Setting.PointList[
                    precedingItem.Setting.PointList.Count - 1];
                foreach (var followingItem in followingSettings)
                {
                    var followingEndpoint = followingItem.Setting.PointList[0];
                    var centerDistance = Math.Abs(new Vector3d(
                            precedingEndpoint,
                            followingEndpoint)
                        .Dot(boundaryThicknessDirection));
                    var requiredCenterDistance =
                        (precedingItem.Setting.BusBarThickness +
                         followingItem.Setting.BusBarThickness) / 2d;
                    var hasDirectContact = Math.Abs(
                            centerDistance - requiredCenterDistance) <=
                        GeometryTolerance &&
                        TryCreateInternalSupportFootprint(
                            precedingItem.Setting,
                            followingItem.Setting,
                            boundaryThicknessDirection,
                            out _);
                    var hasSupportConnection = allowFinalSupportBlocks &&
                        conversionResult.Accessories.Any(routeAccessory =>
                            routeAccessory?.Kind ==
                                LinkageRouteAccessoryKind.SupportBlock &&
                            string.Equals(
                                routeAccessory.SupportBlockKey,
                                BuildSupportBlockKey(
                                    boundaryIndex,
                                    precedingItem.MemberIndex,
                                    followingItem.MemberIndex),
                                StringComparison.Ordinal));
                    if (hasDirectContact || hasSupportConnection)
                    {
                        addUndirectedEdge(
                            "P:" + precedingItem.MemberIndex,
                            "F:" + followingItem.MemberIndex);
                    }
                }
            }

            var sourceNodeKey = "P:" + sourcePrecedingItem.MemberIndex;
            var visitedNodeKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                sourceNodeKey
            };
            var pendingNodeKeys = new Queue<string>();
            pendingNodeKeys.Enqueue(sourceNodeKey);
            while (pendingNodeKeys.Count > 0)
            {
                var currentNodeKey = pendingNodeKeys.Dequeue();
                if (currentNodeKey.StartsWith("F:", StringComparison.Ordinal))
                {
                    return true;
                }

                foreach (var adjacentNodeKey in
                    adjacencyByNodeKey[currentNodeKey])
                {
                    if (visitedNodeKeys.Add(adjacentNodeKey))
                    {
                        pendingNodeKeys.Enqueue(adjacentNodeKey);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 异规格实体化后，只校正同一成员中较早物理排的内部搭接末段。
        /// 始端、柜顶的外部搭接段保持锁定；每次从相邻物理排及其余真实
        /// 物理排的两侧面生成候选层位，选择正体积干涉最少的层位。
        /// 厚向间隙由调用方按实际搭接面重建垫块。
        /// </summary>
        internal static bool TrySeparateNonAdjacentPhysicalLapSegments(
            LinkageRouteCenterlineConversionResult conversionResult,
            out bool geometryAdjusted,
            out string failureReason)
        {
            return TrySeparateNonAdjacentPhysicalLapSegmentsCancelable(conversionResult,
                out geometryAdjusted, out failureReason, CancellationToken.None);
        }

        internal static bool TrySeparateNonAdjacentPhysicalLapSegmentsCancelable(
            LinkageRouteCenterlineConversionResult conversionResult,
            out bool geometryAdjusted, out string failureReason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            geometryAdjusted = false;
            failureReason = null;
            if (conversionResult?.StackMembers == null)
            {
                return true;
            }

            var allPhysicalSettings = conversionResult.StackMembers
                .Where(stackMember => stackMember?.PlanarParts != null)
                .SelectMany(stackMember => stackMember.PlanarParts)
                .Where(planarSetting => planarSetting?.PointList?.Count >= 2)
                .ToList();
            foreach (var stackMember in conversionResult.StackMembers
                .Where(currentMember => currentMember?.PlanarParts != null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var memberPhysicalSettings = stackMember.PlanarParts
                    .Where(planarSetting =>
                        planarSetting?.PointList?.Count >= 2)
                    .OrderBy(planarSetting =>
                        planarSetting.PhysicalSectionIndex)
                    .ToList();
                for (var precedingListIndex = 0;
                    precedingListIndex < memberPhysicalSettings.Count - 1;
                    precedingListIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var precedingSetting = memberPhysicalSettings[
                        precedingListIndex];
                    var precedingSectionIndex = precedingSetting
                        .PhysicalSectionIndex;
                    var precedingSegmentIndex = precedingSetting.PointList.Count - 2;
                    if (precedingSegmentIndex <= 0 ||
                        !TryGetLocalThicknessDirection(
                            precedingSetting,
                            precedingSegmentIndex,
                            out var precedingThicknessDirection))
                    {
                        continue;
                    }

                    var currentIntersectionCount =
                        CountLapNeighborhoodPositiveVolumeIntersections(
                            precedingSetting,
                            false,
                            allPhysicalSettings,
                            precedingSetting);
                    if (currentIntersectionCount <= 0)
                    {
                        continue;
                    }

                    var normalizedPrecedingThicknessDirection =
                        precedingThicknessDirection.Normalized;
                    var precedingCoordinate = DotPoint(
                        precedingSetting.PointList[
                            precedingSetting.PointList.Count - 1],
                        normalizedPrecedingThicknessDirection);
                    var intendedFollowingSetting = memberPhysicalSettings[
                        precedingListIndex + 1];
                    var layerReferenceSettings = new[]
                        {
                            intendedFollowingSetting
                        }
                        .Concat(allPhysicalSettings.Where(planarSetting =>
                            !ReferenceEquals(
                                planarSetting,
                                intendedFollowingSetting)))
                        .Where(planarSetting =>
                            !ReferenceEquals(planarSetting, precedingSetting) &&
                            planarSetting?.PointList?.Count >= 2)
                        .Distinct()
                        .ToList();
                    var targetCoordinateCandidates = new List<double>();
                    var occupiedLayerMinimumCoordinates = new List<double>();
                    var occupiedLayerMaximumCoordinates = new List<double>();
                    foreach (var layerReferenceSetting in layerReferenceSettings)
                    {
                        if (!TryGetLocalThicknessDirection(
                                layerReferenceSetting,
                                0,
                                out var referenceThicknessDirection) ||
                            Math.Abs(normalizedPrecedingThicknessDirection.Dot(
                                referenceThicknessDirection.Normalized)) <
                                    1d - GeometryTolerance)
                        {
                            continue;
                        }

                        var referenceCoordinate = DotPoint(
                            layerReferenceSetting.PointList[0],
                            normalizedPrecedingThicknessDirection);
                        var requiredCenterDistance =
                            (precedingSetting.BusBarThickness +
                             layerReferenceSetting.BusBarThickness) / 2d;
                        targetCoordinateCandidates.Add(
                            referenceCoordinate - requiredCenterDistance);
                        targetCoordinateCandidates.Add(
                            referenceCoordinate + requiredCenterDistance);
                        occupiedLayerMinimumCoordinates.Add(
                            referenceCoordinate - requiredCenterDistance);
                        occupiedLayerMaximumCoordinates.Add(
                            referenceCoordinate + requiredCenterDistance);
                    }

                    // 相交实体的局部厚向可能平行，但其物理段首段方向并不
                    // 平行，因而无法从参考首段投影出整层候选。此时仍应
                    // 围绕当前位置逐毫米尝试两侧微移，处理斜段边缘产生的
                    // 小正体积重叠；只偏移可分段的 N 排末段，不移动 I 排。
                    var localDisplacementStepCount = Math.Max(
                        8,
                        allPhysicalSettings.Count + 2);
                    for (var localDisplacementStepIndex = 1;
                        localDisplacementStepIndex <=
                            localDisplacementStepCount;
                        localDisplacementStepIndex++)
                    {
                        targetCoordinateCandidates.Add(
                            precedingCoordinate -
                                localDisplacementStepIndex);
                        targetCoordinateCandidates.Add(
                            precedingCoordinate +
                                localDisplacementStepIndex);
                    }

                    if (occupiedLayerMinimumCoordinates.Count > 0)
                    {
                        // 当现有各排侧面候选都被其他相交段占用时，继续向
                        // 整个厚向包络两侧扩展层位。最多按实体数逐层外移，
                        // 使后续轮次可以联动避开转角，而不是在原层位循环。
                        var lowerEnvelopeCoordinate =
                            occupiedLayerMinimumCoordinates.Min();
                        var upperEnvelopeCoordinate =
                            occupiedLayerMaximumCoordinates.Max();
                        var outwardLayerStep = Math.Max(
                            precedingSetting.BusBarThickness,
                            1d);
                        for (var outwardLayerIndex = 0;
                            outwardLayerIndex <= allPhysicalSettings.Count + 2;
                            outwardLayerIndex++)
                        {
                            var outwardLayerDistance = outwardLayerIndex *
                                outwardLayerStep;
                            targetCoordinateCandidates.Add(
                                lowerEnvelopeCoordinate - outwardLayerDistance);
                            targetCoordinateCandidates.Add(
                                upperEnvelopeCoordinate + outwardLayerDistance);
                        }
                    }

                    List<Point3d> bestAdjustedPointList = null;
                    var bestIntersectionCount = currentIntersectionCount;
                    var bestWorldDisplacement = 0d;
                    string latestAdjustmentFailureReason = null;
                    var candidateIntersectionSummaries = new List<string>();
                    foreach (var targetCoordinate in targetCoordinateCandidates
                        .Where(candidateCoordinate => Math.Abs(
                            candidateCoordinate - precedingCoordinate) >
                                GeometryTolerance)
                        .Distinct()
                        .OrderBy(candidateCoordinate => Math.Abs(
                            candidateCoordinate - precedingCoordinate)))
                    {
                        var worldDisplacement = targetCoordinate -
                            precedingCoordinate;
                        var segmentOffsetDistances = new double[
                            precedingSetting.PointList.Count - 1];
                        segmentOffsetDistances[precedingSegmentIndex] =
                            worldDisplacement;
                        if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(
                                precedingSetting,
                                segmentOffsetDistances,
                                true,
                                out var adjustedPointList,
                                out latestAdjustmentFailureReason))
                        {
                            if (candidateIntersectionSummaries.Count < 8)
                            {
                                candidateIntersectionSummaries.Add(string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0:F3}(偏移失败)",
                                    targetCoordinate));
                            }
                            continue;
                        }

                        var adjustedSetting = precedingSetting.Copy();
                        adjustedSetting.PointList = adjustedPointList;
                        var adjustedIntersectionCount =
                            CountLapNeighborhoodPositiveVolumeIntersections(
                                adjustedSetting,
                                false,
                                allPhysicalSettings,
                                precedingSetting);
                        if (candidateIntersectionSummaries.Count < 8)
                        {
                            candidateIntersectionSummaries.Add(string.Format(
                                CultureInfo.InvariantCulture,
                                "{0:F3}(干涉{1})",
                                targetCoordinate,
                                adjustedIntersectionCount));
                        }
                        if (adjustedIntersectionCount >= bestIntersectionCount)
                        {
                            continue;
                        }

                        bestAdjustedPointList = adjustedPointList;
                        bestIntersectionCount = adjustedIntersectionCount;
                        bestWorldDisplacement = worldDisplacement;
                        if (bestIntersectionCount == 0)
                        {
                            break;
                        }
                    }

                    if (bestAdjustedPointList == null)
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "第 {0} 层第 {1} 个物理排内部搭接段本轮未找到可减少正体积干涉的层位：{2}",
                            stackMember.MemberIndex + 1,
                            precedingSectionIndex + 1,
                            string.IsNullOrWhiteSpace(latestAdjustmentFailureReason)
                                ? "需要下一轮联动相邻搭接段或补充垫块；候选=" +
                                  string.Join(",", candidateIntersectionSummaries)
                                : latestAdjustmentFailureReason + "；候选=" +
                                  string.Join(",", candidateIntersectionSummaries));
                        continue;
                    }

                    precedingSetting.PointList = bestAdjustedPointList;
                    geometryAdjusted = true;
                    conversionResult.Diagnostics.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0} 层第 {1} 个物理排的内部搭接末段已沿排厚方向偏移 {2:F3} mm；正体积干涉数由 {3} 降至 {4}。",
                        stackMember.MemberIndex + 1,
                        precedingSectionIndex + 1,
                        bestWorldDisplacement,
                        currentIntersectionCount,
                        bestIntersectionCount));
                }
            }

            return true;
        }

        /// <summary>
        /// 内部层位校正会重新生成 N 排点列，因此必须在该轮所有厚向
        /// 校正完成后再稳定 N 形过渡。只延长发生实体相交的端部直段，
        /// 不删减折点，也不把已经形成的 N 排重新归一成 I 排。
        /// 每次只处理一处，让下一轮基于最新实体继续联动。
        /// </summary>
        internal static bool TryStabilizePersistentNTransitions(
            LinkageRouteCenterlineConversionResult conversionResult,
            out bool geometryAdjusted)
        {
            geometryAdjusted = false;
            if (conversionResult?.StackMembers == null)
            {
                return true;
            }

            var allPhysicalSettings = conversionResult.StackMembers
                .Where(stackMember => stackMember?.PlanarParts != null)
                .SelectMany(stackMember => stackMember.PlanarParts)
                .Where(planarSetting => planarSetting?.PointList?.Count >= 2)
                .ToList();
            var memberIndexBySetting = conversionResult.StackMembers
                .Where(stackMember => stackMember?.PlanarParts != null)
                .SelectMany(stackMember => stackMember.PlanarParts
                    .Where(planarSetting => planarSetting != null)
                    .Select(planarSetting => new
                    {
                        Setting = planarSetting,
                        stackMember.MemberIndex
                    }))
                .GroupBy(item => item.Setting)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().MemberIndex);

            // 三层及以上 N 排若只逐根处理，先移动的外层会占住中层下一
            // 个可用转折位置，形成 0/0/10 mm 而无法继续。先按各层斜段
            // 在搭接方向上的自然顺序整体生成递增错位（如 0/10/20），
            // 再由下面的单根最小移动处理非规则剩余干涉。
            if (TryStaggerPersistentNTransitionLayers(
                    allPhysicalSettings,
                    true,
                    out var startTransitionAdjustmentCount) &&
                startTransitionAdjustmentCount > 0)
            {
                geometryAdjusted = true;
                conversionResult.Diagnostics.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "异规格叠排已整体错开 {0} 根始端 N 形转折，保持递增层位且不回退为 I 排。",
                    startTransitionAdjustmentCount));
            }

            if (TryStaggerPersistentNTransitionLayers(
                    allPhysicalSettings,
                    false,
                    out var endTransitionAdjustmentCount) &&
                endTransitionAdjustmentCount > 0)
            {
                geometryAdjusted = true;
                conversionResult.Diagnostics.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "异规格叠排已整体错开 {0} 根末端 N 形转折，保持递增层位且不回退为 I 排。",
                    endTransitionAdjustmentCount));
            }

            // 分段层位和折弯余量修正后，某一层 N 排的斜过渡可能切入
            // 相邻层仍保持的 I 排或端部直段。保持已经形成的 N 拓扑，
            // 每轮只延长一处发生实体相交的搭接直段，让下一轮基于最新
            // 实体继续联动；不得通过删掉折点把 N 重新归一为 I。
            foreach (var adjustableSetting in allPhysicalSettings
                .Where(planarSetting => planarSetting?.PointList?.Count >= 3))
            {
                var currentSettingAdjusted = false;
                memberIndexBySetting.TryGetValue(
                    adjustableSetting,
                    out var adjustableMemberIndex);
                var comparisonSettings = allPhysicalSettings
                    .Where(planarSetting =>
                        planarSetting != null &&
                        (planarSetting.PhysicalSectionIndex ==
                            adjustableSetting.PhysicalSectionIndex ||
                         !memberIndexBySetting.TryGetValue(
                             planarSetting,
                             out var comparisonMemberIndex) ||
                         comparisonMemberIndex != adjustableMemberIndex))
                    .ToList();
                foreach (var moveStart in new[] { true, false })
                {
                    var currentNeighborhoodIntersectionCount =
                        CountLapNeighborhoodPositiveVolumeIntersections(
                            adjustableSetting,
                            moveStart,
                            comparisonSettings,
                            adjustableSetting);
                    if (currentNeighborhoodIntersectionCount <= 0)
                    {
                        continue;
                    }

                    var adjustedSetting = adjustableSetting.Copy();
                    var existingBoundaryLength = GetBoundarySegmentLength(
                        adjustedSetting,
                        moveStart);
                    if (!TryMoveLapTransitionBeyondIntersections(
                            adjustedSetting,
                            moveStart,
                            comparisonSettings,
                            adjustableSetting,
                            existingBoundaryLength,
                            out var transitionExtensionDistance) ||
                        transitionExtensionDistance <= GeometryTolerance)
                    {
                        continue;
                    }

                    adjustableSetting.PointList = adjustedSetting.PointList;
                    geometryAdjusted = true;
                    currentSettingAdjusted = true;
                    conversionResult.Diagnostics.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "异规格叠排第 {0} 个物理段的 N 形搭接过渡已沿{1}端直段延长 {2:F3} mm，保留 N 形并消除与相邻 I/N 排的正体积重叠。",
                        adjustableSetting.PhysicalSectionIndex + 1,
                        moveStart ? "始" : "末",
                        transitionExtensionDistance));
                    break;
                }

                if (currentSettingAdjusted)
                {
                    continue;
                }
            }

            return true;
        }

        private static bool TryStaggerPersistentNTransitionLayers(
            IReadOnlyList<PlanarBendBusbarSetting> allPhysicalSettings,
            bool useStartBoundary,
            out int adjustedSettingCount)
        {
            adjustedSettingCount = 0;
            if (allPhysicalSettings == null)
            {
                return true;
            }

            foreach (var physicalSectionGroup in allPhysicalSettings
                .Where(planarSetting =>
                    planarSetting?.PointList?.Count >= 3)
                .GroupBy(planarSetting => planarSetting.PhysicalSectionIndex))
            {
                var sectionSettings = allPhysicalSettings
                    .Where(planarSetting => planarSetting != null &&
                        planarSetting.PhysicalSectionIndex ==
                            physicalSectionGroup.Key)
                    .ToList();
                var nSettings = physicalSectionGroup.ToList();
                if (nSettings.Count < 2 ||
                    !nSettings.Any(planarSetting =>
                        CountLapNeighborhoodPositiveVolumeIntersections(
                            planarSetting,
                            useStartBoundary,
                            sectionSettings,
                            planarSetting) > 0))
                {
                    continue;
                }

                var referenceSetting = nSettings[0];
                var referencePointCount = referenceSetting.PointList.Count;
                var referenceLapStartIndex = useStartBoundary
                    ? 0
                    : referencePointCount - 2;
                var referenceLapEndIndex = useStartBoundary
                    ? 1
                    : referencePointCount - 1;
                var referenceLapDirection = new Vector3d(
                    referenceSetting.PointList[referenceLapStartIndex],
                    referenceSetting.PointList[referenceLapEndIndex]);
                if (referenceLapDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                referenceLapDirection = referenceLapDirection.Normalized;
                var referenceTransitionMoveDirection = useStartBoundary
                    ? referenceLapDirection
                    : new Vector3d(
                        -referenceLapDirection.X,
                        -referenceLapDirection.Y,
                        -referenceLapDirection.Z);
                var orderedLayerSettings =
                    new List<PersistentNTransitionLayer>();
                var directionsAreParallel = true;
                foreach (var nSetting in nSettings)
                {
                    var pointCount = nSetting.PointList.Count;
                    var lapStartIndex = useStartBoundary
                        ? 0
                        : pointCount - 2;
                    var lapEndIndex = useStartBoundary
                        ? 1
                        : pointCount - 1;
                    var adjacentInteriorPointIndex = useStartBoundary
                        ? 2
                        : pointCount - 3;
                    var lapDirection = new Vector3d(
                        nSetting.PointList[lapStartIndex],
                        nSetting.PointList[lapEndIndex]);
                    if (lapDirection.Norm <= GeometryTolerance ||
                        Math.Abs(lapDirection.Normalized.Dot(
                            referenceLapDirection)) <
                                1d - GeometryTolerance)
                    {
                        directionsAreParallel = false;
                        break;
                    }

                    orderedLayerSettings.Add(
                        new PersistentNTransitionLayer
                        {
                            Setting = nSetting,
                            CurrentBoundaryLength = GetBoundarySegmentLength(
                                nSetting,
                                useStartBoundary),
                            InteriorCoordinate = DotPoint(
                                nSetting.PointList[
                                    adjacentInteriorPointIndex],
                                referenceTransitionMoveDirection)
                        });
                }

                if (!directionsAreParallel)
                {
                    continue;
                }

                foreach (var orderedLayers in new[]
                    {
                        orderedLayerSettings
                            .OrderBy(layer => layer.InteriorCoordinate)
                            .ToList(),
                        orderedLayerSettings
                            .OrderByDescending(layer => layer.InteriorCoordinate)
                            .ToList()
                    })
                {
                    var baseTransitionStep = Math.Max(
                        1d,
                        orderedLayers.Max(layer =>
                            layer.Setting.BusBarThickness));
                    var maximumAdditionalTransitionStep = Math.Max(
                        baseTransitionStep,
                        orderedLayers.Max(layer =>
                            layer.Setting.BusBarWidth));
                    for (var additionalTransitionStep = 0d;
                        additionalTransitionStep <=
                            maximumAdditionalTransitionStep +
                                GeometryTolerance;
                        additionalTransitionStep += 1d)
                    {
                        var candidateSettingsBySource =
                            new Dictionary<PlanarBendBusbarSetting,
                                PlanarBendBusbarSetting>();
                        var previousTargetBoundaryLength = 0d;
                        PlanarBendBusbarSetting previousSourceSetting = null;
                        var candidateIsValid = true;
                        foreach (var orderedLayer in orderedLayers)
                        {
                            var targetBoundaryLength =
                                orderedLayer.CurrentBoundaryLength;
                            if (previousSourceSetting != null)
                            {
                                var requiredTransitionStep = Math.Max(
                                    baseTransitionStep,
                                    (previousSourceSetting.BusBarThickness +
                                     orderedLayer.Setting.BusBarThickness) / 2d) +
                                    additionalTransitionStep;
                                targetBoundaryLength = Math.Max(
                                    targetBoundaryLength,
                                    previousTargetBoundaryLength +
                                        requiredTransitionStep);
                            }

                            var candidateSetting = orderedLayer.Setting.Copy();
                            EnsureBoundarySegmentMinimumLength(
                                candidateSetting,
                                useStartBoundary,
                                targetBoundaryLength);
                            var boundaryLengthChanged = Math.Abs(
                                targetBoundaryLength -
                                    orderedLayer.CurrentBoundaryLength) >
                                GeometryTolerance;
                            if (boundaryLengthChanged &&
                                (!LinkageRouteBendGeometry.HasNoAcuteAngle(
                                     candidateSetting.PointList,
                                     out _,
                                     out _) ||
                                 !LinkageRouteBendGeometry.HasNoSelfIntersection(
                                     candidateSetting.PointList,
                                     out _,
                                     out _)))
                            {
                                candidateIsValid = false;
                                break;
                            }

                            candidateSettingsBySource[orderedLayer.Setting] =
                                candidateSetting;
                            previousTargetBoundaryLength = targetBoundaryLength;
                            previousSourceSetting = orderedLayer.Setting;
                        }

                        if (!candidateIsValid)
                        {
                            continue;
                        }

                        var evaluationSettings = sectionSettings
                            .Select(planarSetting =>
                                candidateSettingsBySource.TryGetValue(
                                    planarSetting,
                                    out var candidateSetting)
                                    ? candidateSetting
                                    : planarSetting)
                            .ToList();
                        var remainingNeighborhoodIntersectionCount =
                            candidateSettingsBySource.Values.Sum(
                                candidateSetting =>
                                    CountLapNeighborhoodPositiveVolumeIntersections(
                                        candidateSetting,
                                        useStartBoundary,
                                        evaluationSettings,
                                        candidateSetting));
                        if (remainingNeighborhoodIntersectionCount > 0)
                        {
                            continue;
                        }

                        foreach (var candidateSettingPair in
                            candidateSettingsBySource)
                        {
                            if (Math.Abs(GetBoundarySegmentLength(
                                    candidateSettingPair.Key,
                                    useStartBoundary) -
                                GetBoundarySegmentLength(
                                    candidateSettingPair.Value,
                                    useStartBoundary)) <= GeometryTolerance)
                            {
                                continue;
                            }

                            candidateSettingPair.Key.PointList =
                                candidateSettingPair.Value.PointList;
                            adjustedSettingCount++;
                        }

                        return true;
                    }
                }
            }

            return true;
        }

        private sealed class PersistentNTransitionLayer
        {
            public PlanarBendBusbarSetting Setting { get; set; }
            public double CurrentBoundaryLength { get; set; }
            public double InteriorCoordinate { get; set; }
        }

        /// <summary>
        /// 异规格物理段边界若仅存在可消除的厚向缝隙，先尝试把相邻的
        /// 可分段偏移 N 排贴到实际搭接排上。单段 I 排不能在这里整体
        /// 移动，否则会同时破坏它的另一端搭接；I 排无法拆分时保留给
        /// 下一轮相邻链联动，仍无法闭合才由最终阶段生成垫块。
        /// 显式配置的中间间距属于用户要求，不在本校正中强行消除。
        /// </summary>
        internal static bool TrySeparateInternalPhysicalLapGaps(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out bool geometryAdjusted,
            out string failureReason)
        {
            return TrySeparateInternalPhysicalLapGapsCancelable(conversionResult, routeRequest,
                routeGroupRequest, out geometryAdjusted, out failureReason, CancellationToken.None);
        }

        internal static bool TrySeparateInternalPhysicalLapGapsCancelable(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest, LinkageRouteGroupRequest routeGroupRequest,
            out bool geometryAdjusted, out string failureReason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            geometryAdjusted = false;
            failureReason = null;
            if (conversionResult?.StackMembers == null ||
                conversionResult.CenterlineParts == null ||
                conversionResult.CenterlineParts.Count < 2 ||
                routeRequest == null || routeGroupRequest == null ||
                routeGroupRequest.StackMiddleSpace > GeometryTolerance)
            {
                return true;
            }

            if (!TryCreateProcessingStateFromExistingGeometry(
                    conversionResult,
                    out var processingState,
                    out failureReason))
            {
                // 缝隙校正属于后处理增强，状态无法重建时不能把普通搜索
                // 候选直接判失败，交由最终闭环校验报告明确原因。
                return true;
            }

            var allPhysicalSettings = conversionResult.StackMembers
                .Where(stackMember => stackMember?.PlanarParts != null)
                .SelectMany(stackMember => stackMember.PlanarParts)
                .Where(planarSetting => planarSetting?.PointList?.Count >= 2)
                .ToList();
            var physicalSectionCount = conversionResult.CenterlineParts.Count;
            foreach (var boundaryIndex in processingState
                .InternalSupportBoundaryIndices.OrderBy(index => index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (conversionResult.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(boundaryIndex) ||
                    !processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex,
                        out var precedingSettings) ||
                    !processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex + 1,
                    out var followingSettings) ||
                    precedingSettings.Count == 0 ||
                    followingSettings.Count == 0 ||
                    !TryGetLocalThicknessDirection(
                        precedingSettings[0].Setting,
                        precedingSettings[0].Setting.PointList.Count - 2,
                        out var boundaryThicknessDirection))
                {
                    continue;
                }

                boundaryThicknessDirection = boundaryThicknessDirection.Normalized;
                var lapPairs = new List<InternalSupportPair>();
                if (precedingSettings.Count == followingSettings.Count)
                {
                    var orderedPrecedingSettings = precedingSettings
                        .OrderBy(item => item.MemberIndex)
                        .ToList();
                    var orderedFollowingSettings = followingSettings
                        .OrderBy(item => item.MemberIndex)
                        .ToList();
                    for (var memberIndex = 0;
                        memberIndex < orderedPrecedingSettings.Count;
                        memberIndex++)
                    {
                        AddUniqueInternalSupportPair(
                            lapPairs,
                            orderedPrecedingSettings[memberIndex],
                            orderedFollowingSettings[memberIndex]);
                    }
                }
                else
                {
                    foreach (var precedingItem in precedingSettings)
                    {
                        var precedingEndpoint = precedingItem.Setting.PointList[
                            precedingItem.Setting.PointList.Count - 1];
                        var nearestFollowingItem = followingSettings
                            .OrderBy(item => Math.Abs(new Vector3d(
                                    precedingEndpoint,
                                    item.Setting.PointList[0])
                                .Dot(boundaryThicknessDirection)))
                            .First();
                        AddUniqueInternalSupportPair(
                            lapPairs,
                            precedingItem,
                            nearestFollowingItem);
                    }

                    foreach (var followingItem in followingSettings)
                    {
                        var followingEndpoint = followingItem.Setting.PointList[0];
                        var nearestPrecedingItem = precedingSettings
                            .OrderBy(item => Math.Abs(new Vector3d(
                                    item.Setting.PointList[
                                        item.Setting.PointList.Count - 1],
                                    followingEndpoint)
                                .Dot(boundaryThicknessDirection)))
                            .First();
                        AddUniqueInternalSupportPair(
                            lapPairs,
                            nearestPrecedingItem,
                            followingItem);
                    }
                }

                if (precedingSettings.Count != followingSettings.Count)
                {
                    AddAdjacentInternalSupportGapPairs(
                        lapPairs,
                        precedingSettings,
                        followingSettings,
                        boundaryThicknessDirection);
                }
                // 同数量的逐成员配对可能全部贴合，但下一段仍穿入相邻层。
                // 把真实投影相交的跨成员穿透也纳入修正，不能只检查 i→i。
                var crossLayerOverlapPairs = new HashSet<InternalSupportPair>();
                foreach (var crossPreceding in precedingSettings)
                foreach (var crossFollowing in followingSettings)
                {
                    var crossCenterDistance = Math.Abs(new Vector3d(
                        crossPreceding.Setting.PointList.Last(), crossFollowing.Setting.PointList[0])
                        .Dot(boundaryThicknessDirection));
                    if (crossCenterDistance + GeometryTolerance >=
                            (crossPreceding.Setting.BusBarThickness + crossFollowing.Setting.BusBarThickness) / 2d ||
                        lapPairs.Any(pair => pair.Preceding == crossPreceding && pair.Following == crossFollowing) ||
                        !TryCreateInternalSupportFootprint(crossPreceding.Setting, crossFollowing.Setting,
                            boundaryThicknessDirection, out _)) continue;
                    var overlapPair = new InternalSupportPair { Preceding = crossPreceding, Following = crossFollowing };
                    lapPairs.Add(overlapPair);
                    crossLayerOverlapPairs.Add(overlapPair);
                }
                foreach (var lapPair in lapPairs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var precedingSetting = lapPair.Preceding.Setting;
                    var followingSetting = lapPair.Following.Setting;
                    var precedingEndpoint = precedingSetting.PointList[
                        precedingSetting.PointList.Count - 1];
                    var followingEndpoint = followingSetting.PointList[0];
                    var signedCenterDistance = new Vector3d(
                            precedingEndpoint,
                            followingEndpoint)
                        .Dot(boundaryThicknessDirection);
                    // 两侧中心线可能在同一排厚层位起步，但实体宽度已经
                    // 产生正体积重叠。此时不能跳过边界：后续候选会同时
                    // 尝试正、反两个排厚方向，选择能通过实体和折弯余量
                    // 复核的一侧。默认取正方向只用于计算候选，刚性 I
                    // 排仍不会被整体移动。
                    var separationSign = Math.Sign(signedCenterDistance);
                    if (separationSign == 0)
                    {
                        separationSign = 1;
                    }
                    var requiredCenterDistance =
                        (precedingSetting.BusBarThickness +
                         followingSetting.BusBarThickness) / 2d;
                    var thicknessGap = Math.Abs(signedCenterDistance) -
                        requiredCenterDistance;
                    var hasFiniteSupportFootprint =
                        LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                            precedingSetting, false, followingSetting, true,
                            Math.Min(precedingSetting.BusBarWidth, followingSetting.BusBarWidth),
                            out _);
                    var requiresLapEndpointRecovery = !hasFiniteSupportFootprint &&
                        TryGetProjectedPerpendicularIntersection(
                            precedingSetting, precedingSetting.PointList.Count - 2,
                            followingSetting, 0, out _);
                    if (crossLayerOverlapPairs.Contains(lapPair) && thicknessGap >= -GeometryTolerance)
                        continue;
                    // 可分段偏移的 N 排仍优先闭合厚向缝隙；只有无法
                    // 转成 N 的刚性 I 排会被下面的候选过滤并保留缝隙，
                    // 最终再按实际搭接面补垫块。
                    if ((Math.Abs(thicknessGap) <= GeometryTolerance &&
                            !requiresLapEndpointRecovery) ||
                        (!requiresLapEndpointRecovery && !TryCreateInternalSupportFootprint(
                            precedingSetting,
                            followingSetting,
                            boundaryThicknessDirection,
                            out _)))
                    {
                        continue;
                    }

                    // 起始段和柜顶段的端部位置已经确定，边界处优先移动
                    // 中间物理段；对于同一边界的两个内部段，先尝试前段，
                    // 失败再尝试后段。单段 I 会被跳过，避免两端同时移位。
                    // 同一组异规格叠排既可能存在厚向缝隙，也可能因为实际
                    // 排厚变化形成正体积重叠；两种情况都只移动可折弯排的
                    // 搭接段，刚性 I 排保持原中心线和层位不变。
                    var movePrecedingFirst = boundaryIndex > 0 &&
                        boundaryIndex < physicalSectionCount - 2;
                    var nearestSignedCenterDistance = separationSign *
                        requiredCenterDistance;
                    var oppositeSignedCenterDistance = -separationSign *
                        requiredCenterDistance;
                    var precedingNearestDisplacement = signedCenterDistance -
                        nearestSignedCenterDistance;
                    var precedingOppositeDisplacement = signedCenterDistance -
                        oppositeSignedCenterDistance;
                    var followingNearestDisplacement =
                        nearestSignedCenterDistance - signedCenterDistance;
                    var followingOppositeDisplacement =
                        oppositeSignedCenterDistance - signedCenterDistance;
                    var moveCandidates = movePrecedingFirst
                        ? new[]
                        {
                            new InternalBoundaryMoveCandidate
                            {
                                Setting = precedingSetting,
                                MoveStart = false,
                                WorldDisplacement = precedingNearestDisplacement
                            },
                            new InternalBoundaryMoveCandidate
                            {
                                Setting = precedingSetting,
                                MoveStart = false,
                                WorldDisplacement = precedingOppositeDisplacement
                            },
                            new InternalBoundaryMoveCandidate
                            {
                                Setting = followingSetting,
                                MoveStart = true,
                                WorldDisplacement = followingNearestDisplacement
                            },
                            new InternalBoundaryMoveCandidate
                            {
                                Setting = followingSetting,
                                MoveStart = true,
                                WorldDisplacement = followingOppositeDisplacement
                            }
                        }
                        : boundaryIndex == 0
                            ? new[]
                            {
                                new InternalBoundaryMoveCandidate
                                {
                                    Setting = followingSetting,
                                    MoveStart = true,
                                    WorldDisplacement =
                                        followingNearestDisplacement
                                },
                                new InternalBoundaryMoveCandidate
                                {
                                    Setting = followingSetting,
                                    MoveStart = true,
                                    WorldDisplacement =
                                        followingOppositeDisplacement
                                },
                                new InternalBoundaryMoveCandidate
                                {
                                    Setting = precedingSetting,
                                    MoveStart = false,
                                    WorldDisplacement =
                                        precedingNearestDisplacement
                                },
                                new InternalBoundaryMoveCandidate
                                {
                                    Setting = precedingSetting,
                                    MoveStart = false,
                                    WorldDisplacement =
                                        precedingOppositeDisplacement
                                }
                            }
                            : new[]
                            {
                                new InternalBoundaryMoveCandidate
                                {
                                    Setting = precedingSetting,
                                    MoveStart = false,
                                    WorldDisplacement =
                                        precedingNearestDisplacement
                                },
                                new InternalBoundaryMoveCandidate
                                {
                                    Setting = precedingSetting,
                                    MoveStart = false,
                                    WorldDisplacement =
                                        precedingOppositeDisplacement
                                },
                                new InternalBoundaryMoveCandidate
                                {
                                    Setting = followingSetting,
                                    MoveStart = true,
                                    WorldDisplacement =
                                        followingNearestDisplacement
                                },
                                new InternalBoundaryMoveCandidate
                                {
                                    Setting = followingSetting,
                                    MoveStart = true,
                                    WorldDisplacement =
                                        followingOppositeDisplacement
                                }
                            };

                    // 优先尝试其它层的真实厚面边界，避免仅按等排厚步长
                    // 外扩时永远错过薄固定排形成的不等间距可用层位。
                    var originalBoundaryMoveCandidates = moveCandidates;
                    moveCandidates = moveCandidates.Concat(precedingSettings.SelectMany(host => new[] { -1d, 1d }
                        .Select(side => new InternalBoundaryMoveCandidate
                        {
                            Setting = followingSetting, MoveStart = true,
                            WorldDisplacement = new Vector3d(followingEndpoint, host.Setting.PointList.Last())
                                .Dot(boundaryThicknessDirection) + side *
                                (followingSetting.BusBarThickness + host.Setting.BusBarThickness) / 2d
                        }))).Concat(followingSettings.SelectMany(host => new[] { -1d, 1d }
                        .Select(side => new InternalBoundaryMoveCandidate
                        {
                            Setting = precedingSetting, MoveStart = false,
                            WorldDisplacement = new Vector3d(precedingEndpoint, host.Setting.PointList[0])
                                .Dot(boundaryThicknessDirection) + side *
                                (precedingSetting.BusBarThickness + host.Setting.BusBarThickness) / 2d
                        }))).ToArray();
                    // 最小贴合位移可能会让搭接段相邻的转折段继续穿入
                    // 刚性 I 排。沿同一排厚方向逐级尝试更外侧的层位，
                    // 每个候选仍需完整通过实体、搭接面和折弯余量复核；
                    // 这里只扩大实体化后的校正候选，不改变路径搜索。
                    var maximumLayerStep = Math.Max(
                        2,
                        allPhysicalSettings.Count + 2);
                    var moveCandidateList = moveCandidates
                        .Concat(Enumerable.Range(1, maximumLayerStep)
                            .SelectMany(layerStep => originalBoundaryMoveCandidates.SelectMany(
                                moveCandidate =>
                                {
                                    var layerDistance = layerStep * Math.Max(
                                        Math.Min(
                                            moveCandidate.Setting.BusBarThickness,
                                            moveCandidate.MoveStart
                                                ? followingSetting.BusBarThickness
                                                : precedingSetting.BusBarThickness),
                                        1d);
                                    return new[]
                                    {
                                        new InternalBoundaryMoveCandidate
                                        {
                                            Setting = moveCandidate.Setting,
                                            MoveStart = moveCandidate.MoveStart,
                                            WorldDisplacement =
                                                moveCandidate.WorldDisplacement -
                                                layerDistance
                                        },
                                        new InternalBoundaryMoveCandidate
                                        {
                                            Setting = moveCandidate.Setting,
                                            MoveStart = moveCandidate.MoveStart,
                                            WorldDisplacement =
                                                moveCandidate.WorldDisplacement +
                                                layerDistance
                                        }
                                    };
                                })))
                        .ToList();

                    foreach (var moveCandidate in moveCandidateList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (moveCandidate.Setting?.PointList?.Count <= 2)
                        {
                            // 内部自由 I 排同样可整体移动并向后传递；固定端
                            // I 排不动。这里只试算一个候选，失败恢复该试算副本。
                            if (moveCandidate.MoveStart && Math.Abs(moveCandidate.WorldDisplacement) > GeometryTolerance)
                            {
                                var movableMember = conversionResult.StackMembers.FirstOrDefault(member =>
                                    member.PlanarParts.Contains(moveCandidate.Setting));
                                var movablePartIndex = movableMember?.PlanarParts.IndexOf(moveCandidate.Setting) ?? -1;
                                if (movablePartIndex >= 1 && movableMember.PlanarParts.Skip(movablePartIndex + 1)
                                    .Any(part => part.PointList.Count > 2))
                                {
                                    var originalMemberPoints = movableMember.PlanarParts.Select(part => part.PointList
                                        .Select(point => new Point3d(point.X, point.Y, point.Z)).ToList()).ToList();
                                    if (LinkageRouteTerminalAccessoryBuilder.TryMoveStartTerminalLapChain(conversionResult,
                                            movableMember, movablePartIndex, boundaryThicknessDirection,
                                            moveCandidate.WorldDisplacement, out _) &&
                                        CountLapSegmentPositiveVolumeIntersections(moveCandidate.Setting, 0,
                                            allPhysicalSettings, moveCandidate.Setting) == 0 &&
                                        TryCreateInternalSupportFootprint(precedingSetting, followingSetting,
                                            boundaryThicknessDirection, out _))
                                    {
                                        geometryAdjusted = true;
                                        conversionResult.Diagnostics.Add("内部跨层干涉：自由 I 排已整体移动并向后续非 I 排传递位移。");
                                        return true;
                                    }
                                    for (var restoreIndex = 0; restoreIndex < movableMember.PlanarParts.Count; restoreIndex++)
                                        movableMember.PlanarParts[restoreIndex].PointList = originalMemberPoints[restoreIndex];
                                }
                            }
                            continue;
                        }

                        if (Math.Abs(moveCandidate.WorldDisplacement) <=
                            GeometryTolerance && !requiresLapEndpointRecovery)
                        {
                            continue;
                        }

                        var segmentIndex = moveCandidate.MoveStart ? 0 :
                            moveCandidate.Setting.PointList.Count - 2;
                        if (!TryGetLocalThicknessDirection(
                                moveCandidate.Setting,
                                segmentIndex,
                                out var localThicknessDirection))
                        {
                            continue;
                        }

                        var localDirectionDot = localThicknessDirection.Normalized
                            .Dot(boundaryThicknessDirection);
                        // 斜向 N 排的局部厚向可能相对刚性 I 排发生旋转；
                        // 只要在共同边界厚向上仍有稳定投影，就按该投影
                        // 反算局部偏移量，不能因方向不完全平行而漏掉
                        // 1~数毫米的实体重叠校正候选。
                        if (Math.Abs(localDirectionDot) < 0.5d)
                        {
                            continue;
                        }

                        var offsetDistance = moveCandidate.WorldDisplacement *
                            (1d / localDirectionDot);
                        var segmentOffsetDistances = Enumerable.Repeat(
                                0d,
                                moveCandidate.Setting.PointList.Count - 1)
                            .ToArray();
                        segmentOffsetDistances[segmentIndex] = offsetDistance;
                        if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(
                                moveCandidate.Setting,
                                segmentOffsetDistances,
                                true,
                                out var adjustedPointList,
                                out _))
                        {
                            continue;
                        }

                        var adjustedSetting = moveCandidate.Setting.Copy();
                        adjustedSetting.PointList = adjustedPointList;
                        if (requiresLapEndpointRecovery)
                        {
                            var opposingSetting = moveCandidate.MoveStart
                                ? precedingSetting : followingSetting;
                            if (!TryExtendPerpendicularBoundaryEndpoint(
                                    adjustedSetting, moveCandidate.MoveStart,
                                    opposingSetting, opposingSetting.BusBarWidth))
                            {
                                continue;
                            }
                        }
                        var adjustedLapSegmentIndex = moveCandidate.MoveStart
                            ? 0
                            : adjustedPointList.Count - 2;
                        var candidateIntersectionCount =
                            CountLapSegmentPositiveVolumeIntersections(
                                adjustedSetting,
                                adjustedLapSegmentIndex,
                                allPhysicalSettings,
                                moveCandidate.Setting);
                        var candidateHasSupportFootprint = requiresLapEndpointRecovery
                            ? LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                                moveCandidate.MoveStart ? precedingSetting : adjustedSetting,
                                false,
                                moveCandidate.MoveStart ? adjustedSetting : followingSetting,
                                true,
                                Math.Min(precedingSetting.BusBarWidth, followingSetting.BusBarWidth),
                                out _)
                            : TryCreateInternalSupportFootprint(
                                moveCandidate.MoveStart
                                    ? precedingSetting
                                    : adjustedSetting,
                                moveCandidate.MoveStart
                                    ? adjustedSetting
                                    : followingSetting,
                                boundaryThicknessDirection,
                                out _);
                        if (candidateIntersectionCount > 0 ||
                            !candidateHasSupportFootprint)
                        {
                            continue;
                        }

                        // 只偏移搭接直线段时，Offsetter 会用相邻斜段把它
                        // 接回原层位。若斜段在离开搭接排宽包络前就开始回
                        // 位，虽然搭接段本身已贴面，斜段仍可能穿过刚性 I
                        // 排。此时只沿搭接段方向后移该局部转折点，保持 I
                        // 排、远端路径和层位不动，直到搭接段及其相邻过渡段
                        // 都不再与其他实体发生正体积重叠。
                        // 同一物理段的其他可调 N 排此刻可能仍处在旧层位；
                        // 它们会在后续成员配对中联动校正，不能用这个瞬时
                        // 状态阻止当前 N 排先贴到对应刚性排。其他物理段和
                        // 刚性排仍须参与当前过渡段实体门禁。
                        var adjustableSectionSettings = new HashSet<
                            PlanarBendBusbarSetting>(
                            (moveCandidate.MoveStart
                                ? followingSettings
                                : precedingSettings)
                            .Select(sectionSetting => sectionSetting.Setting));
                        var transitionComparisonSettings = allPhysicalSettings
                            .Where(otherSetting =>
                                !adjustableSectionSettings.Contains(otherSetting))
                            .ToList();
                        if (!TryMoveLapTransitionBeyondIntersections(
                                adjustedSetting,
                                moveCandidate.MoveStart,
                                transitionComparisonSettings,
                                moveCandidate.Setting,
                                (moveCandidate.MoveStart
                                    ? precedingSetting.BusBarWidth
                                    : followingSetting.BusBarWidth) +
                                LinkageRouteStackGeometry.GetStackBendAllowance(
                                    adjustedSetting.BusBarThickness,
                                    moveCandidate.MoveStart
                                        ? followingSettings.Count
                                        : precedingSettings.Count,
                                    routeGroupRequest.StackMiddleSpace,
                                    routeRequest.GetEffectiveBendAllowance()),
                                out var transitionExtensionDistance))
                        {
                            continue;
                        }

                        moveCandidate.Setting.PointList = adjustedSetting.PointList;
                        geometryAdjusted = true;
                        if (requiresLapEndpointRecovery)
                        {
                            conversionResult.Diagnostics.Add(
                                "内部垂直搭接端头已按真实对侧排宽恢复，厚向移位与端头延伸在同一副本中通过实体复核。");
                        }
                        conversionResult.Diagnostics.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            thicknessGap > 0d
                                ? "第 {0} 个内部搭接面第 {1}→{2} 层已沿排厚方向闭合 {3:F3} mm 缝隙，局部过渡后移 {4:F3} mm，未生成多余垫块。"
                                : "第 {0} 个内部搭接面第 {1}→{2} 层已沿排厚方向消除 {3:F3} mm 正体积重叠，局部过渡后移 {4:F3} mm，刚性 I 排未移动。",
                            boundaryIndex + 1,
                            lapPair.Preceding.MemberIndex + 1,
                            lapPair.Following.MemberIndex + 1,
                            Math.Abs(thicknessGap),
                            transitionExtensionDistance));
                        return true;
                    }
                }
            }

            return true;
        }

        internal static bool TryMoveLapTransitionBeyondIntersections(
            PlanarBendBusbarSetting adjustedSetting,
            bool moveStart,
            IEnumerable<PlanarBendBusbarSetting> otherSettings,
            PlanarBendBusbarSetting sourceSettingToExclude,
            double requiredLapSegmentLength,
            out double transitionExtensionDistance)
        {
            transitionExtensionDistance = 0d;
            // 三点排只有一个公共转折点，不能为了单侧折弯余量移动它；
            // 与既有端部余量规则一致，仅四点及以上、能独立调整搭接侧
            // 转折点的可折弯排在这里强制恢复实际排宽折弯余量。
            var normalizedRequiredLapSegmentLength =
                adjustedSetting?.PointList?.Count >= 4
                    ? Math.Max(0d, requiredLapSegmentLength)
                    : 0d;
            if (adjustedSetting?.PointList == null ||
                adjustedSetting.PointList.Count < 3)
            {
                return CountLapNeighborhoodPositiveVolumeIntersections(
                    adjustedSetting,
                    moveStart,
                    otherSettings,
                    sourceSettingToExclude) == 0 &&
                    GetBoundarySegmentLength(adjustedSetting, moveStart) +
                        GeometryTolerance >= normalizedRequiredLapSegmentLength;
            }

            if (CountLapNeighborhoodPositiveVolumeIntersections(
                    adjustedSetting,
                    moveStart,
                    otherSettings,
                    sourceSettingToExclude) == 0 &&
                GetBoundarySegmentLength(adjustedSetting, moveStart) +
                    GeometryTolerance >= normalizedRequiredLapSegmentLength)
            {
                return true;
            }

            var pointCount = adjustedSetting.PointList.Count;
            var lapStartIndex = moveStart ? 0 : pointCount - 2;
            var lapEndIndex = moveStart ? 1 : pointCount - 1;
            var transitionPointIndex = moveStart ? 1 : pointCount - 2;
            var adjacentInteriorPointIndex = moveStart ? 2 : pointCount - 3;
            var lapDirection = new Vector3d(
                adjustedSetting.PointList[lapStartIndex],
                adjustedSetting.PointList[lapEndIndex]);
            if (lapDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            lapDirection = lapDirection.Normalized;
            var transitionMoveDirection = moveStart
                ? lapDirection
                : new Vector3d(
                    -lapDirection.X,
                    -lapDirection.Y,
                    -lapDirection.Z);
            var transitionPoint = adjustedSetting.PointList[
                transitionPointIndex];
            var adjacentInteriorPoint = adjustedSetting.PointList[
                adjacentInteriorPointIndex];
            var minimumRemainingTransitionLength = Math.Max(
                1d,
                2d * (Math.Max(0d, adjustedSetting.BarBendingRadius) +
                    adjustedSetting.BusBarThickness));
            // 斜过渡通常主要沿另一个坐标方向延伸，不能只用它在搭接
            // 直段方向上的投影作为可移动余量；投影接近零时仍可能有
            // 数百毫米真实长度。按欧氏长度给出保守上限，并继续由锐角、
            // 自交和实体复核逐毫米筛选。
            var maximumExtensionDistance = transitionPoint.DistanceTo(
                    adjacentInteriorPoint) -
                minimumRemainingTransitionLength;
            if (maximumExtensionDistance <= GeometryTolerance)
            {
                return false;
            }

            var requiredLengthExtension = Math.Max(
                0d,
                normalizedRequiredLapSegmentLength -
                    GetBoundarySegmentLength(adjustedSetting, moveStart));
            var firstExtensionDistance = requiredLengthExtension >
                    GeometryTolerance
                ? requiredLengthExtension
                : 1d;
            for (var extensionDistance = firstExtensionDistance;
                extensionDistance <= maximumExtensionDistance +
                    GeometryTolerance;
                extensionDistance += 1d)
            {
                var candidateSetting = adjustedSetting.Copy();
                var candidateExtensionDistance = Math.Min(
                    extensionDistance,
                    maximumExtensionDistance);
                candidateSetting.PointList[transitionPointIndex] = new Point3d(
                    transitionPoint.X + transitionMoveDirection.X *
                        candidateExtensionDistance,
                    transitionPoint.Y + transitionMoveDirection.Y *
                        candidateExtensionDistance,
                    transitionPoint.Z + transitionMoveDirection.Z *
                        candidateExtensionDistance);
                if (!LinkageRouteBendGeometry.HasNoAcuteAngle(
                        candidateSetting.PointList,
                        out _,
                        out _) ||
                    !LinkageRouteBendGeometry.HasNoSelfIntersection(
                        candidateSetting.PointList,
                        out _,
                        out _) ||
                    CountLapNeighborhoodPositiveVolumeIntersections(
                        candidateSetting,
                        moveStart,
                        otherSettings,
                        sourceSettingToExclude) > 0)
                {
                    continue;
                }

                adjustedSetting.PointList = candidateSetting.PointList;
                transitionExtensionDistance = Math.Min(
                    extensionDistance,
                    maximumExtensionDistance);
                return true;
            }

            return false;
        }

        private static double GetBoundarySegmentLength(
            PlanarBendBusbarSetting planarSetting,
            bool useStartBoundary)
        {
            if (planarSetting?.PointList == null ||
                planarSetting.PointList.Count < 2)
            {
                return 0d;
            }

            var segmentStartIndex = useStartBoundary
                ? 0
                : planarSetting.PointList.Count - 2;
            return planarSetting.PointList[segmentStartIndex].DistanceTo(
                planarSetting.PointList[segmentStartIndex + 1]);
        }

        private static int CountLapNeighborhoodPositiveVolumeIntersections(
            PlanarBendBusbarSetting adjustedSetting,
            bool useStartLap,
            IEnumerable<PlanarBendBusbarSetting> otherSettings,
            PlanarBendBusbarSetting sourceSettingToExclude)
        {
            if (adjustedSetting?.PointList == null ||
                adjustedSetting.PointList.Count < 2)
            {
                return int.MaxValue;
            }

            var lastSegmentIndex = adjustedSetting.PointList.Count - 2;
            var neighborhoodSegmentIndices = useStartLap
                ? new[] { 0, Math.Min(1, lastSegmentIndex) }
                : new[]
                {
                    lastSegmentIndex,
                    Math.Max(0, lastSegmentIndex - 1)
                };
            var uniqueSegmentIndices = neighborhoodSegmentIndices.Distinct();
            var intersectionCount = 0;
            foreach (var neighborhoodSegmentIndex in uniqueSegmentIndices)
            {
                intersectionCount += CountLapSegmentPositiveVolumeIntersections(
                    adjustedSetting,
                    neighborhoodSegmentIndex,
                    otherSettings,
                    sourceSettingToExclude);
            }

            return intersectionCount;
        }

        private sealed class InternalBoundaryMoveCandidate
        {
            public PlanarBendBusbarSetting Setting { get; set; }
            public bool MoveStart { get; set; }
            public double WorldDisplacement { get; set; }
        }

        private static int CountLapSegmentPositiveVolumeIntersections(
            PlanarBendBusbarSetting lapSetting,
            int lapSegmentIndex,
            IEnumerable<PlanarBendBusbarSetting> otherSettings,
            PlanarBendBusbarSetting sourceSettingToExclude = null)
        {
            if (!TryCreateLapSegmentSolid(
                    lapSetting,
                    lapSegmentIndex,
                    out var lapSegmentSolid))
            {
                return int.MaxValue;
            }

            var intersectionCount = 0;
            foreach (var otherSetting in otherSettings ??
                Enumerable.Empty<PlanarBendBusbarSetting>())
            {
                if (ReferenceEquals(otherSetting, lapSetting) ||
                    ReferenceEquals(otherSetting, sourceSettingToExclude) ||
                    otherSetting?.PointList == null)
                {
                    continue;
                }

                for (var otherSegmentIndex = 0;
                    otherSegmentIndex < otherSetting.PointList.Count - 1;
                    otherSegmentIndex++)
                {
                    if (TryCreateLapSegmentSolid(
                            otherSetting,
                            otherSegmentIndex,
                            out var otherSegmentSolid) &&
                        LinkageRouteSolidDistance.HasPositiveVolumeIntersection(
                            lapSegmentSolid,
                            otherSegmentSolid))
                    {
                        intersectionCount++;
                        break;
                    }
                }
            }

            return intersectionCount;
        }

        /// <summary>
        /// 内部垫块只填充两段真实铜排之间尚未被其他铜排占用的厚向缝隙。
        /// 若垫块与非关联物理排发生正体积干涉，则在原搭接面内沿排宽方向
        /// 裁去已被铜排占用的区间，保留最大的连续有效填充区。
        /// </summary>
        internal static bool TryTrimInternalSupportBlocksAroundPhysicalIntersections(
            LinkageRouteCenterlineConversionResult conversionResult,
            out bool supportGeometryAdjusted,
            out string failureReason)
        {
            supportGeometryAdjusted = false;
            failureReason = null;
            if (conversionResult?.Accessories == null ||
                conversionResult.StackMembers == null)
            {
                return true;
            }

            var physicalSettings = conversionResult.StackMembers
                .Where(stackMember => stackMember?.PlanarParts != null)
                .SelectMany(stackMember => stackMember.PlanarParts)
                .Where(planarSetting => planarSetting?.PointList?.Count >= 2)
                .ToList();
            var physicalSolids = new List<LinkageRouteSolid>();
            foreach (var physicalSetting in physicalSettings)
            {
                for (var segmentIndex = 0;
                    segmentIndex < physicalSetting.PointList.Count - 1;
                    segmentIndex++)
                {
                    if (TryCreateLapSegmentSolid(
                            physicalSetting,
                            segmentIndex,
                            out var physicalSolid))
                    {
                        physicalSolids.Add(physicalSolid);
                    }
                }
            }

            foreach (var supportAccessory in conversionResult.Accessories
                .Where(routeAccessory =>
                    routeAccessory?.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                    !string.IsNullOrWhiteSpace(routeAccessory.SupportBlockKey) &&
                    routeAccessory.Setting?.PointList?.Count >= 2)
                .ToList())
            {
                var supportSetting = supportAccessory.Setting;
                if (supportSetting.WidthVector == null ||
                    supportSetting.WidthVector.Norm <= GeometryTolerance ||
                    !TryCreateLapSegmentSolid(
                        supportSetting,
                        0,
                        out var supportSolid))
                {
                    continue;
                }

                var intersectingPhysicalSolids = physicalSolids
                    .Where(physicalSolid => LinkageRouteSolidDistance
                        .HasPositiveVolumeIntersection(
                            supportSolid,
                            physicalSolid))
                    .ToList();
                if (intersectingPhysicalSolids.Count == 0)
                {
                    continue;
                }

                var normalizedWidthDirection = supportSetting.WidthVector.Normalized;
                var supportCenterCoordinate = DotPoint(
                    supportSetting.PointList[0],
                    normalizedWidthDirection);
                var supportMinimumCoordinate = supportCenterCoordinate -
                    supportSetting.BusBarWidth / 2d;
                var supportMaximumCoordinate = supportCenterCoordinate +
                    supportSetting.BusBarWidth / 2d;
                var blockedIntervals = intersectingPhysicalSolids
                    .Select(physicalSolid =>
                    {
                        var projectedCoordinates = physicalSolid.GetVertices()
                            .Select(vertex => DotPoint(
                                vertex,
                                normalizedWidthDirection))
                            .ToList();
                        return new[]
                        {
                            Math.Max(
                                supportMinimumCoordinate,
                                projectedCoordinates.Min()),
                            Math.Min(
                                supportMaximumCoordinate,
                                projectedCoordinates.Max())
                        };
                    })
                    .Where(interval => interval[1] - interval[0] >
                        GeometryTolerance)
                    .OrderBy(interval => interval[0])
                    .ToList();
                if (blockedIntervals.Count == 0)
                {
                    continue;
                }

                var mergedBlockedIntervals = new List<double[]>();
                foreach (var blockedInterval in blockedIntervals)
                {
                    var lastMergedInterval = mergedBlockedIntervals.LastOrDefault();
                    if (lastMergedInterval == null ||
                        blockedInterval[0] > lastMergedInterval[1] +
                            GeometryTolerance)
                    {
                        mergedBlockedIntervals.Add(new[]
                        {
                            blockedInterval[0],
                            blockedInterval[1]
                        });
                    }
                    else
                    {
                        lastMergedInterval[1] = Math.Max(
                            lastMergedInterval[1],
                            blockedInterval[1]);
                    }
                }

                var availableIntervals = new List<double[]>();
                var availableStartCoordinate = supportMinimumCoordinate;
                foreach (var blockedInterval in mergedBlockedIntervals)
                {
                    if (blockedInterval[0] - availableStartCoordinate >
                        GeometryTolerance)
                    {
                        availableIntervals.Add(new[]
                        {
                            availableStartCoordinate,
                            blockedInterval[0]
                        });
                    }

                    availableStartCoordinate = Math.Max(
                        availableStartCoordinate,
                        blockedInterval[1]);
                }
                if (supportMaximumCoordinate - availableStartCoordinate >
                    GeometryTolerance)
                {
                    availableIntervals.Add(new[]
                    {
                        availableStartCoordinate,
                        supportMaximumCoordinate
                    });
                }

                PlanarBendBusbarSetting bestTrimmedSetting = null;
                var bestRetainedWidth = 0d;
                foreach (var availableInterval in availableIntervals
                    .OrderByDescending(interval => interval[1] - interval[0]))
                {
                    var retainedWidth = availableInterval[1] -
                        availableInterval[0];
                    var retainedCenterCoordinate =
                        (availableInterval[0] + availableInterval[1]) / 2d;
                    var centerDisplacement = retainedCenterCoordinate -
                        supportCenterCoordinate;
                    var trimmedSetting = supportSetting.Copy();
                    trimmedSetting.PointList = supportSetting.PointList
                        .Select(point => TranslatePoint(
                            point,
                            normalizedWidthDirection,
                            centerDisplacement))
                        .ToList();
                    trimmedSetting.BusBarWidth = retainedWidth;
                    trimmedSetting.StartLapWidth = retainedWidth;
                    trimmedSetting.EndLapWidth = retainedWidth;
                    if (!TryCreateLapSegmentSolid(
                            trimmedSetting,
                            0,
                            out var trimmedSupportSolid) ||
                        physicalSolids.Any(physicalSolid =>
                            LinkageRouteSolidDistance
                                .HasPositiveVolumeIntersection(
                                    trimmedSupportSolid,
                                    physicalSolid)))
                    {
                        continue;
                    }

                    bestTrimmedSetting = trimmedSetting;
                    bestRetainedWidth = retainedWidth;
                    break;
                }

                if (bestTrimmedSetting == null)
                {
                    if (!TrySplitSupportBlockThicknessAroundPhysicalIntersections(
                            supportSetting,
                            intersectingPhysicalSolids,
                            physicalSolids,
                            out var retainedThicknessSettings))
                    {
                        failureReason = "内部垫块与实体排干涉，本轮无法在实际搭接面内裁出连续填充区域。";
                        continue;
                    }

                    conversionResult.Accessories.Remove(supportAccessory);
                    for (var retainedSettingIndex = 0;
                        retainedSettingIndex < retainedThicknessSettings.Count;
                        retainedSettingIndex++)
                    {
                        conversionResult.Accessories.Add(
                            new LinkageRouteAccessory
                            {
                                Kind = supportAccessory.Kind,
                                TerminalSide = supportAccessory.TerminalSide,
                                RelatedGeneratedMemberIndex = supportAccessory
                                    .RelatedGeneratedMemberIndex,
                                Setting = retainedThicknessSettings[
                                    retainedSettingIndex],
                                SupportBlockKey = supportAccessory.SupportBlockKey +
                                    "|piece:" + retainedSettingIndex
                            });
                    }
                    supportGeometryAdjusted = true;
                    conversionResult.Diagnostics.Add(
                        retainedThicknessSettings.Count == 0
                            ? "内部垫块所在厚向缝隙已被其他实体排连续填满，已移除重复垫块。"
                            : string.Format(
                                CultureInfo.InvariantCulture,
                                "内部垫块已避开实体排并拆成 {0} 个连续厚向填充段。",
                                retainedThicknessSettings.Count));
                    continue;
                }

                supportAccessory.Setting = bestTrimmedSetting;
                supportGeometryAdjusted = true;
                conversionResult.Diagnostics.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "内部垫块已按其他实体排占用区域裁剪，保留连续排宽 {0:F3} mm。",
                    bestRetainedWidth));
            }

            RemoveDuplicateInternalSupportBlocks(conversionResult, ref supportGeometryAdjusted);
            return true;
        }

        private static void RemoveDuplicateInternalSupportBlocks(
            LinkageRouteCenterlineConversionResult conversionResult,
            ref bool supportGeometryAdjusted)
        {
            // 同一接口可由同段夹缝与跨段搭接两种关系描述；长宽互换的
            // I 型中心线也可能表示完全相同的长方体。只合并八顶点一致的
            // 垫块，部分重叠仍交给最终实体门禁拒绝，不能直接删除整块。
            var retainedSupports = new List<KeyValuePair<
                LinkageRouteAccessory, LinkageRouteSolid>>();
            var orderedSupports = conversionResult.Accessories
                .Where(accessory => accessory?.Kind ==
                        LinkageRouteAccessoryKind.SupportBlock &&
                    accessory.SupportBlockKey?.StartsWith(
                        "boundary:", StringComparison.Ordinal) == true &&
                    accessory.Setting?.PointList?.Count == 2)
                .OrderBy(accessory => accessory.SupportBlockKey.IndexOf(
                    "|same:", StringComparison.Ordinal) >= 0 ? 1 : 0)
                .ToList();
            foreach (var supportAccessory in orderedSupports)
            {
                if (!TryCreateLapSegmentSolid(
                        supportAccessory.Setting, 0, out var supportSolid))
                {
                    continue;
                }

                var supportBoundaryKey = supportAccessory.SupportBlockKey
                    .Split('|')[0];
                var supportVertices = supportSolid.GetVertices();
                var duplicatesExistingSupport = retainedSupports.Any(retained =>
                    string.Equals(
                        retained.Key.SupportBlockKey.Split('|')[0],
                        supportBoundaryKey, StringComparison.Ordinal) &&
                    retained.Value.Center.DistanceTo(supportSolid.Center) <=
                        GeometryTolerance &&
                    supportVertices.All(vertex => retained.Value.GetVertices()
                        .Any(retainedVertex => retainedVertex.DistanceTo(vertex) <=
                            GeometryTolerance)));
                if (duplicatesExistingSupport)
                {
                    conversionResult.Accessories.Remove(supportAccessory);
                    supportGeometryAdjusted = true;
                    conversionResult.Diagnostics.Add(
                        "同一内部搭接接口的重复垫块实体已合并，保留完整实际填充体积。");
                    continue;
                }

                retainedSupports.Add(new KeyValuePair<
                    LinkageRouteAccessory, LinkageRouteSolid>(
                        supportAccessory, supportSolid));
            }
        }

        private static bool
            TrySplitSupportBlockThicknessAroundPhysicalIntersections(
            PlanarBendBusbarSetting supportSetting,
            IReadOnlyList<LinkageRouteSolid> intersectingPhysicalSolids,
            IReadOnlyList<LinkageRouteSolid> allPhysicalSolids,
            out List<PlanarBendBusbarSetting> retainedSettings)
        {
            retainedSettings = new List<PlanarBendBusbarSetting>();
            if (supportSetting?.PointList == null ||
                supportSetting.PointList.Count < 2 ||
                !TryGetLocalThicknessDirection(
                    supportSetting,
                    0,
                    out var supportThicknessDirection))
            {
                return false;
            }

            supportThicknessDirection = supportThicknessDirection.Normalized;
            var supportCenterCoordinate = DotPoint(
                supportSetting.PointList[0],
                supportThicknessDirection);
            var supportMinimumCoordinate = supportCenterCoordinate -
                supportSetting.BusBarThickness / 2d;
            var supportMaximumCoordinate = supportCenterCoordinate +
                supportSetting.BusBarThickness / 2d;
            var blockedIntervals = (intersectingPhysicalSolids ??
                    Array.Empty<LinkageRouteSolid>())
                .Select(physicalSolid =>
                {
                    var projectedCoordinates = physicalSolid.GetVertices()
                        .Select(vertex => DotPoint(
                            vertex,
                            supportThicknessDirection))
                        .ToList();
                    return new[]
                    {
                        Math.Max(
                            supportMinimumCoordinate,
                            projectedCoordinates.Min()),
                        Math.Min(
                            supportMaximumCoordinate,
                            projectedCoordinates.Max())
                    };
                })
                .Where(interval => interval[1] - interval[0] >
                    GeometryTolerance)
                .OrderBy(interval => interval[0])
                .ToList();
            if (blockedIntervals.Count == 0)
            {
                return false;
            }

            var mergedBlockedIntervals = new List<double[]>();
            foreach (var blockedInterval in blockedIntervals)
            {
                var lastMergedInterval = mergedBlockedIntervals.LastOrDefault();
                if (lastMergedInterval == null ||
                    blockedInterval[0] > lastMergedInterval[1] +
                        GeometryTolerance)
                {
                    mergedBlockedIntervals.Add(new[]
                    {
                        blockedInterval[0],
                        blockedInterval[1]
                    });
                }
                else
                {
                    lastMergedInterval[1] = Math.Max(
                        lastMergedInterval[1],
                        blockedInterval[1]);
                }
            }

            var availableIntervals = new List<double[]>();
            var availableStartCoordinate = supportMinimumCoordinate;
            foreach (var blockedInterval in mergedBlockedIntervals)
            {
                if (blockedInterval[0] - availableStartCoordinate >
                    GeometryTolerance)
                {
                    availableIntervals.Add(new[]
                    {
                        availableStartCoordinate,
                        blockedInterval[0]
                    });
                }
                availableStartCoordinate = Math.Max(
                    availableStartCoordinate,
                    blockedInterval[1]);
            }
            if (supportMaximumCoordinate - availableStartCoordinate >
                GeometryTolerance)
            {
                availableIntervals.Add(new[]
                {
                    availableStartCoordinate,
                    supportMaximumCoordinate
                });
            }

            foreach (var availableInterval in availableIntervals)
            {
                var retainedThickness = availableInterval[1] -
                    availableInterval[0];
                var retainedCenterCoordinate =
                    (availableInterval[0] + availableInterval[1]) / 2d;
                var centerDisplacement = retainedCenterCoordinate -
                    supportCenterCoordinate;
                var retainedSetting = supportSetting.Copy();
                retainedSetting.PointList = supportSetting.PointList
                    .Select(point => TranslatePoint(
                        point,
                        supportThicknessDirection,
                        centerDisplacement))
                    .ToList();
                retainedSetting.BusBarThickness = retainedThickness;
                if (!TryCreateLapSegmentSolid(
                        retainedSetting,
                        0,
                        out var retainedSolid) ||
                    (allPhysicalSolids ?? Array.Empty<LinkageRouteSolid>())
                        .Any(physicalSolid => LinkageRouteSolidDistance
                            .HasPositiveVolumeIntersection(
                                retainedSolid,
                                physicalSolid)))
                {
                    return false;
                }
                retainedSettings.Add(retainedSetting);
            }

            return true;
        }

        private static bool TryCreateLapSegmentSolid(
            PlanarBendBusbarSetting planarSetting,
            int segmentIndex,
            out LinkageRouteSolid lapSegmentSolid)
        {
            lapSegmentSolid = null;
            if (planarSetting?.PointList == null ||
                segmentIndex < 0 ||
                segmentIndex >= planarSetting.PointList.Count - 1 ||
                planarSetting.WidthVector == null ||
                planarSetting.WidthVector.Norm <= GeometryTolerance)
            {
                return false;
            }

            var segmentStart = planarSetting.PointList[segmentIndex];
            var segmentEnd = planarSetting.PointList[segmentIndex + 1];
            var segmentDirection = new Vector3d(segmentStart, segmentEnd);
            if (segmentDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            var normalizedWidthDirection = planarSetting.WidthVector.Normalized;
            var thicknessDirection = segmentDirection.Normalized.Cross(
                normalizedWidthDirection);
            if (thicknessDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            lapSegmentSolid = LinkageRouteSolidGeometry.CreateSegmentSolid(
                "SECTION-SPECIFICATION-LAP",
                segmentStart,
                segmentEnd,
                normalizedWidthDirection,
                thicknessDirection.Normalized,
                planarSetting.BusBarWidth,
                planarSetting.BusBarThickness,
                segmentIndex,
                "SECTION-SPECIFICATION-LAP");
            return true;
        }

        private static void AlignStraightSingleSectionBoundaryCoordinates(
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            IReadOnlyList<double> startBoundaryCoordinates,
            ref double[] endBoundaryCoordinates,
            double minimumInterMemberGap,
            out double[] alignedStartBoundaryCoordinates)
        {
            var bridgeGeneratedMemberIndex = startTerminalPlacement
                ?.DoubleStartHorizontalBridgeGeneratedMemberIndex;
            if (bridgeGeneratedMemberIndex.HasValue &&
                bridgeGeneratedMemberIndex.Value >= 0 &&
                bridgeGeneratedMemberIndex.Value <
                    (startBoundaryCoordinates?.Count ?? 0) &&
                bridgeGeneratedMemberIndex.Value <
                    (endBoundaryCoordinates?.Length ?? 0))
            {
                var sharedIBarLayerDisplacement =
                    startBoundaryCoordinates[
                        bridgeGeneratedMemberIndex.Value] -
                    endBoundaryCoordinates[
                        bridgeGeneratedMemberIndex.Value];
                if (startBoundaryCoordinates.Count == 2)
                {
                    // 两根排时先锁定实际桥接的 I 排；另一根排保留柜顶侧
                    // 层位，后续由中间搭接边界计算贴合，避免整体平移造成串位。
                    var twoMemberAlignedBoundaryCoordinates =
                        endBoundaryCoordinates.ToArray();
                    twoMemberAlignedBoundaryCoordinates[
                        bridgeGeneratedMemberIndex.Value] +=
                        sharedIBarLayerDisplacement;
                    var otherMemberIndex =
                        bridgeGeneratedMemberIndex.Value == 0 ? 1 : 0;
                    var currentInterMemberGap = Math.Abs(
                        twoMemberAlignedBoundaryCoordinates[otherMemberIndex] -
                        twoMemberAlignedBoundaryCoordinates[
                            bridgeGeneratedMemberIndex.Value]);
                    if (currentInterMemberGap + GeometryTolerance <
                        minimumInterMemberGap)
                    {
                        var memberLayerDirection = Math.Sign(
                            startBoundaryCoordinates[otherMemberIndex] -
                            startBoundaryCoordinates[
                                bridgeGeneratedMemberIndex.Value]);
                        if (memberLayerDirection == 0)
                        {
                            memberLayerDirection = 1;
                        }
                        twoMemberAlignedBoundaryCoordinates[otherMemberIndex] =
                            twoMemberAlignedBoundaryCoordinates[
                                bridgeGeneratedMemberIndex.Value] +
                            memberLayerDirection * minimumInterMemberGap;
                    }
                    alignedStartBoundaryCoordinates =
                        twoMemberAlignedBoundaryCoordinates.ToArray();
                    endBoundaryCoordinates =
                        twoMemberAlignedBoundaryCoordinates;
                    return;
                }
                var bridgeAlignedBoundaryCoordinates =
                    endBoundaryCoordinates
                        .Select(boundaryCoordinate => boundaryCoordinate +
                            sharedIBarLayerDisplacement)
                        .ToArray();
                alignedStartBoundaryCoordinates =
                    bridgeAlignedBoundaryCoordinates.ToArray();
                endBoundaryCoordinates =
                    bridgeAlignedBoundaryCoordinates.ToArray();
                return;
            }

            alignedStartBoundaryCoordinates =
                endBoundaryCoordinates?.ToArray() ?? Array.Empty<double>();
        }

        private static double ResolveTwoMemberInterLapThickness(
            SectionSpecification sectionSpecification,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (sectionSpecification?.StackCount == 2)
            {
                return routeGroupRequest.GetCabinetTopLapBusbarThickness(
                    routeRequest);
            }

            return sectionSpecification?.Thickness ?? 0d;
        }

        internal static bool TryAddInternalSupportBlocks(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            ProcessingState processingState,
            out string failureReason)
        {
            failureReason = null;
            if (processingState?.InternalSupportBoundaryIndices == null ||
                processingState.InternalSupportBoundaryIndices.Count == 0)
            {
                return true;
            }

            foreach (var boundaryIndex in processingState
                .InternalSupportBoundaryIndices.OrderBy(index => index))
            {
                // 已合并进主路径的多始端连接排与下一物理段属于水平
                // 排贴排接口。该处只允许最终实体层位直接贴合，不能把
                // 偏移前的剩余距离解释为需要补齐的内部厚差垫块。
                if (conversionResult.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(boundaryIndex))
                {
                    continue;
                }

                if (!processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex,
                        out var precedingSettings) ||
                    !processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex + 1,
                        out var followingSettings))
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0} 个内部搭接面缺少分段排索引。",
                        boundaryIndex + 1);
                    return false;
                }
                if (precedingSettings.Count == 0 || followingSettings.Count == 0)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0} 个内部搭接面无法取得两侧实际铜排。",
                        boundaryIndex + 1);
                    return false;
                }

                if (!TryGetLocalThicknessDirection(
                        precedingSettings[0].Setting,
                        precedingSettings[0].Setting.PointList.Count - 2,
                        out var boundaryThicknessDirection))
                {
                    failureReason = "内部厚差垫块无法建立搭接面排厚方向。";
                    return false;
                }

                boundaryThicknessDirection =
                    boundaryThicknessDirection.Normalized;
                var supportPairs = new List<InternalSupportPair>();
                foreach (var precedingItem in precedingSettings)
                {
                    var precedingEndpoint = precedingItem.Setting.PointList[
                        precedingItem.Setting.PointList.Count - 1];
                    var nearestFollowingItem = followingSettings
                        .OrderBy(item => Math.Abs(new Vector3d(
                                precedingEndpoint,
                                item.Setting.PointList[0])
                            .Dot(boundaryThicknessDirection)))
                        .First();
                    AddUniqueInternalSupportPair(
                        supportPairs,
                        precedingItem,
                        nearestFollowingItem);
                }

                // 2↔3 搭接不能只从数量较少的一侧找最近层，否则数量较多
                // 一侧最外层可能既没有贴面也没有垫块。反向再覆盖一次，保证
                // 搭接面两侧的每一根实际排都至少关联到最近的一根排。
                foreach (var followingItem in followingSettings)
                {
                    var followingEndpoint = followingItem.Setting.PointList[0];
                    var nearestPrecedingItem = precedingSettings
                        .OrderBy(item => Math.Abs(new Vector3d(
                                item.Setting.PointList[
                                    item.Setting.PointList.Count - 1],
                                followingEndpoint)
                            .Dot(boundaryThicknessDirection)))
                        .First();
                    AddUniqueInternalSupportPair(
                        supportPairs,
                        nearestPrecedingItem,
                        followingItem);
                }

                // 最近层配对只保证每根排至少有关联，无法覆盖 2↔3 交错
                // 排列中夹在两组已贴合层之间的第二个空隙。再按真实厚度
                // 区间排序扫描一次，把相邻且分属接口两侧的每条夹缝补齐。
                AddAdjacentInternalSupportGapPairs(
                    supportPairs,
                    precedingSettings,
                    followingSettings,
                    boundaryThicknessDirection);

                AddSameSectionAdjacentGapSupports(conversionResult, routeRequest, routeGroupRequest,
                    boundaryIndex, precedingSettings, followingSettings, boundaryThicknessDirection);
                foreach (var supportPair in supportPairs)
                {
                    if (!TryAddInternalSupportBlock(
                            conversionResult,
                            routeRequest,
                            routeGroupRequest,
                            boundaryIndex,
                            supportPair.Preceding,
                            supportPair.Following,
                            boundaryThicknessDirection,
                            out failureReason))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void AddSameSectionAdjacentGapSupports(
            LinkageRouteCenterlineConversionResult conversion, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, int boundaryIndex,
            IList<SectionMemberSetting> preceding, IList<SectionMemberSetting> following,
            Vector3d normal)
        {
            var reference = preceding[0].Setting.PointList.Last();
            var layers = preceding.Select(item => CreateInternalSupportLayerInterval(item, true, reference, normal, false))
                .Concat(following.Select(item => CreateInternalSupportLayerInterval(item, false, reference, normal, true)))
                .OrderBy(layer => layer.MinimumCoordinate).ThenBy(layer => layer.MaximumCoordinate).ToList();
            for (var layerIndex = 0; layerIndex + 1 < layers.Count; layerIndex++)
            {
                var lower = layers[layerIndex];
                var upper = layers[layerIndex + 1];
                if (lower.IsPrecedingSection != upper.IsPrecedingSection ||
                    upper.MinimumCoordinate - lower.MaximumCoordinate <= GeometryTolerance) continue;
                var useStart = !lower.IsPrecedingSection;
                var lowerSetting = lower.SectionMember.Setting;
                var upperSetting = upper.SectionMember.Setting;
                if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                        lowerSetting, useStart, upperSetting, useStart,
                        Math.Min(lowerSetting.BusBarWidth, upperSetting.BusBarWidth), out var footprint)) continue;
                // 同段层间只补真实内部接口范围，不能把两根平行长腿的
                // 整段投影重合误当成搭接区域。
                LinkageRouteLapFootprint boundedFootprint = null;
                foreach (var opposing in lower.IsPrecedingSection ? following : preceding)
                {
                    LinkageRouteLapFootprint opposingFootprint;
                    var hasOpposingLap = lower.IsPrecedingSection
                        ? TryCreateInternalSupportFootprint(lowerSetting, opposing.Setting, normal, out opposingFootprint)
                        : TryCreateInternalSupportFootprint(opposing.Setting, lowerSetting, normal, out opposingFootprint);
                    if (hasOpposingLap && LinkageRouteTerminalAccessoryBuilder.TryIntersectLapFootprints(
                            new[] { footprint, opposingFootprint }, out boundedFootprint)) break;
                }
                if (boundedFootprint == null) continue;
                var accessory = LinkageRouteTerminalAccessoryBuilder.CreateSupportBlockAccessory(
                    boundedFootprint, reference, normal, lower.MaximumCoordinate, upper.MinimumCoordinate,
                    request, group, boundaryIndex == 0, lower.SectionMember.MemberIndex);
                accessory.SupportBlockKey = "boundary:" + boundaryIndex + "|same:" + lower.IsPrecedingSection +
                    "|lower:" + lower.SectionMember.MemberIndex + "|upper:" + upper.SectionMember.MemberIndex;
                conversion.Accessories.Add(accessory);
            }
        }

        private static void AddUniqueInternalSupportPair(
            ICollection<InternalSupportPair> supportPairs,
            SectionMemberSetting precedingItem,
            SectionMemberSetting followingItem)
        {
            if (supportPairs.Any(supportPair =>
                    supportPair.Preceding.MemberIndex ==
                        precedingItem.MemberIndex &&
                    supportPair.Following.MemberIndex ==
                        followingItem.MemberIndex))
            {
                return;
            }

            supportPairs.Add(new InternalSupportPair
            {
                Preceding = precedingItem,
                Following = followingItem
            });
        }

        private static void AddAdjacentInternalSupportGapPairs(
            ICollection<InternalSupportPair> supportPairs,
            IList<SectionMemberSetting> precedingSettings,
            IList<SectionMemberSetting> followingSettings,
            Vector3d boundaryThicknessDirection)
        {
            var boundaryReferencePoint = precedingSettings[0].Setting.PointList[
                precedingSettings[0].Setting.PointList.Count - 1];
            var orderedLayerIntervals = precedingSettings
                .Select(precedingItem => CreateInternalSupportLayerInterval(
                    precedingItem,
                    true,
                    boundaryReferencePoint,
                    boundaryThicknessDirection,
                    false))
                .Concat(followingSettings.Select(followingItem =>
                    CreateInternalSupportLayerInterval(
                        followingItem,
                        false,
                        boundaryReferencePoint,
                        boundaryThicknessDirection,
                        true)))
                .OrderBy(layerInterval => layerInterval.MinimumCoordinate)
                .ThenBy(layerInterval => layerInterval.MaximumCoordinate)
                .ToList();
            for (var layerIntervalIndex = 0;
                layerIntervalIndex + 1 < orderedLayerIntervals.Count;
                layerIntervalIndex++)
            {
                var lowerLayerInterval = orderedLayerIntervals[
                    layerIntervalIndex];
                var upperLayerInterval = orderedLayerIntervals[
                    layerIntervalIndex + 1];
                if (lowerLayerInterval.IsPrecedingSection ==
                        upperLayerInterval.IsPrecedingSection ||
                    upperLayerInterval.MinimumCoordinate -
                        lowerLayerInterval.MaximumCoordinate <=
                            GeometryTolerance)
                {
                    continue;
                }

                var precedingGapItem = lowerLayerInterval.IsPrecedingSection
                    ? lowerLayerInterval.SectionMember
                    : upperLayerInterval.SectionMember;
                var followingGapItem = lowerLayerInterval.IsPrecedingSection
                    ? upperLayerInterval.SectionMember
                    : lowerLayerInterval.SectionMember;
                // 厚向排序相邻并不代表两根排在排长/排宽平面上真实相交。
                // 附加夹缝配对只用于补齐已有搭接面内的空层；若没有实际
                // 搭接区域，不能虚构跨成员垫块，也不能因此淘汰本来已经
                // 由逐成员配对闭合的 I→N 方案。
                if (!TryCreateInternalSupportFootprint(
                        precedingGapItem.Setting,
                        followingGapItem.Setting,
                        boundaryThicknessDirection,
                        out _))
                {
                    continue;
                }

                AddUniqueInternalSupportPair(
                    supportPairs,
                    precedingGapItem,
                    followingGapItem);
            }
        }

        private static InternalSupportLayerInterval
            CreateInternalSupportLayerInterval(
                SectionMemberSetting sectionMember,
                bool isPrecedingSection,
                Point3d boundaryReferencePoint,
                Vector3d boundaryThicknessDirection,
                bool useSectionStart)
        {
            var boundaryPoint = useSectionStart
                ? sectionMember.Setting.PointList[0]
                : sectionMember.Setting.PointList[
                    sectionMember.Setting.PointList.Count - 1];
            var centerCoordinate = new Vector3d(
                    boundaryReferencePoint,
                    boundaryPoint)
                .Dot(boundaryThicknessDirection);
            var halfThickness = sectionMember.Setting.BusBarThickness / 2d;
            return new InternalSupportLayerInterval
            {
                SectionMember = sectionMember,
                IsPrecedingSection = isPrecedingSection,
                MinimumCoordinate = centerCoordinate - halfThickness,
                MaximumCoordinate = centerCoordinate + halfThickness
            };
        }

        private static bool TryAddInternalSupportBlock(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int boundaryIndex,
            SectionMemberSetting precedingItem,
            SectionMemberSetting followingItem,
            Vector3d thicknessDirection,
            out string failureReason)
        {
            failureReason = null;
            var precedingEndpoint = precedingItem.Setting.PointList[
                precedingItem.Setting.PointList.Count - 1];
            var followingEndpoint = followingItem.Setting.PointList[0];
            var signedFollowingCenterCoordinate = new Vector3d(
                    precedingEndpoint,
                    followingEndpoint)
                .Dot(thicknessDirection);
            var requiredCenterDistance =
                (precedingItem.Setting.BusBarThickness +
                 followingItem.Setting.BusBarThickness) / 2d;
            var supportThickness = Math.Abs(signedFollowingCenterCoordinate) -
                requiredCenterDistance;
            if (supportThickness <= GeometryTolerance)
            {
                return true;
            }

            if (!TryCreateInternalSupportFootprint(
                    precedingItem.Setting,
                    followingItem.Setting,
                    thicknessDirection,
                    out var supportFootprint))
            {
                failureReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "第 {0} 个内部搭接面厚差垫块无法取得真实搭接区域。",
                    boundaryIndex + 1);
                return false;
            }

            var supportMinimumCoordinate = signedFollowingCenterCoordinate > 0d
                ? precedingItem.Setting.BusBarThickness / 2d
                : signedFollowingCenterCoordinate +
                  followingItem.Setting.BusBarThickness / 2d;
            var supportMaximumCoordinate = signedFollowingCenterCoordinate > 0d
                ? signedFollowingCenterCoordinate -
                  followingItem.Setting.BusBarThickness / 2d
                : -precedingItem.Setting.BusBarThickness / 2d;
            var supportBlockAccessory =
                LinkageRouteTerminalAccessoryBuilder.CreateSupportBlockAccessory(
                    supportFootprint,
                    precedingEndpoint,
                    thicknessDirection,
                    Math.Min(
                        supportMinimumCoordinate,
                        supportMaximumCoordinate),
                    Math.Max(
                        supportMinimumCoordinate,
                        supportMaximumCoordinate),
                    routeRequest,
                    routeGroupRequest,
                    boundaryIndex == 0,
                    precedingItem.MemberIndex);
            supportBlockAccessory.SupportBlockKey = BuildSupportBlockKey(
                boundaryIndex,
                precedingItem.MemberIndex,
                followingItem.MemberIndex);
            conversionResult.Accessories.Add(supportBlockAccessory);
            return true;
        }

        private static string BuildSupportBlockKey(
            int boundaryIndex,
            int precedingMemberIndex,
            int followingMemberIndex)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "boundary:{0}|preceding:{1}|following:{2}",
                boundaryIndex,
                precedingMemberIndex,
                followingMemberIndex);
        }

        private static bool TryCreateInternalSupportFootprint(
            PlanarBendBusbarSetting precedingSetting,
            PlanarBendBusbarSetting followingSetting,
            Vector3d thicknessDirection,
            out LinkageRouteLapFootprint supportFootprint)
        {
            if (LinkageRouteTerminalLapRegionValidator
                .TryCreateProjectedLapFootprint(
                    precedingSetting,
                    false,
                    followingSetting,
                    true,
                    Math.Min(
                        precedingSetting.BusBarWidth,
                        followingSetting.BusBarWidth),
                    out supportFootprint))
            {
                return true;
            }

            var precedingSegmentIndex = precedingSetting.PointList.Count - 2;
            if (!TryGetProjectedPerpendicularIntersection(
                    precedingSetting,
                    precedingSegmentIndex,
                    followingSetting,
                    0,
                    out var projectedIntersection))
            {
                supportFootprint = null;
                return false;
            }

            // 分段规格后处理已把两侧端点延伸到对方排宽边。若通用投影
            // 交集因端点恰落在容差边界而无法建面，按两根垂直排的实际
            // 排宽直接重建同一个矩形搭接面，避免 3↔2 外层遗漏垫块。
            var supportLengthDirection = new Vector3d(
                precedingSetting.PointList[precedingSegmentIndex],
                precedingSetting.PointList[precedingSegmentIndex + 1]).Normalized;
            var supportWidthDirection = precedingSetting.WidthVector.Normalized;
            var halfSupportLength = followingSetting.BusBarWidth / 2d;
            var halfSupportWidth = precedingSetting.BusBarWidth / 2d;
            var minimumLengthWidthPoint = LinkageRouteSolidGeometry.Translate(
                LinkageRouteSolidGeometry.Translate(
                    projectedIntersection,
                    supportLengthDirection,
                    -halfSupportLength),
                supportWidthDirection,
                -halfSupportWidth);
            var maximumLengthMinimumWidthPoint =
                LinkageRouteSolidGeometry.Translate(
                    LinkageRouteSolidGeometry.Translate(
                        projectedIntersection,
                        supportLengthDirection,
                        halfSupportLength),
                    supportWidthDirection,
                    -halfSupportWidth);
            var maximumLengthWidthPoint = LinkageRouteSolidGeometry.Translate(
                LinkageRouteSolidGeometry.Translate(
                    projectedIntersection,
                    supportLengthDirection,
                    halfSupportLength),
                supportWidthDirection,
                halfSupportWidth);
            var minimumLengthMaximumWidthPoint =
                LinkageRouteSolidGeometry.Translate(
                    LinkageRouteSolidGeometry.Translate(
                        projectedIntersection,
                        supportLengthDirection,
                        -halfSupportLength),
                    supportWidthDirection,
                    halfSupportWidth);
            supportFootprint = new LinkageRouteLapFootprint
            {
                Corners = new[]
                {
                    minimumLengthWidthPoint,
                    maximumLengthMinimumWidthPoint,
                    maximumLengthWidthPoint,
                    minimumLengthMaximumWidthPoint
                },
                LengthDirection = supportLengthDirection,
                WidthDirection = supportWidthDirection,
                PlaneNormal = thicknessDirection.Normalized
            };
            return true;
        }

        /// <summary>
        /// 统一排规路径不再因端部规格差异拆分物理段；该入口保留用于
        /// 兼容旧调用，但始终保持原始中心线和物理段数量。
        /// </summary>
        private static bool TrySplitSingleCoplanarTerminalSections(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            failureReason = null;
            if (conversionResult?.CenterlineParts == null ||
                conversionResult.CenterlineParts.Count != 1 ||
                conversionResult.StackMembers == null ||
                conversionResult.StackMembers.Count == 0 ||
                routeRequest == null || routeGroupRequest == null)
            {
                return true;
            }

            // Keep one physical path segment intact.  Terminal width/position
            // differences are represented by StartLapWidth/EndLapWidth on the
            // materialized setting; they must not create an extra I section.
            if (routeGroupRequest.GetStackCount(routeRequest) > 0)
            {
                return true;
            }

            // 物理桥已经是第一段实际铜排，不能再次按普通始端/柜顶
            // 端段拆分，否则会把连接排本体的分段身份破坏。
            if (conversionResult.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(0) ||
                IsSingleStackMaterialization(routeRequest, routeGroupRequest) ||
                IsSingleEffectiveTerminalLap(routeRequest, routeGroupRequest))
            {
                return true;
            }

            var startWidth = routeGroupRequest.GetStartLapBusbarWidth(
                routeRequest);
            var startThickness = routeGroupRequest.GetStartLapBusbarThickness(
                routeRequest);
            var startStackCount = routeGroupRequest.GetStartLapStackCount(
                routeRequest);
            var cabinetTopWidth = routeGroupRequest
                .GetCabinetTopLapBusbarWidth(routeRequest);
            var cabinetTopThickness = routeGroupRequest
                .GetCabinetTopLapBusbarThickness(routeRequest);
            var cabinetTopStackCount = routeGroupRequest
                .GetCabinetTopLapStackCount(routeRequest);
            if (Math.Abs(startWidth - cabinetTopWidth) <= GeometryTolerance &&
                Math.Abs(startThickness - cabinetTopThickness) <=
                    GeometryTolerance &&
                startStackCount == cabinetTopStackCount)
            {
                return true;
            }

            var templateSetting = conversionResult.CenterlineParts[0];
            if (templateSetting?.PointList == null ||
                templateSetting.PointList.Count < 2)
            {
                failureReason = "单共面异规格端部拆分缺少有效中心线。";
                return false;
            }

            var splitPointIndex = -1;
            if (splitPointIndex < 0)
            {
                var firstPoint = templateSetting.PointList[0];
                var lastPoint = templateSetting.PointList[1];
                if (firstPoint == null || lastPoint == null ||
                    firstPoint.DistanceTo(lastPoint) <= GeometryTolerance)
                {
                    failureReason = "单共面异规格端部拆分的中心线首段长度不足。";
                    return false;
                }
            }

            var splitCenterlineParts = SplitPlanarSettingAtStartBoundary(
                templateSetting,
                splitPointIndex,
                ResolveStartSectionSplitFraction(
                    templateSetting,
                    routeRequest,
                    startWidth));
            if (splitCenterlineParts == null ||
                splitCenterlineParts.Count != 2)
            {
                failureReason = "单共面异规格端部拆分无法建立始端和柜顶物理段。";
                return false;
            }

            var splitStackMemberParts = new Dictionary<
                LinkageStackedRouteMember,
                IList<PlanarBendBusbarSetting>>();
            foreach (var stackMember in conversionResult.StackMembers)
            {
                if (stackMember?.PlanarParts == null ||
                    stackMember.PlanarParts.Count != 1)
                {
                    failureReason =
                        "单共面异规格端部拆分要求每层先只有一个物理段。";
                    return false;
                }

                var splitMemberParts = SplitPlanarSettingAtStartBoundary(
                    stackMember.PlanarParts[0],
                    splitPointIndex,
                    ResolveStartSectionSplitFraction(
                        stackMember.PlanarParts[0],
                        routeRequest,
                        startWidth));
                if (splitMemberParts == null || splitMemberParts.Count != 2)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "叠排第 {0} 层的单共面异规格端部拆分失败。",
                        stackMember.MemberIndex + 1);
                    return false;
                }

                splitStackMemberParts[stackMember] = splitMemberParts;
            }

            conversionResult.CenterlineParts.Clear();
            foreach (var splitCenterlinePart in splitCenterlineParts)
            {
                conversionResult.CenterlineParts.Add(splitCenterlinePart);
            }

            foreach (var stackMember in conversionResult.StackMembers)
            {
                stackMember.PlanarParts.Clear();
                foreach (var splitMemberPart in splitStackMemberParts[
                    stackMember])
                {
                    stackMember.PlanarParts.Add(splitMemberPart);
                }
            }

            conversionResult.Diagnostics.Add(
                "异规格单共面路径已拆分为始端搭接段和柜顶搭接段。");
            return true;
        }

        private static double ResolveStartSectionSplitFraction(
            PlanarBendBusbarSetting sourceSetting,
            LinkageMultiGroupRouteRequest routeRequest,
            double startBusbarWidth)
        {
            if (sourceSetting?.PointList == null ||
                sourceSetting.PointList.Count < 2)
            {
                return 0.5d;
            }

            if (sourceSetting.PointList[0] == null ||
                sourceSetting.PointList[1] == null)
            {
                return 0.5d;
            }

            var firstSegmentLength = sourceSetting.PointList[0].DistanceTo(
                sourceSetting.PointList[1]);
            if (firstSegmentLength <= GeometryTolerance)
            {
                return 0.5d;
            }

            var minimumStartLength = Math.Max(
                routeRequest?.MinimumSegmentLength ?? 0d,
                startBusbarWidth / 2d);
            var minimumFollowingLength = Math.Max(
                routeRequest?.MinimumSegmentLength ?? 0d,
                0d);
            if (firstSegmentLength <= minimumStartLength +
                    minimumFollowingLength + GeometryTolerance)
            {
                return 0.5d;
            }

            var preferredSplitDistance = Math.Max(
                minimumStartLength,
                firstSegmentLength / 2d);
            var maximumSplitDistance = firstSegmentLength -
                minimumFollowingLength;
            return Math.Max(
                0.01d,
                Math.Min(
                    0.99d,
                    Math.Min(preferredSplitDistance, maximumSplitDistance) /
                        firstSegmentLength));
        }

        private static IList<PlanarBendBusbarSetting>
            SplitPlanarSettingAtStartBoundary(
                PlanarBendBusbarSetting sourceSetting,
                int splitPointIndex)
        {
            return SplitPlanarSettingAtStartBoundary(
                sourceSetting,
                splitPointIndex,
                0.5d);
        }

        private static IList<PlanarBendBusbarSetting>
            SplitPlanarSettingAtStartBoundary(
                PlanarBendBusbarSetting sourceSetting,
                int splitPointIndex,
                double splitFraction)
        {
            if (sourceSetting?.PointList == null ||
                sourceSetting.PointList.Count < 2)
            {
                return null;
            }

            var sourcePoints = sourceSetting.PointList;
            IList<Point3d> firstPointList;
            IList<Point3d> secondPointList;
            if (splitPointIndex >= 1 &&
                splitPointIndex < sourcePoints.Count - 1)
            {
                firstPointList = sourcePoints
                    .Take(splitPointIndex + 1)
                    .ToList();
                secondPointList = sourcePoints
                    .Skip(splitPointIndex)
                    .ToList();
            }
            else
            {
                var firstSourcePoint = sourcePoints[0];
                var followingSourcePoint = sourcePoints[1];
                if (firstSourcePoint == null || followingSourcePoint == null ||
                    firstSourcePoint.DistanceTo(followingSourcePoint) <=
                        GeometryTolerance)
                {
                    return null;
                }

                var splitPoint = new Point3d(
                    firstSourcePoint.X +
                        (followingSourcePoint.X - firstSourcePoint.X) *
                        Math.Max(0.01d, Math.Min(0.99d, splitFraction)),
                    firstSourcePoint.Y +
                        (followingSourcePoint.Y - firstSourcePoint.Y) *
                        Math.Max(0.01d, Math.Min(0.99d, splitFraction)),
                    firstSourcePoint.Z +
                        (followingSourcePoint.Z - firstSourcePoint.Z) *
                        Math.Max(0.01d, Math.Min(0.99d, splitFraction)));
                firstPointList = new List<Point3d>
                {
                    firstSourcePoint,
                    splitPoint
                };
                secondPointList = new List<Point3d> { splitPoint };
                foreach (var remainingSourcePoint in sourcePoints.Skip(1))
                {
                    secondPointList.Add(remainingSourcePoint);
                }
            }

            if (firstPointList.Count < 2 || secondPointList.Count < 2 ||
                firstPointList.Any(point => point == null) ||
                secondPointList.Any(point => point == null))
            {
                return null;
            }

            var firstSetting = sourceSetting.Copy();
            firstSetting.PointList = firstPointList
                .Select(point => new Point3d(point.X, point.Y, point.Z))
                .ToList();
            var secondSetting = sourceSetting.Copy();
            secondSetting.PointList = secondPointList
                .Select(point => new Point3d(point.X, point.Y, point.Z))
                .ToList();
            return new List<PlanarBendBusbarSetting>
            {
                firstSetting,
                secondSetting
            };
        }

        private static SectionSpecification ResolveSectionSpecification(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int partIndex,
            int partCount,
            bool isDoubleStartPhysicalBridgeSection)
        {
            return ResolveSectionSpecificationWithOutputMode(
                routeRequest,
                routeGroupRequest,
                partIndex,
                partCount,
                isDoubleStartPhysicalBridgeSection,
                false);
        }

        private static SectionSpecification ResolveSectionSpecificationWithOutputMode(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int partIndex,
            int partCount,
            bool isDoubleStartPhysicalBridgeSection,
            bool applySingleStackCabinetTopSpecification)
        {
            if (isDoubleStartPhysicalBridgeSection)
            {
                return new SectionSpecification
                {
                    Label = "双始端连接排",
                    Width = routeGroupRequest.GetDoubleStartBridgeBusbarWidth(
                        routeRequest),
                    Thickness = routeGroupRequest
                        .GetDoubleStartBridgeBusbarThickness(routeRequest),
                    // 双始端连接排始终继承始端搭接排的宽厚；连接排叠排数
                    // 同样继承始端搭接排，不因柜顶侧只有一根而切换规格。
                    StackCount = Math.Max(
                        1,
                        routeGroupRequest.GetStartLapStackCount(routeRequest)),
                    StartLapWidth = routeGroupRequest.GetStartLapBusbarWidth(
                        routeRequest),
                    EndLapWidth = routeGroupRequest.GetStartLapBusbarWidth(
                        routeRequest)
                };
            }

            // 路径搜索只使用统一的中间排规格；进入实体化后必须按已有
            // 物理段恢复始端搭接排、中间排和柜顶搭接排的真实规格。
            // 这里只改变既有物理排的宽、厚和叠排数量，不能为规格变化
            // 新增路径段。单根物理排同时连接两端时仍按柜顶规格处理。
            if (applySingleStackCabinetTopSpecification &&
                IsSingleEffectiveTerminalLap(routeRequest, routeGroupRequest))
            {
                return new SectionSpecification
                {
                    Label = "柜顶搭接排",
                    Width = routeGroupRequest.GetCabinetTopLapBusbarWidth(
                        routeRequest),
                    Thickness = routeGroupRequest
                        .GetCabinetTopLapBusbarThickness(routeRequest),
                    StackCount = routeGroupRequest.GetCabinetTopLapStackCount(
                        routeRequest),
                    StartLapWidth = routeGroupRequest.GetStartLapBusbarWidth(
                        routeRequest),
                    EndLapWidth = routeGroupRequest
                        .GetCabinetTopLapBusbarWidth(routeRequest)
                };
            }

            var sectionWidth = routeGroupRequest.GetSectionBusbarWidth(
                routeRequest,
                partIndex,
                partCount);
            var sectionThickness = routeGroupRequest.GetSectionBusbarThickness(
                routeRequest,
                partIndex,
                partCount);
            var sectionStackCount = routeGroupRequest.GetSectionStackCount(
                routeRequest,
                partIndex,
                partCount);
            var sectionLabel = partCount <= 1 ||
                partIndex >= partCount - 1
                    ? "柜顶搭接排"
                    : partIndex <= 0
                        ? "始端搭接排"
                        : "中间排";
            return new SectionSpecification
            {
                Label = sectionLabel,
                Width = sectionWidth,
                Thickness = sectionThickness,
                StackCount = Math.Max(1, sectionStackCount),
                StartLapWidth = partIndex == 0
                    ? routeGroupRequest.GetStartLapBusbarWidth(routeRequest)
                    : 0d,
                EndLapWidth = partIndex >= partCount - 1
                    ? routeGroupRequest.GetCabinetTopLapBusbarWidth(
                        routeRequest)
                    : 0d
            };
        }

        private static bool IsSingleEffectiveTerminalLap(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            return routeRequest != null &&
                routeGroupRequest != null &&
                routeGroupRequest.GetStartLapStackCount(routeRequest) == 1 &&
                routeGroupRequest.GetCabinetTopLapStackCount(routeRequest) == 1;
        }

        private static bool IsSingleStackMaterialization(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            return routeRequest != null &&
                routeGroupRequest != null &&
                routeGroupRequest.GetStackCount(routeRequest) <= 1;
        }

        private static bool IsHorizontalLapMergePostProcessingEnabled()
        {
            // The restored unified-path flow deliberately leaves physical
            // section topology untouched.  Terminal differences are handled
            // as lap metadata/placement adjustments after path generation.
            return false;
        }

        private static bool TryResolveTerminalBoundaryCoordinates(
            LinkageTerminalAssembly terminalAssembly,
            SectionSpecification sectionSpecification,
            double terminalAdditionalSpace,
            double middleAdditionalSpace,
            bool useSpacerlessTerminalLap,
            PlanarBendBusbarSetting templateSetting,
            bool useStartBoundary,
            double partOrientationSign,
            out LinkageRouteStackTerminalPlacement selectedPlacement,
            out double[] boundaryCoordinates,
            out string failureReason)
        {
            selectedPlacement = null;
            boundaryCoordinates = null;
            failureReason = null;
            var placementCandidates = LinkageRouteStackPlacementSolver
                .ResolveTerminalPlacements(
                    terminalAssembly,
                    sectionSpecification.StackCount,
                    sectionSpecification.Thickness,
                    terminalAdditionalSpace,
                    middleAdditionalSpace,
                    useSpacerlessTerminalLap,
                    useStartBoundary)
                .ToList();
            if (placementCandidates.Count == 0)
            {
                failureReason = (useStartBoundary ? "始端" : "柜顶") +
                    sectionSpecification.Label + "无法取得叠排搭接层位。";
                return false;
            }

            var templateBoundaryCoordinate = MeasureBoundaryCoordinate(
                templateSetting,
                useStartBoundary);
            selectedPlacement = placementCandidates
                .OrderBy(placement => ConvertPlacementToBoundaryCoordinates(
                        terminalAssembly,
                        placement,
                        templateSetting,
                        useStartBoundary)
                    .Average(coordinate => Math.Abs(
                        coordinate - templateBoundaryCoordinate)))
                .ThenBy(placement => Math.Abs(placement.BaseCenterOffset))
                .First();
            selectedPlacement = ReorderPlacementByCanonicalLayerOrder(
                terminalAssembly,
                selectedPlacement,
                templateSetting,
                useStartBoundary,
                partOrientationSign);
            if (selectedPlacement == null)
            {
                failureReason = (useStartBoundary ? "始端" : "柜顶") +
                    sectionSpecification.Label + "无法保持叠排内外层顺序。";
                return false;
            }
            boundaryCoordinates = ConvertPlacementToBoundaryCoordinates(
                terminalAssembly,
                selectedPlacement,
                templateSetting,
                useStartBoundary);
            return true;
        }

        private static LinkageRouteStackTerminalPlacement
            ReorderPlacementByCanonicalLayerOrder(
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement sourcePlacement,
            PlanarBendBusbarSetting templateSetting,
            bool useStartBoundary,
            double partOrientationSign)
        {
            if (sourcePlacement?.GeneratedMemberOffsets == null)
            {
                return null;
            }

            var sourceBoundaryCoordinates = ConvertPlacementToBoundaryCoordinates(
                terminalAssembly,
                sourcePlacement,
                templateSetting,
                useStartBoundary);
            if (sourceBoundaryCoordinates.Length !=
                sourcePlacement.GeneratedMemberOffsets.Count)
            {
                return null;
            }

            var normalizedPartOrientationSign =
                partOrientationSign < 0d ? -1d : 1d;
            var canonicalLayers = Enumerable.Range(
                    0,
                    sourceBoundaryCoordinates.Length)
                .OrderBy(sourceMemberIndex =>
                    sourceBoundaryCoordinates[sourceMemberIndex] *
                    normalizedPartOrientationSign)
                .ToList();
            var sourceToCanonicalMemberIndex = new int[
                canonicalLayers.Count];
            for (var canonicalMemberIndex = 0;
                canonicalMemberIndex < canonicalLayers.Count;
                canonicalMemberIndex++)
            {
                sourceToCanonicalMemberIndex[
                    canonicalLayers[canonicalMemberIndex]] =
                    canonicalMemberIndex;
            }

            return new LinkageRouteStackTerminalPlacement
            {
                Signature = sourcePlacement.Signature,
                BaseCenterOffset = sourcePlacement.BaseCenterOffset,
                GeneratedMemberOffsets = canonicalLayers
                    .Select(sourceMemberIndex =>
                        sourcePlacement.GeneratedMemberOffsets[
                            sourceMemberIndex])
                    .ToList(),
                SpacerlessMovedGeneratedMemberIndices = (sourcePlacement
                        .SpacerlessMovedGeneratedMemberIndices ??
                            new List<int>())
                    .Select(sourceMemberIndex =>
                        sourceToCanonicalMemberIndex[sourceMemberIndex])
                    .OrderBy(canonicalMemberIndex => canonicalMemberIndex)
                    .ToList(),
                Contacts = sourcePlacement.Contacts
                    .Select(terminalContact =>
                        new LinkageRouteStackTerminalContact
                        {
                            GeneratedMemberIndex =
                                sourceToCanonicalMemberIndex[
                                    terminalContact.GeneratedMemberIndex],
                            FixedMemberIndex = terminalContact.FixedMemberIndex
                        })
                    .ToList(),
                DoubleStartHorizontalBridgeGeneratedMemberIndex =
                    sourcePlacement
                        .DoubleStartHorizontalBridgeGeneratedMemberIndex
                        .HasValue
                        ? (int?)sourceToCanonicalMemberIndex[sourcePlacement
                            .DoubleStartHorizontalBridgeGeneratedMemberIndex
                            .Value]
                        : null,
                DoubleStartHorizontalBridgeSideSign = sourcePlacement
                    .DoubleStartHorizontalBridgeSideSign,
                DoubleStartHorizontalBridgeThickness = sourcePlacement
                    .DoubleStartHorizontalBridgeThickness,
                DoubleStartHorizontalBridgePlacements =
                    (sourcePlacement.DoubleStartHorizontalBridgePlacements ??
                        new List<LinkageRouteTerminalBridgePlacement>())
                    .Select(bridgePlacement => new LinkageRouteTerminalBridgePlacement
                    {
                        GeneratedMemberIndex = bridgePlacement.GeneratedMemberIndex,
                        SideSign = bridgePlacement.SideSign,
                        Thickness = bridgePlacement.Thickness
                    }).ToList()
            };
        }

        private static double[] ConvertPlacementToBoundaryCoordinates(
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            PlanarBendBusbarSetting templateSetting,
            bool useStartBoundary)
        {
            var referencePoint = terminalAssembly.Members
                .First(member => member?.FixedBusbar?.PointList?.Count > 0)
                .FixedBusbar.PointList[0];
            var terminalThicknessDirection = terminalAssembly.ThicknessDirection
                .Normalized;
            TryGetLocalThicknessDirection(
                templateSetting,
                useStartBoundary
                    ? 0
                    : templateSetting.PointList.Count - 2,
                out var localThicknessDirection);
            return terminalPlacement.GeneratedMemberOffsets
                .Select(memberOffset => LinkageRouteSolidGeometry.Translate(
                    referencePoint,
                    terminalThicknessDirection,
                    terminalPlacement.BaseCenterOffset + memberOffset))
                .Select(memberCenterPoint => DotPoint(
                    memberCenterPoint,
                    localThicknessDirection.Normalized))
                .ToArray();
        }

        private static bool TryBuildInternalBoundaryCoordinates(
            PlanarBendBusbarSetting precedingTemplate,
            SectionSpecification precedingSpecification,
            PlanarBendBusbarSetting followingTemplate,
            SectionSpecification followingSpecification,
            IReadOnlyList<LinkageStackedRouteMember> ordinaryMembers,
            int boundaryIndex,
            double precedingPartOrientationSign,
            out double[] precedingEndCoordinates,
            out double[] followingStartCoordinates,
            out string failureReason)
        {
            precedingEndCoordinates = null;
            followingStartCoordinates = null;
            failureReason = null;
            if (!TryGetLocalThicknessDirection(
                    precedingTemplate,
                    precedingTemplate.PointList.Count - 2,
                    out var sharedThicknessDirection) ||
                !TryGetLocalThicknessDirection(
                    followingTemplate,
                    0,
                    out var followingThicknessDirection) ||
                Math.Abs(sharedThicknessDirection.Normalized.Dot(
                    followingThicknessDirection.Normalized)) <
                    1d - GeometryTolerance)
            {
                failureReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "第 {0} 个内部搭接面无法建立共同排厚方向。",
                    boundaryIndex + 1);
                return false;
            }

            sharedThicknessDirection = sharedThicknessDirection.Normalized;
            var ordinaryBoundaryCoordinates = ordinaryMembers.SelectMany(
                    stackMember => new[]
                    {
                        DotPoint(
                            stackMember.PlanarParts[boundaryIndex].PointList[
                                stackMember.PlanarParts[boundaryIndex]
                                    .PointList.Count - 1],
                            sharedThicknessDirection),
                        DotPoint(
                            stackMember.PlanarParts[boundaryIndex + 1]
                                .PointList[0],
                            sharedThicknessDirection)
                    })
                .ToList();
            var boundaryAnchorCoordinate = ordinaryBoundaryCoordinates.Average();
            var combinedThickness = precedingSpecification.Thickness +
                followingSpecification.Thickness;
            double[] precedingWorldCoordinates;
            double[] followingWorldCoordinates;
            if (precedingSpecification.StackCount >
                followingSpecification.StackCount)
            {
                precedingWorldCoordinates = CreateCenteredCoordinates(
                    precedingSpecification.StackCount,
                    combinedThickness);
                followingWorldCoordinates = Enumerable.Range(
                        0,
                        followingSpecification.StackCount)
                    .Select(index =>
                        (precedingWorldCoordinates[index] +
                         precedingWorldCoordinates[index + 1]) / 2d)
                    .ToArray();
            }
            else if (followingSpecification.StackCount >
                precedingSpecification.StackCount)
            {
                followingWorldCoordinates = CreateCenteredCoordinates(
                    followingSpecification.StackCount,
                    combinedThickness);
                precedingWorldCoordinates = Enumerable.Range(
                        0,
                        precedingSpecification.StackCount)
                    .Select(index =>
                        (followingWorldCoordinates[index] +
                         followingWorldCoordinates[index + 1]) / 2d)
                    .ToArray();
            }
            else
            {
                precedingWorldCoordinates = CreateCenteredCoordinates(
                    precedingSpecification.StackCount,
                    combinedThickness);
                var ordinarySideSign = ResolveOrdinaryBoundarySideSign(
                    ordinaryMembers,
                    boundaryIndex,
                    sharedThicknessDirection);
                followingWorldCoordinates = precedingWorldCoordinates
                    .Select(coordinate => coordinate + ordinarySideSign *
                        combinedThickness / 2d)
                    .ToArray();
            }

            var rawCombinedAverage = precedingWorldCoordinates
                .Concat(followingWorldCoordinates)
                .Average();
            var normalizedPrecedingOrientationSign =
                precedingPartOrientationSign < 0d ? -1d : 1d;
            precedingWorldCoordinates = precedingWorldCoordinates
                .Select(coordinate => coordinate - rawCombinedAverage +
                    boundaryAnchorCoordinate)
                .OrderBy(coordinate =>
                    coordinate * normalizedPrecedingOrientationSign)
                .ToArray();
            followingWorldCoordinates = followingWorldCoordinates
                .Select(coordinate => coordinate - rawCombinedAverage +
                    boundaryAnchorCoordinate)
                .OrderBy(coordinate =>
                    coordinate * normalizedPrecedingOrientationSign)
                .ToArray();
            precedingEndCoordinates = precedingWorldCoordinates;
            var followingOrientationSign = sharedThicknessDirection.Dot(
                followingThicknessDirection.Normalized);
            followingStartCoordinates = followingWorldCoordinates
                .Select(coordinate => coordinate * followingOrientationSign)
                .ToArray();
            return true;
        }

        private static bool TryCreateSectionMemberSetting(
            PlanarBendBusbarSetting templateSetting,
            SectionSpecification sectionSpecification,
            SectionSpecification precedingSpecification,
            SectionSpecification followingSpecification,
            double requiredStartCoordinate,
            double requiredEndCoordinate,
            double interiorOffsetDistance,
            double precedingThickness,
            double followingThickness,
            bool preserveStartTerminalBoundary,
            bool preserveEndTerminalBoundary,
            out PlanarBendBusbarSetting rebuiltSetting,
            out bool rigidConflictAtStart,
            out bool rigidConflictAtEnd,
            out string failureReason)
        {
            rebuiltSetting = null;
            rigidConflictAtStart = false;
            rigidConflictAtEnd = false;
            failureReason = null;
            var segmentCount = templateSetting.PointList.Count - 1;
            if (!TryGetLocalThicknessDirection(
                    templateSetting,
                    0,
                    out var startThicknessDirection) ||
                !TryGetLocalThicknessDirection(
                    templateSetting,
                    segmentCount - 1,
                    out var endThicknessDirection))
            {
                failureReason = "无法建立首末段排厚方向。";
                return false;
            }

            var startDisplacement = requiredStartCoordinate - DotPoint(
                templateSetting.PointList[0],
                startThicknessDirection.Normalized);
            var endDisplacement = requiredEndCoordinate - DotPoint(
                templateSetting.PointList[templateSetting.PointList.Count - 1],
                endThicknessDirection.Normalized);
            if (segmentCount == 1)
            {
                // I 排保持直线和点数，但不等于锁死旧模板层位。已锁定
                // 的固定端或上游接口优先；另一端可以暂时脱开，由后续
                // 非 I 排吸收残差，再走完整搭接/干涉终检。无锁定边界
                // 的旧双端协调分支仍使用相邻较厚侧作为基准。
                var rigidReferenceDisplacement = interiorOffsetDistance;
                if (preserveStartTerminalBoundary)
                {
                    rigidReferenceDisplacement = startDisplacement;
                }
                else if (preserveEndTerminalBoundary)
                {
                    rigidReferenceDisplacement = endDisplacement;
                }
                else if (precedingThickness > followingThickness +
                    GeometryTolerance)
                {
                    rigidReferenceDisplacement = startDisplacement;
                }
                else if (followingThickness > precedingThickness +
                    GeometryTolerance)
                {
                    rigidReferenceDisplacement = endDisplacement;
                }

                rigidConflictAtStart = Math.Abs(
                    startDisplacement - rigidReferenceDisplacement) >
                    GeometryTolerance;
                rigidConflictAtEnd = Math.Abs(
                    endDisplacement - rigidReferenceDisplacement) >
                    GeometryTolerance;
                startDisplacement = rigidReferenceDisplacement;
                endDisplacement = rigidReferenceDisplacement;
            }

            var segmentOffsetDistances = Enumerable.Repeat(
                    interiorOffsetDistance,
                    segmentCount)
                .ToArray();
            segmentOffsetDistances[0] = startDisplacement;
            segmentOffsetDistances[segmentCount - 1] = endDisplacement;

            if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(
                    templateSetting,
                    segmentOffsetDistances,
                    true,
                    out var rebuiltPoints,
                    out failureReason))
            {
                return false;
            }

            rebuiltSetting = templateSetting.Copy();
            rebuiltSetting.PointList = rebuiltPoints;
            ApplySettingDimensions(
                rebuiltSetting,
                sectionSpecification,
                precedingSpecification,
                followingSpecification);
            return true;
        }

        private static bool TryAlignFollowingBoundaryToPrecedingCoordinates(
            PlanarBendBusbarSetting precedingTemplate,
            SectionSpecification precedingSpecification,
            PlanarBendBusbarSetting followingTemplate,
            SectionSpecification followingSpecification,
            IReadOnlyList<double> desiredFollowingCoordinates,
            IReadOnlyList<double> fixedPrecedingCoordinates,
            double precedingPartOrientationSign,
            out double[] alignedFollowingCoordinates,
            out string failureReason)
        {
            alignedFollowingCoordinates = null;
            failureReason = null;
            if (desiredFollowingCoordinates == null ||
                desiredFollowingCoordinates.Count !=
                    followingSpecification.StackCount ||
                fixedPrecedingCoordinates == null ||
                fixedPrecedingCoordinates.Count !=
                    precedingSpecification.StackCount ||
                !TryGetLocalThicknessDirection(
                    precedingTemplate,
                    precedingTemplate.PointList.Count - 2,
                    out var precedingThicknessDirection) ||
                !TryGetLocalThicknessDirection(
                    followingTemplate,
                    0,
                    out var followingThicknessDirection))
            {
                failureReason = "内部规格搭接缺少可从始端向后传播的层位。";
                return false;
            }

            var thicknessDirectionAlignment =
                precedingThicknessDirection.Normalized.Dot(
                    followingThicknessDirection.Normalized);
            if (Math.Abs(thicknessDirectionAlignment) <
                1d - GeometryTolerance)
            {
                failureReason = "内部规格搭接两侧排厚方向不平行。";
                return false;
            }

            var normalizedPrecedingOrientationSign =
                precedingPartOrientationSign < 0d ? -1d : 1d;
            var fixedPrecedingCanonicalCoordinates = fixedPrecedingCoordinates
                .Select(coordinate =>
                    coordinate * normalizedPrecedingOrientationSign)
                .ToArray();
            var followingToPrecedingOrientationSign =
                thicknessDirectionAlignment < 0d ? -1d : 1d;
            var desiredFollowingCanonicalCoordinates =
                desiredFollowingCoordinates
                .Select(coordinate =>
                    coordinate * followingToPrecedingOrientationSign *
                    normalizedPrecedingOrientationSign)
                .ToArray();
            var requiredContactCenterDistance =
                (precedingSpecification.Thickness +
                 followingSpecification.Thickness) / 2d;
            var fixedPrecedingCount = precedingSpecification.StackCount;
            var followingCount = followingSpecification.StackCount;
            var resolvedFollowingCoordinates = new double[followingCount];

            if (followingCount < fixedPrecedingCount)
            {
                for (var followingIndex = 0;
                    followingIndex < followingCount;
                    followingIndex++)
                {
                    var lowerContactCoordinate =
                        fixedPrecedingCanonicalCoordinates[followingIndex] +
                        requiredContactCenterDistance;
                    var upperContactCoordinate =
                        fixedPrecedingCanonicalCoordinates[followingIndex + 1] -
                        requiredContactCenterDistance;
                    resolvedFollowingCoordinates[followingIndex] =
                        SelectNearestCoordinate(
                            desiredFollowingCanonicalCoordinates[followingIndex],
                            lowerContactCoordinate,
                            upperContactCoordinate);
                }
            }
            else if (followingCount > fixedPrecedingCount)
            {
                resolvedFollowingCoordinates[0] =
                    fixedPrecedingCanonicalCoordinates[0] -
                    requiredContactCenterDistance;
                resolvedFollowingCoordinates[followingCount - 1] =
                    fixedPrecedingCanonicalCoordinates[
                        fixedPrecedingCount - 1] +
                    requiredContactCenterDistance;
                for (var followingIndex = 1;
                    followingIndex < followingCount - 1;
                    followingIndex++)
                {
                    var lowerContactCoordinate =
                        fixedPrecedingCanonicalCoordinates[
                            followingIndex - 1] +
                        requiredContactCenterDistance;
                    var upperContactCoordinate =
                        fixedPrecedingCanonicalCoordinates[followingIndex] -
                        requiredContactCenterDistance;
                    resolvedFollowingCoordinates[followingIndex] =
                        SelectNearestCoordinate(
                            desiredFollowingCanonicalCoordinates[followingIndex],
                            lowerContactCoordinate,
                            upperContactCoordinate);
                }
            }
            else
            {
                var lowerSideCoordinates = fixedPrecedingCanonicalCoordinates
                    .Select(coordinate =>
                        coordinate - requiredContactCenterDistance)
                    .ToArray();
                var upperSideCoordinates = fixedPrecedingCanonicalCoordinates
                    .Select(coordinate =>
                        coordinate + requiredContactCenterDistance)
                    .ToArray();
                resolvedFollowingCoordinates = GetCoordinateDeviationSquared(
                        desiredFollowingCanonicalCoordinates,
                        lowerSideCoordinates) <=
                    GetCoordinateDeviationSquared(
                        desiredFollowingCanonicalCoordinates,
                        upperSideCoordinates)
                        ? lowerSideCoordinates
                        : upperSideCoordinates;
                if (!TrySelectNonIntersectingContactLayers(
                        fixedPrecedingCanonicalCoordinates,
                        desiredFollowingCanonicalCoordinates,
                        requiredContactCenterDistance,
                        followingSpecification.Thickness,
                        resolvedFollowingCoordinates,
                        out resolvedFollowingCoordinates))
                {
                    failureReason = "内部规格搭接不存在保持层序且无跨层重叠的后续接触侧。";
                    return false;
                }
            }

            alignedFollowingCoordinates = resolvedFollowingCoordinates
                .Select(coordinate =>
                    coordinate * normalizedPrecedingOrientationSign *
                    followingToPrecedingOrientationSign)
                .ToArray();
            return true;
        }

        private static bool TryAlignPrecedingBoundaryToFollowingCoordinates(
            PlanarBendBusbarSetting precedingTemplate,
            SectionSpecification precedingSpecification,
            PlanarBendBusbarSetting followingTemplate,
            SectionSpecification followingSpecification,
            IReadOnlyList<double> desiredPrecedingCoordinates,
            IReadOnlyList<double> fixedFollowingCoordinates,
            double precedingPartOrientationSign,
            out double[] alignedPrecedingCoordinates,
            out string failureReason)
        {
            alignedPrecedingCoordinates = null;
            failureReason = null;
            if (desiredPrecedingCoordinates == null ||
                desiredPrecedingCoordinates.Count !=
                    precedingSpecification.StackCount ||
                fixedFollowingCoordinates == null ||
                fixedFollowingCoordinates.Count !=
                    followingSpecification.StackCount ||
                !TryGetLocalThicknessDirection(
                    precedingTemplate,
                    precedingTemplate.PointList.Count - 2,
                    out var precedingThicknessDirection) ||
                !TryGetLocalThicknessDirection(
                    followingTemplate,
                    0,
                    out var followingThicknessDirection))
            {
                failureReason = "内部规格搭接缺少可反向传播的层位。";
                return false;
            }

            var thicknessDirectionAlignment =
                precedingThicknessDirection.Normalized.Dot(
                    followingThicknessDirection.Normalized);
            if (Math.Abs(thicknessDirectionAlignment) <
                1d - GeometryTolerance)
            {
                failureReason = "内部规格搭接两侧排厚方向不平行。";
                return false;
            }

            var normalizedPrecedingOrientationSign =
                precedingPartOrientationSign < 0d ? -1d : 1d;
            var followingCanonicalCoordinates = fixedFollowingCoordinates
                .Select(coordinate =>
                    coordinate *
                    (thicknessDirectionAlignment < 0d ? -1d : 1d) *
                    normalizedPrecedingOrientationSign)
                .ToArray();
            var desiredCanonicalCoordinates = desiredPrecedingCoordinates
                .Select(coordinate =>
                    coordinate * normalizedPrecedingOrientationSign)
                .ToArray();
            var requiredContactCenterDistance =
                (precedingSpecification.Thickness +
                 followingSpecification.Thickness) / 2d;
            var precedingCount = precedingSpecification.StackCount;
            var followingCount = followingSpecification.StackCount;
            var resolvedCoordinates = new double[precedingCount];

            if (precedingCount < followingCount)
            {
                for (var precedingIndex = 0;
                    precedingIndex < precedingCount;
                    precedingIndex++)
                {
                    var lowerContactCoordinate =
                        followingCanonicalCoordinates[precedingIndex] +
                        requiredContactCenterDistance;
                    var upperContactCoordinate =
                        followingCanonicalCoordinates[precedingIndex + 1] -
                        requiredContactCenterDistance;
                    resolvedCoordinates[precedingIndex] =
                        SelectNearestCoordinate(
                            desiredCanonicalCoordinates[precedingIndex],
                            lowerContactCoordinate,
                            upperContactCoordinate);
                }
            }
            else if (precedingCount > followingCount)
            {
                resolvedCoordinates[0] = followingCanonicalCoordinates[0] -
                    requiredContactCenterDistance;
                resolvedCoordinates[precedingCount - 1] =
                    followingCanonicalCoordinates[followingCount - 1] +
                    requiredContactCenterDistance;
                for (var precedingIndex = 1;
                    precedingIndex < precedingCount - 1;
                    precedingIndex++)
                {
                    var lowerContactCoordinate =
                        followingCanonicalCoordinates[precedingIndex - 1] +
                        requiredContactCenterDistance;
                    var upperContactCoordinate =
                        followingCanonicalCoordinates[precedingIndex] -
                        requiredContactCenterDistance;
                    resolvedCoordinates[precedingIndex] =
                        SelectNearestCoordinate(
                            desiredCanonicalCoordinates[precedingIndex],
                            lowerContactCoordinate,
                            upperContactCoordinate);
                }
            }
            else
            {
                var lowerSideCoordinates = followingCanonicalCoordinates
                    .Select(coordinate =>
                        coordinate - requiredContactCenterDistance)
                    .ToArray();
                var upperSideCoordinates = followingCanonicalCoordinates
                    .Select(coordinate =>
                        coordinate + requiredContactCenterDistance)
                    .ToArray();
                resolvedCoordinates = GetCoordinateDeviationSquared(
                        desiredCanonicalCoordinates,
                        lowerSideCoordinates) <=
                    GetCoordinateDeviationSquared(
                        desiredCanonicalCoordinates,
                        upperSideCoordinates)
                        ? lowerSideCoordinates
                        : upperSideCoordinates;
                if (!TrySelectNonIntersectingContactLayers(
                        followingCanonicalCoordinates,
                        desiredCanonicalCoordinates,
                        requiredContactCenterDistance,
                        precedingSpecification.Thickness,
                        resolvedCoordinates,
                        out resolvedCoordinates))
                {
                    failureReason = "内部规格搭接不存在保持层序且无跨层重叠的前序接触侧。";
                    return false;
                }
            }

            alignedPrecedingCoordinates = resolvedCoordinates
                .Select(coordinate =>
                    coordinate * normalizedPrecedingOrientationSign)
                .ToArray();
            return true;
        }

        private static bool HaveDifferentPhysicalSectionSpecifications(
            IReadOnlyList<SectionSpecification> sectionSpecifications)
        {
            if (sectionSpecifications == null ||
                sectionSpecifications.Count <= 1)
            {
                return false;
            }

            var referenceSpecification = sectionSpecifications[0];
            return sectionSpecifications.Skip(1).Any(sectionSpecification =>
                Math.Abs(sectionSpecification.Width -
                    referenceSpecification.Width) > GeometryTolerance ||
                Math.Abs(sectionSpecification.Thickness -
                    referenceSpecification.Thickness) > GeometryTolerance ||
                sectionSpecification.StackCount !=
                    referenceSpecification.StackCount);
        }

        private static bool CanPropagatePhysicalLayersFromStart(
            bool haveDifferentPhysicalSectionSpecifications,
            IReadOnlyList<PlanarBendBusbarSetting> templates)
        {
            return haveDifferentPhysicalSectionSpecifications &&
                templates != null &&
                templates.Count > 0 &&
                (templates.Count > 1 || !IsStraightIBar(templates[0]));
        }

        internal static bool TryAlignPhysicalBridgeFollowingLapEndpoints(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            out string failureReason)
        {
            failureReason = null;
            if (conversionResult?.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(0) != true)
            {
                return true;
            }

            // 是否属于水平始端搭接必须以实际固定始端与生成首段的方向
            // 判定，不能只看合并连接段与后一物理段是否恰好平行。
            // 后一种局部几何在垂直始端方案中同样可能出现，若继续执行
            // 水平层位对齐就会把已经正确的垂直搭接段整体移位。
            if (startTerminalAssembly != null &&
                !LinkageRouteTerminalAccessoryBuilder
                    .IsHorizontalStartTerminalLap(
                        conversionResult,
                        startTerminalAssembly))
            {
                return true;
            }

            var allBridgeFollowingPairs = (conversionResult.StackMembers ??
                    new List<LinkageStackedRouteMember>())
                .Where(stackMember => stackMember?.PlanarParts != null &&
                    stackMember.PlanarParts.Count >= 2 &&
                    stackMember.PlanarParts[0]?.PointList?.Count >= 2 &&
                    stackMember.PlanarParts[1]?.PointList?.Count >= 2)
                .Select(stackMember => new
                {
                    StackMember = stackMember,
                    BridgeSetting = stackMember.PlanarParts[0],
                    FollowingSetting = stackMember.PlanarParts[1]
                })
                .ToList();
            if (allBridgeFollowingPairs.Count == 0)
            {
                failureReason =
                    "双始端实体连接排输出后处理找不到后续普通搭接排。";
                return false;
            }

            // 连接排层位外移只适用于两段排长方向平行的水平搭接。
            // 垂直搭接必须保留搜索阶段已经确定的交叉搭接区域，不能因为
            // 首段带有多始端连接排标记就移动下一段的排厚位置。
            var bridgeFollowingPairs = allBridgeFollowingPairs
                .Where(pair => ArePhysicalBridgeFollowingSegmentsParallel(
                    pair.BridgeSetting,
                    pair.FollowingSetting))
                .ToList();
            if (bridgeFollowingPairs.Count == 0)
            {
                return true;
            }

            var referenceBridgeSetting = bridgeFollowingPairs[0].BridgeSetting;
            if (!TryGetLocalThicknessDirection(
                    referenceBridgeSetting,
                    referenceBridgeSetting.PointList.Count - 2,
                    out var commonThicknessDirection))
            {
                failureReason =
                    "双始端实体连接排输出后处理无法建立连接排末端厚向。";
                return false;
            }

            commonThicknessDirection = commonThicknessDirection.Normalized;
            var bridgeMinimumBoundary = bridgeFollowingPairs.Min(pair =>
                DotPoint(
                    pair.BridgeSetting.PointList[
                        pair.BridgeSetting.PointList.Count - 1],
                    commonThicknessDirection) -
                pair.BridgeSetting.BusBarThickness / 2d);
            var bridgeMaximumBoundary = bridgeFollowingPairs.Max(pair =>
                DotPoint(
                    pair.BridgeSetting.PointList[
                        pair.BridgeSetting.PointList.Count - 1],
                    commonThicknessDirection) +
                pair.BridgeSetting.BusBarThickness / 2d);
            var followingMinimumBoundary = bridgeFollowingPairs.Min(pair =>
                DotPoint(
                    pair.FollowingSetting.PointList[0],
                    commonThicknessDirection) -
                pair.FollowingSetting.BusBarThickness / 2d);
            var followingMaximumBoundary = bridgeFollowingPairs.Max(pair =>
                DotPoint(
                    pair.FollowingSetting.PointList[0],
                    commonThicknessDirection) +
                pair.FollowingSetting.BusBarThickness / 2d);
            if (bridgeMaximumBoundary <=
                    followingMinimumBoundary + GeometryTolerance ||
                followingMaximumBoundary <=
                    bridgeMinimumBoundary + GeometryTolerance)
            {
                return true;
            }

            var lowerSideDisplacement = bridgeMinimumBoundary -
                followingMaximumBoundary;
            var upperSideDisplacement = bridgeMaximumBoundary -
                followingMinimumBoundary;
            var selectedCommonDisplacement =
                Math.Abs(lowerSideDisplacement) <=
                    Math.Abs(upperSideDisplacement)
                    ? lowerSideDisplacement
                    : upperSideDisplacement;
            foreach (var bridgeFollowingPair in bridgeFollowingPairs)
            {
                if (!TryMovePhysicalBridgeFollowingSettingStart(
                        conversionResult,
                        bridgeFollowingPair.StackMember,
                        commonThicknessDirection,
                        selectedCommonDisplacement,
                        out failureReason))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ArePhysicalBridgeFollowingSegmentsParallel(
            PlanarBendBusbarSetting bridgeSetting,
            PlanarBendBusbarSetting followingSetting)
        {
            if (bridgeSetting?.PointList == null ||
                bridgeSetting.PointList.Count < 2 ||
                followingSetting?.PointList == null ||
                followingSetting.PointList.Count < 2)
            {
                return false;
            }

            var bridgeTerminalDirection = new Vector3d(
                bridgeSetting.PointList[bridgeSetting.PointList.Count - 2],
                bridgeSetting.PointList[bridgeSetting.PointList.Count - 1]);
            var followingStartDirection = new Vector3d(
                followingSetting.PointList[0],
                followingSetting.PointList[1]);
            if (bridgeTerminalDirection.Norm <= GeometryTolerance ||
                followingStartDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            return Math.Abs(bridgeTerminalDirection.Normalized.Dot(
                       followingStartDirection.Normalized)) >=
                1d - GeometryTolerance;
        }

        private static bool TryMovePhysicalBridgeFollowingSettingStart(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageStackedRouteMember stackedMember,
            Vector3d commonThicknessDirection,
            double selectedCommonDisplacement,
            out string failureReason)
        {
            if (!LinkageRouteTerminalAccessoryBuilder
                    .TryMoveStartTerminalLapChain(
                        conversionResult,
                        stackedMember,
                        1,
                        commonThicknessDirection,
                        selectedCommonDisplacement,
                        out failureReason))
            {
                failureReason =
                    "双始端连接排后续普通搭接排末端调整失败：" +
                    failureReason;
                return false;
            }

            return true;
        }

        private static double SelectNearestCoordinate(
            double desiredCoordinate,
            double firstCandidateCoordinate,
            double secondCandidateCoordinate)
        {
            return Math.Abs(firstCandidateCoordinate - desiredCoordinate) <=
                Math.Abs(secondCandidateCoordinate - desiredCoordinate)
                    ? firstCandidateCoordinate
                    : secondCandidateCoordinate;
        }

        private static double GetCoordinateDeviationSquared(
            IReadOnlyList<double> firstCoordinates,
            IReadOnlyList<double> secondCoordinates)
        {
            var coordinateDeviationSquared = 0d;
            for (var coordinateIndex = 0;
                coordinateIndex < firstCoordinates.Count;
                coordinateIndex++)
            {
                var coordinateDifference = firstCoordinates[coordinateIndex] -
                    secondCoordinates[coordinateIndex];
                coordinateDeviationSquared += coordinateDifference *
                    coordinateDifference;
            }

            return coordinateDeviationSquared;
        }

        private static bool IsStraightIBar(
            PlanarBendBusbarSetting planarSetting)
        {
            return planarSetting?.PointList?.Count == 2;
        }

        private static bool ArePhysicalBoundarySegmentsParallel(
            PlanarBendBusbarSetting precedingSetting,
            PlanarBendBusbarSetting followingSetting)
        {
            if (precedingSetting?.PointList == null ||
                precedingSetting.PointList.Count < 2 ||
                followingSetting?.PointList == null ||
                followingSetting.PointList.Count < 2)
            {
                return false;
            }

            var precedingDirection = new Vector3d(
                precedingSetting.PointList[
                    precedingSetting.PointList.Count - 2],
                precedingSetting.PointList[
                    precedingSetting.PointList.Count - 1]);
            var followingDirection = new Vector3d(
                followingSetting.PointList[0],
                followingSetting.PointList[1]);
            return precedingDirection.Norm > GeometryTolerance &&
                followingDirection.Norm > GeometryTolerance &&
                precedingDirection.Normalized.Cross(
                    followingDirection.Normalized).Norm <= GeometryTolerance;
        }

        private static void ApplySettingDimensions(
            PlanarBendBusbarSetting planarSetting,
            SectionSpecification sectionSpecification,
            SectionSpecification precedingSpecification,
            SectionSpecification followingSpecification)
        {
            planarSetting.BusBarWidth = sectionSpecification.Width;
            planarSetting.BusBarThickness = sectionSpecification.Thickness;
            if (sectionSpecification.StartLapWidth > GeometryTolerance)
            {
                planarSetting.StartLapWidth = sectionSpecification.StartLapWidth;
            }
            else if (precedingSpecification != null)
            {
                planarSetting.StartLapWidth = precedingSpecification.Width;
            }

            if (sectionSpecification.EndLapWidth > GeometryTolerance)
            {
                planarSetting.EndLapWidth = sectionSpecification.EndLapWidth;
            }
            else if (followingSpecification != null)
            {
                planarSetting.EndLapWidth = followingSpecification.Width;
            }
        }

        private static void EnsureInternalPerpendicularLapWidths(
            ProcessingState processingState,
            int centerlinePartCount)
        {
            for (var boundaryIndex = 0;
                boundaryIndex < centerlinePartCount - 1;
                boundaryIndex++)
            {
                if (!processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex,
                        out var precedingMemberSettings) ||
                    !processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex + 1,
                        out var followingMemberSettings))
                {
                    continue;
                }

                var precedingSettings = precedingMemberSettings
                    .Select(memberSetting => memberSetting.Setting)
                    .ToList();
                var followingSettings = followingMemberSettings
                    .Select(memberSetting => memberSetting.Setting)
                    .ToList();
                foreach (var precedingSetting in precedingSettings)
                {
                    var nearestFollowingSetting = FindNearestBoundarySetting(
                        precedingSetting,
                        false,
                        followingSettings,
                        true);
                    if (nearestFollowingSetting != null)
                    {
                        TryExtendPerpendicularBoundaryEndpoint(
                            precedingSetting,
                            false,
                            nearestFollowingSetting,
                            nearestFollowingSetting.BusBarWidth);
                    }
                }

                foreach (var followingSetting in followingSettings)
                {
                    var nearestPrecedingSetting = FindNearestBoundarySetting(
                        followingSetting,
                        true,
                        precedingSettings,
                        false);
                    if (nearestPrecedingSetting != null)
                    {
                        TryExtendPerpendicularBoundaryEndpoint(
                            followingSetting,
                            true,
                            nearestPrecedingSetting,
                            nearestPrecedingSetting.BusBarWidth);
                    }
                }
            }
        }

        private static void EnsureInternalPerpendicularLapBendReserves(
            ProcessingState processingState,
            IReadOnlyList<SectionSpecification> sectionSpecifications,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            for (var boundaryIndex = 0;
                boundaryIndex < sectionSpecifications.Count - 1;
                boundaryIndex++)
            {
                if (!processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex,
                        out var precedingMemberSettings) ||
                    !processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex + 1,
                        out var followingMemberSettings))
                {
                    continue;
                }

                var precedingSpecification =
                    sectionSpecifications[boundaryIndex];
                var followingSpecification =
                    sectionSpecifications[boundaryIndex + 1];
                var precedingStackBendAllowance =
                    LinkageRouteStackGeometry.GetStackBendAllowance(
                        precedingSpecification.Thickness,
                        precedingSpecification.StackCount,
                        routeGroupRequest.StackMiddleSpace,
                        routeRequest.GetEffectiveBendAllowance());
                var followingStackBendAllowance =
                    LinkageRouteStackGeometry.GetStackBendAllowance(
                        followingSpecification.Thickness,
                        followingSpecification.StackCount,
                        routeGroupRequest.StackMiddleSpace,
                        routeRequest.GetEffectiveBendAllowance());
                var precedingSettingsRequiringReserve =
                    new List<PlanarBendBusbarSetting>();
                var followingSettingsRequiringReserve =
                    new List<PlanarBendBusbarSetting>();
                foreach (var precedingMemberSetting in precedingMemberSettings)
                {
                    var precedingSetting = precedingMemberSetting?.Setting;
                    var followingSetting = FindNearestBoundarySetting(
                        precedingSetting,
                        false,
                        followingMemberSettings.Select(memberSetting =>
                            memberSetting.Setting),
                        true);
                    if (!AreBoundarySegmentsPerpendicular(
                            precedingSetting,
                            false,
                            followingSetting,
                            true))
                    {
                        continue;
                    }

                    precedingSettingsRequiringReserve.Add(precedingSetting);
                    followingSettingsRequiringReserve.Add(followingSetting);
                }

                foreach (var followingMemberSetting in followingMemberSettings)
                {
                    var followingSetting = followingMemberSetting?.Setting;
                    var precedingSetting = FindNearestBoundarySetting(
                        followingSetting,
                        true,
                        precedingMemberSettings.Select(memberSetting =>
                            memberSetting.Setting),
                        false);
                    if (!AreBoundarySegmentsPerpendicular(
                            precedingSetting,
                            false,
                            followingSetting,
                            true))
                    {
                        continue;
                    }

                    precedingSettingsRequiringReserve.Add(precedingSetting);
                    followingSettingsRequiringReserve.Add(followingSetting);
                }

                if (routeRequest.AllowBendGeometricLimit)
                {
                    // These are already expanded physical members. Reapplying
                    // the complete stack envelope here overextends a legal L
                    // corner and tilts the adjoining terminal leg. In explicit
                    // extreme mode use each actual arc tangent, not the envelope.
                    EnsureExtremeActualBoundaryReserves(precedingSettingsRequiringReserve,
                        false, followingSpecification.Width, routeRequest);
                    EnsureExtremeActualBoundaryReserves(followingSettingsRequiringReserve,
                        true, precedingSpecification.Width, routeRequest);
                }
                else
                {
                    EnsureBoundarySegmentMinimumLengthsPreservingOffsets(
                        precedingSettingsRequiringReserve,
                        false,
                        followingSpecification.Width +
                            precedingStackBendAllowance);
                    EnsureBoundarySegmentMinimumLengthsPreservingOffsets(
                        followingSettingsRequiringReserve,
                        true,
                        precedingSpecification.Width +
                            followingStackBendAllowance);
                }
            }
        }

        private static void EnsureExtremeActualBoundaryReserves(
            IEnumerable<PlanarBendBusbarSetting> actualSettings,
            bool useStartBoundary, double opposingWidth,
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var actualBoundaryMembers = actualSettings.Where(setting =>
                setting?.PointList?.Count >= 3).Distinct().ToList();
            var commonExtension = 0d;
            foreach (var actualBoundaryMember in actualBoundaryMembers)
            {
                var actualReserve = LinkageRouteRoundedLapGeometry.GetActualMemberReserve(
                    routeRequest, actualBoundaryMember, useStartBoundary);
                // An invalid arc is rejected by the unchanged physical gate;
                // never generate infinity/NaN coordinates while constructing it.
                if (double.IsNaN(actualReserve) || double.IsInfinity(actualReserve))
                    continue;
                commonExtension = Math.Max(commonExtension,
                    opposingWidth + actualReserve - GetBoundarySegmentLength(
                        actualBoundaryMember, useStartBoundary));
            }
            if (commonExtension <= GeometryTolerance)
                return;
            foreach (var actualBoundaryMember in actualBoundaryMembers)
                EnsureBoundarySegmentMinimumLength(actualBoundaryMember, useStartBoundary,
                    GetBoundarySegmentLength(actualBoundaryMember, useStartBoundary) + commonExtension);
        }

        internal static bool
            TryEnsureFinalTerminalPerpendicularLapBendReservesFromExistingGeometry(
                LinkageRouteCenterlineConversionResult conversionResult,
                LinkageMultiGroupRouteRequest routeRequest,
                LinkageRouteGroupRequest routeGroupRequest,
                out string failureReason)
        {
            failureReason = null;
            if (conversionResult == null || routeRequest == null ||
                routeGroupRequest == null ||
                !TryCreateProcessingStateFromExistingGeometry(
                    conversionResult, out var processingState,
                    out failureReason))
            {
                return false;
            }

            var physicalSectionCount = conversionResult.CenterlineParts.Count;
            var sectionSpecifications = new List<SectionSpecification>();
            for (var sectionIndex = 0;
                sectionIndex < physicalSectionCount;
                sectionIndex++)
            {
                var currentSectionSettings = processingState
                    .SettingsBySectionIndex[sectionIndex];
                var referenceSetting = currentSectionSettings
                    .FirstOrDefault()?.Setting;
                if (referenceSetting == null)
                {
                    failureReason = "最终端腿复核缺少实际物理分段。";
                    return false;
                }

                sectionSpecifications.Add(new SectionSpecification
                {
                    Label = "最终实际物理分段",
                    Width = referenceSetting.BusBarWidth,
                    Thickness = referenceSetting.BusBarThickness,
                    StackCount = currentSectionSettings.Count,
                    StartLapWidth = sectionIndex == 0
                        ? GetActualTerminalOpposingWidth(
                            currentSectionSettings,
                            routeGroupRequest.StartTerminal, true,
                            referenceSetting.StartLapWidth > GeometryTolerance
                                ? referenceSetting.StartLapWidth
                                : routeGroupRequest.GetStartLapBusbarWidth(
                                    routeRequest))
                        : 0d,
                    EndLapWidth = sectionIndex == physicalSectionCount - 1
                        ? GetActualTerminalOpposingWidth(
                            currentSectionSettings,
                            routeGroupRequest.EndTerminal, false,
                            referenceSetting.EndLapWidth > GeometryTolerance
                                ? referenceSetting.EndLapWidth
                                : routeGroupRequest.GetCabinetTopLapBusbarWidth(
                                    routeRequest))
                        : 0d
                });
            }

            if (!routeRequest.AllowPartialStartPerpendicularLap &&
                !TryEnsureFinalTerminalReserveWithChain(conversionResult,
                    processingState.SettingsBySectionIndex[0], sectionSpecifications[0],
                    routeRequest, routeGroupRequest, true, out failureReason))
                return false;
            if (!TryEnsureFinalTerminalReserveWithChain(conversionResult,
                    processingState.SettingsBySectionIndex[physicalSectionCount - 1],
                    sectionSpecifications[physicalSectionCount - 1],
                    routeRequest, routeGroupRequest, false, out failureReason))
                return false;
            return true;
        }

        private static bool TryEnsureFinalTerminalReserveWithChain(
            LinkageRouteCenterlineConversionResult reserveConversion,
            IEnumerable<SectionMemberSetting> reserveMembers,
            SectionSpecification reserveSpecification,
            LinkageMultiGroupRouteRequest reserveRequest,
            LinkageRouteGroupRequest reserveGroupRequest,
            bool reserveAtStart,
            out string failureReason,
            double additionalExtension = 0d)
        {
            failureReason = null;
            var reserveMemberList = reserveMembers.ToList();
            var reserveOriginalSettings = reserveMemberList.ToDictionary(
                member => member.Setting, member => member.Setting.Copy());
            var reserveProposedMembers = reserveMemberList.Select(member =>
                new SectionMemberSetting { MemberIndex = member.MemberIndex,
                    Setting = member.Setting.Copy() }).ToList();
            if (Math.Abs(additionalExtension) > GeometryTolerance)
            {
                foreach (var proposed in reserveProposedMembers.Where(member => member.Setting.PointList.Count >= 3))
                {
                    var proposedPoints = proposed.Setting.PointList;
                    var anchorIndex = reserveAtStart ? 0 : proposedPoints.Count - 1;
                    var cornerIndex = reserveAtStart ? 1 : proposedPoints.Count - 2;
                    var boundaryDirection = new Vector3d(proposedPoints[anchorIndex], proposedPoints[cornerIndex]);
                    if (boundaryDirection.Norm + additionalExtension <= GeometryTolerance)
                    { failureReason = "圆弧极限调整不能产生零长端腿。"; return false; }
                    proposedPoints[cornerIndex] = new Point3d(
                        proposedPoints[cornerIndex].X + boundaryDirection.Normalized.X * additionalExtension,
                        proposedPoints[cornerIndex].Y + boundaryDirection.Normalized.Y * additionalExtension,
                        proposedPoints[cornerIndex].Z + boundaryDirection.Normalized.Z * additionalExtension);
                }
            }
            else
                EnsureTerminalPerpendicularLapBendReserve(reserveProposedMembers,
                    reserveAtStart ? reserveGroupRequest.StartTerminal : reserveGroupRequest.EndTerminal,
                    reserveAtStart ? reserveConversion.StartStackPlacement : reserveConversion.EndStackPlacement,
                    reserveSpecification, reserveAtStart, reserveRequest, reserveGroupRequest);
            for (var reserveMemberIndex = 0; reserveMemberIndex < reserveMemberList.Count; reserveMemberIndex++)
            {
                var reserveMember = reserveMemberList[reserveMemberIndex];
                var reserveSetting = reserveMember.Setting;
                var reserveOriginal = reserveOriginalSettings[reserveSetting];
                if (reserveOriginal.PointList.Count < 3)
                    continue;
                var reserveCornerIndex = reserveAtStart ? 1 : reserveOriginal.PointList.Count - 2;
                var reserveCornerDisplacement = new Vector3d(reserveOriginal.PointList[reserveCornerIndex],
                    reserveProposedMembers[reserveMemberIndex].Setting.PointList[reserveCornerIndex]);
                if (reserveCornerDisplacement.Norm <= GeometryTolerance)
                    continue;
                // Offset the adjacent straight segment and intersect its original neighbours,
                // rather than tilting it by editing only the shared corner.
                reserveSetting.PointList = reserveOriginal.PointList.Select(point => point.Copy()).ToList();
                var reserveAdjacentSegment = reserveAtStart ? 1 : reserveOriginal.PointList.Count - 3;
                var reserveFirstThickness = new Vector3d(reserveOriginal.PointList[0], reserveOriginal.PointList[1])
                    .Normalized.Cross(reserveOriginal.WidthVector.Normalized).Normalized;
                var reserveOrientation = reserveFirstThickness.Dot(reserveOriginal.StartBusbarVector.Normalized) < 0d ? -1d : 1d;
                var reserveAdjacentThickness = new Vector3d(reserveOriginal.PointList[reserveAdjacentSegment],
                    reserveOriginal.PointList[reserveAdjacentSegment + 1]).Normalized
                    .Cross(reserveOriginal.WidthVector.Normalized).Normalized;
                var reserveOffsets = new double[reserveOriginal.PointList.Count - 1];
                reserveOffsets[reserveAdjacentSegment] = reserveOrientation * reserveAdjacentThickness.Dot(reserveCornerDisplacement);
                if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(reserveOriginal, reserveOffsets, true,
                        out var reserveAlignedPoints, out failureReason) ||
                    reserveAlignedPoints.Count != reserveOriginal.PointList.Count)
                {
                    failureReason = "最终端腿补齐无法保持相邻直段：" + failureReason;
                    return false;
                }
                reserveSetting.PointList = reserveAlignedPoints;
                var reserveFixedIndex = reserveAtStart ? 0 : reserveAlignedPoints.Count - 1;
                if (reserveOriginal.PointList[reserveFixedIndex].DistanceTo(reserveAlignedPoints[reserveFixedIndex]) > GeometryTolerance)
                {
                    reserveSetting.PointList = reserveOriginal.PointList;
                    failureReason = "最终端腿补齐会移动已锁定的端部锚点。";
                    return false;
                }
                var reserveFreeIndex = reserveAtStart ? reserveAlignedPoints.Count - 1 : 0;
                var reserveFreeDisplacement = new Vector3d(reserveOriginal.PointList[reserveFreeIndex],
                    reserveAlignedPoints[reserveFreeIndex]);
                if (reserveFreeDisplacement.Norm <= GeometryTolerance)
                    continue;
                var reserveStackMember = reserveConversion.StackMembers.FirstOrDefault(member =>
                    member.PlanarParts.Any(part => ReferenceEquals(part, reserveSetting)));
                var reservePartIndex = reserveStackMember?.PlanarParts.IndexOf(reserveSetting) ?? -1;
                var reserveNextIndex = reservePartIndex + (reserveAtStart ? 1 : -1);
                if (reservePartIndex < 0 || reserveNextIndex < 0 || reserveNextIndex >= reserveStackMember.PlanarParts.Count)
                {
                    reserveSetting.PointList = reserveOriginal.PointList;
                    failureReason = "最终端腿补齐没有可吸收自由端位移的相邻物理排。";
                    return false;
                }
                // Never propagate into a locked merged connection plate. Ordinary
                // rigid I bars may still move and leave a real gap before this boundary.
                var reservePropagationIndices = new List<int>();
                for (var reservePropagationIndex = reserveNextIndex;
                    reservePropagationIndex >= 0 && reservePropagationIndex < reserveStackMember.PlanarParts.Count;
                    reservePropagationIndex += reserveAtStart ? 1 : -1)
                {
                    if (reserveConversion.DoubleStartPhysicalBridgeSectionIndices.Contains(
                            reserveStackMember.PlanarParts[reservePropagationIndex].PhysicalSectionIndex))
                        break;
                    reservePropagationIndices.Add(reservePropagationIndex);
                }
                if (reservePropagationIndices.Count == 0)
                {
                    reserveSetting.PointList = reserveOriginal.PointList;
                    failureReason = "最终端腿补齐受限于锁定连接排，不能向其传递位移。";
                    return false;
                }
                var reservePropagationMember = new LinkageStackedRouteMember { MemberIndex = reserveStackMember.MemberIndex };
                foreach (var reservePropagationIndex in reservePropagationIndices)
                {
                    var reservePropagationPart = reserveStackMember.PlanarParts[reservePropagationIndex].Copy();
                    if (!reserveAtStart)
                    {
                        reservePropagationPart.PointList.Reverse();
                        reservePropagationPart.StartBusbarVector = new Vector3d(reservePropagationPart.PointList[0],
                            reservePropagationPart.PointList[1]).Normalized.Cross(reservePropagationPart.WidthVector.Normalized).Normalized;
                    }
                    reservePropagationMember.PlanarParts.Add(reservePropagationPart);
                }
                var reservePropagationSucceeded = LinkageRouteTerminalAccessoryBuilder.TryMoveStartTerminalLapChain(
                    reserveConversion, reservePropagationMember, 0, reserveFreeDisplacement.Normalized,
                    reserveFreeDisplacement.Norm, out failureReason);
                for (var reservePropagationIndex = 0; reservePropagationIndex < reservePropagationIndices.Count; reservePropagationIndex++)
                {
                    var reservePropagationPoints = reservePropagationMember.PlanarParts[reservePropagationIndex].PointList.AsEnumerable();
                    if (!reserveAtStart)
                        reservePropagationPoints = reservePropagationPoints.Reverse();
                    reserveStackMember.PlanarParts[reservePropagationIndices[reservePropagationIndex]].PointList =
                        reservePropagationPoints.Select(point => point.Copy()).ToList();
                }
                if (!reservePropagationSucceeded)
                    return false;
            }
            return true;
        }

        internal static bool TryValidateFinalTerminalPerpendicularLapBendReserves(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageGroupRouteSolution groupSolution,
            out string failureReason)
        {
            failureReason = null;
            if (routeRequest == null || routeGroupRequest == null ||
                groupSolution?.StackMembers == null ||
                groupSolution.PlanarParts == null ||
                groupSolution.PlanarParts.Count == 0)
            {
                failureReason = "最终端部搭接折弯余量复核缺少请求或实际叠排。";
                return false;
            }

            var firstPhysicalSectionIndex =
                groupSolution.PlanarParts[0]?.PhysicalSectionIndex >= 0
                    ? groupSolution.PlanarParts[0].PhysicalSectionIndex
                    : 0;
            var lastPhysicalSectionIndex =
                groupSolution.PlanarParts[groupSolution.PlanarParts.Count - 1]
                    ?.PhysicalSectionIndex >= 0
                    ? groupSolution.PlanarParts[groupSolution.PlanarParts.Count - 1]
                        .PhysicalSectionIndex
                    : groupSolution.PlanarParts.Count - 1;
            foreach (var stackedRouteMember in groupSolution.StackMembers)
            {
                if (stackedRouteMember?.PlanarParts == null ||
                    stackedRouteMember.PlanarParts.Count == 0)
                {
                    failureReason = "最终端部搭接折弯余量复核缺少实际铜排。";
                    return false;
                }

                var firstMemberPart = stackedRouteMember.PlanarParts[0];
                var lastMemberPart = stackedRouteMember.PlanarParts[
                    stackedRouteMember.PlanarParts.Count - 1];
                // 异规格成员可从第二个物理段开始或在末段前结束。
                // 只有明确标记了物理段身份时才跳过缺席端；身份缺失时
                // 保持原来的保守门禁，不把无法识别当成允许发布。
                var reachesStartTerminal = firstMemberPart
                    .PhysicalSectionIndex < 0 ||
                    firstMemberPart.PhysicalSectionIndex ==
                    firstPhysicalSectionIndex;
                var reachesEndTerminal = lastMemberPart
                    .PhysicalSectionIndex < 0 ||
                    lastMemberPart.PhysicalSectionIndex ==
                    lastPhysicalSectionIndex;
                if (!routeRequest.AllowPartialStartPerpendicularLap &&
                    reachesStartTerminal &&
                    !TryValidateFinalTerminalPerpendicularLapBendReserve(
                        routeRequest, routeGroupRequest, groupSolution.GroupKey,
                        firstMemberPart,
                        routeGroupRequest.StartTerminal, true,
                        routeGroupRequest.GetStartLapBusbarThickness(routeRequest),
                        routeGroupRequest.GetStartLapStackCount(routeRequest),
                        out failureReason))
                {
                    return false;
                }

                if (reachesEndTerminal &&
                    !TryValidateFinalTerminalPerpendicularLapBendReserve(
                        routeRequest, routeGroupRequest, groupSolution.GroupKey,
                        lastMemberPart,
                        routeGroupRequest.EndTerminal, false,
                        routeGroupRequest.GetCabinetTopLapBusbarThickness(routeRequest),
                        routeGroupRequest.GetCabinetTopLapStackCount(routeRequest),
                        out failureReason))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateFinalTerminalPerpendicularLapBendReserve(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            string groupKey,
            PlanarBendBusbarSetting generatedSetting,
            LinkageTerminalAssembly terminalAssembly,
            bool useGeneratedStart,
            double sectionThickness,
            int sectionStackCount,
            out string failureReason)
        {
            failureReason = null;
            if (generatedSetting?.PointList == null ||
                generatedSetting.PointList.Count < 3 ||
                terminalAssembly?.Members == null)
            {
                return true;
            }

            var opposingWidth = GetActualTerminalOpposingWidth(
                generatedSetting, terminalAssembly, useGeneratedStart);
            if (opposingWidth <= GeometryTolerance)
            {
                return true;
            }

            var requiredLength = opposingWidth +
                LinkageRouteRoundedLapGeometry.GetActualMemberReserve(
                    routeRequest, generatedSetting, useGeneratedStart);
            var actualLength = GetBoundarySegmentLength(
                generatedSetting, useGeneratedStart);
            if (actualLength +
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison >=
                requiredLength)
            {
                return true;
            }

            failureReason = string.Format(CultureInfo.InvariantCulture,
                "组 {0} 的{1}垂直搭接折弯过早：实际 {2:F3} mm，至少需要固定排宽加折弯余量 {3:F3} mm。",
                groupKey, useGeneratedStart ? "始端" : "柜顶",
                actualLength, requiredLength);
            return false;
        }

        private static double GetActualTerminalOpposingWidth(
            IEnumerable<SectionMemberSetting> generatedMembers,
            LinkageTerminalAssembly terminalAssembly,
            bool useGeneratedStart,
            double fallbackWidth)
        {
            var actualWidth = (generatedMembers ??
                    Enumerable.Empty<SectionMemberSetting>())
                .Select(member => GetActualTerminalOpposingWidth(
                    member?.Setting, terminalAssembly, useGeneratedStart))
                .DefaultIfEmpty(0d)
                .Max();
            return actualWidth > GeometryTolerance
                ? actualWidth
                : fallbackWidth;
        }

        private static double GetActualTerminalOpposingWidth(
            PlanarBendBusbarSetting generatedSetting,
            LinkageTerminalAssembly terminalAssembly,
            bool useGeneratedStart)
        {
            var perpendicularSettings = (terminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
                .Select(member => member?.FixedBusbar)
                .Where(fixedSetting => fixedSetting != null &&
                    AreTerminalBoundarySegmentsPerpendicular(
                        generatedSetting, useGeneratedStart, fixedSetting))
                .ToList();
            var contactedSettings = perpendicularSettings
                .Where(fixedSetting => HasActualTerminalLapContact(
                    generatedSetting, useGeneratedStart, fixedSetting))
                .ToList();
            if (contactedSettings.Count > 0)
            {
                return contactedSettings.Max(setting => setting.BusBarWidth);
            }

            // 搭接投影在端腿短差处可能恰好未达到满宽；不能因此让
            // 发布端腿门禁失效。按实际有限端段实体净距选对应固定排，
            // 不以中心线距离误选邻近但位于另一厚度层的宽排。
            // 真正未搭接仍会由完整搭接门禁拒绝。
            var nearestCandidates = perpendicularSettings
                .Select(setting => new
                {
                    Setting = setting,
                    Distance = GetTerminalBoundarySolidDistance(
                        generatedSetting, useGeneratedStart, setting)
                })
                .OrderBy(candidate => candidate.Distance)
                .ToList();
            if (nearestCandidates.Count == 0)
            {
                return 0d;
            }
            var minimumDistance = nearestCandidates[0].Distance;
            return nearestCandidates
                .Where(candidate => candidate.Distance <= minimumDistance +
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                .Max(candidate => candidate.Setting.BusBarWidth);
        }

        private static double GetTerminalBoundarySolidDistance(
            PlanarBendBusbarSetting generatedSetting,
            bool useGeneratedStart,
            PlanarBendBusbarSetting fixedSetting)
        {
            var generatedSegmentIndex = useGeneratedStart
                ? 0
                : generatedSetting.PointList.Count - 2;
            var fixedSegmentIndex = useGeneratedStart
                ? fixedSetting.PointList.Count - 2
                : 0;
            if (TryCreateLapSegmentSolid(generatedSetting,
                    generatedSegmentIndex, out var generatedSolid) &&
                TryCreateLapSegmentSolid(fixedSetting,
                    fixedSegmentIndex, out var fixedSolid))
            {
                return LinkageRouteSolidDistance.GetMinimumDistance(
                    generatedSolid, fixedSolid);
            }
            return GetTerminalEndpointToFixedBoundaryDistance(
                generatedSetting, useGeneratedStart, fixedSetting);
        }

        private static double GetTerminalEndpointToFixedBoundaryDistance(
            PlanarBendBusbarSetting generatedSetting,
            bool useGeneratedStart,
            PlanarBendBusbarSetting fixedSetting)
        {
            var generatedEndpoint = useGeneratedStart
                ? generatedSetting.PointList[0]
                : generatedSetting.PointList.Last();
            var fixedSegmentStart = useGeneratedStart
                ? fixedSetting.PointList[fixedSetting.PointList.Count - 2]
                : fixedSetting.PointList[0];
            var fixedSegmentEnd = useGeneratedStart
                ? fixedSetting.PointList.Last()
                : fixedSetting.PointList[1];
            var fixedDirection = new Vector3d(
                fixedSegmentStart, fixedSegmentEnd);
            var projection = Math.Max(0d, Math.Min(1d,
                new Vector3d(fixedSegmentStart, generatedEndpoint)
                    .Dot(fixedDirection) /
                (fixedDirection.Norm * fixedDirection.Norm)));
            var nearestPoint = new Point3d(
                fixedSegmentStart.X + fixedDirection.X * projection,
                fixedSegmentStart.Y + fixedDirection.Y * projection,
                fixedSegmentStart.Z + fixedDirection.Z * projection);
            return new Vector3d(nearestPoint, generatedEndpoint).Norm;
        }

        internal static bool TryValidateFinalInternalPerpendicularLapBendReserves(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageGroupRouteSolution groupSolution,
            out string failureReason)
        {
            failureReason = null;
            if (routeRequest == null || routeGroupRequest == null ||
                groupSolution?.PlanarParts == null ||
                !routeGroupRequest.HasSectionSpecificBusbarSettings(routeRequest))
            {
                return true;
            }

            var referenceSectionCount = groupSolution.PlanarParts.Count;
            if (referenceSectionCount < 2)
            {
                return true;
            }

            var indexedReferenceParts = groupSolution.PlanarParts.All(
                setting => setting != null &&
                    (setting.SpecificationSectionIndex >= 0 ||
                     setting.PhysicalSectionIndex >= 0));
            var specificationSectionCount = indexedReferenceParts
                ? groupSolution.PlanarParts.Max(setting =>
                    setting.SpecificationSectionIndex >= 0
                        ? setting.SpecificationSectionIndex
                        : setting.PhysicalSectionIndex) + 1
                : referenceSectionCount;
            var firstPartsBySpecification = new Dictionary<int,
                List<PlanarBendBusbarSetting>>();
            var lastPartsBySpecification = new Dictionary<int,
                List<PlanarBendBusbarSetting>>();
            foreach (var stackedRouteMember in groupSolution.StackMembers ??
                new List<LinkageStackedRouteMember>())
            {
                if (stackedRouteMember?.PlanarParts == null)
                {
                    failureReason = "最终异规格输出缺少实际叠排成员。";
                    return false;
                }

                var memberPartsBySpecification = new Dictionary<int,
                    List<PlanarBendBusbarSetting>>();
                for (var memberPartIndex = 0;
                    memberPartIndex < stackedRouteMember.PlanarParts.Count;
                    memberPartIndex++)
                {
                    var memberPart = stackedRouteMember.PlanarParts[memberPartIndex];
                    var specificationIndex = memberPart?.SpecificationSectionIndex ?? -1;
                    if (specificationIndex < 0 && memberPart != null)
                    {
                        specificationIndex = memberPart.PhysicalSectionIndex >= 0 &&
                            referenceSectionCount == specificationSectionCount
                            ? memberPart.PhysicalSectionIndex
                            : stackedRouteMember.PlanarParts.Count ==
                                specificationSectionCount
                                ? memberPartIndex
                                : -1;
                    }
                    if (specificationIndex < 0 ||
                        specificationIndex >= specificationSectionCount)
                    {
                        failureReason = string.Format(CultureInfo.InvariantCulture,
                            "组 {0} 第 {1} 层实际铜排无法识别规格段索引。",
                            groupSolution.GroupKey, stackedRouteMember.MemberIndex + 1);
                        return false;
                    }
                    if (!memberPartsBySpecification.TryGetValue(specificationIndex,
                            out var memberSpecificationParts))
                    {
                        memberSpecificationParts = new List<PlanarBendBusbarSetting>();
                        memberPartsBySpecification.Add(specificationIndex,
                            memberSpecificationParts);
                    }
                    memberSpecificationParts.Add(memberPart);
                }

                foreach (var specificationParts in memberPartsBySpecification)
                {
                    var specificationIndex = specificationParts.Key;
                    var parts = specificationParts.Value;
                    if (!firstPartsBySpecification.TryGetValue(specificationIndex,
                            out var firstParts))
                    {
                        firstParts = new List<PlanarBendBusbarSetting>();
                        firstPartsBySpecification.Add(specificationIndex, firstParts);
                        lastPartsBySpecification.Add(specificationIndex,
                            new List<PlanarBendBusbarSetting>());
                    }
                    firstParts.Add(parts[0]);
                    lastPartsBySpecification[specificationIndex].Add(
                        parts[parts.Count - 1]);
                    for (var splitPartIndex = 0;
                        splitPartIndex < parts.Count - 1;
                        splitPartIndex++)
                    {
                        if (!TryValidateFinalPerpendicularBoundaryPair(
                                routeRequest, routeGroupRequest, groupSolution.GroupKey,
                                parts[splitPartIndex], parts[splitPartIndex + 1],
                                specificationIndex, specificationIndex,
                                specificationSectionCount, out failureReason))
                        {
                            return false;
                        }
                    }
                }
            }

            for (var boundaryIndex = 0;
                boundaryIndex < specificationSectionCount - 1;
                boundaryIndex++)
            {
                if (!lastPartsBySpecification.TryGetValue(boundaryIndex,
                        out var precedingSettings) ||
                    !firstPartsBySpecification.TryGetValue(boundaryIndex + 1,
                        out var followingSettings))
                {
                    failureReason = string.Format(CultureInfo.InvariantCulture,
                        "组 {0} 第 {1} 个规格搭接面缺少实际铜排。",
                        groupSolution.GroupKey, boundaryIndex + 1);
                    return false;
                }
                foreach (var precedingSetting in precedingSettings)
                {
                    var followingSetting = FindNearestBoundarySetting(
                        precedingSetting, false, followingSettings, true);
                    if (!TryValidateFinalPerpendicularBoundaryPair(
                            routeRequest, routeGroupRequest, groupSolution.GroupKey,
                            precedingSetting, followingSetting,
                            boundaryIndex, boundaryIndex + 1,
                            specificationSectionCount, out failureReason))
                    {
                        return false;
                    }
                }
                foreach (var followingSetting in followingSettings)
                {
                    var precedingSetting = FindNearestBoundarySetting(
                        followingSetting, true, precedingSettings, false);
                    if (!TryValidateFinalPerpendicularBoundaryPair(
                            routeRequest, routeGroupRequest, groupSolution.GroupKey,
                            precedingSetting, followingSetting,
                            boundaryIndex, boundaryIndex + 1,
                            specificationSectionCount, out failureReason))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryValidateFinalPerpendicularBoundaryPair(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            string groupKey,
            PlanarBendBusbarSetting precedingSetting,
            PlanarBendBusbarSetting followingSetting,
            int precedingSpecificationIndex,
            int followingSpecificationIndex,
            int specificationSectionCount,
            out string failureReason)
        {
            failureReason = null;
            if (!AreBoundarySegmentsPerpendicular(precedingSetting,
                    false, followingSetting, true))
            {
                return true;
            }

            var requiredPrecedingLength = followingSetting.BusBarWidth +
                LinkageRouteRoundedLapGeometry.GetActualMemberReserve(
                    routeRequest, precedingSetting, false);
            if (precedingSetting.PointList.Count >= 3 &&
                GetBoundarySegmentLength(precedingSetting, false) +
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison < requiredPrecedingLength)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "组 {0} 第 {1} 个规格搭接面的前段折弯过早：实际 {2:F3} mm，至少需要 {3:F3} mm。",
                    groupKey, precedingSpecificationIndex + 1,
                    GetBoundarySegmentLength(precedingSetting, false),
                    requiredPrecedingLength);
                return false;
            }

            var requiredFollowingLength = precedingSetting.BusBarWidth +
                LinkageRouteRoundedLapGeometry.GetActualMemberReserve(
                    routeRequest, followingSetting, true);
            if (followingSetting.PointList.Count >= 3 &&
                GetBoundarySegmentLength(followingSetting, true) +
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison < requiredFollowingLength)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "组 {0} 第 {1} 个规格搭接面的后段折弯过早：实际 {2:F3} mm，至少需要 {3:F3} mm。",
                    groupKey, precedingSpecificationIndex + 1,
                    GetBoundarySegmentLength(followingSetting, true),
                    requiredFollowingLength);
                return false;
            }
            return true;
        }

        private static bool TryValidateInternalPerpendicularLapBendReserves(
            ProcessingState processingState,
            IReadOnlyList<SectionSpecification> sectionSpecifications,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            failureReason = null;
            for (var boundaryIndex = 0;
                boundaryIndex < sectionSpecifications.Count - 1;
                boundaryIndex++)
            {
                if (!processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex,
                        out var precedingMemberSettings) ||
                    !processingState.SettingsBySectionIndex.TryGetValue(
                        boundaryIndex + 1,
                        out var followingMemberSettings))
                {
                    continue;
                }

                var precedingSpecification =
                    sectionSpecifications[boundaryIndex];
                var followingSpecification =
                    sectionSpecifications[boundaryIndex + 1];
                var requiredPrecedingLength = followingSpecification.Width +
                    LinkageRouteStackGeometry.GetStackBendAllowance(
                        precedingSpecification.Thickness,
                        precedingSpecification.StackCount,
                        routeGroupRequest.StackMiddleSpace,
                        routeRequest.GetEffectiveBendAllowance());
                var requiredFollowingLength = precedingSpecification.Width +
                    LinkageRouteStackGeometry.GetStackBendAllowance(
                        followingSpecification.Thickness,
                        followingSpecification.StackCount,
                        routeGroupRequest.StackMiddleSpace,
                        routeRequest.GetEffectiveBendAllowance());
                foreach (var precedingMemberSetting in precedingMemberSettings)
                {
                    var precedingSetting = precedingMemberSetting?.Setting;
                    var followingSetting = FindNearestBoundarySetting(
                        precedingSetting,
                        false,
                        followingMemberSettings.Select(memberSetting =>
                            memberSetting.Setting),
                        true);
                    if (!AreBoundarySegmentsPerpendicular(
                            precedingSetting,
                            false,
                            followingSetting,
                            true))
                    {
                        continue;
                    }

                    // 实际排已经落位：允许减少时，准备因优选余量淘汰前先按本排真实圆弧下界验算。
                    // 初始Ensure仍优先构造设置B；这里不重新搜索或修改请求，也不豁免后续实际搭接面。
                    var allowPhysicalBendReserve = routeRequest.AllowBendGeometricLimit ||
                        routeRequest.AllowConstrainedBendAllowanceReduction;
                    var acceptedPrecedingLength = allowPhysicalBendReserve
                        ? followingSetting.BusBarWidth + LinkageRouteRoundedLapGeometry.GetActualMemberReserve(
                            routeRequest, precedingSetting, false) : requiredPrecedingLength;
                    var acceptedFollowingLength = allowPhysicalBendReserve
                        ? precedingSetting.BusBarWidth + LinkageRouteRoundedLapGeometry.GetActualMemberReserve(
                            routeRequest, followingSetting, true) : requiredFollowingLength;
                    if (precedingSetting.PointList.Count >= 3 &&
                        GetBoundarySegmentLength(precedingSetting, false) +
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison < acceptedPrecedingLength)
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "第 {0} 个内部搭接面的前段折弯过早：实际 {1:F3} mm，至少需要 {2:F3} mm。",
                            boundaryIndex + 1,
                            GetBoundarySegmentLength(precedingSetting, false),
                            acceptedPrecedingLength);
                        return false;
                    }

                    if (followingSetting.PointList.Count >= 3 &&
                        GetBoundarySegmentLength(followingSetting, true) +
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison < acceptedFollowingLength)
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "第 {0} 个内部搭接面的后段折弯过早：实际 {1:F3} mm，至少需要 {2:F3} mm。",
                            boundaryIndex + 1,
                            GetBoundarySegmentLength(followingSetting, true),
                            acceptedFollowingLength);
                        return false;
                    }
                }
            }

            return true;
        }

        private static void EnsureTerminalPerpendicularLapBendReserves(
            ProcessingState processingState,
            IReadOnlyList<SectionSpecification> sectionSpecifications,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            LinkageRouteStackTerminalPlacement endTerminalPlacement,
            bool preservePartialStartPerpendicularLap)
        {
            if (processingState?.SettingsBySectionIndex == null ||
                sectionSpecifications == null ||
                sectionSpecifications.Count == 0)
            {
                return;
            }

            if (!preservePartialStartPerpendicularLap &&
                processingState.SettingsBySectionIndex.TryGetValue(
                    0,
                    out var startSettings))
            {
                EnsureTerminalPerpendicularLapBendReserve(
                    startSettings,
                    routeGroupRequest.StartTerminal,
                    startTerminalPlacement,
                    sectionSpecifications[0],
                    true,
                    routeRequest,
                    routeGroupRequest);
            }

            var cabinetTopSectionIndex = sectionSpecifications.Count - 1;
            if (processingState.SettingsBySectionIndex.TryGetValue(
                    cabinetTopSectionIndex,
                    out var cabinetTopSettings))
            {
                EnsureTerminalPerpendicularLapBendReserve(
                    cabinetTopSettings,
                    routeGroupRequest.EndTerminal,
                    endTerminalPlacement,
                    sectionSpecifications[cabinetTopSectionIndex],
                    false,
                    routeRequest,
                    routeGroupRequest);
            }
        }

        private static void EnsureTerminalPerpendicularLapBendReserve(
            IEnumerable<SectionMemberSetting> generatedMemberSettings,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            SectionSpecification sectionSpecification,
            bool useGeneratedStart,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (terminalAssembly?.Members == null ||
                sectionSpecification == null)
            {
                return;
            }

            var fixedSettings = terminalAssembly.Members
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(fixedSetting => fixedSetting?.PointList != null &&
                    fixedSetting.PointList.Count >= 2)
                .ToList();
            var effectiveOpposingWidth = useGeneratedStart &&
                    sectionSpecification.StartLapWidth > GeometryTolerance
                ? sectionSpecification.StartLapWidth
                : !useGeneratedStart &&
                    sectionSpecification.EndLapWidth > GeometryTolerance
                    ? sectionSpecification.EndLapWidth
                    : sectionSpecification.Width;
            if (fixedSettings.Count == 0 ||
                effectiveOpposingWidth <= GeometryTolerance)
            {
                return;
            }

            var stackBendAllowance = LinkageRouteStackGeometry
                .GetStackBendAllowance(
                    sectionSpecification.Thickness,
                    sectionSpecification.StackCount,
                    routeGroupRequest.StackMiddleSpace,
                    routeRequest.GetEffectiveBendAllowance());
            var settingsRequiringBendReserve =
                new List<PlanarBendBusbarSetting>();
            foreach (var generatedMemberSetting in generatedMemberSettings ??
                Enumerable.Empty<SectionMemberSetting>())
            {
                var generatedSetting = generatedMemberSetting?.Setting;
                if (generatedSetting?.PointList == null ||
                    generatedSetting.PointList.Count < 2)
                {
                    continue;
                }

                var contactedFixedIndices = (terminalPlacement?.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Where(terminalContact => terminalContact != null &&
                        terminalContact.GeneratedMemberIndex ==
                            generatedMemberSetting.MemberIndex &&
                        terminalContact.FixedMemberIndex >= 0 &&
                        terminalContact.FixedMemberIndex < terminalAssembly.Members.Count)
                    .Select(terminalContact => terminalContact.FixedMemberIndex)
                    .Distinct()
                    .ToList();
                var candidateFixedSettings = contactedFixedIndices.Count > 0
                    ? contactedFixedIndices
                        .Select(fixedMemberIndex => terminalAssembly.Members[
                            fixedMemberIndex]?.FixedBusbar)
                        .Where(fixedSetting => fixedSetting?.PointList != null &&
                            fixedSetting.PointList.Count >= 2)
                        .ToList()
                    : fixedSettings;
                var hasPerpendicularTerminalBoundary = candidateFixedSettings
                    .Where(fixedSetting =>
                        AreTerminalBoundarySegmentsPerpendicular(
                            generatedSetting,
                            useGeneratedStart,
                            fixedSetting))
                    .Any();
                if (!hasPerpendicularTerminalBoundary)
                {
                    continue;
                }

                settingsRequiringBendReserve.Add(generatedSetting);
            }

            EnsureBoundarySegmentMinimumLengthsPreservingOffsets(
                settingsRequiringBendReserve,
                useGeneratedStart,
                effectiveOpposingWidth + stackBendAllowance);
        }

        private static bool AreTerminalBoundarySegmentsPerpendicular(
            PlanarBendBusbarSetting generatedSetting,
            bool useGeneratedStart,
            PlanarBendBusbarSetting fixedSetting)
        {
            if (generatedSetting?.PointList == null ||
                generatedSetting.PointList.Count < 2 ||
                fixedSetting?.PointList == null ||
                fixedSetting.PointList.Count < 2)
            {
                return false;
            }

            var generatedSegmentIndex = useGeneratedStart
                ? 0
                : generatedSetting.PointList.Count - 2;
            var fixedSegmentIndex = useGeneratedStart
                ? fixedSetting.PointList.Count - 2
                : 0;
            var generatedDirection = new Vector3d(
                generatedSetting.PointList[generatedSegmentIndex],
                generatedSetting.PointList[generatedSegmentIndex + 1]);
            var fixedDirection = new Vector3d(
                fixedSetting.PointList[fixedSegmentIndex],
                fixedSetting.PointList[fixedSegmentIndex + 1]);
            return generatedDirection.Norm > GeometryTolerance &&
                fixedDirection.Norm > GeometryTolerance &&
                Math.Abs(generatedDirection.Normalized.Dot(
                    fixedDirection.Normalized)) <= GeometryTolerance;
        }

        private static bool AreBoundarySegmentsPerpendicular(
            PlanarBendBusbarSetting firstSetting,
            bool useFirstStart,
            PlanarBendBusbarSetting secondSetting,
            bool useSecondStart)
        {
            if (firstSetting?.PointList == null ||
                secondSetting?.PointList == null ||
                firstSetting.PointList.Count < 2 ||
                secondSetting.PointList.Count < 2)
            {
                return false;
            }

            var firstSegmentIndex = useFirstStart
                ? 0
                : firstSetting.PointList.Count - 2;
            var secondSegmentIndex = useSecondStart
                ? 0
                : secondSetting.PointList.Count - 2;
            var firstDirection = new Vector3d(
                firstSetting.PointList[firstSegmentIndex],
                firstSetting.PointList[firstSegmentIndex + 1]);
            var secondDirection = new Vector3d(
                secondSetting.PointList[secondSegmentIndex],
                secondSetting.PointList[secondSegmentIndex + 1]);
            return firstDirection.Norm > GeometryTolerance &&
                secondDirection.Norm > GeometryTolerance &&
                Math.Abs(firstDirection.Normalized.Dot(
                    secondDirection.Normalized)) <= GeometryTolerance;
        }

        private static void EnsureBoundarySegmentMinimumLength(
            PlanarBendBusbarSetting planarSetting,
            bool useStartBoundary,
            double requiredSegmentLength)
        {
            // 三点排虽然共用一个折弯点，但内部异规格搭接仍必须先满足
            // 当前搭接面的“对方排宽+折弯余量”；另一端会在相邻边界
            // 的同一轮复核中继续校正，最终再统一检查锐角和实体干涉。
            if (planarSetting?.PointList == null ||
                planarSetting.PointList.Count < 3 ||
                requiredSegmentLength <= GeometryTolerance)
            {
                return;
            }

            var endpointIndex = useStartBoundary
                ? 0
                : planarSetting.PointList.Count - 1;
            var adjacentPointIndex = useStartBoundary
                ? 1
                : planarSetting.PointList.Count - 2;
            var endpointToAdjacentDirection = new Vector3d(
                planarSetting.PointList[endpointIndex],
                planarSetting.PointList[adjacentPointIndex]);
            if (endpointToAdjacentDirection.Norm + GeometryTolerance >=
                    requiredSegmentLength ||
                endpointToAdjacentDirection.Norm <= GeometryTolerance)
            {
                return;
            }

            planarSetting.PointList[adjacentPointIndex] =
                LinkageRouteSolidGeometry.Translate(
                    planarSetting.PointList[endpointIndex],
                    endpointToAdjacentDirection.Normalized,
                    requiredSegmentLength);
        }

        private static void EnsureBoundarySegmentMinimumLengthsPreservingOffsets(
            IEnumerable<PlanarBendBusbarSetting> planarSettings,
            bool useStartBoundary,
            double requiredMinimumSegmentLength)
        {
            var adjustableSettings = (planarSettings ??
                    Enumerable.Empty<PlanarBendBusbarSetting>())
                .Where(planarSetting =>
                    planarSetting?.PointList?.Count >= 3)
                .Distinct()
                .Select(planarSetting => new
                {
                    Setting = planarSetting,
                    CurrentLength = GetBoundarySegmentLength(
                        planarSetting,
                        useStartBoundary)
                })
                .Where(item => item.CurrentLength > GeometryTolerance)
                .ToList();
            if (adjustableSettings.Count == 0)
            {
                return;
            }

            var commonExtensionDistance = Math.Max(
                0d,
                requiredMinimumSegmentLength -
                    adjustableSettings.Min(item => item.CurrentLength));
            if (commonExtensionDistance <= GeometryTolerance)
            {
                return;
            }

            // 叠排 N 的各层由统一母线偏移后，边界直段原本带有避免
            // 相邻层转折相交的长度错位。补足折弯余量时必须对所有层
            // 增加同一个长度，不能把每层都压到同一端面，否则外层的
            // 斜过渡会切入相邻层的直段，并把已经形成的 N 重新退化成
            // 不可制造的交错 I/N 包络。
            foreach (var adjustableSetting in adjustableSettings)
            {
                EnsureBoundarySegmentMinimumLength(
                    adjustableSetting.Setting,
                    useStartBoundary,
                    adjustableSetting.CurrentLength +
                        commonExtensionDistance);
            }
        }

        /// <summary>
        /// 始端搭接排与下一物理排采用水平搭接且排宽不同时，先沿始端
        /// 搭接排自身排宽方向移动整根始端物理排，使较窄排完整落入较宽排。
        /// 固定始端和连接排均不移动；移动区间同时受始端外部搭接范围约束。
        /// </summary>
        private static int EnsureStartInternalHorizontalLapWidthPlacement(
            ProcessingState processingState,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            int centerlinePartCount)
        {
            if (centerlinePartCount < 2 || processingState == null ||
                routeGroupRequest?.StartTerminal == null ||
                !processingState.SettingsBySectionIndex.TryGetValue(
                    0,
                    out var startSectionMemberSettings) ||
                !processingState.SettingsBySectionIndex.TryGetValue(
                    1,
                    out var followingSectionMemberSettings) ||
                startSectionMemberSettings.Count == 0 ||
                followingSectionMemberSettings.Count == 0)
            {
                return 0;
            }

            var referenceStartSetting = startSectionMemberSettings
                .Select(memberSetting => memberSetting?.Setting)
                .FirstOrDefault(setting => setting?.PointList?.Count >= 2 &&
                    setting.WidthVector != null &&
                    setting.WidthVector.Norm > GeometryTolerance);
            if (referenceStartSetting == null)
            {
                return 0;
            }

            var movementDirection = referenceStartSetting.WidthVector.Normalized;
            var minimumTranslation = double.NegativeInfinity;
            var maximumTranslation = double.PositiveInfinity;
            var hasHorizontalWidthMismatch = false;
            var preferredWidthTranslationMagnitude = 0d;
            foreach (var startSectionMemberSetting in
                startSectionMemberSettings)
            {
                var startSetting = startSectionMemberSetting?.Setting;
                var followingSetting = FindNearestBoundarySetting(
                    startSetting,
                    false,
                    followingSectionMemberSettings.Select(memberSetting =>
                        memberSetting.Setting),
                    true);
                if (startSetting == null || followingSetting == null ||
                    Math.Abs(startSetting.BusBarWidth -
                        followingSetting.BusBarWidth) <= GeometryTolerance ||
                    !TryResolveParallelLapWidthTranslationInterval(
                        startSetting,
                        false,
                        followingSetting,
                        true,
                        movementDirection,
                        out var memberMinimumTranslation,
                        out var memberMaximumTranslation))
                {
                    continue;
                }

                hasHorizontalWidthMismatch = true;
                preferredWidthTranslationMagnitude = Math.Max(
                    preferredWidthTranslationMagnitude,
                    Math.Abs(startSetting.BusBarWidth -
                        followingSetting.BusBarWidth));
                minimumTranslation = Math.Max(
                    minimumTranslation,
                    memberMinimumTranslation);
                maximumTranslation = Math.Min(
                    maximumTranslation,
                    memberMaximumTranslation);
            }

            if (!hasHorizontalWidthMismatch ||
                minimumTranslation >
                    maximumTranslation + GeometryTolerance ||
                !TryRestrictStartTerminalWidthTranslationInterval(
                    startSectionMemberSettings,
                    routeGroupRequest.StartTerminal,
                    startTerminalPlacement,
                    movementDirection,
                    ref minimumTranslation,
                    ref maximumTranslation))
            {
                return 0;
            }

            var selectedTranslation = SelectStartInternalWidthTranslation(
                minimumTranslation,
                maximumTranslation,
                preferredWidthTranslationMagnitude);
            if (Math.Abs(selectedTranslation) <= GeometryTolerance)
            {
                return 0;
            }

            var adjustedSettingCount = 0;
            foreach (var startSectionMemberSetting in
                startSectionMemberSettings)
            {
                var startSetting = startSectionMemberSetting?.Setting;
                if (startSetting?.PointList == null)
                {
                    continue;
                }

                startSetting.PointList = startSetting.PointList
                    .Select(sourcePoint => LinkageRouteSolidGeometry.Translate(
                        sourcePoint,
                        movementDirection,
                        selectedTranslation))
                    .ToList();
                adjustedSettingCount++;
            }

            return adjustedSettingCount;
        }

        private static double SelectStartInternalWidthTranslation(
            double minimumTranslation,
            double maximumTranslation,
            double preferredTranslationMagnitude)
        {
            if (minimumTranslation <= GeometryTolerance &&
                maximumTranslation >= -GeometryTolerance)
            {
                return 0d;
            }

            var normalizedPreferredMagnitude = Math.Max(
                0d,
                preferredTranslationMagnitude);
            var preferredPositiveTranslation = normalizedPreferredMagnitude;
            var preferredNegativeTranslation = -normalizedPreferredMagnitude;
            var positiveIsValid = preferredPositiveTranslation >=
                    minimumTranslation - GeometryTolerance &&
                preferredPositiveTranslation <=
                    maximumTranslation + GeometryTolerance;
            var negativeIsValid = preferredNegativeTranslation >=
                    minimumTranslation - GeometryTolerance &&
                preferredNegativeTranslation <=
                    maximumTranslation + GeometryTolerance;
            if (positiveIsValid || negativeIsValid)
            {
                if (!positiveIsValid)
                {
                    return preferredNegativeTranslation;
                }

                if (!negativeIsValid)
                {
                    return preferredPositiveTranslation;
                }

                var intervalCenter =
                    (minimumTranslation + maximumTranslation) / 2d;
                return intervalCenter < 0d
                    ? preferredNegativeTranslation
                    : preferredPositiveTranslation;
            }

            // 固定端或连接排范围比理论排宽差更窄时，仍选取最近的共同
            // 合法边界，保证完整搭接，不因无法走满理论位移而放弃校正。
            return Math.Max(
                minimumTranslation,
                Math.Min(maximumTranslation, 0d));
        }

        private static bool TryRestrictStartTerminalWidthTranslationInterval(
            IEnumerable<SectionMemberSetting> startSectionMemberSettings,
            LinkageTerminalAssembly startTerminalAssembly,
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            Vector3d movementDirection,
            ref double minimumTranslation,
            ref double maximumTranslation)
        {
            if (startTerminalAssembly?.Members?.Count >= 2 &&
                LinkageRouteDoubleStartLapGeometry.TryCreateCollectiveFixedBusbar(
                    startTerminalAssembly,
                    out var collectiveFixedBusbar))
            {
                var collectiveWidthDirection = collectiveFixedBusbar.WidthVector
                    .Normalized;
                var collectiveLengthDirection = new Vector3d(
                    collectiveFixedBusbar.PointList[0],
                    collectiveFixedBusbar.PointList[1]).Normalized;
                var collectiveHalfWidth = collectiveFixedBusbar.BusBarWidth / 2d;
                var collectiveCenterPoint = collectiveFixedBusbar.PointList[0];
                var virtualBridgeSetting = new PlanarBendBusbarSetting
                {
                    PointList = new List<Point3d>
                    {
                        LinkageRouteSolidGeometry.Translate(
                            collectiveCenterPoint,
                            collectiveWidthDirection,
                            -collectiveHalfWidth),
                        LinkageRouteSolidGeometry.Translate(
                            collectiveCenterPoint,
                            collectiveWidthDirection,
                            collectiveHalfWidth)
                    },
                    StartBusbarVector = collectiveFixedBusbar
                        .StartBusbarVector,
                    WidthVector = new Vector3d(
                        collectiveLengthDirection.X,
                        collectiveLengthDirection.Y,
                        collectiveLengthDirection.Z),
                    BusBarWidth = collectiveFixedBusbar.PointList[0]
                        .DistanceTo(collectiveFixedBusbar.PointList[1]),
                    BusBarThickness = collectiveFixedBusbar.BusBarThickness
                };
                foreach (var startSectionMemberSetting in
                    startSectionMemberSettings)
                {
                    if (!TryResolvePerpendicularLapWidthTranslationInterval(
                            startSectionMemberSetting?.Setting,
                            true,
                            virtualBridgeSetting,
                            0,
                            movementDirection,
                            out var localMinimumTranslation,
                            out var localMaximumTranslation))
                    {
                        return false;
                    }

                    minimumTranslation = Math.Max(
                        minimumTranslation,
                        localMinimumTranslation);
                    maximumTranslation = Math.Min(
                        maximumTranslation,
                        localMaximumTranslation);
                }

                return minimumTranslation <=
                    maximumTranslation + GeometryTolerance;
            }

            var terminalMembers = startTerminalAssembly?.Members ??
                new List<LinkageTerminalMember>();
            var fixedSettings = terminalMembers
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(fixedSetting => fixedSetting?.PointList?.Count >= 2)
                .ToList();
            if (fixedSettings.Count == 0)
            {
                return false;
            }

            foreach (var startSectionMemberSetting in startSectionMemberSettings)
            {
                var generatedSetting = startSectionMemberSetting?.Setting;
                var contactedFixedSettings = (startTerminalPlacement?.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Where(terminalContact => terminalContact != null &&
                        terminalContact.GeneratedMemberIndex ==
                            startSectionMemberSetting.MemberIndex &&
                        terminalContact.FixedMemberIndex >= 0 &&
                        terminalContact.FixedMemberIndex < terminalMembers.Count)
                    .Select(terminalContact => terminalMembers[
                        terminalContact.FixedMemberIndex]?.FixedBusbar)
                    .Where(fixedSetting => fixedSetting?.PointList?.Count >= 2)
                    .Distinct()
                    .ToList();
                if (contactedFixedSettings.Count == 0)
                {
                    var nearestFixedSetting = FindNearestBoundarySetting(
                        generatedSetting,
                        true,
                        fixedSettings,
                        false);
                    if (nearestFixedSetting != null)
                    {
                        contactedFixedSettings.Add(nearestFixedSetting);
                    }
                }

                foreach (var fixedSetting in contactedFixedSettings)
                {
                    if (!TryResolveTerminalLapWidthTranslationInterval(
                            generatedSetting,
                            fixedSetting,
                            movementDirection,
                            out var localMinimumTranslation,
                            out var localMaximumTranslation))
                    {
                        return false;
                    }

                    minimumTranslation = Math.Max(
                        minimumTranslation,
                        localMinimumTranslation);
                    maximumTranslation = Math.Min(
                        maximumTranslation,
                        localMaximumTranslation);
                }
            }

            return minimumTranslation <=
                maximumTranslation + GeometryTolerance;
        }

        private static bool TryResolveTerminalLapWidthTranslationInterval(
            PlanarBendBusbarSetting generatedSetting,
            PlanarBendBusbarSetting fixedSetting,
            Vector3d movementDirection,
            out double minimumTranslation,
            out double maximumTranslation)
        {
            minimumTranslation = 0d;
            maximumTranslation = 0d;
            if (generatedSetting?.PointList?.Count < 2 ||
                fixedSetting?.PointList?.Count < 2)
            {
                return false;
            }

            var generatedDirection = new Vector3d(
                generatedSetting.PointList[0],
                generatedSetting.PointList[1]);
            var fixedSegmentIndex = fixedSetting.PointList.Count - 2;
            var fixedDirection = new Vector3d(
                fixedSetting.PointList[fixedSegmentIndex],
                fixedSetting.PointList[fixedSegmentIndex + 1]);
            if (generatedDirection.Norm <= GeometryTolerance ||
                fixedDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            if (generatedDirection.Normalized.Cross(
                    fixedDirection.Normalized).Norm <= GeometryTolerance)
            {
                return TryResolveParallelLapWidthTranslationInterval(
                    generatedSetting,
                    true,
                    fixedSetting,
                    false,
                    movementDirection,
                    out minimumTranslation,
                    out maximumTranslation);
            }

            return TryResolvePerpendicularLapWidthTranslationInterval(
                generatedSetting,
                true,
                fixedSetting,
                fixedSegmentIndex,
                movementDirection,
                out minimumTranslation,
                out maximumTranslation);
        }

        private static bool TryResolveParallelLapWidthTranslationInterval(
            PlanarBendBusbarSetting movableSetting,
            bool useMovableStart,
            PlanarBendBusbarSetting opposingSetting,
            bool useOpposingStart,
            Vector3d movementDirection,
            out double minimumTranslation,
            out double maximumTranslation)
        {
            minimumTranslation = 0d;
            maximumTranslation = 0d;
            if (movableSetting?.PointList?.Count < 2 ||
                opposingSetting?.PointList?.Count < 2 ||
                movementDirection == null ||
                movementDirection.Norm <= GeometryTolerance ||
                movableSetting.BusBarWidth <= GeometryTolerance ||
                opposingSetting.BusBarWidth <= GeometryTolerance)
            {
                return false;
            }

            var movableSegmentIndex = useMovableStart
                ? 0
                : movableSetting.PointList.Count - 2;
            var opposingSegmentIndex = useOpposingStart
                ? 0
                : opposingSetting.PointList.Count - 2;
            var movableSegmentDirection = new Vector3d(
                movableSetting.PointList[movableSegmentIndex],
                movableSetting.PointList[movableSegmentIndex + 1]);
            var opposingSegmentDirection = new Vector3d(
                opposingSetting.PointList[opposingSegmentIndex],
                opposingSetting.PointList[opposingSegmentIndex + 1]);
            var normalizedMovementDirection = movementDirection.Normalized;
            if (movableSegmentDirection.Norm <= GeometryTolerance ||
                opposingSegmentDirection.Norm <= GeometryTolerance ||
                movableSegmentDirection.Normalized.Cross(
                    opposingSegmentDirection.Normalized).Norm >
                        GeometryTolerance ||
                movableSetting.WidthVector == null ||
                movableSetting.WidthVector.Norm <= GeometryTolerance ||
                opposingSetting.WidthVector == null ||
                opposingSetting.WidthVector.Norm <= GeometryTolerance ||
                Math.Abs(movableSetting.WidthVector.Normalized.Dot(
                    normalizedMovementDirection)) < 1d - GeometryTolerance ||
                Math.Abs(opposingSetting.WidthVector.Normalized.Dot(
                    normalizedMovementDirection)) < 1d - GeometryTolerance)
            {
                return false;
            }

            var opposingCenterOffset = new Vector3d(
                    movableSetting.PointList[movableSegmentIndex],
                    opposingSetting.PointList[opposingSegmentIndex])
                .Dot(normalizedMovementDirection);
            var currentWidthContactLimit =
                (movableSetting.BusBarWidth + opposingSetting.BusBarWidth) / 2d;
            if (Math.Abs(opposingCenterOffset) >
                currentWidthContactLimit + GeometryTolerance)
            {
                // 方向平行并不代表位于同一个搭接接触区。候选端段与另一端
                // 固定排可能恰好平行，但宽向投影完全分离；这种远端对象不
                // 得参与满宽落位，否则会把整根端部排跨组平移数百毫米。
                return false;
            }

            var fullOverlapHalfRange = Math.Abs(
                movableSetting.BusBarWidth -
                opposingSetting.BusBarWidth) / 2d;
            minimumTranslation = opposingCenterOffset - fullOverlapHalfRange;
            maximumTranslation = opposingCenterOffset + fullOverlapHalfRange;
            return true;
        }

        private static bool TryResolvePerpendicularLapWidthTranslationInterval(
            PlanarBendBusbarSetting movableSetting,
            bool useMovableStart,
            PlanarBendBusbarSetting opposingSetting,
            int opposingSegmentIndex,
            Vector3d movementDirection,
            out double minimumTranslation,
            out double maximumTranslation)
        {
            minimumTranslation = 0d;
            maximumTranslation = 0d;
            if (movableSetting?.PointList?.Count < 2 ||
                opposingSetting?.PointList?.Count < 2 ||
                opposingSegmentIndex < 0 ||
                opposingSegmentIndex + 1 >= opposingSetting.PointList.Count ||
                movementDirection == null ||
                movementDirection.Norm <= GeometryTolerance ||
                movableSetting.BusBarWidth <= GeometryTolerance)
            {
                return false;
            }

            var movableSegmentIndex = useMovableStart
                ? 0
                : movableSetting.PointList.Count - 2;
            if (!TryGetProjectedPerpendicularIntersection(
                    movableSetting,
                    movableSegmentIndex,
                    opposingSetting,
                    opposingSegmentIndex,
                    out var projectedIntersection))
            {
                return false;
            }

            var opposingStartPoint = opposingSetting.PointList[
                opposingSegmentIndex];
            var opposingEndPoint = opposingSetting.PointList[
                opposingSegmentIndex + 1];
            var opposingDirection = new Vector3d(
                opposingStartPoint,
                opposingEndPoint);
            var opposingLength = opposingDirection.Norm;
            var normalizedMovementDirection = movementDirection.Normalized;
            if (opposingLength + GeometryTolerance <
                    movableSetting.BusBarWidth ||
                opposingLength <= GeometryTolerance)
            {
                return false;
            }

            opposingDirection = opposingDirection.Normalized;
            var movementAlignment = normalizedMovementDirection.Dot(
                opposingDirection);
            if (Math.Abs(movementAlignment) < 1d - GeometryTolerance)
            {
                return false;
            }

            var currentIntersectionCoordinate = new Vector3d(
                    opposingStartPoint,
                    projectedIntersection)
                .Dot(opposingDirection);
            var requiredHalfWidth = movableSetting.BusBarWidth / 2d;
            var firstBoundary =
                (requiredHalfWidth - currentIntersectionCoordinate) /
                movementAlignment;
            var secondBoundary =
                (opposingLength - requiredHalfWidth -
                    currentIntersectionCoordinate) / movementAlignment;
            minimumTranslation = Math.Min(firstBoundary, secondBoundary);
            maximumTranslation = Math.Max(firstBoundary, secondBoundary);
            return true;
        }

        private static int EnsureTerminalPerpendicularLapWidthPlacements(
            ProcessingState processingState,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            LinkageRouteStackTerminalPlacement endTerminalPlacement,
            int centerlinePartCount,
            double searchBusbarWidth,
            bool preservePartialStartPerpendicularLap)
        {
            var adjustedSettingCount = 0;
            // 单一物理段同时受始端和柜顶两个外部锚点约束，不能用一次
            // 端部排宽移动覆盖另一端；这类路径继续保留原中心面，仅修正
            // 两端长度。两段及以上时，各端物理段才具有独立移动自由度。
            if (centerlinePartCount <= 1)
            {
                return adjustedSettingCount;
            }

            if (!preservePartialStartPerpendicularLap &&
                processingState.SettingsBySectionIndex.TryGetValue(
                    0,
                    out var startSectionSettings))
            {
                adjustedSettingCount +=
                    EnsureTerminalPerpendicularLapWidthPlacement(
                        startSectionSettings,
                        routeGroupRequest.StartTerminal,
                        startTerminalPlacement,
                        true,
                        searchBusbarWidth);
            }

            if (processingState.SettingsBySectionIndex.TryGetValue(
                    centerlinePartCount - 1,
                    out var cabinetTopSectionSettings))
            {
                adjustedSettingCount +=
                    EnsureTerminalPerpendicularLapWidthPlacement(
                        cabinetTopSectionSettings,
                        routeGroupRequest.EndTerminal,
                        endTerminalPlacement,
                        false,
                        searchBusbarWidth);
            }

            return adjustedSettingCount;
        }

        /// <summary>
        /// 通用中间排骨架可能以固定端排宽边界作为端点。实体化后的端部
        /// 若与固定排平行搭接，即使真实排宽恰好等于搜索排宽，也必须把
        /// 完整排宽重新落入实际固定排；否则中心线停在边界会向相邻相位
        /// 多伸出半个排宽。这里只沿生成排自身 WidthVector 平移整根端部
        /// 物理排，既有 I/N/M 点数、折弯顺序和厚向层位均保持不变。
        /// </summary>
        private static int EnsureTerminalParallelLapWidthPlacements(
            ProcessingState processingState,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            LinkageRouteStackTerminalPlacement endTerminalPlacement,
            int centerlinePartCount)
        {
            if (processingState?.SettingsBySectionIndex == null ||
                routeGroupRequest == null || centerlinePartCount <= 0)
            {
                return 0;
            }

            var adjustedSettingCount = 0;
            if (processingState.SettingsBySectionIndex.TryGetValue(
                    0,
                    out var startSectionSettings))
            {
                adjustedSettingCount +=
                    EnsureTerminalParallelLapWidthPlacement(
                        startSectionSettings,
                        routeGroupRequest.StartTerminal,
                        startTerminalPlacement,
                        true);
            }

            if (processingState.SettingsBySectionIndex.TryGetValue(
                    centerlinePartCount - 1,
                    out var endSectionSettings))
            {
                adjustedSettingCount +=
                    EnsureTerminalParallelLapWidthPlacement(
                        endSectionSettings,
                        routeGroupRequest.EndTerminal,
                        endTerminalPlacement,
                        false);
            }

            return adjustedSettingCount;
        }

        /// <summary>
        /// 搜索骨架使用通用中间排宽，实体化后端部排可能恢复成更宽规格。
        /// 对平行端部搭接必须按两根实际铜排中较窄者重新保证排长方向的
        /// 满搭接；只向端部外侧延长，不移动折弯内端点，也不缩短既有路径。
        /// </summary>
        private static void EnsureTerminalParallelLapLengths(
            ProcessingState processingState,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            LinkageRouteStackTerminalPlacement endTerminalPlacement,
            int centerlinePartCount)
        {
            if (processingState?.SettingsBySectionIndex == null ||
                routeGroupRequest == null || centerlinePartCount <= 1)
            {
                return;
            }

            if (processingState.SettingsBySectionIndex.TryGetValue(
                    0,
                    out var startSectionSettings))
            {
                EnsureTerminalParallelLapLength(
                    startSectionSettings,
                    routeGroupRequest.StartTerminal,
                    startTerminalPlacement,
                    true);
            }

            if (processingState.SettingsBySectionIndex.TryGetValue(
                    centerlinePartCount - 1,
                    out var endSectionSettings))
            {
                EnsureTerminalParallelLapLength(
                    endSectionSettings,
                    routeGroupRequest.EndTerminal,
                    endTerminalPlacement,
                    false);
            }
        }

        private static void EnsureTerminalParallelLapLength(
            IEnumerable<SectionMemberSetting> generatedMemberSettings,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            bool useGeneratedStart)
        {
            var terminalMembers = terminalAssembly?.Members ??
                new List<LinkageTerminalMember>();
            var allFixedSettings = terminalMembers
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(fixedSetting => fixedSetting?.PointList?.Count >= 2)
                .ToList();
            if (allFixedSettings.Count == 0)
            {
                return;
            }

            foreach (var generatedMemberSetting in generatedMemberSettings ??
                Enumerable.Empty<SectionMemberSetting>())
            {
                var generatedSetting = generatedMemberSetting?.Setting;
                if (generatedSetting?.PointList?.Count < 2)
                {
                    continue;
                }

                var contactedFixedSettings = (terminalPlacement?.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Where(terminalContact => terminalContact != null &&
                        terminalContact.GeneratedMemberIndex ==
                            generatedMemberSetting.MemberIndex &&
                        terminalContact.FixedMemberIndex >= 0 &&
                        terminalContact.FixedMemberIndex < terminalMembers.Count)
                    .Select(terminalContact => terminalMembers[
                        terminalContact.FixedMemberIndex]?.FixedBusbar)
                    .Where(fixedSetting => fixedSetting?.PointList?.Count >= 2)
                    .Distinct()
                    .ToList();
                if (contactedFixedSettings.Count == 0)
                {
                    var nearestFixedSetting = FindNearestBoundarySetting(
                        generatedSetting,
                        useGeneratedStart,
                        allFixedSettings,
                        !useGeneratedStart);
                    if (nearestFixedSetting != null)
                    {
                        contactedFixedSettings.Add(nearestFixedSetting);
                    }
                }

                var endpointIndex = useGeneratedStart
                    ? 0
                    : generatedSetting.PointList.Count - 1;
                var interiorPointIndex = useGeneratedStart
                    ? 1
                    : generatedSetting.PointList.Count - 2;
                var interiorPoint = generatedSetting.PointList[interiorPointIndex];
                var outwardDirectionVector = new Vector3d(
                    interiorPoint,
                    generatedSetting.PointList[endpointIndex]);
                if (outwardDirectionVector.Norm <= GeometryTolerance)
                {
                    continue;
                }

                var outwardDirection = outwardDirectionVector.Normalized;
                var requiredOutwardLength = outwardDirectionVector.Norm;
                foreach (var fixedSetting in contactedFixedSettings)
                {
                    var generatedSegmentIndex = useGeneratedStart
                        ? 0
                        : generatedSetting.PointList.Count - 2;
                    var fixedSegmentIndex = useGeneratedStart
                        ? fixedSetting.PointList.Count - 2
                        : 0;
                    var generatedDirection = new Vector3d(
                        generatedSetting.PointList[generatedSegmentIndex],
                        generatedSetting.PointList[generatedSegmentIndex + 1]);
                    var fixedDirection = new Vector3d(
                        fixedSetting.PointList[fixedSegmentIndex],
                        fixedSetting.PointList[fixedSegmentIndex + 1]);
                    if (generatedDirection.Norm <= GeometryTolerance ||
                        fixedDirection.Norm <= GeometryTolerance ||
                        generatedDirection.Normalized.Cross(
                            fixedDirection.Normalized).Norm > GeometryTolerance)
                    {
                        continue;
                    }

                    var fixedFirstCoordinate = new Vector3d(
                            interiorPoint,
                            fixedSetting.PointList[fixedSegmentIndex])
                        .Dot(outwardDirection);
                    var fixedSecondCoordinate = new Vector3d(
                            interiorPoint,
                            fixedSetting.PointList[fixedSegmentIndex + 1])
                        .Dot(outwardDirection);
                    var fixedMinimumCoordinate = Math.Min(
                        fixedFirstCoordinate,
                        fixedSecondCoordinate);
                    var fixedMaximumCoordinate = Math.Max(
                        fixedFirstCoordinate,
                        fixedSecondCoordinate);
                    var requiredOverlapLength = Math.Min(
                        generatedSetting.BusBarWidth,
                        fixedSetting.BusBarWidth);
                    var requiredEndpointCoordinate = Math.Max(
                            0d,
                            fixedMinimumCoordinate) +
                        requiredOverlapLength;
                    if (fixedMaximumCoordinate + GeometryTolerance <
                        requiredEndpointCoordinate)
                    {
                        continue;
                    }

                    requiredOutwardLength = Math.Max(
                        requiredOutwardLength,
                        requiredEndpointCoordinate);
                }

                if (requiredOutwardLength >
                    outwardDirectionVector.Norm + GeometryTolerance)
                {
                    generatedSetting.PointList[endpointIndex] =
                        LinkageRouteSolidGeometry.Translate(
                            interiorPoint,
                            outwardDirection,
                            requiredOutwardLength);
                }
            }
        }

        private static int EnsureTerminalParallelLapWidthPlacement(
            IEnumerable<SectionMemberSetting> generatedMemberSettings,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            bool useGeneratedStart)
        {
            var terminalMembers = terminalAssembly?.Members ??
                new List<LinkageTerminalMember>();
            var fixedTerminalSettings = terminalMembers
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(fixedSetting => fixedSetting?.PointList?.Count >= 2)
                .ToList();
            if (fixedTerminalSettings.Count == 0)
            {
                return 0;
            }

            var adjustedSettingCount = 0;
            foreach (var generatedMemberSetting in generatedMemberSettings ??
                Enumerable.Empty<SectionMemberSetting>())
            {
                var generatedSetting = generatedMemberSetting?.Setting;
                if (generatedSetting?.PointList?.Count < 2 ||
                    generatedSetting.WidthVector == null ||
                    generatedSetting.WidthVector.Norm <= GeometryTolerance)
                {
                    continue;
                }

                var contactedFixedSettings = (terminalPlacement?.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Where(terminalContact => terminalContact != null &&
                        terminalContact.GeneratedMemberIndex ==
                            generatedMemberSetting.MemberIndex &&
                        terminalContact.FixedMemberIndex >= 0 &&
                        terminalContact.FixedMemberIndex < terminalMembers.Count)
                    .Select(terminalContact => terminalMembers[
                        terminalContact.FixedMemberIndex]?.FixedBusbar)
                    .Where(fixedSetting => fixedSetting?.PointList?.Count >= 2)
                    .Distinct()
                    .ToList();
                if (contactedFixedSettings.Count == 0)
                {
                    var nearestFixedSetting = FindNearestBoundarySetting(
                        generatedSetting,
                        useGeneratedStart,
                        fixedTerminalSettings,
                        !useGeneratedStart);
                    if (nearestFixedSetting != null)
                    {
                        contactedFixedSettings.Add(nearestFixedSetting);
                    }
                }

                var movementDirection = generatedSetting.WidthVector.Normalized;
                var minimumTranslation = double.NegativeInfinity;
                var maximumTranslation = double.PositiveInfinity;
                var hasParallelTerminalContact = false;
                foreach (var contactedFixedSetting in contactedFixedSettings)
                {
                    var generatedSegmentIndex = useGeneratedStart
                        ? 0
                        : generatedSetting.PointList.Count - 2;
                    var fixedSegmentIndex = useGeneratedStart
                        ? contactedFixedSetting.PointList.Count - 2
                        : 0;
                    var generatedSegmentDirection = new Vector3d(
                        generatedSetting.PointList[generatedSegmentIndex],
                        generatedSetting.PointList[generatedSegmentIndex + 1]);
                    var fixedSegmentDirection = new Vector3d(
                        contactedFixedSetting.PointList[fixedSegmentIndex],
                        contactedFixedSetting.PointList[fixedSegmentIndex + 1]);
                    if (generatedSegmentDirection.Norm <= GeometryTolerance ||
                        fixedSegmentDirection.Norm <= GeometryTolerance ||
                        generatedSegmentDirection.Normalized.Cross(
                            fixedSegmentDirection.Normalized).Norm >
                                GeometryTolerance ||
                        !TryResolveParallelLapWidthTranslationInterval(
                            generatedSetting,
                            useGeneratedStart,
                            contactedFixedSetting,
                            !useGeneratedStart,
                            movementDirection,
                            out var memberMinimumTranslation,
                            out var memberMaximumTranslation))
                    {
                        continue;
                    }

                    hasParallelTerminalContact = true;
                    minimumTranslation = Math.Max(
                        minimumTranslation,
                        memberMinimumTranslation);
                    maximumTranslation = Math.Min(
                        maximumTranslation,
                        memberMaximumTranslation);
                }

                if (!hasParallelTerminalContact ||
                    minimumTranslation >
                        maximumTranslation + GeometryTolerance)
                {
                    continue;
                }

                var selectedTranslation = Math.Max(
                    minimumTranslation,
                    Math.Min(maximumTranslation, 0d));
                if (Math.Abs(selectedTranslation) <= GeometryTolerance)
                {
                    continue;
                }

                var isWidthBoundaryCenteredPlacement =
                    contactedFixedSettings.Any(fixedSetting =>
                        Math.Abs(
                            Math.Abs(selectedTranslation) -
                            Math.Min(
                                generatedSetting.BusBarWidth,
                                fixedSetting.BusBarWidth) / 2d) <=
                        GeometryTolerance);
                if (!isWidthBoundaryCenteredPlacement)
                {
                    // 普通叠排成员会因层距产生小幅中心差，这不是搜索骨架
                    // 落在固定排宽边界的情形。只有所需平移恰好等于较窄排
                    // 半宽时，才说明当前中心线位于宽度边界并执行整排对中。
                    continue;
                }

                generatedSetting.PointList = generatedSetting.PointList
                    .Select(sourcePoint => LinkageRouteSolidGeometry.Translate(
                        sourcePoint,
                        movementDirection,
                        selectedTranslation))
                    .ToList();
                adjustedSettingCount++;
            }

            return adjustedSettingCount;
        }

        private static int EnsureTerminalPerpendicularLapWidthPlacement(
            IEnumerable<SectionMemberSetting> generatedMemberSettings,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            bool useGeneratedStart,
            double searchBusbarWidth)
        {
            var terminalMembers = terminalAssembly?.Members ??
                new List<LinkageTerminalMember>();
            var fixedTerminalSettings = terminalMembers
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(fixedSetting => fixedSetting?.PointList != null &&
                    fixedSetting.PointList.Count >= 2)
                .ToList();
            if (fixedTerminalSettings.Count == 0)
            {
                return 0;
            }

            var adjustedSettingCount = 0;
            foreach (var generatedMemberSetting in generatedMemberSettings ??
                Enumerable.Empty<SectionMemberSetting>())
            {
                var generatedSetting = generatedMemberSetting?.Setting;
                if (generatedSetting?.PointList == null ||
                    generatedSetting.PointList.Count < 2)
                {
                    continue;
                }

                var contactedFixedSettings = (terminalPlacement?.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Where(terminalContact => terminalContact != null &&
                        terminalContact.GeneratedMemberIndex ==
                            generatedMemberSetting.MemberIndex &&
                        terminalContact.FixedMemberIndex >= 0 &&
                        terminalContact.FixedMemberIndex < terminalMembers.Count)
                    .Select(terminalContact => terminalMembers[
                        terminalContact.FixedMemberIndex]?.FixedBusbar)
                    .Where(fixedSetting => fixedSetting?.PointList != null &&
                        fixedSetting.PointList.Count >= 2)
                    .Distinct()
                    .ToList();
                if (contactedFixedSettings.Count == 0)
                {
                    var nearestFixedSetting = FindNearestBoundarySetting(
                        generatedSetting,
                        useGeneratedStart,
                        fixedTerminalSettings,
                        !useGeneratedStart);
                    if (nearestFixedSetting != null)
                    {
                        contactedFixedSettings.Add(nearestFixedSetting);
                    }
                }

                if (TryTranslatePerpendicularLapSettingWithinOpposingWidths(
                        generatedSetting,
                        useGeneratedStart,
                        contactedFixedSettings))
                {
                    adjustedSettingCount++;
                }
            }

            return adjustedSettingCount;
        }

        internal static bool
            TryTranslatePerpendicularLapSettingWithinOpposingWidths(
                PlanarBendBusbarSetting movableSetting,
                bool useMovableStart,
                IEnumerable<PlanarBendBusbarSetting> opposingSettings)
        {
            if (movableSetting?.PointList == null ||
                movableSetting.PointList.Count < 2 ||
                movableSetting.WidthVector == null ||
                movableSetting.WidthVector.Norm <= GeometryTolerance ||
                movableSetting.BusBarWidth <= GeometryTolerance)
            {
                return false;
            }

            var movableWidthDirection = movableSetting.WidthVector.Normalized;
            var movableSegmentIndex = useMovableStart
                ? 0
                : movableSetting.PointList.Count - 2;
            var requiredTranslationMinimum = double.NegativeInfinity;
            var requiredTranslationMaximum = double.PositiveInfinity;
            var hasPerpendicularOpposingSetting = false;
            foreach (var opposingSetting in opposingSettings ??
                Enumerable.Empty<PlanarBendBusbarSetting>())
            {
                if (opposingSetting?.PointList == null ||
                    opposingSetting.PointList.Count < 2)
                {
                    continue;
                }

                var opposingSegmentIndex = useMovableStart
                    ? opposingSetting.PointList.Count - 2
                    : 0;
                if (!TryGetProjectedPerpendicularIntersection(
                        movableSetting,
                        movableSegmentIndex,
                        opposingSetting,
                        opposingSegmentIndex,
                        out var projectedIntersection))
                {
                    continue;
                }

                var opposingSegmentStart = opposingSetting.PointList[
                    opposingSegmentIndex];
                var opposingSegmentEnd = opposingSetting.PointList[
                    opposingSegmentIndex + 1];
                var opposingDirection = new Vector3d(
                    opposingSegmentStart,
                    opposingSegmentEnd);
                if (opposingDirection.Norm + GeometryTolerance <
                        movableSetting.BusBarWidth ||
                    opposingDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                opposingDirection = opposingDirection.Normalized;
                var widthDirectionAlignment = movableWidthDirection.Dot(
                    opposingDirection);
                if (Math.Abs(widthDirectionAlignment) <
                    1d - GeometryTolerance)
                {
                    continue;
                }

                var currentIntersectionCoordinate = new Vector3d(
                        opposingSegmentStart,
                        projectedIntersection)
                    .Dot(opposingDirection);
                var requiredHalfWidth = movableSetting.BusBarWidth / 2d;
                var minimumAllowedCoordinate = requiredHalfWidth;
                var maximumAllowedCoordinate =
                    opposingSegmentStart.DistanceTo(opposingSegmentEnd) -
                    requiredHalfWidth;
                var firstTranslationBoundary =
                    (minimumAllowedCoordinate - currentIntersectionCoordinate) /
                    widthDirectionAlignment;
                var secondTranslationBoundary =
                    (maximumAllowedCoordinate - currentIntersectionCoordinate) /
                    widthDirectionAlignment;
                requiredTranslationMinimum = Math.Max(
                    requiredTranslationMinimum,
                    Math.Min(
                        firstTranslationBoundary,
                        secondTranslationBoundary));
                requiredTranslationMaximum = Math.Min(
                    requiredTranslationMaximum,
                    Math.Max(
                        firstTranslationBoundary,
                        secondTranslationBoundary));
                hasPerpendicularOpposingSetting = true;
            }

            if (!hasPerpendicularOpposingSetting ||
                requiredTranslationMinimum >
                    requiredTranslationMaximum + GeometryTolerance)
            {
                return false;
            }

            var selectedTranslationDistance = 0d;
            if (selectedTranslationDistance < requiredTranslationMinimum)
            {
                selectedTranslationDistance = requiredTranslationMinimum;
            }
            else if (selectedTranslationDistance > requiredTranslationMaximum)
            {
                selectedTranslationDistance = requiredTranslationMaximum;
            }

            if (Math.Abs(selectedTranslationDistance) <= GeometryTolerance)
            {
                return false;
            }

            movableSetting.PointList = movableSetting.PointList
                .Select(sourcePoint => LinkageRouteSolidGeometry.Translate(
                    sourcePoint,
                    movableWidthDirection,
                    selectedTranslationDistance))
                .ToList();
            return true;
        }

        private static void EnsureTerminalPerpendicularLapWidths(
            ProcessingState processingState,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            LinkageRouteStackTerminalPlacement endTerminalPlacement,
            int centerlinePartCount,
            bool preservePartialStartPerpendicularLap)
        {
            if (!preservePartialStartPerpendicularLap &&
                processingState.SettingsBySectionIndex.TryGetValue(
                    0,
                    out var startSectionSettings))
            {
                EnsureTerminalPerpendicularLapWidth(
                    startSectionSettings,
                    routeGroupRequest.StartTerminal,
                    startTerminalPlacement,
                    true);
            }

            if (processingState.SettingsBySectionIndex.TryGetValue(
                    centerlinePartCount - 1,
                    out var cabinetTopSectionSettings))
            {
                EnsureTerminalPerpendicularLapWidth(
                    cabinetTopSectionSettings,
                    routeGroupRequest.EndTerminal,
                    endTerminalPlacement,
                    false);
            }
        }

        private static void EnsureTerminalPerpendicularLapWidth(
            IEnumerable<SectionMemberSetting> generatedMemberSettings,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            bool useGeneratedStart)
        {
            var terminalMembers = terminalAssembly?.Members ??
                new List<LinkageTerminalMember>();
            var fixedTerminalSettings = terminalMembers
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(fixedSetting => fixedSetting?.PointList != null &&
                    fixedSetting.PointList.Count >= 2)
                .ToList();
            if (fixedTerminalSettings.Count == 0)
            {
                return;
            }

            var generatedMemberSettingList = (generatedMemberSettings ??
                Enumerable.Empty<SectionMemberSetting>()).ToList();
            foreach (var generatedMemberSetting in generatedMemberSettingList)
            {
                var generatedSetting = generatedMemberSetting?.Setting;
                if (generatedSetting?.PointList == null ||
                    generatedSetting.PointList.Count < 2)
                {
                    continue;
                }

                // 叠排数量或排厚变化后，几何最近的一根固定排不一定就是
                // 普通方案实际选择的搭接面。优先沿层位求解器保留下来的
                // GeneratedMemberIndex→FixedMemberIndex 接触映射取固定排，
                // 只有旧结果未携带接触信息时才退回几何最近值。
                // Contacts 的 FixedMemberIndex 始终对应 terminalAssembly.Members 原始索引；
                // 不能先过滤无效固定排后再按索引读取，否则会把不同规格固定排错位。
                var contactedFixedTerminalSettings = (terminalPlacement
                        ?.Contacts ?? new List<LinkageRouteStackTerminalContact>())
                    .Where(terminalContact => terminalContact != null &&
                        terminalContact.GeneratedMemberIndex ==
                            generatedMemberSetting.MemberIndex &&
                        terminalContact.FixedMemberIndex >= 0 &&
                        terminalContact.FixedMemberIndex <
                            terminalMembers.Count)
                    .Select(terminalContact => terminalMembers[
                        terminalContact.FixedMemberIndex]?.FixedBusbar)
                    .Where(fixedSetting => fixedSetting?.PointList != null &&
                        fixedSetting.PointList.Count >= 2)
                    .Distinct()
                    .ToList();
                var fixedTerminalSettingsForMember =
                    contactedFixedTerminalSettings.Count > 0
                        ? contactedFixedTerminalSettings
                        : fixedTerminalSettings;
                var generatedEndpointIndex = useGeneratedStart
                    ? 0
                    : generatedSetting.PointList.Count - 1;
                var generatedInteriorPointIndex = useGeneratedStart
                    ? 1
                    : generatedSetting.PointList.Count - 2;
                var generatedInteriorPoint = generatedSetting.PointList[
                    generatedInteriorPointIndex];
                var originalOutwardDirection = new Vector3d(
                    generatedInteriorPoint,
                    generatedSetting.PointList[generatedEndpointIndex]);
                if (originalOutwardDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                originalOutwardDirection = originalOutwardDirection.Normalized;
                Point3d farthestRequiredEndpoint = null;
                var farthestRequiredOutwardProjection =
                    double.NegativeInfinity;
                foreach (var contactedFixedTerminalSetting in
                    fixedTerminalSettingsForMember)
                {
                    if (!TryGetPerpendicularBoundaryEndpoint(
                            generatedSetting,
                            useGeneratedStart,
                            contactedFixedTerminalSetting,
                            contactedFixedTerminalSetting.BusBarWidth,
                            out var requiredEndpoint))
                    {
                        continue;
                    }

                    var requiredOutwardProjection = new Vector3d(
                            generatedInteriorPoint,
                            requiredEndpoint)
                        .Dot(originalOutwardDirection);
                    if (requiredOutwardProjection <=
                        farthestRequiredOutwardProjection)
                    {
                        continue;
                    }

                    farthestRequiredOutwardProjection =
                        requiredOutwardProjection;
                    farthestRequiredEndpoint = requiredEndpoint;
                }

                if (farthestRequiredEndpoint != null)
                {
                    generatedSetting.PointList[generatedEndpointIndex] =
                        farthestRequiredEndpoint;
                }
            }

        }

        /// <summary>
        /// 同一端部先以层位求解器确认的直接搭接成员为主排；主排完成与
        /// 始端、柜顶或连接排的实际搭接后，其余叠排成员的端头统一落在
        /// 主排端头平面。这里只调整端部直段端点，垫块必须在此后按最终
        /// 端面生成，不能用各成员原先参差的端头分别补块。
        /// </summary>
        internal static bool AlignFinalTerminalLapEndpointsToPrimaryMembers(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            if (conversionResult?.StackMembers == null ||
                conversionResult.CenterlineParts == null ||
                conversionResult.CenterlineParts.Count == 0 ||
                routeRequest == null || routeGroupRequest == null)
            {
                return false;
            }

            var physicalSectionCount =
                conversionResult.CenterlineParts.Count;
            var firstPhysicalSectionIndex =
                conversionResult.CenterlineParts[0]?.PhysicalSectionIndex >= 0
                    ? conversionResult.CenterlineParts[0].PhysicalSectionIndex
                    : 0;
            var lastPhysicalSectionIndex =
                conversionResult.CenterlineParts[physicalSectionCount - 1]
                    ?.PhysicalSectionIndex >= 0
                    ? conversionResult.CenterlineParts[physicalSectionCount - 1]
                        .PhysicalSectionIndex
                    : physicalSectionCount - 1;
            var startMembers = conversionResult.StackMembers
                .Where(member => member?.PlanarParts?.Count > 0 &&
                    member.PlanarParts[0] != null &&
                    (member.PlanarParts[0].PhysicalSectionIndex ==
                        firstPhysicalSectionIndex ||
                     (member.PlanarParts[0].PhysicalSectionIndex < 0 &&
                      member.PlanarParts.Count == physicalSectionCount)))
                .Select(member => new SectionMemberSetting
                {
                    MemberIndex = member.MemberIndex,
                    Setting = member.PlanarParts.First()
                })
                .ToList();
            var endMembers = conversionResult.StackMembers
                .Where(member => member?.PlanarParts?.Count > 0 &&
                    member.PlanarParts[member.PlanarParts.Count - 1] != null &&
                    (member.PlanarParts[member.PlanarParts.Count - 1]
                            .PhysicalSectionIndex ==
                        lastPhysicalSectionIndex ||
                     (member.PlanarParts[member.PlanarParts.Count - 1]
                            .PhysicalSectionIndex < 0 &&
                      member.PlanarParts.Count == physicalSectionCount)))
                .Select(member => new SectionMemberSetting
                {
                    MemberIndex = member.MemberIndex,
                    Setting = member.PlanarParts.Last()
                })
                .ToList();
            var startAdjusted = AlignFinalTerminalLapEndpointsToPrimaryMember(
                startMembers, routeGroupRequest.StartTerminal,
                conversionResult.StartStackPlacement, true,
                conversionResult.Accessories
                    .Where(accessory => accessory?.Kind ==
                        LinkageRouteAccessoryKind.DoubleStartHorizontalBridge));
            var endAdjusted = AlignFinalTerminalLapEndpointsToPrimaryMember(
                endMembers, routeGroupRequest.EndTerminal,
                conversionResult.EndStackPlacement, false, null);
            return startAdjusted || endAdjusted;
        }

        private static bool AlignFinalTerminalLapEndpointsToPrimaryMember(
            IList<SectionMemberSetting> generatedMembers,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement originalPlacement,
            bool useGeneratedStart,
            IEnumerable<LinkageRouteAccessory> bridgeAccessories)
        {
            if (generatedMembers == null || generatedMembers.Count < 2 ||
                terminalAssembly?.Members == null)
            {
                return false;
            }

            var currentBridgeAccessories = (bridgeAccessories ??
                Enumerable.Empty<LinkageRouteAccessory>())
                .Where(accessory => accessory?.Setting != null)
                .ToList();
            var bridgePrimaryMemberIndices = currentBridgeAccessories
                .Select(accessory => accessory.RelatedGeneratedMemberIndex)
                .Where(index => generatedMembers.Any(member =>
                    member.MemberIndex == index))
                .Distinct()
                .ToList();
            var contactSettings = terminalAssembly.Members
                .Select(member => member?.FixedBusbar)
                .Concat(currentBridgeAccessories.Select(accessory =>
                    accessory.Setting))
                .Where(setting => setting != null)
                .ToList();
            var directlyContactedMemberIndices = new List<int>();
            if (bridgePrimaryMemberIndices.Count > 0)
            {
                // 桥排关联的生成成员是本接口的主排；旁侧成员即使和桥排
                // 边缘仍有部分接触，也不得据此反选为端头对齐锚点。
                directlyContactedMemberIndices.AddRange(
                    bridgePrimaryMemberIndices);
            }
            foreach (var generatedMember in generatedMembers)
            {
                if (bridgePrimaryMemberIndices.Count > 0)
                {
                    continue;
                }
                var generatedSetting = generatedMember.Setting;
                if (generatedSetting?.PointList?.Count < 2)
                {
                    continue;
                }

                if (contactSettings.Any(contactSetting =>
                    HasActualTerminalLapContact(generatedSetting,
                        useGeneratedStart, contactSetting)))
                {
                    directlyContactedMemberIndices.Add(
                        generatedMember.MemberIndex);
                }
            }

            if (directlyContactedMemberIndices.Count == 0)
            {
                directlyContactedMemberIndices.AddRange(
                    (originalPlacement?.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Where(contact => contact != null)
                    .Select(contact => contact.GeneratedMemberIndex)
                    .Distinct());
            }
            if (directlyContactedMemberIndices.Count == 0)
            {
                return false;
            }

            var finalPlacement = new LinkageRouteStackTerminalPlacement();
            foreach (var memberIndex in directlyContactedMemberIndices
                .OrderBy(index => index))
            {
                finalPlacement.Contacts.Add(new LinkageRouteStackTerminalContact
                {
                    GeneratedMemberIndex = memberIndex
                });
            }

            var endpointsBeforeAlignment = generatedMembers
                .Select(member => member.Setting?.PointList?.Count > 0
                    ? (useGeneratedStart
                        ? member.Setting.PointList[0]
                        : member.Setting.PointList.Last())
                    : null)
                .ToList();
            AlignTerminalLapEndpointsToPrimaryMember(generatedMembers,
                finalPlacement, useGeneratedStart);
            for (var memberIndex = 0;
                memberIndex < generatedMembers.Count;
                memberIndex++)
            {
                var pointList = generatedMembers[memberIndex].Setting?.PointList;
                if (pointList == null || pointList.Count == 0)
                {
                    continue;
                }
                var currentEndpoint = useGeneratedStart
                    ? pointList[0]
                    : pointList[pointList.Count - 1];
                if (endpointsBeforeAlignment[memberIndex] != null &&
                    new Vector3d(endpointsBeforeAlignment[memberIndex],
                        currentEndpoint).Norm > GeometryTolerance)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasActualTerminalLapContact(
            PlanarBendBusbarSetting generatedSetting,
            bool useGeneratedStart,
            PlanarBendBusbarSetting fixedSetting)
        {
            if (generatedSetting?.PointList?.Count < 2 ||
                fixedSetting?.PointList?.Count < 2)
            {
                return false;
            }
            var requiredWidth = Math.Min(generatedSetting.BusBarWidth,
                fixedSetting.BusBarWidth);
            return LinkageRouteTerminalLapRegionValidator
                    .TryCreateActualLapFootprint(generatedSetting,
                        useGeneratedStart, fixedSetting, true,
                        requiredWidth, out _,
                        tolerance: LinkageRouteGeometryTolerances
                            .FinalAcceptanceComparison) ||
                LinkageRouteTerminalLapRegionValidator
                    .TryCreateActualLapFootprint(generatedSetting,
                        useGeneratedStart, fixedSetting, false,
                        requiredWidth, out _,
                        tolerance: LinkageRouteGeometryTolerances
                            .FinalAcceptanceComparison);
        }

        private static void AlignTerminalLapEndpointsToPrimaryMember(
            IEnumerable<SectionMemberSetting> generatedMemberSettingList,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            bool useGeneratedStart)
        {
            var validMemberSettings = (generatedMemberSettingList ??
                    Enumerable.Empty<SectionMemberSetting>())
                .Where(generatedMemberSetting =>
                    generatedMemberSetting?.Setting?.PointList != null &&
                    generatedMemberSetting.Setting.PointList.Count >= 2)
                .ToList();
            if (validMemberSettings.Count == 0)
            {
                return;
            }

            var directContactMemberIndices = (terminalPlacement?.Contacts ??
                    new List<LinkageRouteStackTerminalContact>())
                .Where(terminalContact => terminalContact != null)
                .Select(terminalContact => terminalContact.GeneratedMemberIndex)
                .Distinct()
                .ToList();
            var primaryMemberSetting = directContactMemberIndices
                .Select(directContactMemberIndex => validMemberSettings
                    .FirstOrDefault(generatedMemberSetting =>
                        generatedMemberSetting.MemberIndex ==
                            directContactMemberIndex))
                .FirstOrDefault(generatedMemberSetting =>
                    generatedMemberSetting != null) ??
                validMemberSettings.OrderBy(generatedMemberSetting =>
                    generatedMemberSetting.MemberIndex).First();
            var primarySetting = primaryMemberSetting.Setting;
            var primaryEndpointIndex = useGeneratedStart
                ? 0
                : primarySetting.PointList.Count - 1;
            var primaryInteriorPointIndex = useGeneratedStart
                ? 1
                : primarySetting.PointList.Count - 2;
            var primaryOutwardDirection = new Vector3d(
                primarySetting.PointList[primaryInteriorPointIndex],
                primarySetting.PointList[primaryEndpointIndex]);
            if (primaryOutwardDirection.Norm <= GeometryTolerance)
            {
                return;
            }

            var normalizedPrimaryOutwardDirection = primaryOutwardDirection
                .Normalized;
            var primaryEndpointPlaneCoordinate = DotPoint(
                primarySetting.PointList[primaryEndpointIndex],
                normalizedPrimaryOutwardDirection);
            foreach (var generatedMemberSetting in validMemberSettings)
            {
                var generatedSetting = generatedMemberSetting.Setting;
                var endpointIndex = useGeneratedStart
                    ? 0
                    : generatedSetting.PointList.Count - 1;
                var interiorPointIndex = useGeneratedStart
                    ? 1
                    : generatedSetting.PointList.Count - 2;
                var interiorPoint = generatedSetting.PointList[
                    interiorPointIndex];
                var memberOutwardDirection = new Vector3d(
                    interiorPoint,
                    generatedSetting.PointList[endpointIndex]);
                if (memberOutwardDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                memberOutwardDirection = memberOutwardDirection.Normalized;
                var directionAlignment = memberOutwardDirection.Dot(
                    normalizedPrimaryOutwardDirection);
                if (directionAlignment < 1d - GeometryTolerance)
                {
                    continue;
                }

                var requiredLapLength =
                    (primaryEndpointPlaneCoordinate -
                     DotPoint(
                         interiorPoint,
                         normalizedPrimaryOutwardDirection)) /
                    directionAlignment;
                if (requiredLapLength <= GeometryTolerance)
                {
                    continue;
                }

                generatedSetting.PointList[endpointIndex] =
                    LinkageRouteSolidGeometry.Translate(
                        interiorPoint,
                        memberOutwardDirection,
                        requiredLapLength);
            }
        }

        /// <summary>
        /// 始端搭接端头收口。同组叠排成员用"统一首段长度"对齐时，垂直搭接
        /// 成员的折弯内端点相差一个柜顶叠排层位，取最长首段会把层位靠外的
        /// 成员端头推到被搭接固定排端面之外（端头比固定排端面多伸出一个或
        /// 多个生成排排厚）。这里沿首段方向把超出部分回收到固定排端面：
        /// 平行搭接收到排长端面，垂直搭接收到排宽端面；只回收、不延长，
        /// 也不改变横向位置，属于最终输出几何修正，不参与候选搜索和排序。
        /// </summary>
        private static int ClampStartPerpendicularLapEndpointsToFixedBoundary(
            IList<SectionMemberSetting> generatedMemberSettings,
            LinkageTerminalAssembly startTerminalAssembly,
            LinkageRouteStackTerminalPlacement startTerminalPlacement,
            ICollection<string> diagnostics)
        {
            if (generatedMemberSettings == null ||
                generatedMemberSettings.Count == 0 ||
                startTerminalAssembly?.Members == null ||
                startTerminalAssembly.Members.Count == 0)
            {
                return 0;
            }

            var fixedTerminalSettings = startTerminalAssembly.Members
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(fixedSetting => fixedSetting?.PointList != null &&
                    fixedSetting.PointList.Count >= 2)
                .ToList();
            if (fixedTerminalSettings.Count == 0)
            {
                return 0;
            }

            var clampedMemberCount = 0;
            foreach (var generatedMemberSetting in generatedMemberSettings)
            {
                var generatedSetting = generatedMemberSetting?.Setting;
                if (generatedSetting?.PointList == null ||
                    generatedSetting.PointList.Count < 2)
                {
                    continue;
                }

                var endpointPoint = generatedSetting.PointList[0];
                var interiorPoint = generatedSetting.PointList[1];
                var outwardDirection = new Vector3d(interiorPoint, endpointPoint);
                if (outwardDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                outwardDirection = outwardDirection.Normalized;
                var contactedFixedSettings = (startTerminalPlacement?.Contacts ??
                        new List<LinkageRouteStackTerminalContact>())
                    .Where(terminalContact => terminalContact != null &&
                        terminalContact.GeneratedMemberIndex ==
                            generatedMemberSetting.MemberIndex &&
                        terminalContact.FixedMemberIndex >= 0 &&
                        terminalContact.FixedMemberIndex <
                            startTerminalAssembly.Members.Count)
                    .Select(terminalContact => startTerminalAssembly.Members[
                        terminalContact.FixedMemberIndex]?.FixedBusbar)
                    .Where(fixedSetting => fixedSetting?.PointList != null &&
                        fixedSetting.PointList.Count >= 2)
                    .Distinct()
                    .ToList();
                var fixedSettingsForMember = contactedFixedSettings.Count > 0
                    ? contactedFixedSettings
                    : fixedTerminalSettings;

                // 端头的有效边界：
                //  - 首段与固定排排长平行（平行搭接）→ 边界取固定排排长端面；
                //  - 首段与固定排排长垂直（垂直搭接）→ 端头是沿固定排排宽方向
                //    伸出的，边界取固定排排宽端面（中心线 + 排宽/2）；
                //  - 固定排本身是多向折弯排或斜向关系时边界不唯一，不做收口。
                var boundaryCoordinate = double.NegativeInfinity;
                foreach (var fixedSetting in fixedSettingsForMember)
                {
                    Vector3d fixedLongitudinalDirection = null;
                    for (var pointIndex = 0;
                        pointIndex + 1 < fixedSetting.PointList.Count;
                        pointIndex++)
                    {
                        var fixedSegment = new Vector3d(
                            fixedSetting.PointList[pointIndex],
                            fixedSetting.PointList[pointIndex + 1]);
                        if (fixedSegment.Norm <= GeometryTolerance)
                        {
                            continue;
                        }

                        var normalizedFixedSegment = fixedSegment.Normalized;
                        if (fixedLongitudinalDirection == null)
                        {
                            fixedLongitudinalDirection = normalizedFixedSegment;
                            continue;
                        }

                        if (Math.Abs(fixedLongitudinalDirection.Dot(
                                normalizedFixedSegment)) <
                            1d - GeometryTolerance)
                        {
                            fixedLongitudinalDirection = null;
                            break;
                        }
                    }

                    if (fixedLongitudinalDirection == null)
                    {
                        continue;
                    }

                    var alignment = Math.Abs(
                        fixedLongitudinalDirection.Dot(outwardDirection));
                    if (alignment >= 1d - GeometryTolerance)
                    {
                        boundaryCoordinate = Math.Max(
                            boundaryCoordinate,
                            fixedSetting.PointList.Max(fixedPoint =>
                                DotPoint(fixedPoint, outwardDirection)));
                    }
                    else if (alignment <= GeometryTolerance)
                    {
                        boundaryCoordinate = Math.Max(
                            boundaryCoordinate,
                            DotPoint(
                                fixedSetting.PointList[0],
                                outwardDirection) +
                                fixedSetting.BusBarWidth / 2d);
                    }
                }

                if (double.IsNegativeInfinity(boundaryCoordinate))
                {
                    continue;
                }

                var currentEndpointCoordinate = DotPoint(
                    endpointPoint,
                    outwardDirection);
                var overshoot = currentEndpointCoordinate - boundaryCoordinate;
                if (overshoot <= GeometryTolerance)
                {
                    continue;
                }

                generatedSetting.PointList[0] =
                    LinkageRouteSolidGeometry.Translate(
                        endpointPoint,
                        outwardDirection,
                        -overshoot);
                clampedMemberCount++;
                if (diagnostics != null)
                {
                    diagnostics.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0} 层始端搭接排端头超出被搭接固定排端面 {1:F3} mm，已沿首段方向回收到固定排端面；只修正输出几何，不改变搜索规格。",
                        generatedMemberSetting.MemberIndex + 1,
                        overshoot));
                }
            }

            return clampedMemberCount;
        }

        private static PlanarBendBusbarSetting FindNearestBoundarySetting(
            PlanarBendBusbarSetting sourceSetting,
            bool useSourceStart,
            IEnumerable<PlanarBendBusbarSetting> targetSettings,
            bool useTargetStart)
        {
            if (sourceSetting?.PointList == null ||
                sourceSetting.PointList.Count < 2)
            {
                return null;
            }

            var sourcePoint = useSourceStart
                ? sourceSetting.PointList[0]
                : sourceSetting.PointList[sourceSetting.PointList.Count - 1];
            if (!TryGetLocalThicknessDirection(
                    sourceSetting,
                    useSourceStart ? 0 : sourceSetting.PointList.Count - 2,
                    out var thicknessDirection))
            {
                return null;
            }

            return (targetSettings ?? Enumerable.Empty<PlanarBendBusbarSetting>())
                .Where(targetSetting => targetSetting?.PointList != null &&
                    targetSetting.PointList.Count >= 2)
                .OrderBy(targetSetting => Math.Abs(
                    new Vector3d(
                        sourcePoint,
                        useTargetStart
                            ? targetSetting.PointList[0]
                            : targetSetting.PointList[
                                targetSetting.PointList.Count - 1])
                    .Dot(thicknessDirection.Normalized)))
                .FirstOrDefault();
        }

        internal static bool TryExtendPerpendicularBoundaryEndpoint(
            PlanarBendBusbarSetting targetSetting,
            bool useTargetStart,
            PlanarBendBusbarSetting opposingSetting,
            double opposingBusbarWidth)
        {
            if (!TryGetPerpendicularBoundaryEndpoint(
                    targetSetting,
                    useTargetStart,
                    opposingSetting,
                    opposingBusbarWidth,
                    out var requiredBoundaryEndpoint))
            {
                return false;
            }

            var endpointIndex = useTargetStart
                ? 0
                : targetSetting.PointList.Count - 1;
            targetSetting.PointList[endpointIndex] = requiredBoundaryEndpoint;
            return true;
        }

        private static bool TryGetPerpendicularBoundaryEndpoint(
            PlanarBendBusbarSetting targetSetting,
            bool useTargetStart,
            PlanarBendBusbarSetting opposingSetting,
            double opposingBusbarWidth,
            out Point3d requiredBoundaryEndpoint)
        {
            return TryGetPerpendicularBoundaryEndpoint(targetSetting, useTargetStart,
                opposingSetting, !useTargetStart, opposingBusbarWidth,
                out requiredBoundaryEndpoint);
        }

        internal static bool TryGetPerpendicularBoundaryEndpoint(
            PlanarBendBusbarSetting targetSetting,
            bool useTargetStart,
            PlanarBendBusbarSetting opposingSetting,
            bool useOpposingStart,
            double opposingBusbarWidth,
            out Point3d requiredBoundaryEndpoint)
        {
            requiredBoundaryEndpoint = null;
            var targetSegmentIndex = useTargetStart
                ? 0
                : targetSetting.PointList.Count - 2;
            var opposingSegmentIndex = useOpposingStart
                ? 0
                : opposingSetting.PointList.Count - 2;
            if (!TryGetProjectedPerpendicularIntersection(
                    targetSetting,
                    targetSegmentIndex,
                    opposingSetting,
                    opposingSegmentIndex,
                    out var projectedIntersection))
            {
                return false;
            }

            var interiorPointIndex = useTargetStart
                ? 1
                : targetSetting.PointList.Count - 2;
            var intersectionToInteriorDirection = new Vector3d(
                projectedIntersection,
                targetSetting.PointList[interiorPointIndex]);
            if (intersectionToInteriorDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            requiredBoundaryEndpoint =
                LinkageRouteSolidGeometry.Translate(
                    projectedIntersection,
                    intersectionToInteriorDirection.Normalized,
                    -opposingBusbarWidth / 2d);
            return true;
        }

        private static bool TryGetProjectedPerpendicularIntersection(
            PlanarBendBusbarSetting firstSetting,
            int firstSegmentIndex,
            PlanarBendBusbarSetting secondSetting,
            int secondSegmentIndex,
            out Point3d projectedIntersection)
        {
            projectedIntersection = null;
            if (!TryGetLocalThicknessDirection(
                    firstSetting,
                    firstSegmentIndex,
                    out var firstThicknessDirection))
            {
                return false;
            }

            var firstStart = firstSetting.PointList[firstSegmentIndex];
            var firstEnd = firstSetting.PointList[firstSegmentIndex + 1];
            var secondStart = ProjectToPlane(
                secondSetting.PointList[secondSegmentIndex],
                firstStart,
                firstThicknessDirection.Normalized);
            var secondEnd = ProjectToPlane(
                secondSetting.PointList[secondSegmentIndex + 1],
                firstStart,
                firstThicknessDirection.Normalized);
            var firstVector = new Vector3d(firstStart, firstEnd);
            var secondVector = new Vector3d(secondStart, secondEnd);
            if (firstVector.Norm <= GeometryTolerance ||
                secondVector.Norm <= GeometryTolerance ||
                Math.Abs(firstVector.Normalized.Dot(secondVector.Normalized)) >
                    GeometryTolerance)
            {
                return false;
            }

            var startDifference = new Vector3d(firstStart, secondStart);
            var denominator = firstThicknessDirection.Normalized.Dot(
                firstVector.Cross(secondVector));
            if (Math.Abs(denominator) <= GeometryTolerance)
            {
                return false;
            }

            var firstParameter = firstThicknessDirection.Normalized.Dot(
                startDifference.Cross(secondVector)) / denominator;
            projectedIntersection = LinkageRouteSolidGeometry.Translate(
                firstStart,
                firstVector,
                firstParameter);
            return true;
        }

        private static double[] CreateCenteredCoordinates(
            int stackCount,
            double memberPitch)
        {
            return Enumerable.Range(0, stackCount)
                .Select(memberIndex =>
                    (memberIndex - (stackCount - 1) / 2d) * memberPitch)
                .ToArray();
        }

        private static int ResolveOrdinaryBoundarySideSign(
            IEnumerable<LinkageStackedRouteMember> ordinaryMembers,
            int boundaryIndex,
            Vector3d normalizedThicknessDirection)
        {
            foreach (var ordinaryMember in ordinaryMembers)
            {
                var precedingSetting = ordinaryMember.PlanarParts[boundaryIndex];
                var followingSetting = ordinaryMember.PlanarParts[
                    boundaryIndex + 1];
                var signedDistance = new Vector3d(
                        precedingSetting.PointList[
                            precedingSetting.PointList.Count - 1],
                        followingSetting.PointList[0])
                    .Dot(normalizedThicknessDirection);
                if (Math.Abs(signedDistance) > GeometryTolerance)
                {
                    return signedDistance > 0d ? 1 : -1;
                }
            }

            return 1;
        }

        private static bool TryGetPartOffsetOrientationSigns(
            IReadOnlyList<PlanarBendBusbarSetting> planarParts,
            out IReadOnlyList<double> partOffsetOrientationSigns,
            out string failureReason)
        {
            var resolvedOrientationSigns = new List<double>();
            partOffsetOrientationSigns = resolvedOrientationSigns;
            failureReason = null;
            if (planarParts == null || planarParts.Count == 0)
            {
                failureReason = "分段排规格后处理缺少可传播层序的平面排。";
                return false;
            }

            resolvedOrientationSigns.Add(1d);
            for (var partIndex = 1; partIndex < planarParts.Count; partIndex++)
            {
                var precedingPart = planarParts[partIndex - 1];
                var followingPart = planarParts[partIndex];
                if (!TryGetLocalThicknessDirection(
                        precedingPart,
                        precedingPart.PointList.Count - 2,
                        out var precedingBoundaryThicknessDirection) ||
                    !TryGetLocalThicknessDirection(
                        followingPart,
                        0,
                        out var followingBoundaryThicknessDirection))
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "分段排规格后处理无法建立第 {0} 个搭接面的层序方向。",
                        partIndex);
                    return false;
                }

                var boundaryThicknessAlignment =
                    precedingBoundaryThicknessDirection.Normalized.Dot(
                        followingBoundaryThicknessDirection.Normalized);
                if (Math.Abs(boundaryThicknessAlignment) <
                    1d - GeometryTolerance)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "分段排规格后处理第 {0} 个搭接面两侧排厚方向不平行。",
                        partIndex);
                    return false;
                }

                resolvedOrientationSigns.Add(
                    resolvedOrientationSigns[partIndex - 1] *
                    (boundaryThicknessAlignment < 0d ? -1d : 1d));
            }

            return true;
        }

        private static double ResolvePrecedingThickness(
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<SectionSpecification> sectionSpecifications,
            int partIndex)
        {
            return partIndex > 0
                ? sectionSpecifications[partIndex - 1].Thickness
                : ResolveMaximumTerminalThickness(routeGroupRequest.StartTerminal);
        }

        private static double ResolveFollowingThickness(
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<SectionSpecification> sectionSpecifications,
            int partIndex)
        {
            return partIndex + 1 < sectionSpecifications.Count
                ? sectionSpecifications[partIndex + 1].Thickness
                : ResolveMaximumTerminalThickness(routeGroupRequest.EndTerminal);
        }

        private static double ResolveMaximumTerminalThickness(
            LinkageTerminalAssembly terminalAssembly)
        {
            var validThicknesses = (terminalAssembly?.Members ??
                    new List<LinkageTerminalMember>())
                .Where(member => member?.FixedBusbar?.BusBarThickness > 0d)
                .Select(member => member.FixedBusbar.BusBarThickness)
                .ToList();
            return validThicknesses.Count == 0
                ? 0d
                : validThicknesses.Max();
        }

        private static double MeasureBoundaryCoordinate(
            PlanarBendBusbarSetting planarSetting,
            bool useStartBoundary)
        {
            var segmentIndex = useStartBoundary
                ? 0
                : planarSetting.PointList.Count - 2;
            if (!TryGetLocalThicknessDirection(
                    planarSetting,
                    segmentIndex,
                    out var thicknessDirection))
            {
                return 0d;
            }

            var boundaryPoint = useStartBoundary
                ? planarSetting.PointList[0]
                : planarSetting.PointList[planarSetting.PointList.Count - 1];
            return DotPoint(boundaryPoint, thicknessDirection.Normalized);
        }

        private static bool TryGetLocalThicknessDirection(
            PlanarBendBusbarSetting planarSetting,
            int segmentIndex,
            out Vector3d localThicknessDirection)
        {
            localThicknessDirection = null;
            if (planarSetting?.PointList == null ||
                planarSetting.PointList.Count < 2 ||
                planarSetting.StartBusbarVector == null ||
                planarSetting.StartBusbarVector.Norm <= GeometryTolerance ||
                planarSetting.WidthVector == null ||
                planarSetting.WidthVector.Norm <= GeometryTolerance ||
                segmentIndex < 0 ||
                segmentIndex + 1 >= planarSetting.PointList.Count)
            {
                return false;
            }

            var firstSegmentDirection = new Vector3d(
                planarSetting.PointList[0],
                planarSetting.PointList[1]).Normalized;
            var firstSegmentThicknessDirection = firstSegmentDirection
                .Cross(planarSetting.WidthVector.Normalized).Normalized;
            var pathThicknessOrientationSign = firstSegmentThicknessDirection.Dot(
                    planarSetting.StartBusbarVector.Normalized) < 0d
                ? -1d
                : 1d;
            var targetSegmentDirection = new Vector3d(
                planarSetting.PointList[segmentIndex],
                planarSetting.PointList[segmentIndex + 1]).Normalized;
            localThicknessDirection = targetSegmentDirection
                .Cross(planarSetting.WidthVector.Normalized).Normalized;
            if (pathThicknessOrientationSign < 0d)
            {
                localThicknessDirection = new Vector3d(
                    -localThicknessDirection.X,
                    -localThicknessDirection.Y,
                    -localThicknessDirection.Z);
            }

            return localThicknessDirection.Norm > GeometryTolerance;
        }

        private static Point3d ProjectToPlane(
            Point3d sourcePoint,
            Point3d planePoint,
            Vector3d normalizedPlaneNormal)
        {
            var signedDistance = new Vector3d(planePoint, sourcePoint).Dot(
                normalizedPlaneNormal);
            return LinkageRouteSolidGeometry.Translate(
                sourcePoint,
                normalizedPlaneNormal,
                -signedDistance);
        }

        private static double DotPoint(
            Point3d sourcePoint,
            Vector3d normalizedDirection)
        {
            return sourcePoint.X * normalizedDirection.X +
                sourcePoint.Y * normalizedDirection.Y +
                sourcePoint.Z * normalizedDirection.Z;
        }
    }
}
