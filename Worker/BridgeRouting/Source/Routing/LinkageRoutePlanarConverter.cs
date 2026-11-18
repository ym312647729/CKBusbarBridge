using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using static RouteLinkBar.Algorithm.Geometry.GeometRiMethod;

namespace RouteLinkBar.Algorithm.Routing
{
    public sealed class LinkageRouteCenterlineConversionResult
    {
        public bool IsValid { get; internal set; }
        public IList<PlanarBendBusbarSetting> CenterlineParts { get; } =
            new List<PlanarBendBusbarSetting>();
        public IList<LinkageStackedRouteMember> StackMembers { get; } =
            new List<LinkageStackedRouteMember>();
        public IList<LinkageRouteAccessory> Accessories { get; } =
            new List<LinkageRouteAccessory>();
        public IList<string> Diagnostics { get; } = new List<string>();
        public double ActualCopperLength { get; internal set; }
        internal LinkageRouteStackTerminalPlacement StartStackPlacement { get; set; }
        internal LinkageRouteStackTerminalPlacement EndStackPlacement { get; set; }
        internal ISet<int> StartTerminalMemberIndices { get; } =
            new HashSet<int>();
        internal ISet<int> EndTerminalMemberIndices { get; } =
            new HashSet<int>();
        internal ISet<int> DoubleStartPhysicalBridgeSectionIndices { get; } =
            new HashSet<int>();
        internal bool UsesSectionSpecificBusbarGeometry { get; set; }
        internal bool TerminalHoleLapGeometryAdjusted { get; set; }
        internal bool SupportReductionGeometryAdjusted { get; set; }
        internal bool USplitWasAttempted { get; set; }
        internal int USplitContactSide { get; set; } = 1;

        /// <summary>
        /// The unmodified XYZ centerline captured before section-specific
        /// materialization.  It is intentionally internal and is never part
        /// of candidate signatures or the Worker transport contract.
        /// </summary>
        internal OriginalCenterlineSnapshot OriginalCenterlineSnapshot { get; set; }
    }

    /// <summary>
    /// Immutable-in-practice source geometry used when a section-specific
    /// split has to be folded back into the original direction chain.
    /// </summary>
    internal sealed class OriginalCenterlineSnapshot
    {
        internal IList<PlanarBendBusbarSetting> CenterlineParts { get; set; } =
            new List<PlanarBendBusbarSetting>();
        internal IList<Vector3d> DirectionSequence { get; set; } =
            new List<Vector3d>();
        internal IList<Point3d> SegmentStartPoints { get; set; } =
            new List<Point3d>();
        internal IList<Point3d> SegmentEndPoints { get; set; } =
            new List<Point3d>();
        internal IList<int> OriginalPhysicalPartMap { get; set; } =
            new List<int>();
        internal IList<Point3d> ManufacturableCenterline { get; set; } =
            new List<Point3d>();
        internal Point3d OriginalStartPoint { get; set; }
        internal Point3d OriginalEndPoint { get; set; }
        internal IList<Point3d> RealBendPoints { get; set; } =
            new List<Point3d>();
    }

    /// <summary>
    /// 将完整 XYZ 中心线拆成可制造的平弯铜排。跨平面处保留固定交点，
    /// 两侧各延伸半个排宽，并沿局部排厚方向错开一个单根排厚形成满搭接。
    /// </summary>
    public static class LinkageRouteCenterlineConverter
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.PointCoincidence;
        private const int CompleteTerminalCoveragePreference = 1000;

        private sealed class PlanarConversionInvariantContext
        {
            public bool StartStackPlacementResolved { get; set; }
            public LinkageRouteStackTerminalPlacement StartStackPlacement
            {
                get;
                set;
            }
            public string StartStackPlacementFailureReason { get; set; }
            public bool EndStackPlacementResolved { get; set; }
            public LinkageRouteStackTerminalPlacement EndStackPlacement
            {
                get;
                set;
            }
            public string EndStackPlacementFailureReason { get; set; }
        }

        public static LinkageRouteCenterlineConversionResult Convert(
            IList<Point3d> centerlinePoints,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            bool applySingleStackCabinetTopSpecification = false)
        {
            return ConvertForUContactSide(centerlinePoints, routeRequest, routeGroupRequest,
                applySingleStackCabinetTopSpecification, 1);
        }

        internal static LinkageRouteCenterlineConversionResult ConvertForUContactSide(
            IList<Point3d> centerlinePoints, LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest, bool applySingleStackCabinetTopSpecification,
            int contactOffsetSign)
        {
            return ConvertForUContactSideCancelable(centerlinePoints, routeRequest, routeGroupRequest,
                applySingleStackCabinetTopSpecification, contactOffsetSign, CancellationToken.None);
        }

        internal static LinkageRouteCenterlineConversionResult ConvertForUContactSideCancelable(
            IList<Point3d> centerlinePoints, LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest, bool applySingleStackCabinetTopSpecification,
            int contactOffsetSign, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 初始平面转换永远属于普通方案生成。端头无垫块只能由完整方案
            // 发布边界显式调用后处理入口，不能通过请求开关改变这里的层位。
            var pathSearchRouteRequest = routeRequest?.CreatePathSearchRequest();
            if (centerlinePoints == null || centerlinePoints.Count < 2 ||
                pathSearchRouteRequest == null || routeGroupRequest == null)
            {
                return ConvertWithTransitionSigns(
                    centerlinePoints,
                    pathSearchRouteRequest,
                    routeGroupRequest,
                    new int[0]);
            }

            var actualTransitionCount = CountActualPlanarTransitions(
                centerlinePoints,
                routeGroupRequest.StartTerminal.ThicknessDirection);
            var maximumTransitionCount = Math.Min(8, actualTransitionCount);
            var invariantContext = CreateInvariantContext(
                centerlinePoints,
                pathSearchRouteRequest,
                routeGroupRequest);
            var conversionCandidates = new List<LinkageRouteCenterlineConversionResult>();
            var transitionCombinationCount = 1 << maximumTransitionCount;
            for (var transitionMask = 0;
                transitionMask < transitionCombinationCount;
                transitionMask++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var transitionSigns = new int[maximumTransitionCount];
                for (var transitionIndex = 0;
                    transitionIndex < maximumTransitionCount;
                    transitionIndex++)
                {
                    transitionSigns[transitionIndex] =
                        (transitionMask & (1 << transitionIndex)) == 0 ? -1 : 1;
                }

                conversionCandidates.Add(ConvertWithTransitionSignsCore(
                    centerlinePoints,
                    pathSearchRouteRequest,
                    routeGroupRequest,
                    transitionSigns,
                    invariantContext));
            }

            var rankedConversionCandidates = conversionCandidates
                .Select(conversionCandidate => new
                {
                    ConversionCandidate = conversionCandidate,
                    StartCoverageScore = GetTerminalLapCoverageScore(
                        conversionCandidate,
                        routeGroupRequest.StartTerminal,
                        routeGroupRequest.GetStartLapBusbarWidth(
                            pathSearchRouteRequest),
                        routeGroupRequest.GetStartLapBusbarThickness(
                            pathSearchRouteRequest),
                        true),
                    EndCoverageScore = GetTerminalLapCoverageScore(
                        conversionCandidate,
                        routeGroupRequest.EndTerminal,
                        routeGroupRequest.GetCabinetTopLapBusbarWidth(
                            pathSearchRouteRequest),
                        routeGroupRequest.GetCabinetTopLapBusbarThickness(
                            pathSearchRouteRequest),
                        false)
                })
                .OrderByDescending(rankedCandidate =>
                    rankedCandidate.ConversionCandidate.IsValid)
                .ThenByDescending(rankedCandidate =>
                    rankedCandidate.StartCoverageScore +
                    rankedCandidate.EndCoverageScore)
                .ThenByDescending(rankedCandidate =>
                    rankedCandidate.StartCoverageScore)
                .ThenByDescending(rankedCandidate =>
                    rankedCandidate.EndCoverageScore)
                .ThenBy(rankedCandidate => rankedCandidate
                    .ConversionCandidate.ActualCopperLength)
                .Select(rankedCandidate => rankedCandidate.ConversionCandidate)
                .ToList();
            var selectedConversionCandidate =
                rankedConversionCandidates.First();
            var selectedValidConversionCandidate = false;
            foreach (var rankedConversionCandidate in
                rankedConversionCandidates.Where(candidate =>
                    candidate.IsValid))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (routeGroupRequest.HasSectionSpecificBusbarSettings(
                        pathSearchRouteRequest) &&
                    !TryApplySectionSpecificBusbarGeometry(
                        rankedConversionCandidate,
                        pathSearchRouteRequest,
                        routeGroupRequest,
                        out var sectionSpecificGeometryFailureReason,
                        applySingleStackCabinetTopSpecification))
                {
                    rankedConversionCandidate.IsValid = false;
                    rankedConversionCandidate.Diagnostics.Add(
                        sectionSpecificGeometryFailureReason);
                    continue;
                }

                rankedConversionCandidate.USplitWasAttempted = !pathSearchRouteRequest.AllowUShapeRoutes &&
                    LinkageRouteUShapeOutputTransformer.ContainsRestrictedReturn(rankedConversionCandidate.StackMembers
                        .SelectMany(member => member.PlanarParts));
                var replacedUShapeGeometry = false;
                if (!pathSearchRouteRequest.AllowUShapeRoutes &&
                    !LinkageRouteUShapeOutputTransformer.TryReplaceUShapesWithOffsetDirection(
                        rankedConversionCandidate,
                        contactOffsetSign,
                        out replacedUShapeGeometry,
                        out var uShapeReplacementFailureReason))
                {
                    rankedConversionCandidate.IsValid = false;
                    rankedConversionCandidate.Diagnostics.Add(
                        uShapeReplacementFailureReason);
                    continue;
                }
                else if (!pathSearchRouteRequest.AllowUShapeRoutes &&
                    replacedUShapeGeometry &&
                    !LinkageRouteTerminalAccessoryBuilder.TryBuild(
                        rankedConversionCandidate,
                        pathSearchRouteRequest,
                        routeGroupRequest,
                        out var rebuiltAccessoryFailureReason,
                        true))
                {
                    rankedConversionCandidate.IsValid = false;
                    rankedConversionCandidate.Diagnostics.Add(
                        rebuiltAccessoryFailureReason);
                    continue;
                }
                else if (!pathSearchRouteRequest.AllowUShapeRoutes &&
                    LinkageRouteUShapeOutputTransformer.ContainsRestrictedReturn(
                    rankedConversionCandidate.StackMembers
                        .Where(stackMember => stackMember?.PlanarParts != null)
                        .SelectMany(stackMember => stackMember.PlanarParts)))
                {
                    rankedConversionCandidate.IsValid = false;
                    rankedConversionCandidate.Diagnostics.Add(
                        "当前M/U长度限制转换后仍有中段不大于两端之和的实体。" );
                    continue;
                }

                // 异规格实体化会从同一中心线路径派生多种平面转向组合。
                // 不能先选中一个组合，再到最终输出阶段因为内部搭接需要
                // 移动刚性 I 排而整体失败；这里对每个组合执行同一套最多
                // 10 轮的局部搭接段闭合，只保留无需移动 I 排且最终能够
                // 直接贴合或由末段垫块补差的组合，然后继续择优下一候选。
                if (routeGroupRequest.HasSectionSpecificBusbarSettings(
                        pathSearchRouteRequest) &&
                    !TryCloseSectionSpecificInternalLapsForSelectionCancelable(
                        rankedConversionCandidate,
                        pathSearchRouteRequest,
                        routeGroupRequest,
                        out var internalLapClosureFailureReason, cancellationToken))
                {
                    rankedConversionCandidate.IsValid = false;
                    rankedConversionCandidate.Diagnostics.Add(
                        internalLapClosureFailureReason);
                    continue;
                }

                selectedConversionCandidate = rankedConversionCandidate;
                selectedValidConversionCandidate = true;
                break;
            }

            if (!selectedValidConversionCandidate)
            {
                selectedConversionCandidate.IsValid = false;
            }

            if (selectedConversionCandidate.IsValid)
            {
                LinkageRouteTerminalAccessoryBuilder
                    .ApplyOutputTerminalLapGeometry(
                        selectedConversionCandidate,
                        routeGroupRequest.StartTerminal);
                // ApplyOutputTerminalLapGeometry 只调整已选方案的端部实体端点，
                // 不再参与候选筛选；端点长度改变后必须同步刷新输出总长，
                // 否则实体复算会比 ActualCopperLength 多出端部延伸量。
                selectedConversionCandidate.ActualCopperLength =
                    selectedConversionCandidate.StackMembers
                        .Where(stackMember => stackMember?.PlanarParts != null)
                        .SelectMany(stackMember => stackMember.PlanarParts)
                        .Where(planarPart => planarPart != null)
                        .Sum(planarPart => planarPart.GetDevelopedLength()) +
                    LinkageRouteCopperLength.GetBridgeLength(selectedConversionCandidate.Accessories);
            }

            if (actualTransitionCount > maximumTransitionCount)
            {
                selectedConversionCandidate.Diagnostics.Insert(
                    0,
                    string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "检测到 {0} 个实际平面转换，当前仅枚举前 {1} 个转换的方向组合。",
                        actualTransitionCount,
                        maximumTransitionCount));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return selectedConversionCandidate;
        }

        private static bool TryCloseSectionSpecificInternalLapsForSelection(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            return TryCloseSectionSpecificInternalLapsForSelectionCancelable(conversionResult,
                routeRequest, routeGroupRequest, out failureReason, CancellationToken.None);
        }

        private static bool TryCloseSectionSpecificInternalLapsForSelectionCancelable(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest, LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            failureReason = null;
            const int maximumAdjustmentCount = 10;
            for (var adjustmentIndex = 0;
                adjustmentIndex < maximumAdjustmentCount;
                adjustmentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LinkageRouteTerminalAccessoryBuilder
                    .ApplyOutputTerminalBridgeLayerAlignment(
                        conversionResult);

                if (!LinkageRouteSectionSpecificationPostProcessor
                        .TrySeparateNonAdjacentPhysicalLapSegmentsCancelable(
                            conversionResult,
                            out var nonAdjacentGeometryAdjusted,
                            out var nonAdjacentFailureReason, cancellationToken))
                {
                    failureReason = nonAdjacentFailureReason;
                    return false;
                }

                if (!LinkageRouteSectionSpecificationPostProcessor
                        .TrySeparateInternalPhysicalLapGapsCancelable(
                            conversionResult,
                            routeRequest,
                            routeGroupRequest,
                            out var internalLapGeometryAdjusted,
                            out var internalLapFailureReason, cancellationToken))
                {
                    failureReason = internalLapFailureReason;
                    return false;
                }

                // 厚向层位校正会重建 N 排的点列，因此 N 形稳定必须放在
                // 本轮最后，避免刚延长的过渡又被后续偏移恢复成原交叉点。
                LinkageRouteSectionSpecificationPostProcessor
                    .TryStabilizePersistentNTransitions(
                        conversionResult,
                        out var persistentNTransitionAdjusted);

                if (!nonAdjacentGeometryAdjusted &&
                    !internalLapGeometryAdjusted &&
                    !persistentNTransitionAdjusted)
                {
                    break;
                }
            }

            if (LinkageRouteSectionSpecificationPostProcessor
                .TryValidateInternalPhysicalLapClosure(
                    conversionResult,
                    true,
                    out _,
                    out var validationFailureReason))
            {
                return true;
            }

            failureReason = "异规格平面转换候选内部搭接未闭合：" +
                validationFailureReason + "；" +
                DescribePhysicalSectionGeometry(conversionResult);
            return false;
        }

        private static string DescribePhysicalSectionGeometry(
            LinkageRouteCenterlineConversionResult conversionResult)
        {
            var descriptions = new List<string>();
            foreach (var stackMember in conversionResult.StackMembers
                .Where(currentMember => currentMember?.PlanarParts != null))
            {
                foreach (var planarPart in stackMember.PlanarParts
                    .Where(currentPart =>
                        currentPart?.PointList != null &&
                        currentPart.PointList.Count >= 2))
                {
                    var firstPoint = planarPart.PointList[0];
                    var lastPoint = planarPart.PointList[
                        planarPart.PointList.Count - 1];
                    descriptions.Add(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "成员{0}/物理段{1}/规格段{2}/点数{3}/规格{4:F1}×{5:F1}/首({6:F1},{7:F1},{8:F1})/末({9:F1},{10:F1},{11:F1})",
                        stackMember.MemberIndex + 1,
                        planarPart.PhysicalSectionIndex + 1,
                        planarPart.SpecificationSectionIndex + 1,
                        planarPart.PointList.Count,
                        planarPart.BusBarWidth,
                        planarPart.BusBarThickness,
                        firstPoint.X,
                        firstPoint.Y,
                        firstPoint.Z,
                        lastPoint.X,
                        lastPoint.Y,
                        lastPoint.Z));
                }
            }

            return "候选实体=" + string.Join("，", descriptions);
        }

        /// <summary>
        /// 对已经完成普通转换并通过候选校验的方案深拷贝执行终端输出
        /// 后处理。保留已选平面拆排和转向符号，只缩短物理始端垂直搭接、
        /// 重建无垫块端部层位、实际叠排成员及附件，避免重新转换时改变
        /// 双始端 I/L 桥或逐成员裁切结果。
        /// </summary>
        internal static LinkageRouteCenterlineConversionResult
            CreateTerminalOutputPostProcessingResult(
            LinkageRouteCenterlineConversionResult ordinaryConversionResult,
            IList<Point3d> ordinaryManufacturableCenterline,
            LinkageMultiGroupRouteRequest outputRouteRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            var outputConversionResult =
                new LinkageRouteCenterlineConversionResult();
            if (ordinaryConversionResult?.IsValid != true ||
                ordinaryConversionResult.CenterlineParts == null ||
                ordinaryConversionResult.CenterlineParts.Count == 0 ||
                ordinaryManufacturableCenterline == null ||
                ordinaryManufacturableCenterline.Count < 2 ||
                outputRouteRequest == null || routeGroupRequest == null)
            {
                outputConversionResult.Diagnostics.Add(
                    "终端输出后处理缺少完整的普通方案转换结果。" );
                return outputConversionResult;
            }

            foreach (var ordinaryCenterlinePart in
                ordinaryConversionResult.CenterlineParts)
            {
                if (ordinaryCenterlinePart == null)
                {
                    outputConversionResult.Diagnostics.Add(
                        "终端输出后处理遇到空的平面铜排。" );
                    return outputConversionResult;
                }

                outputConversionResult.CenterlineParts.Add(
                    ordinaryCenterlinePart.Copy());
            }

            outputConversionResult.OriginalCenterlineSnapshot =
                CopyOriginalCenterlineSnapshot(
                    ordinaryConversionResult.OriginalCenterlineSnapshot);
            outputConversionResult.USplitContactSide = ordinaryConversionResult.USplitContactSide;
            outputConversionResult.USplitWasAttempted = ordinaryConversionResult.USplitWasAttempted;

            foreach (var doubleStartPhysicalBridgeSectionIndex in
                ordinaryConversionResult.DoubleStartPhysicalBridgeSectionIndices)
            {
                outputConversionResult.DoubleStartPhysicalBridgeSectionIndices.Add(
                    doubleStartPhysicalBridgeSectionIndex);
            }
            foreach (var startTerminalMemberIndex in
                ordinaryConversionResult.StartTerminalMemberIndices)
            {
                outputConversionResult.StartTerminalMemberIndices.Add(
                    startTerminalMemberIndex);
            }
            foreach (var endTerminalMemberIndex in
                ordinaryConversionResult.EndTerminalMemberIndices)
            {
                outputConversionResult.EndTerminalMemberIndices.Add(
                    endTerminalMemberIndex);
            }

            var generatedSingleBusbarThickness = routeGroupRequest
                .GetBusbarThickness(outputRouteRequest);
            var generatedBusbarWidth = routeGroupRequest.GetBusbarWidth(
                outputRouteRequest);
            var ordinaryStartPlacementResolved =
                TryCreatePhysicalTerminalPlacement(
                    ordinaryConversionResult,
                    routeGroupRequest.StartTerminal,
                    ordinaryConversionResult.StartStackPlacement,
                    true,
                    out var ordinaryStartTerminalPlacement,
                    out var ordinaryStartPlacementFailureReason);
            var ordinaryEndPlacementResolved =
                TryCreatePhysicalTerminalPlacement(
                    ordinaryConversionResult,
                    routeGroupRequest.EndTerminal,
                    ordinaryConversionResult.EndStackPlacement,
                    false,
                    out var ordinaryEndTerminalPlacement,
                    out var ordinaryEndPlacementFailureReason);
            if (!ordinaryStartPlacementResolved ||
                !ordinaryEndPlacementResolved)
            {
                outputConversionResult.Diagnostics.Add(
                    ordinaryStartPlacementFailureReason ??
                    ordinaryEndPlacementFailureReason ??
                    "端头无垫块后处理无法读取普通方案的实际端部层位。" );
                return outputConversionResult;
            }

            // 先把已经通过普通终检的逐层实际铜排完整复制到输出对象。
            // 此时输入、输出层位相同，因此只发生深拷贝，不改变任何几何。
            if (!TryMoveOrdinaryStackMemberTerminalSegments(
                    outputConversionResult,
                    ordinaryConversionResult,
                    routeGroupRequest.StartTerminal,
                    ordinaryStartTerminalPlacement,
                    ordinaryStartTerminalPlacement,
                    routeGroupRequest.EndTerminal,
                    ordinaryEndTerminalPlacement,
                    ordinaryEndTerminalPlacement,
                    out var ordinaryStackMemberCopyFailureReason))
            {
                outputConversionResult.Diagnostics.Add(
                    ordinaryStackMemberCopyFailureReason);
                return outputConversionResult;
            }

            // 始端短搭接必须先在普通方案深拷贝上完成。后续无垫块闭缝
            // 只沿厚度方向移动既有端段，不得重新生成或覆盖短搭接几何。
            if (outputRouteRequest.AllowPartialStartPerpendicularLap &&
                !TryShortenPhysicalStartPerpendicularLaps(
                    outputConversionResult,
                    routeGroupRequest.StartTerminal,
                    ordinaryStartTerminalPlacement,
                    outputRouteRequest,
                    routeGroupRequest,
                    out var partialStartLapFailureReason))
            {
                outputConversionResult.Diagnostics.Add(
                    partialStartLapFailureReason);
                return outputConversionResult;
            }

            var outputStartTerminalPlacement = ordinaryStartTerminalPlacement;
            if (outputRouteRequest.ShouldUseSpacerlessTerminalLap(true))
            {
                // 异数量实体的全局成员可能只出现在中间/柜顶，不能把它们
                // 当作始端层位重新插桥。闭缝用实际始端子集，完成后还原全局身份。
                var actualSingleStartMemberIndices = outputRouteRequest.EnableSingleStartConnectionBar &&
                    routeGroupRequest.StartTerminal.Members.Count == 1 &&
                    ordinaryStartTerminalPlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex.HasValue
                    ? ordinaryConversionResult.StartTerminalMemberIndices.OrderBy(index => index).ToList()
                    : null;
                var usesSingleStartMemberSubset = actualSingleStartMemberIndices?.Count > 0 &&
                    actualSingleStartMemberIndices.Count < ordinaryStartTerminalPlacement.GeneratedMemberOffsets.Count;
                var startPackingPhysicalPlacement = usesSingleStartMemberSubset
                    ? CreateTerminalPlacementMemberSubset(ordinaryStartTerminalPlacement, actualSingleStartMemberIndices)
                    : ordinaryStartTerminalPlacement;
                var startPackingSourcePlacement =
                    CreateSpacerlessPackingSourcePlacement(
                        startPackingPhysicalPlacement,
                        true);
                if (!LinkageRouteStackPlacementSolver
                        .TryCreateSpacerlessOutputPlacement(
                            routeGroupRequest.StartTerminal,
                            startPackingSourcePlacement,
                            generatedSingleBusbarThickness,
                            true,
                            out outputStartTerminalPlacement))
                {
                    outputConversionResult.Diagnostics.Add(
                        "始端无垫块后处理无法闭合普通垫块对应的夹缝。" );
                    return outputConversionResult;
                }

                // 普通方案若已经包含双始端横向桥排，局部闭缝前先还原
                // 桥排插入前层位，闭缝后再按原桥接侧重新插入。特殊 I/L
                // 桥本身属于路径铜排，没有此标记，绝不能在这里新增桥排。
                if (ordinaryStartTerminalPlacement
                    .DoubleStartHorizontalBridgeGeneratedMemberIndex.HasValue)
                {
                    if (!LinkageRouteTerminalAccessoryBuilder
                            .TryPrepareDoubleStartHorizontalBridgePlacement(
                                outputStartTerminalPlacement,
                                outputConversionResult.CenterlineParts[0],
                                outputRouteRequest,
                                routeGroupRequest,
                                false,
                                out var preparedOutputStartTerminalPlacement,
                                out var doubleStartBridgePlacementFailureReason))
                    {
                        outputConversionResult.Diagnostics.Add(
                            doubleStartBridgePlacementFailureReason);
                        return outputConversionResult;
                    }

                    outputStartTerminalPlacement =
                        preparedOutputStartTerminalPlacement;
                }
                if (usesSingleStartMemberSubset)
                    outputStartTerminalPlacement = RestoreTerminalPlacementMemberSubset(
                        ordinaryStartTerminalPlacement, outputStartTerminalPlacement, actualSingleStartMemberIndices);
            }

            var outputEndTerminalPlacement = ordinaryEndTerminalPlacement;
            if (outputRouteRequest.ShouldUseSpacerlessTerminalLap(false) &&
                !LinkageRouteStackPlacementSolver
                    .TryCreateSpacerlessOutputPlacement(
                        routeGroupRequest.EndTerminal,
                        ordinaryEndTerminalPlacement,
                        generatedSingleBusbarThickness,
                        false,
                        out outputEndTerminalPlacement))
            {
                outputConversionResult.Diagnostics.Add(
                    "柜顶无垫块后处理无法闭合普通垫块对应的夹缝。" );
                return outputConversionResult;
            }

            if (!TryMoveOrdinaryStackMemberTerminalSegments(
                    outputConversionResult,
                    outputConversionResult,
                    routeGroupRequest.StartTerminal,
                    ordinaryStartTerminalPlacement,
                    outputStartTerminalPlacement,
                    routeGroupRequest.EndTerminal,
                    ordinaryEndTerminalPlacement,
                    outputEndTerminalPlacement,
                    out var terminalSegmentMoveFailureReason))
            {
                outputConversionResult.Diagnostics.Add(
                    terminalSegmentMoveFailureReason);
                return outputConversionResult;
            }

            outputConversionResult.StartStackPlacement =
                CopyTerminalPlacement(outputStartTerminalPlacement);
            outputConversionResult.EndStackPlacement =
                CopyTerminalPlacement(outputEndTerminalPlacement);

            LinkageRouteSectionSpecificationPostProcessor.ProcessingState
                sectionSpecificationProcessingState = null;
            var usesSectionSpecificBusbarSettings = routeGroupRequest
                .HasSectionSpecificBusbarSettings(outputRouteRequest);
            if (usesSectionSpecificBusbarSettings &&
                (!ordinaryConversionResult.UsesSectionSpecificBusbarGeometry
                    ? !LinkageRouteSectionSpecificationPostProcessor.TryApply(
                        outputConversionResult,
                        outputRouteRequest,
                        routeGroupRequest,
                        out sectionSpecificationProcessingState,
                        out var sectionSpecificationFailureReason)
                    : !LinkageRouteSectionSpecificationPostProcessor
                        .TryCreateProcessingStateFromExistingGeometry(
                            outputConversionResult,
                            out sectionSpecificationProcessingState,
                            out sectionSpecificationFailureReason)))
            {
                outputConversionResult.Diagnostics.Add(
                    sectionSpecificationFailureReason);
                return outputConversionResult;
            }

            // 已合并到主路径中的连接排需要在方案选定后，按完整连接排叠排包络
            // 调整下一物理段；该修正不能进入前面的候选搜索与淘汰阶段。
            if (!LinkageRouteSectionSpecificationPostProcessor
                    .TryAlignPhysicalBridgeFollowingLapEndpoints(
                        outputConversionResult,
                        routeGroupRequest.StartTerminal,
                        out var physicalBridgeFollowingLapFailureReason))
            {
                outputConversionResult.Diagnostics.Add(
                    physicalBridgeFollowingLapFailureReason);
                return outputConversionResult;
            }

            if (!ValidateStackedUShapePreviewMinimumSegments(
                    outputConversionResult.StackMembers,
                    outputRouteRequest.MinimumSegmentLength,
                    out var stackedUShapeMinimumSegmentFailureReason))
            {
                outputConversionResult.Diagnostics.Add(
                    stackedUShapeMinimumSegmentFailureReason);
                return outputConversionResult;
            }

            if (!LinkageRouteTerminalAccessoryBuilder.TryBuild(
                    outputConversionResult,
                    outputRouteRequest,
                    routeGroupRequest,
                    out var terminalAccessoryFailureReason,
                    true))
            {
                outputConversionResult.Diagnostics.Add(
                    terminalAccessoryFailureReason);
                return outputConversionResult;
            }

            // 独立附件式连接排在全部附件实体化后统一修正关联搭接排层位。
            // 该入口不区分路径形状，只修改最终输出实体，并由后续终检重算绝缘距离。
            LinkageRouteTerminalAccessoryBuilder
                .ApplyOutputTerminalBridgeLayerAlignment(
                    outputConversionResult);

            if (usesSectionSpecificBusbarSettings &&
                !LinkageRouteSectionSpecificationPostProcessor
                    .TryAddInternalSupportBlocks(
                        outputConversionResult,
                        outputRouteRequest,
                        routeGroupRequest,
                        sectionSpecificationProcessingState,
                        out var internalSupportBlockFailureReason))
            {
                outputConversionResult.Diagnostics.Add(
                    internalSupportBlockFailureReason);
                return outputConversionResult;
            }

            // 异规格内部补差可能在连接排层位修正后重新加入始端垫块。
            // 水平多始端连接端必须最终再次统一清除垫块并复核逐层贴合。
            LinkageRouteTerminalAccessoryBuilder
                .ApplyOutputTerminalBridgeLayerAlignment(
                    outputConversionResult);

            outputConversionResult.UsesSectionSpecificBusbarGeometry =
                usesSectionSpecificBusbarSettings;

            outputConversionResult.ActualCopperLength =
                outputConversionResult.StackMembers
                    .SelectMany(stackMember => stackMember.PlanarParts)
                    .Sum(planarPart => planarPart.GetDevelopedLength()) +
                LinkageRouteCopperLength.GetBridgeLength(outputConversionResult.Accessories);

            foreach (var outputStackMember in
                outputConversionResult.StackMembers)
            {
                if (!usesSectionSpecificBusbarSettings &&
                    !ValidatePartLaps(
                        outputStackMember.PlanarParts,
                        generatedBusbarWidth,
                        outputRouteRequest.GetEffectiveBendAllowance(),
                        out var stackLapFailureReason,
                        outputConversionResult.DoubleStartPhysicalBridgeSectionIndices,
                        reserveRequest: outputRouteRequest))
                {
                    outputConversionResult.Diagnostics.Add(
                        "叠排第 " + (outputStackMember.MemberIndex + 1) +
                        " 层" + stackLapFailureReason);
                    return outputConversionResult;
                }
            }

            outputConversionResult.IsValid =
                outputConversionResult.CenterlineParts.Count > 0 &&
                outputConversionResult.StackMembers.Count > 0;
            return outputConversionResult;
        }

        /// <summary>
        /// 仅为已经完成普通搜索和真实分段规格实体化的多始端水平连接排
        /// 创建输出深拷贝。该路径不重新求解端部层位、不重复应用异规格
        /// 分段，也不要求启用垂直短搭接或无垫块开关；只修正连接排搭接
        /// 端点及厚向层位，避免后台优化方案被无关端部后处理淘汰。
        /// </summary>
        internal static LinkageRouteCenterlineConversionResult
            CreateMultiStartConnectionPlateOutputAlignmentResult(
            LinkageRouteCenterlineConversionResult ordinaryConversionResult,
            LinkageTerminalAssembly startTerminalAssembly)
        {
            var outputConversionResult =
                new LinkageRouteCenterlineConversionResult();
            if (ordinaryConversionResult?.IsValid != true)
            {
                outputConversionResult.Diagnostics.Add(
                    "多始端连接排输出层位修正缺少有效普通方案。" );
                return outputConversionResult;
            }

            foreach (var centerlinePart in ordinaryConversionResult
                .CenterlineParts)
            {
                outputConversionResult.CenterlineParts.Add(
                    centerlinePart?.Copy());
            }

            foreach (var stackMember in ordinaryConversionResult.StackMembers)
            {
                outputConversionResult.StackMembers.Add(
                    new LinkageStackedRouteMember
                    {
                        MemberIndex = stackMember.MemberIndex,
                        CenterlineOffset = stackMember.CenterlineOffset,
                        PlanarParts = (stackMember.PlanarParts ??
                                new List<PlanarBendBusbarSetting>())
                            .Select(planarPart => planarPart?.Copy())
                            .ToList()
                    });
            }

            foreach (var routeAccessory in ordinaryConversionResult.Accessories)
            {
                outputConversionResult.Accessories.Add(
                    new LinkageRouteAccessory
                    {
                        Kind = routeAccessory.Kind,
                        TerminalSide = routeAccessory.TerminalSide,
                        RelatedGeneratedMemberIndex =
                            routeAccessory.RelatedGeneratedMemberIndex,
                        Setting = routeAccessory.Setting?.Copy(),
                        SupportBlockKey = routeAccessory.SupportBlockKey
                    });
            }

            foreach (var diagnostic in ordinaryConversionResult.Diagnostics)
            {
                outputConversionResult.Diagnostics.Add(diagnostic);
            }

            outputConversionResult.StartStackPlacement =
                CopyTerminalPlacement(
                    ordinaryConversionResult.StartStackPlacement);
            outputConversionResult.EndStackPlacement =
                CopyTerminalPlacement(
                    ordinaryConversionResult.EndStackPlacement);
            foreach (var memberIndex in ordinaryConversionResult
                .StartTerminalMemberIndices)
            {
                outputConversionResult.StartTerminalMemberIndices.Add(
                    memberIndex);
            }
            if (outputConversionResult.StartTerminalMemberIndices.Count == 0 &&
                ordinaryConversionResult.StackMembers.Count > 1)
            {
                foreach (var stackMember in ordinaryConversionResult.StackMembers)
                {
                    if (stackMember != null)
                    {
                        outputConversionResult.StartTerminalMemberIndices.Add(
                            stackMember.MemberIndex);
                    }
                }
            }
            foreach (var memberIndex in ordinaryConversionResult
                .EndTerminalMemberIndices)
            {
                outputConversionResult.EndTerminalMemberIndices.Add(
                    memberIndex);
            }
            foreach (var physicalBridgeSectionIndex in ordinaryConversionResult
                .DoubleStartPhysicalBridgeSectionIndices)
            {
                outputConversionResult.DoubleStartPhysicalBridgeSectionIndices
                    .Add(physicalBridgeSectionIndex);
            }

            outputConversionResult.OriginalCenterlineSnapshot =
                CopyOriginalCenterlineSnapshot(
                    ordinaryConversionResult.OriginalCenterlineSnapshot);
            outputConversionResult.UsesSectionSpecificBusbarGeometry =
                ordinaryConversionResult.UsesSectionSpecificBusbarGeometry;

            LinkageRouteTerminalAccessoryBuilder
                .ApplyOutputTerminalLapGeometry(
                    outputConversionResult,
                    startTerminalAssembly);
            if (!LinkageRouteSectionSpecificationPostProcessor
                    .TryAlignPhysicalBridgeFollowingLapEndpoints(
                        outputConversionResult,
                        startTerminalAssembly,
                        out var physicalBridgeFollowingLapFailureReason) &&
                !string.IsNullOrWhiteSpace(
                    physicalBridgeFollowingLapFailureReason) &&
                !outputConversionResult.Diagnostics.Contains(
                    physicalBridgeFollowingLapFailureReason))
            {
                // 输出层位修正失败只记录错误，不能反向淘汰已经通过普通
                // 搜索的水平搭接候选；后续仍按现有实体复算干涉和净距。
                outputConversionResult.Diagnostics.Add(
                    physicalBridgeFollowingLapFailureReason);
            }
            LinkageRouteTerminalAccessoryBuilder
                .ApplyOutputTerminalBridgeLayerAlignment(
                    outputConversionResult);

            outputConversionResult.ActualCopperLength =
                outputConversionResult.StackMembers
                    .Where(stackMember => stackMember?.PlanarParts != null)
                    .SelectMany(stackMember => stackMember.PlanarParts)
                    .Where(planarPart => planarPart != null)
                    .Sum(planarPart => planarPart.GetDevelopedLength()) +
                LinkageRouteCopperLength.GetBridgeLength(outputConversionResult.Accessories);
            outputConversionResult.IsValid =
                outputConversionResult.StackMembers.Count > 0 &&
                outputConversionResult.StackMembers.All(stackMember =>
                    stackMember?.PlanarParts != null &&
                    stackMember.PlanarParts.Count > 0);
            return outputConversionResult;
        }

        private static OriginalCenterlineSnapshot CopyOriginalCenterlineSnapshot(
            OriginalCenterlineSnapshot sourceSnapshot)
        {
            if (sourceSnapshot == null)
            {
                return null;
            }

            return new OriginalCenterlineSnapshot
            {
                CenterlineParts = (sourceSnapshot.CenterlineParts ??
                        new List<PlanarBendBusbarSetting>())
                    .Where(centerlinePart => centerlinePart != null)
                    .Select(centerlinePart => centerlinePart.Copy())
                    .ToList(),
                DirectionSequence = (sourceSnapshot.DirectionSequence ??
                        new List<Vector3d>())
                    .Where(direction => direction != null)
                    .Select(direction => new Vector3d(
                        direction.X,
                        direction.Y,
                        direction.Z))
                    .ToList(),
                SegmentStartPoints = (sourceSnapshot.SegmentStartPoints ??
                        new List<Point3d>())
                    .Where(point => point != null)
                    .Select(Copy)
                    .ToList(),
                SegmentEndPoints = (sourceSnapshot.SegmentEndPoints ??
                        new List<Point3d>())
                    .Where(point => point != null)
                    .Select(Copy)
                    .ToList(),
                OriginalPhysicalPartMap = (sourceSnapshot
                        .OriginalPhysicalPartMap ?? new List<int>()).ToList(),
                ManufacturableCenterline = (sourceSnapshot
                        .ManufacturableCenterline ?? new List<Point3d>())
                    .Where(point => point != null)
                    .Select(Copy)
                    .ToList(),
                OriginalStartPoint = sourceSnapshot.OriginalStartPoint == null
                    ? null
                    : Copy(sourceSnapshot.OriginalStartPoint),
                OriginalEndPoint = sourceSnapshot.OriginalEndPoint == null
                    ? null
                    : Copy(sourceSnapshot.OriginalEndPoint),
                RealBendPoints = (sourceSnapshot.RealBendPoints ??
                        new List<Point3d>())
                    .Where(point => point != null)
                    .Select(Copy)
                    .ToList()
            };
        }

        /// <summary>
        /// 普通方案已经完成满搭接校验后，只移动实际逐层铜排的始端端点。
        /// 先从已声明的垂直接触求整组“固定排远侧边界减去一个生成排宽”
        /// 的共同最短端面，再把该端面应用到首段方向相同的每根实际叠排。
        /// Contacts 只表示直接接触，不能据此漏掉由垫块间接搭接的成员。
        /// 其余点、折弯、层序和拓扑不变。
        /// </summary>
        private static bool TryShortenPhysicalStartPerpendicularLaps(
            LinkageRouteCenterlineConversionResult outputConversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            LinkageRouteStackTerminalPlacement ordinaryStartTerminalPlacement,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            failureReason = null;
            // 端部实际搭接宽度由当前始端搭接排规格决定；固定端几何只提供
            // 中心线、方向和投影位置，不能用旧固定排宽度把输出截短。
            var effectiveStartLapWidth = routeGroupRequest
                .GetStartLapBusbarWidth(routeRequest);
            if (outputConversionResult?.StackMembers == null ||
                startTerminalAssembly?.Members == null ||
                ordinaryStartTerminalPlacement?.Contacts == null)
            {
                failureReason =
                    "始端垂直短搭接后处理缺少普通方案叠排或接触关系。";
                return false;
            }

            var outputMembersByIndex = outputConversionResult.StackMembers
                .Where(stackMember => stackMember != null)
                .GroupBy(stackMember => stackMember.MemberIndex)
                .ToDictionary(
                    stackMemberGroup => stackMemberGroup.Key,
                    stackMemberGroup => stackMemberGroup.First());
            Vector3d sharedGeneratedStartDirection = null;
            var sharedTargetStartEndpointCoordinate = double.PositiveInfinity;
            var sharedPerpendicularContactCount = 0;
            var requiredReserveByGeneratedMemberIndex =
                new Dictionary<int, double>();
            foreach (var terminalContact in ordinaryStartTerminalPlacement
                .Contacts.Where(placementContact => placementContact != null))
            {
                if (!outputMembersByIndex.TryGetValue(
                        terminalContact.GeneratedMemberIndex,
                        out var contactedOutputStackMember) ||
                    contactedOutputStackMember.PlanarParts == null ||
                    contactedOutputStackMember.PlanarParts.Count == 0)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "始端垂直短搭接后处理找不到第 {0} 层实际铜排。",
                        terminalContact.GeneratedMemberIndex + 1);
                    return false;
                }

                var contactedGeneratedStartSetting =
                    contactedOutputStackMember.PlanarParts[0];
                if (contactedGeneratedStartSetting?.PointList == null ||
                    contactedGeneratedStartSetting.PointList.Count < 2)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "始端垂直短搭接后处理无法读取第 {0} 层首段。",
                        terminalContact.GeneratedMemberIndex + 1);
                    return false;
                }

                var contactedGeneratedStartPoint =
                    contactedGeneratedStartSetting.PointList[0];
                var contactedGeneratedAdjacentPoint =
                    contactedGeneratedStartSetting.PointList[1];
                var contactedGeneratedStartDirection = new Vector3d(
                    contactedGeneratedStartPoint,
                    contactedGeneratedAdjacentPoint);
                if (contactedGeneratedStartDirection.Norm <= GeometryTolerance)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "始端垂直短搭接后处理检测到第 {0} 层首段长度为零。",
                        terminalContact.GeneratedMemberIndex + 1);
                    return false;
                }

                var normalizedContactedGeneratedStartDirection =
                    contactedGeneratedStartDirection.Normalized;
                if (terminalContact.FixedMemberIndex < 0 ||
                    terminalContact.FixedMemberIndex >=
                        startTerminalAssembly.Members.Count)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "始端垂直短搭接后处理检测到无效固定排索引 {0}。",
                        terminalContact.FixedMemberIndex);
                    return false;
                }

                var contactedFixedStartSetting = startTerminalAssembly.Members[
                    terminalContact.FixedMemberIndex]?.FixedBusbar;
                if (contactedFixedStartSetting?.PointList == null ||
                    contactedFixedStartSetting.PointList.Count < 2 ||
                    contactedFixedStartSetting.WidthVector == null ||
                    contactedFixedStartSetting.WidthVector.Norm <=
                        GeometryTolerance)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "始端垂直短搭接后处理无法读取第 {0} 根固定排。",
                        terminalContact.FixedMemberIndex + 1);
                    return false;
                }

                var contactedFixedStartDirection = new Vector3d(
                    contactedFixedStartSetting.PointList[0],
                    contactedFixedStartSetting.PointList[1]);
                var normalizedContactedFixedWidthDirection =
                    contactedFixedStartSetting.WidthVector.Normalized;
                if (contactedFixedStartDirection.Norm <= GeometryTolerance ||
                    Math.Abs(contactedFixedStartDirection.Normalized.Dot(
                        normalizedContactedGeneratedStartDirection)) > 1e-6d ||
                    Math.Abs(normalizedContactedFixedWidthDirection.Dot(
                        normalizedContactedGeneratedStartDirection)) <
                        1d - 1e-6d)
                {
                    continue;
                }

                if (sharedGeneratedStartDirection == null)
                {
                    sharedGeneratedStartDirection =
                        normalizedContactedGeneratedStartDirection;
                }
                else if (sharedGeneratedStartDirection.Dot(
                        normalizedContactedGeneratedStartDirection) <
                    1d - 1e-6d)
                {
                    failureReason =
                        "始端垂直短搭接后处理检测到叠排首段方向不一致。";
                    return false;
                }

                var contactedFixedWidthCenterCoordinate =
                    GetPointCoordinateAlongDirection(
                        contactedFixedStartSetting.PointList[0],
                        sharedGeneratedStartDirection);
                var contactedFixedHalfWidthAlongGeneratedDirection = Math.Abs(
                        normalizedContactedFixedWidthDirection.Dot(
                            sharedGeneratedStartDirection)) *
                    effectiveStartLapWidth / 2d;
                var contactedFixedFarBoundaryCoordinate =
                    contactedFixedWidthCenterCoordinate +
                    contactedFixedHalfWidthAlongGeneratedDirection;
                var requiredContactLength = Math.Min(
                    contactedGeneratedStartSetting.BusBarWidth,
                    effectiveStartLapWidth);
                if (contactedGeneratedStartSetting.PointList.Count > 2)
                {
                    var generatedStackBendAllowance =
                        routeGroupRequest.HasSectionSpecificBusbarSettings(
                            routeRequest)
                            ? routeGroupRequest
                                .GetStartLapSectionStackBendAllowance(
                                    routeRequest)
                            : LinkageRouteStackGeometry.GetStackBendAllowance(
                                contactedGeneratedStartSetting.BusBarThickness >
                                    GeometryTolerance
                                    ? contactedGeneratedStartSetting
                                        .BusBarThickness
                                    : routeGroupRequest.GetBusbarThickness(
                                        routeRequest),
                                Math.Max(
                                    1,
                                    routeGroupRequest.GetStackCount(
                                        routeRequest)),
                                routeGroupRequest.StackMiddleSpace,
                                routeRequest.GetEffectiveBendAllowance());
                    requiredReserveByGeneratedMemberIndex[
                        terminalContact.GeneratedMemberIndex] =
                        effectiveStartLapWidth +
                        generatedStackBendAllowance;
                }
                sharedTargetStartEndpointCoordinate = Math.Min(
                    sharedTargetStartEndpointCoordinate,
                    contactedFixedFarBoundaryCoordinate -
                        requiredContactLength);
                sharedPerpendicularContactCount++;
            }

            if (sharedPerpendicularContactCount == 0 ||
                sharedGeneratedStartDirection == null)
            {
                return true;
            }

            // 垂直短搭接的输出共同端面以当前始端实际 RouteAnchor 为准。
            // Contacts 只用于确认哪些成员参与约束；若继续用单个固定排
            // 的宽面边界反推，会把同组成员截到不同的局部端面。
            if (startTerminalAssembly.RouteAnchor != null)
            {
                sharedTargetStartEndpointCoordinate =
                    GetPointCoordinateAlongDirection(
                        startTerminalAssembly.RouteAnchor,
                        sharedGeneratedStartDirection);
            }

            var fallbackRequiredReserve = requiredReserveByGeneratedMemberIndex
                .Values
                .DefaultIfEmpty(0d)
                .Max();

            foreach (var outputStackMember in outputMembersByIndex.Values
                .OrderBy(stackMember => stackMember.MemberIndex))
            {
                var generatedStartSetting = outputStackMember.PlanarParts?[0];
                if (generatedStartSetting?.PointList == null ||
                    generatedStartSetting.PointList.Count < 2)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "始端垂直短搭接后处理无法读取第 {0} 层首段。",
                        outputStackMember.MemberIndex + 1);
                    return false;
                }

                var generatedStartPoint = generatedStartSetting.PointList[0];
                var generatedAdjacentPoint = generatedStartSetting.PointList[1];
                var generatedStartDirection = new Vector3d(
                    generatedStartPoint,
                    generatedAdjacentPoint);
                if (generatedStartDirection.Norm <= GeometryTolerance)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "始端垂直短搭接后处理检测到第 {0} 层首段长度为零。",
                        outputStackMember.MemberIndex + 1);
                    return false;
                }

                var normalizedGeneratedStartDirection =
                    generatedStartDirection.Normalized;
                if (normalizedGeneratedStartDirection.Dot(
                        sharedGeneratedStartDirection) < 1d - 1e-6d)
                {
                    continue;
                }

                var generatedStartPointCoordinate =
                    GetPointCoordinateAlongDirection(
                        generatedStartPoint,
                        sharedGeneratedStartDirection);
                var targetStartEndpointAdvanceDistance =
                    sharedTargetStartEndpointCoordinate -
                    generatedStartPointCoordinate;
                if (Math.Abs(targetStartEndpointAdvanceDistance) <= GeometryTolerance)
                {
                    continue;
                }

                var requiredReserve = requiredReserveByGeneratedMemberIndex
                    .TryGetValue(
                        outputStackMember.MemberIndex,
                        out var memberRequiredReserve)
                    ? memberRequiredReserve
                    : fallbackRequiredReserve;
                var minimumRemainingLength = Math.Max(
                    GeometryTolerance,
                    requiredReserve);
                if (generatedStartDirection.Norm <= minimumRemainingLength)
                {
                    continue;
                }

                var maximumPositiveAdvance = generatedStartDirection.Norm -
                    minimumRemainingLength;
                if (targetStartEndpointAdvanceDistance > maximumPositiveAdvance)
                {
                    targetStartEndpointAdvanceDistance = maximumPositiveAdvance;
                }
                if (Math.Abs(targetStartEndpointAdvanceDistance) <= GeometryTolerance)
                {
                    continue;
                }

                if (targetStartEndpointAdvanceDistance > 0d &&
                    targetStartEndpointAdvanceDistance >=
                    generatedStartDirection.Norm - GeometryTolerance)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "始端垂直短搭接后处理会消除第 {0} 层首段。",
                        outputStackMember.MemberIndex + 1);
                    return false;
                }

                generatedStartSetting.PointList[0] = Translate(
                    generatedStartPoint,
                    normalizedGeneratedStartDirection,
                    targetStartEndpointAdvanceDistance);
            }

            return true;
        }

        private static double GetPointCoordinateAlongDirection(
            Point3d sourcePoint,
            Vector3d normalizedDirection)
        {
            return sourcePoint.X * normalizedDirection.X +
                sourcePoint.Y * normalizedDirection.Y +
                sourcePoint.Z * normalizedDirection.Z;
        }

        private static bool TryCreatePhysicalTerminalPlacement(
            LinkageRouteCenterlineConversionResult ordinaryConversionResult,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement sourceTerminalPlacement,
            bool useStartTerminal,
            out LinkageRouteStackTerminalPlacement physicalTerminalPlacement,
            out string failureReason)
        {
            physicalTerminalPlacement = null;
            failureReason = null;
            if (ordinaryConversionResult?.StackMembers == null ||
                ordinaryConversionResult.StackMembers.Count == 0 ||
                terminalAssembly == null || sourceTerminalPlacement == null)
            {
                failureReason = (useStartTerminal ? "始端" : "柜顶") +
                    "无垫块后处理缺少普通实际叠排或端部层位。";
                return false;
            }

            var orderedOrdinaryStackMembers = ordinaryConversionResult
                .StackMembers
                .OrderBy(stackMember => stackMember.MemberIndex)
                .ToList();
            if (orderedOrdinaryStackMembers
                .Where((stackMember, orderedIndex) =>
                    stackMember == null ||
                    stackMember.MemberIndex != orderedIndex ||
                    stackMember.PlanarParts == null ||
                    stackMember.PlanarParts.Count == 0)
                .Any())
            {
                failureReason = (useStartTerminal ? "始端" : "柜顶") +
                    "无垫块后处理检测到不连续的普通叠排成员索引。";
                return false;
            }

            var physicalGeneratedMemberCoordinates = new double[
                orderedOrdinaryStackMembers.Count];
            foreach (var ordinaryStackMember in orderedOrdinaryStackMembers)
            {
                if (useStartTerminal && terminalAssembly.Members.Count == 1 &&
                    sourceTerminalPlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex.HasValue &&
                    ordinaryConversionResult.StartTerminalMemberIndices.Count > 0 &&
                    !ordinaryConversionResult.StartTerminalMemberIndices.Contains(ordinaryStackMember.MemberIndex))
                {
                    physicalGeneratedMemberCoordinates[ordinaryStackMember.MemberIndex] =
                        sourceTerminalPlacement.BaseCenterOffset + (ordinaryStackMember.MemberIndex <
                            sourceTerminalPlacement.GeneratedMemberOffsets.Count
                            ? sourceTerminalPlacement.GeneratedMemberOffsets[ordinaryStackMember.MemberIndex] : 0d);
                    continue;
                }
                var terminalPlanarPart = useStartTerminal
                    ? ordinaryStackMember.PlanarParts.FirstOrDefault()
                    : ordinaryStackMember.PlanarParts.LastOrDefault();
                if (terminalPlanarPart?.PointList == null ||
                    terminalPlanarPart.PointList.Count < 2)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0}无垫块后处理无法读取第 {1} 层的实际端段。",
                        useStartTerminal ? "始端" : "柜顶",
                        ordinaryStackMember.MemberIndex + 1);
                    return false;
                }

                var actualTerminalEndpoint = useStartTerminal
                    ? terminalPlanarPart.PointList[0]
                    : terminalPlanarPart.PointList[
                        terminalPlanarPart.PointList.Count - 1];
                if (!LinkageRouteStackPlacementSolver
                        .TryMeasureBaseCenterOffset(
                            terminalAssembly,
                            actualTerminalEndpoint,
                            out var physicalGeneratedMemberCoordinate))
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0}无垫块后处理无法测量第 {1} 层的实际厚向坐标。",
                        useStartTerminal ? "始端" : "柜顶",
                        ordinaryStackMember.MemberIndex + 1);
                    return false;
                }

                physicalGeneratedMemberCoordinates[
                    ordinaryStackMember.MemberIndex] =
                    physicalGeneratedMemberCoordinate;
            }

            var physicalDoubleStartBridgeSideSign = sourceTerminalPlacement
                .DoubleStartHorizontalBridgeSideSign;
            if (useStartTerminal && sourceTerminalPlacement
                    .DoubleStartHorizontalBridgeGeneratedMemberIndex
                    .HasValue)
            {
                var bridgeGeneratedMemberIndex = sourceTerminalPlacement
                    .DoubleStartHorizontalBridgeGeneratedMemberIndex.Value;
                if (bridgeGeneratedMemberIndex < 0 ||
                    bridgeGeneratedMemberIndex >=
                        physicalGeneratedMemberCoordinates.Length ||
                    !LinkageRouteTerminalAccessoryBuilder
                        .TryGetConnectionFixedBusbar(
                            terminalAssembly,
                            out var collectiveStartFixedBusbar) ||
                    collectiveStartFixedBusbar?.PointList == null ||
                    collectiveStartFixedBusbar.PointList.Count == 0 ||
                    !LinkageRouteStackPlacementSolver
                        .TryMeasureBaseCenterOffset(
                            terminalAssembly,
                            collectiveStartFixedBusbar.PointList[0],
                            out var collectiveFixedCenterCoordinate) ||
                    Math.Abs(
                        physicalGeneratedMemberCoordinates[
                            bridgeGeneratedMemberIndex] -
                        collectiveFixedCenterCoordinate) <= GeometryTolerance)
                {
                    failureReason =
                        "始端无垫块后处理无法还原双始端横向桥排的物理搭接侧。";
                    return false;
                }

                // 普通 Expand 保存的是局部排厚方向下的 sign；后处理改用
                // 固定端物理厚向坐标后必须重新测量，不能直接沿用局部符号。
                physicalDoubleStartBridgeSideSign =
                    physicalGeneratedMemberCoordinates[
                        bridgeGeneratedMemberIndex] >
                    collectiveFixedCenterCoordinate
                        ? 1
                        : -1;
            }

            physicalTerminalPlacement = new LinkageRouteStackTerminalPlacement
            {
                Signature = sourceTerminalPlacement.Signature,
                // 后处理直接以每根普通实际排测得的物理坐标为准；零基准
                // 避免再次猜测普通路径当时选择的基准中心线和成员映射。
                BaseCenterOffset = 0d,
                GeneratedMemberOffsets = physicalGeneratedMemberCoordinates,
                DoubleStartHorizontalBridgeGeneratedMemberIndex =
                    sourceTerminalPlacement
                        .DoubleStartHorizontalBridgeGeneratedMemberIndex,
                DoubleStartHorizontalBridgeSideSign =
                    physicalDoubleStartBridgeSideSign,
                DoubleStartHorizontalBridgeThickness =
                    sourceTerminalPlacement
                        .DoubleStartHorizontalBridgeThickness,
                DoubleStartHorizontalBridgePlacements =
                    (sourceTerminalPlacement
                            .DoubleStartHorizontalBridgePlacements ??
                        new List<LinkageRouteTerminalBridgePlacement>())
                    .Select(bridgePlacement => new LinkageRouteTerminalBridgePlacement
                    {
                        GeneratedMemberIndex = bridgePlacement.GeneratedMemberIndex,
                        SideSign = bridgePlacement.SideSign,
                        Thickness = bridgePlacement.Thickness
                    }).ToList(),
                Contacts = CopyTerminalContacts(
                    sourceTerminalPlacement.Contacts)
            };
            return true;
        }

        private static LinkageRouteStackTerminalPlacement CreateTerminalPlacementMemberSubset(
            LinkageRouteStackTerminalPlacement sourcePlacement, IReadOnlyList<int> actualMemberIndices)
        {
            var memberIndexMap = actualMemberIndices.Select((globalIndex, localIndex) => new { globalIndex, localIndex })
                .ToDictionary(member => member.globalIndex, member => member.localIndex);
            var subsetPlacement = RemapTerminalPlacementMembers(sourcePlacement, memberIndexMap);
            subsetPlacement.GeneratedMemberOffsets = actualMemberIndices.Select(index => sourcePlacement.GeneratedMemberOffsets[index]).ToList();
            return subsetPlacement;
        }

        private static LinkageRouteStackTerminalPlacement RestoreTerminalPlacementMemberSubset(
            LinkageRouteStackTerminalPlacement originalPlacement, LinkageRouteStackTerminalPlacement subsetPlacement,
            IReadOnlyList<int> actualMemberIndices)
        {
            var memberIndexMap = actualMemberIndices.Select((globalIndex, localIndex) => new { globalIndex, localIndex })
                .ToDictionary(member => member.localIndex, member => member.globalIndex);
            var restoredPlacement = RemapTerminalPlacementMembers(subsetPlacement, memberIndexMap);
            restoredPlacement.GeneratedMemberOffsets = originalPlacement.GeneratedMemberOffsets.ToList();
            for (var localIndex = 0; localIndex < actualMemberIndices.Count; localIndex++)
                restoredPlacement.GeneratedMemberOffsets[actualMemberIndices[localIndex]] = subsetPlacement.BaseCenterOffset +
                    subsetPlacement.GeneratedMemberOffsets[localIndex] - originalPlacement.BaseCenterOffset;
            restoredPlacement.BaseCenterOffset = originalPlacement.BaseCenterOffset;
            return restoredPlacement;
        }

        private static LinkageRouteStackTerminalPlacement RemapTerminalPlacementMembers(
            LinkageRouteStackTerminalPlacement sourcePlacement, IReadOnlyDictionary<int, int> memberIndexMap)
        {
            var mappedPlacement = CopyTerminalPlacement(sourcePlacement);
            mappedPlacement.Contacts = mappedPlacement.Contacts.Where(contact => memberIndexMap.ContainsKey(contact.GeneratedMemberIndex)).ToList();
            foreach (var contact in mappedPlacement.Contacts) contact.GeneratedMemberIndex = memberIndexMap[contact.GeneratedMemberIndex];
            mappedPlacement.DoubleStartHorizontalBridgePlacements = mappedPlacement.DoubleStartHorizontalBridgePlacements
                .Where(bridge => memberIndexMap.ContainsKey(bridge.GeneratedMemberIndex)).ToList();
            foreach (var bridge in mappedPlacement.DoubleStartHorizontalBridgePlacements) bridge.GeneratedMemberIndex = memberIndexMap[bridge.GeneratedMemberIndex];
            mappedPlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex = sourcePlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex.HasValue &&
                memberIndexMap.TryGetValue(sourcePlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex.Value, out var mappedBridgeIndex)
                    ? mappedBridgeIndex : (int?)null;
            mappedPlacement.SpacerlessMovedGeneratedMemberIndices = mappedPlacement.SpacerlessMovedGeneratedMemberIndices
                .Where(memberIndexMap.ContainsKey).Select(index => memberIndexMap[index]).ToList();
            return mappedPlacement;
        }

        private static LinkageRouteStackTerminalPlacement
            CreateSpacerlessPackingSourcePlacement(
            LinkageRouteStackTerminalPlacement ordinaryPhysicalPlacement,
            bool restoreDoubleStartHorizontalBridgeInsertion)
        {
            var packingSourcePlacement = CopyTerminalPlacement(
                ordinaryPhysicalPlacement);
            packingSourcePlacement.SpacerlessMovedGeneratedMemberIndices
                .Clear();
            if (!restoreDoubleStartHorizontalBridgeInsertion ||
                !packingSourcePlacement
                    .DoubleStartHorizontalBridgeGeneratedMemberIndex
                    .HasValue ||
                packingSourcePlacement.DoubleStartHorizontalBridgeThickness <=
                    GeometryTolerance)
            {
                return packingSourcePlacement;
            }

            var bridgeGeneratedMemberIndex = packingSourcePlacement
                .DoubleStartHorizontalBridgeGeneratedMemberIndex.Value;
            if (bridgeGeneratedMemberIndex >= 0 &&
                bridgeGeneratedMemberIndex < packingSourcePlacement
                    .GeneratedMemberOffsets.Count)
            {
                // 普通横向桥排在夹层求解后把搭接成员向外移动了一根桥排
                // 厚度。无垫块闭缝必须先回到插桥前的真实夹层，再由原侧
                // 重新插桥；否则会把桥排本身当成待删除空隙压掉。
                packingSourcePlacement.GeneratedMemberOffsets[
                    bridgeGeneratedMemberIndex] -=
                    packingSourcePlacement
                        .DoubleStartHorizontalBridgeSideSign *
                    packingSourcePlacement
                        .DoubleStartHorizontalBridgeThickness;
            }

            return packingSourcePlacement;
        }

        /// <summary>
        /// 以普通方案的逐成员铜排为唯一几何源，只移动需要贴合的端段。
        /// 单段 I 排发生刚性平移时，把同一世界位移沿既有平面排搭接链传播，
        /// 保留双始端 I/L 桥的成员映射、逐根裁切和所有中间段位置。
        /// </summary>
        private static bool TryMoveOrdinaryStackMemberTerminalSegments(
            LinkageRouteCenterlineConversionResult outputConversionResult,
            LinkageRouteCenterlineConversionResult ordinaryConversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            LinkageRouteStackTerminalPlacement ordinaryStartTerminalPlacement,
            LinkageRouteStackTerminalPlacement outputStartTerminalPlacement,
            LinkageTerminalAssembly endTerminalAssembly,
            LinkageRouteStackTerminalPlacement ordinaryEndTerminalPlacement,
            LinkageRouteStackTerminalPlacement outputEndTerminalPlacement,
            out string failureReason)
        {
            failureReason = null;
            if (outputConversionResult == null ||
                ordinaryConversionResult?.StackMembers == null ||
                ordinaryConversionResult.StackMembers.Count == 0 ||
                ordinaryConversionResult.StackMembers.Any(stackMember =>
                    stackMember == null) ||
                startTerminalAssembly?.ThicknessDirection == null ||
                endTerminalAssembly?.ThicknessDirection == null ||
                ordinaryStartTerminalPlacement?.GeneratedMemberOffsets == null ||
                outputStartTerminalPlacement?.GeneratedMemberOffsets == null ||
                ordinaryEndTerminalPlacement?.GeneratedMemberOffsets == null ||
                outputEndTerminalPlacement?.GeneratedMemberOffsets == null)
            {
                failureReason =
                    "端头无垫块后处理无法匹配普通方案的实际叠排层位。";
                return false;
            }

            var ordinaryMembersByIndex = ordinaryConversionResult.StackMembers
                .GroupBy(stackMember => stackMember.MemberIndex)
                .ToDictionary(
                    stackMemberGroup => stackMemberGroup.Key,
                    stackMemberGroup => stackMemberGroup.First());
            var generatedMemberCount = ordinaryMembersByIndex.Count;
            var startTerminalMemberIndices = ordinaryConversionResult
                .StartTerminalMemberIndices;
            var endTerminalMemberIndices = ordinaryConversionResult
                .EndTerminalMemberIndices;
            if (ordinaryMembersByIndex.Count !=
                    ordinaryConversionResult.StackMembers.Count ||
                Enumerable.Range(0, generatedMemberCount).Any(memberIndex =>
                    !ordinaryMembersByIndex.ContainsKey(memberIndex)) ||
                startTerminalMemberIndices.Any(memberIndex =>
                    memberIndex < 0 ||
                    memberIndex >= ordinaryStartTerminalPlacement
                        .GeneratedMemberOffsets.Count ||
                    memberIndex >= outputStartTerminalPlacement
                        .GeneratedMemberOffsets.Count) ||
                endTerminalMemberIndices.Any(memberIndex =>
                    memberIndex < 0 ||
                    memberIndex >= ordinaryEndTerminalPlacement
                        .GeneratedMemberOffsets.Count ||
                    memberIndex >= outputEndTerminalPlacement
                        .GeneratedMemberOffsets.Count))
            {
                failureReason =
                    "端头无垫块后处理检测到重复或不一致的叠排成员索引。";
                return false;
            }

            var preservedOutputMembers = new List<LinkageStackedRouteMember>();
            var preservedActualCopperLength = 0d;
            foreach (var ordinaryStackMember in ordinaryMembersByIndex.Values
                .OrderBy(stackMember => stackMember.MemberIndex))
            {
                if (ordinaryStackMember?.PlanarParts == null ||
                    ordinaryStackMember.PlanarParts.Count == 0)
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "端头无垫块后处理无法匹配第 {0} 层的逐成员平面铜排。",
                        ordinaryStackMember.MemberIndex + 1);
                    return false;
                }

                var generatedMemberIndex = ordinaryStackMember.MemberIndex;
                var startTerminalWorldDisplacement = new Vector3d(0d, 0d, 0d);
                // 同规格转换沿用旧结构时没有显式记录端部成员集合，空集合
                // 表示所有叠排成员都贯穿到该端；异规格 2↔3 才使用非空集合
                // 精确限定实际存在于端段的成员。
                if (startTerminalMemberIndices.Count == 0 ||
                    startTerminalMemberIndices.Contains(generatedMemberIndex))
                {
                    var ordinaryStartPhysicalCoordinate =
                        ordinaryStartTerminalPlacement.BaseCenterOffset +
                        ordinaryStartTerminalPlacement.GeneratedMemberOffsets[
                            generatedMemberIndex];
                    var outputStartPhysicalCoordinate =
                        outputStartTerminalPlacement.BaseCenterOffset +
                        outputStartTerminalPlacement.GeneratedMemberOffsets[
                            generatedMemberIndex];
                    startTerminalWorldDisplacement = Scale(
                        startTerminalAssembly.ThicknessDirection.Normalized,
                        outputStartPhysicalCoordinate -
                            ordinaryStartPhysicalCoordinate);
                }

                var endTerminalWorldDisplacement = new Vector3d(0d, 0d, 0d);
                if (endTerminalMemberIndices.Count == 0 ||
                    endTerminalMemberIndices.Contains(generatedMemberIndex))
                {
                    var ordinaryEndPhysicalCoordinate =
                        ordinaryEndTerminalPlacement.BaseCenterOffset +
                        ordinaryEndTerminalPlacement.GeneratedMemberOffsets[
                            generatedMemberIndex];
                    var outputEndPhysicalCoordinate =
                        outputEndTerminalPlacement.BaseCenterOffset +
                        outputEndTerminalPlacement.GeneratedMemberOffsets[
                            generatedMemberIndex];
                    endTerminalWorldDisplacement = Scale(
                        endTerminalAssembly.ThicknessDirection.Normalized,
                        outputEndPhysicalCoordinate -
                            ordinaryEndPhysicalCoordinate);
                }

                if (!TryMoveOrdinaryMemberTerminalChain(
                        ordinaryStackMember,
                        startTerminalWorldDisplacement,
                        endTerminalWorldDisplacement,
                        out var preservedStackMember,
                        out var memberTerminalMoveFailureReason))
                {
                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "端头无垫块后处理第 {0} 层失败：{1}",
                        generatedMemberIndex + 1,
                        memberTerminalMoveFailureReason);
                    return false;
                }

                foreach (var preservedPlanarPart in
                    preservedStackMember.PlanarParts)
                {
                    preservedActualCopperLength +=
                        preservedPlanarPart.GetDevelopedLength();
                }

                preservedOutputMembers.Add(preservedStackMember);
            }

            outputConversionResult.StackMembers.Clear();
            foreach (var preservedOutputMember in preservedOutputMembers)
            {
                outputConversionResult.StackMembers.Add(preservedOutputMember);
            }

            outputConversionResult.ActualCopperLength =
                preservedActualCopperLength;
            return true;
        }

        internal static bool TryMoveOrdinaryMemberTerminalChain(
            LinkageStackedRouteMember ordinaryStackMember,
            Vector3d startTerminalWorldDisplacement,
            Vector3d endTerminalWorldDisplacement,
            out LinkageStackedRouteMember movedStackMember,
            out string failureReason)
        {
            movedStackMember = null;
            failureReason = null;
            if (ordinaryStackMember?.PlanarParts == null ||
                ordinaryStackMember.PlanarParts.Count == 0 ||
                ordinaryStackMember.PlanarParts.Any(planarPart =>
                    planarPart?.PointList == null ||
                    planarPart.PointList.Count < 2))
            {
                failureReason = "普通方案包含无效的逐成员平面铜排。";
                return false;
            }

            var segmentOffsetDistancesByPart = ordinaryStackMember.PlanarParts
                .Select(planarPart => new double[
                    planarPart.PointList.Count - 1])
                .ToList();
            var assignedWorldDisplacementsByPart = ordinaryStackMember
                .PlanarParts
                .Select(planarPart => new Vector3d[
                    planarPart.PointList.Count - 1])
                .ToList();
            var assignedSegmentsByPart = ordinaryStackMember.PlanarParts
                .Select(planarPart => new bool[
                    planarPart.PointList.Count - 1])
                .ToList();
            if (!TryAssignTerminalChainDisplacement(
                    ordinaryStackMember.PlanarParts,
                    segmentOffsetDistancesByPart,
                    assignedWorldDisplacementsByPart,
                    assignedSegmentsByPart,
                    true,
                    startTerminalWorldDisplacement,
                    out failureReason) ||
                !TryAssignTerminalChainDisplacement(
                    ordinaryStackMember.PlanarParts,
                    segmentOffsetDistancesByPart,
                    assignedWorldDisplacementsByPart,
                    assignedSegmentsByPart,
                    false,
                    endTerminalWorldDisplacement,
                    out failureReason))
            {
                return false;
            }

            movedStackMember = new LinkageStackedRouteMember
            {
                MemberIndex = ordinaryStackMember.MemberIndex,
                CenterlineOffset = ordinaryStackMember.CenterlineOffset
            };
            for (var planarPartIndex = 0;
                planarPartIndex < ordinaryStackMember.PlanarParts.Count;
                planarPartIndex++)
            {
                var ordinaryPlanarPart = ordinaryStackMember.PlanarParts[
                    planarPartIndex];
                var movedPlanarPart = ordinaryPlanarPart.Copy();
                var segmentOffsetDistances =
                    segmentOffsetDistancesByPart[planarPartIndex];
                if (segmentOffsetDistances.Any(segmentOffsetDistance =>
                        Math.Abs(segmentOffsetDistance) > GeometryTolerance))
                {
                    if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(
                            ordinaryPlanarPart,
                            segmentOffsetDistances,
                            out var movedMemberCenterlinePoints,
                            out var terminalMoveFailureReason))
                    {
                        movedStackMember = null;
                        failureReason = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "第 {0} 根铜排端段移动失败：{1}",
                            planarPartIndex + 1,
                            terminalMoveFailureReason);
                        return false;
                    }

                    movedPlanarPart.PointList = movedMemberCenterlinePoints;
                }

                movedStackMember.PlanarParts.Add(movedPlanarPart);
            }

            return true;
        }

        private static bool TryAssignTerminalChainDisplacement(
            IList<PlanarBendBusbarSetting> ordinaryPlanarParts,
            IList<double[]> segmentOffsetDistancesByPart,
            IList<Vector3d[]> assignedWorldDisplacementsByPart,
            IList<bool[]> assignedSegmentsByPart,
            bool propagateFromStart,
            Vector3d terminalWorldDisplacement,
            out string failureReason)
        {
            failureReason = null;
            for (var propagationIndex = 0;
                propagationIndex < ordinaryPlanarParts.Count;
                propagationIndex++)
            {
                var planarPartIndex = propagateFromStart
                    ? propagationIndex
                    : ordinaryPlanarParts.Count - 1 - propagationIndex;
                var ordinaryPlanarPart = ordinaryPlanarParts[planarPartIndex];
                var segmentCount = ordinaryPlanarPart.PointList.Count - 1;
                var assignedSegmentIndices =
                    GetTerminalRigidSegmentIndices(
                        ordinaryPlanarPart,
                        propagateFromStart,
                        out var planarPartCanAbsorbTerminalDisplacement);
                foreach (var assignedSegmentIndex in assignedSegmentIndices)
                {
                    if (!TryGetLocalThicknessDirection(
                            ordinaryPlanarPart,
                            assignedSegmentIndex,
                            out var localThicknessDirection))
                    {
                        failureReason =
                            (propagateFromStart ? "始端" : "柜顶") +
                            "搭接链无法建立局部排厚方向。";
                        return false;
                    }

                    var normalizedLocalThicknessDirection =
                        localThicknessDirection.Normalized;
                    if (terminalWorldDisplacement.Norm > GeometryTolerance &&
                        Math.Abs(terminalWorldDisplacement.Normalized.Dot(
                            normalizedLocalThicknessDirection)) <
                            1d - GeometryTolerance)
                    {
                        failureReason =
                            (propagateFromStart ? "始端" : "柜顶") +
                            "目标厚向位移与普通铜排端段排厚方向不平行。";
                        return false;
                    }

                    if (assignedSegmentsByPart[planarPartIndex][
                            assignedSegmentIndex])
                    {
                        var assignedWorldDisplacement =
                            assignedWorldDisplacementsByPart[planarPartIndex][
                                assignedSegmentIndex];
                        if (Subtract(
                                assignedWorldDisplacement,
                                terminalWorldDisplacement).Norm >
                            GeometryTolerance)
                        {
                            failureReason =
                                "同一根 I 排搭接链的始末端需要不同刚性位移。";
                            return false;
                        }
                    }
                    else
                    {
                        assignedSegmentsByPart[planarPartIndex][
                            assignedSegmentIndex] = true;
                        assignedWorldDisplacementsByPart[planarPartIndex][
                            assignedSegmentIndex] = Copy(
                                terminalWorldDisplacement);
                        segmentOffsetDistancesByPart[planarPartIndex][
                            assignedSegmentIndex] =
                            terminalWorldDisplacement.Dot(
                                normalizedLocalThicknessDirection);
                    }
                }

                // 只有真实非共线转角才能在本排内完成端部层位过渡；带有
                // 共线中间点的 I 排仍须整根刚性平移并继续传播。
                if (planarPartCanAbsorbTerminalDisplacement)
                {
                    break;
                }
            }

            return true;
        }

        private static IReadOnlyList<int> GetTerminalRigidSegmentIndices(
            PlanarBendBusbarSetting planarPart,
            bool propagateFromStart,
            out bool hasAbsorbingNonCollinearTurn)
        {
            hasAbsorbingNonCollinearTurn = false;
            var rigidSegmentIndices = new List<int>();
            if (planarPart?.PointList == null ||
                planarPart.PointList.Count < 2)
            {
                return rigidSegmentIndices;
            }

            var segmentCount = planarPart.PointList.Count - 1;
            var precedingSegmentIndex = propagateFromStart
                ? 0
                : segmentCount - 1;
            rigidSegmentIndices.Add(precedingSegmentIndex);
            for (var propagationIndex = 1;
                propagationIndex < segmentCount;
                propagationIndex++)
            {
                var followingSegmentIndex = propagateFromStart
                    ? propagationIndex
                    : segmentCount - 1 - propagationIndex;
                var precedingSegmentDirection = new Vector3d(
                    planarPart.PointList[precedingSegmentIndex],
                    planarPart.PointList[precedingSegmentIndex + 1]);
                var followingSegmentDirection = new Vector3d(
                    planarPart.PointList[followingSegmentIndex],
                    planarPart.PointList[followingSegmentIndex + 1]);
                if (precedingSegmentDirection.Norm <= GeometryTolerance ||
                    followingSegmentDirection.Norm <= GeometryTolerance)
                {
                    rigidSegmentIndices.Add(followingSegmentIndex);
                    precedingSegmentIndex = followingSegmentIndex;
                    continue;
                }

                if (Math.Abs(precedingSegmentDirection.Normalized.Dot(
                        followingSegmentDirection.Normalized)) <
                    1d - GeometryTolerance)
                {
                    hasAbsorbingNonCollinearTurn = true;
                    break;
                }

                rigidSegmentIndices.Add(followingSegmentIndex);
                precedingSegmentIndex = followingSegmentIndex;
            }

            return rigidSegmentIndices;
        }

        private static LinkageRouteStackTerminalPlacement CopyTerminalPlacement(
            LinkageRouteStackTerminalPlacement sourceTerminalPlacement)
        {
            return sourceTerminalPlacement == null
                ? null
                : new LinkageRouteStackTerminalPlacement
                {
                    Signature = sourceTerminalPlacement.Signature,
                    BaseCenterOffset = sourceTerminalPlacement.BaseCenterOffset,
                    GeneratedMemberOffsets = sourceTerminalPlacement
                        .GeneratedMemberOffsets.ToList(),
                    Contacts = CopyTerminalContacts(
                        sourceTerminalPlacement.Contacts),
                    SpacerlessMovedGeneratedMemberIndices =
                        (sourceTerminalPlacement
                                .SpacerlessMovedGeneratedMemberIndices ??
                                new List<int>())
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
                        (sourceTerminalPlacement
                                .DoubleStartHorizontalBridgePlacements ??
                            new List<LinkageRouteTerminalBridgePlacement>())
                        .Select(bridgePlacement => new LinkageRouteTerminalBridgePlacement
                        {
                            GeneratedMemberIndex = bridgePlacement.GeneratedMemberIndex,
                            SideSign = bridgePlacement.SideSign,
                            Thickness = bridgePlacement.Thickness
                        }).ToList()
                };
        }

        private static IList<LinkageRouteStackTerminalContact>
            CopyTerminalContacts(
            IEnumerable<LinkageRouteStackTerminalContact> sourceContacts)
        {
            return (sourceContacts ??
                    Enumerable.Empty<LinkageRouteStackTerminalContact>())
                .Where(sourceContact => sourceContact != null)
                .Select(sourceContact =>
                    new LinkageRouteStackTerminalContact
                    {
                        GeneratedMemberIndex =
                            sourceContact.GeneratedMemberIndex,
                        FixedMemberIndex = sourceContact.FixedMemberIndex
                    })
                .ToList();
        }

        internal static int CountActualPlanarTransitions(
            IList<Point3d> centerlinePoints,
            Vector3d initialThicknessDirection)
        {
            return GetActualPlanarTransitionPointIndices(
                centerlinePoints,
                initialThicknessDirection).Count;
        }

        internal static IReadOnlyList<int> GetActualPlanarTransitionPointIndices(
            IList<Point3d> centerlinePoints,
            Vector3d initialThicknessDirection)
        {
            var normalizedCenterline = RemoveRepeatedPoints(centerlinePoints);
            if (normalizedCenterline.Count < 3 ||
                initialThicknessDirection == null ||
                initialThicknessDirection.Norm < GeometryTolerance ||
                !HasValidSegments(normalizedCenterline))
            {
                return Array.Empty<int>();
            }

            var precedingSegmentDirection = new Vector3d(
                normalizedCenterline[0],
                normalizedCenterline[1]).Normalized;
            var currentWidthDirection = initialThicknessDirection.Normalized
                .Cross(precedingSegmentDirection);
            if (currentWidthDirection == null ||
                currentWidthDirection.Norm < GeometryTolerance)
            {
                return Array.Empty<int>();
            }

            currentWidthDirection = currentWidthDirection.Normalized;
            var actualTransitionPointIndices = new List<int>();
            for (var segmentIndex = 1;
                segmentIndex < normalizedCenterline.Count - 1;
                segmentIndex++)
            {
                var nextSegmentDirection = new Vector3d(
                    normalizedCenterline[segmentIndex],
                    normalizedCenterline[segmentIndex + 1]).Normalized;
                if (Math.Abs(nextSegmentDirection.Dot(currentWidthDirection)) <=
                    GeometryTolerance)
                {
                    precedingSegmentDirection = nextSegmentDirection;
                    continue;
                }

                actualTransitionPointIndices.Add(segmentIndex);
                var transitionThicknessDirection = precedingSegmentDirection
                    .Cross(currentWidthDirection);
                if (transitionThicknessDirection == null ||
                    transitionThicknessDirection.Norm < GeometryTolerance)
                {
                    return actualTransitionPointIndices;
                }

                currentWidthDirection = transitionThicknessDirection.Normalized
                    .Cross(nextSegmentDirection).Normalized;
                precedingSegmentDirection = nextSegmentDirection;
            }

            return actualTransitionPointIndices;
        }

        private static LinkageRouteCenterlineConversionResult ConvertWithTransitionSigns(
            IList<Point3d> centerlinePoints,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<int> transitionSigns)
        {
            return ConvertWithTransitionSignsCore(
                centerlinePoints,
                routeRequest,
                routeGroupRequest,
                transitionSigns,
                CreateInvariantContext(
                    centerlinePoints,
                    routeRequest,
                    routeGroupRequest));
        }

        private static LinkageRouteCenterlineConversionResult ConvertWithTransitionSignsCore(
            IList<Point3d> centerlinePoints,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyList<int> transitionSigns,
            PlanarConversionInvariantContext invariantContext)
        {
            var conversionResult = new LinkageRouteCenterlineConversionResult();
            if (centerlinePoints == null || centerlinePoints.Count < 2 ||
                routeRequest == null || routeGroupRequest == null)
            {
                conversionResult.Diagnostics.Add("完整中心线、请求和逻辑组不能为空。");
                return conversionResult;
            }

            var normalizedCenterline = RemoveRepeatedPoints(centerlinePoints);
            if (normalizedCenterline.Count < 2 || !HasValidSegments(normalizedCenterline))
            {
                conversionResult.Diagnostics.Add("待转换中心线包含零长度或无效线段。");
                return conversionResult;
            }

            // Candidate path geometry is always built from one stable
            // specification.  Section-specific start/middle dimensions are
            // applied later while materializing the copper bars and their lap
            // regions; they must not alter path enumeration or turn points.
            var busbarWidth = routeGroupRequest.GetCabinetTopLapBusbarWidth(
                routeRequest);
            var singleBusbarThickness = routeGroupRequest
                .GetCabinetTopLapBusbarThickness(routeRequest);
            var usesSectionSpecificBusbarSettings = routeGroupRequest
                .HasSectionSpecificBusbarSettings(routeRequest);
            // Json 中间排规格相同并不代表固定始端/柜顶排厚度相同。
            // 这类端部厚差仍走普通路径搜索。先构造内部层位，再把已求得
            // 的真实端部层位沿刚性I链传到非I折弯吸收；不独立移动接口。
            // 没有吸收转角的纯I链沿用原端部支撑和完整后处理。
            var allowTerminalThicknessLayerSupport =
                !usesSectionSpecificBusbarSettings &&
                (routeGroupRequest.StartTerminal.Members.Any(member =>
                    member?.FixedBusbar != null &&
                    Math.Abs(member.FixedBusbar.BusBarThickness -
                        singleBusbarThickness) > GeometryTolerance) ||
                 routeGroupRequest.EndTerminal.Members.Any(member =>
                    member?.FixedBusbar != null &&
                    Math.Abs(member.FixedBusbar.BusBarThickness -
                        singleBusbarThickness) > GeometryTolerance));
            var physicalSectionCount = CountActualPlanarTransitions(
                normalizedCenterline,
                routeGroupRequest.StartTerminal.ThicknessDirection) + 1;
            var stackBendAllowance = LinkageRouteStackGeometry.GetStackBendAllowance(
                singleBusbarThickness,
                routeGroupRequest.GetStackCount(routeRequest),
                routeGroupRequest.StackMiddleSpace,
                routeRequest.GetEffectiveBendAllowance());
            var firstSegmentDirection = new Vector3d(
                normalizedCenterline[0], normalizedCenterline[1]).Normalized;
            var initialThicknessDirection = routeGroupRequest.StartTerminal.ThicknessDirection.Normalized;
            var initialWidthDirection = initialThicknessDirection.Cross(firstSegmentDirection);
            if (initialWidthDirection == null || initialWidthDirection.Norm < GeometryTolerance)
            {
                conversionResult.Diagnostics.Add("始端排厚方向不能与首段排长方向平行。");
                return conversionResult;
            }

            var currentWidthDirection = initialWidthDirection.Normalized;
            var currentStartThicknessDirection = initialThicknessDirection;
            var accumulatedTransitionOffset = new Vector3d(0d, 0d, 0d);
            var currentPartPoints = new List<Point3d>
            {
                Copy(normalizedCenterline[0]),
                Copy(normalizedCenterline[1])
            };
            var appliedTransitionIndex = 0;

            for (var segmentIndex = 1; segmentIndex < normalizedCenterline.Count - 1; segmentIndex++)
            {
                var sourceSegmentStart = normalizedCenterline[segmentIndex];
                var sourceSegmentEnd = normalizedCenterline[segmentIndex + 1];
                var nextSegmentDirection = new Vector3d(
                    sourceSegmentStart, sourceSegmentEnd).Normalized;
                var isFinalSourceSegment =
                    segmentIndex == normalizedCenterline.Count - 2;
                var translatedSegmentEnd = TranslateWithTerminalLongitudinalAnchor(
                    sourceSegmentEnd,
                    accumulatedTransitionOffset,
                    nextSegmentDirection,
                    isFinalSourceSegment);

                if (Math.Abs(nextSegmentDirection.Dot(currentWidthDirection)) <= GeometryTolerance)
                {
                    AddPoint(currentPartPoints, translatedSegmentEnd);
                    continue;
                }

                var previousSegmentDirection = new Vector3d(
                    currentPartPoints[currentPartPoints.Count - 2],
                    currentPartPoints[currentPartPoints.Count - 1]).Normalized;
                var localTransitionThicknessDirection = previousSegmentDirection
                    .Cross(currentWidthDirection).Normalized;
                var transitionIntersection = currentPartPoints[currentPartPoints.Count - 1];
                var precedingPhysicalSectionIndex =
                    conversionResult.CenterlineParts.Count;
                var followingPhysicalSectionIndex =
                    precedingPhysicalSectionIndex + 1;
                var precedingPhysicalSectionWidth = busbarWidth;
                var followingPhysicalSectionWidth = busbarWidth;
                var precedingPhysicalSectionThickness = singleBusbarThickness;
                var followingPhysicalSectionThickness = singleBusbarThickness;
                currentPartPoints[currentPartPoints.Count - 1] = Translate(
                    transitionIntersection,
                    previousSegmentDirection,
                    precedingPhysicalSectionWidth / 2d);
                AddPart(
                    conversionResult,
                    currentPartPoints,
                    currentStartThicknessDirection,
                    currentWidthDirection,
                    routeRequest,
                    routeGroupRequest,
                    precedingPhysicalSectionIndex,
                    physicalSectionCount,
                    true);

                var transitionSign = appliedTransitionIndex < transitionSigns.Count
                    ? transitionSigns[appliedTransitionIndex]
                    : 1;
                appliedTransitionIndex++;
                var signedTransitionThicknessDirection = Scale(
                    localTransitionThicknessDirection, transitionSign);
                accumulatedTransitionOffset = Add(
                    accumulatedTransitionOffset,
                    Scale(
                        signedTransitionThicknessDirection,
                        (precedingPhysicalSectionThickness +
                            followingPhysicalSectionThickness) / 2d));
                var shiftedTransitionIntersection = Translate(
                    sourceSegmentStart, accumulatedTransitionOffset, 1d);
                var nextPartStart = Translate(
                    shiftedTransitionIntersection,
                    nextSegmentDirection,
                    -followingPhysicalSectionWidth / 2d);
                currentStartThicknessDirection = signedTransitionThicknessDirection;
                currentWidthDirection = currentStartThicknessDirection
                    .Cross(nextSegmentDirection).Normalized;
                currentPartPoints = new List<Point3d>
                {
                    nextPartStart,
                    TranslateWithTerminalLongitudinalAnchor(
                        sourceSegmentEnd,
                        accumulatedTransitionOffset,
                        nextSegmentDirection,
                        isFinalSourceSegment)
                };
            }

            AddPart(
                conversionResult,
                currentPartPoints,
                currentStartThicknessDirection,
                currentWidthDirection,
                routeRequest,
                routeGroupRequest,
                conversionResult.CenterlineParts.Count,
                physicalSectionCount,
                true);

            // Keep the source chain alongside the materialized geometry.  The
            // section-specific post-processor may split a straight physical
            // part solely to apply terminal dimensions; it must be able to
            // reconstruct that part from the original XYZ direction chain.
            conversionResult.OriginalCenterlineSnapshot =
                CaptureOriginalCenterlineSnapshot(
                    normalizedCenterline,
                    conversionResult.CenterlineParts,
                    routeGroupRequest.StartTerminal?.ThicknessDirection);

            if (!usesSectionSpecificBusbarSettings &&
                !ValidatePartLaps(
                    conversionResult.CenterlineParts,
                    busbarWidth,
                    stackBendAllowance,
                    out var lapFailureReason,
                    conversionResult.DoubleStartPhysicalBridgeSectionIndices.Count > 0
                        ? conversionResult.DoubleStartPhysicalBridgeSectionIndices
                        : ShouldDeferDoubleStartLapClosure(
                            routeRequest,
                            routeGroupRequest)
                            ? new HashSet<int>(Enumerable.Range(
                                0,
                                conversionResult.CenterlineParts.Count))
                            : null,
                    reserveRequest: routeRequest))
            {
                conversionResult.Diagnostics.Add(lapFailureReason);
                return conversionResult;
            }

            if (usesSectionSpecificBusbarSettings)
            {
                // 异规格候选的路径端点已由各自端段真实排厚/层数枚举。
                // 先建立不绑定通用端部层位的完整模板，再一次性按真实三段
                // 规格求层位和贴合，避免中间排规格提前否决合法候选。
                if (!ExpandSectionSpecificTemplateMembers(
                        conversionResult,
                        routeGroupRequest,
                        singleBusbarThickness,
                        routeGroupRequest.GetStackCount(routeRequest),
                        out var sectionTemplateExpansionFailureReason))
                {
                    conversionResult.Diagnostics.Add(
                        sectionTemplateExpansionFailureReason);
                    return conversionResult;
                }
            }
            else
            {
                var startStackPlacementResolved =
                    invariantContext?.StartStackPlacementResolved == true;
                var endStackPlacementResolved =
                    invariantContext?.EndStackPlacementResolved == true;
                var startStackPlacement = CopyTerminalPlacement(
                    invariantContext?.StartStackPlacement);
                var endStackPlacement = CopyTerminalPlacement(
                    invariantContext?.EndStackPlacement);
                if (!startStackPlacementResolved || !endStackPlacementResolved)
                {
                    conversionResult.Diagnostics.Add(
                        invariantContext?.StartStackPlacementFailureReason ??
                        invariantContext?.EndStackPlacementFailureReason ??
                        "叠排端部夹层位置无法匹配当前中心线。" );
                    return conversionResult;
                }

                if (!LinkageRouteTerminalAccessoryBuilder
                        .TryPrepareDoubleStartHorizontalBridgePlacement(
                            startStackPlacement,
                            conversionResult.CenterlineParts[0],
                            routeRequest,
                            routeGroupRequest,
                            false,
                            out var preparedStartStackPlacement,
                            out var doubleStartBridgePlacementFailureReason))
                {
                    conversionResult.Diagnostics.Add(
                        doubleStartBridgePlacementFailureReason);
                    return conversionResult;
                }

                if (!ExpandStackMembers(
                        conversionResult,
                        routeGroupRequest,
                        singleBusbarThickness,
                        routeGroupRequest.GetStackCount(routeRequest),
                        preparedStartStackPlacement,
                        endStackPlacement,
                        normalizedCenterline[0],
                        normalizedCenterline[normalizedCenterline.Count - 1],
                        allowTerminalThicknessLayerSupport,
                        out var stackExpansionFailureReason))
                {
                    conversionResult.Diagnostics.Add(stackExpansionFailureReason);
                    return conversionResult;
                }
            }

            if (!usesSectionSpecificBusbarSettings &&
                !ValidateStackedUShapePreviewMinimumSegments(
                    conversionResult.StackMembers,
                    routeRequest.MinimumSegmentLength,
                    out var stackedUShapeMinimumSegmentFailureReason))
            {
                conversionResult.Diagnostics.Add(
                    stackedUShapeMinimumSegmentFailureReason);
                return conversionResult;
            }

            if (!usesSectionSpecificBusbarSettings &&
                !LinkageRouteTerminalAccessoryBuilder.TryBuild(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    out var terminalAccessoryFailureReason))
            {
                conversionResult.Diagnostics.Add(terminalAccessoryFailureReason);
                return conversionResult;
            }
            foreach (var stackedRouteMember in conversionResult.StackMembers)
            {
                // 基准中心线已用叠排包络折弯余量预留最不利层位；展开后的每根
                // 实际铜排仍是单根排，只需校验真实单排折弯余量，不能重复叠加包络。
                if (!usesSectionSpecificBusbarSettings &&
                    !ValidatePartLaps(
                        stackedRouteMember.PlanarParts,
                        busbarWidth,
                        routeRequest.GetEffectiveBendAllowance(),
                        out var stackLapFailureReason,
                        conversionResult.DoubleStartPhysicalBridgeSectionIndices.Count > 0
                            ? conversionResult.DoubleStartPhysicalBridgeSectionIndices
                            : ShouldDeferDoubleStartLapClosure(
                                routeRequest,
                                routeGroupRequest)
                                ? new HashSet<int>(Enumerable.Range(
                                    0,
                                    stackedRouteMember.PlanarParts.Count))
                                : null,
                        allowTerminalThicknessLayerSupport
                            ? Math.Max(
                                GeometryTolerance,
                                routeRequest.SearchOptions.MinimumAdaptiveRepairStep *
                                    2d)
                            : 0d,
                        reserveRequest: routeRequest))
                {
                    conversionResult.Diagnostics.Add(
                        "叠排第 " + (stackedRouteMember.MemberIndex + 1) + " 层" + stackLapFailureReason);
                    return conversionResult;
                }
            }

            conversionResult.IsValid = conversionResult.CenterlineParts.Count > 0 &&
                conversionResult.StackMembers.Count > 0;
            return conversionResult;
        }

        private static OriginalCenterlineSnapshot CaptureOriginalCenterlineSnapshot(
            IList<Point3d> normalizedCenterline,
            IList<PlanarBendBusbarSetting> originalCenterlineParts,
            Vector3d initialThicknessDirection)
        {
            var snapshot = new OriginalCenterlineSnapshot();
            if (normalizedCenterline == null || normalizedCenterline.Count < 2)
            {
                return snapshot;
            }

            snapshot.ManufacturableCenterline = normalizedCenterline
                .Where(centerlinePoint => centerlinePoint != null)
                .Select(Copy)
                .ToList();
            snapshot.OriginalStartPoint = Copy(normalizedCenterline[0]);
            snapshot.OriginalEndPoint = Copy(
                normalizedCenterline[normalizedCenterline.Count - 1]);
            for (var segmentIndex = 0;
                segmentIndex + 1 < normalizedCenterline.Count;
                segmentIndex++)
            {
                var segmentStartPoint = normalizedCenterline[segmentIndex];
                var segmentEndPoint = normalizedCenterline[segmentIndex + 1];
                if (segmentStartPoint == null || segmentEndPoint == null)
                {
                    continue;
                }

                var segmentDirection = new Vector3d(
                    segmentStartPoint,
                    segmentEndPoint);
                if (segmentDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                snapshot.SegmentStartPoints.Add(Copy(segmentStartPoint));
                snapshot.SegmentEndPoints.Add(Copy(segmentEndPoint));
                snapshot.DirectionSequence.Add(segmentDirection.Normalized);
            }

            var transitionPointIndices = GetActualPlanarTransitionPointIndices(
                snapshot.ManufacturableCenterline,
                initialThicknessDirection);
            var transitionSet = new HashSet<int>(transitionPointIndices);
            var physicalPartIndex = 0;
            for (var segmentIndex = 0;
                segmentIndex < snapshot.DirectionSequence.Count;
                segmentIndex++)
            {
                snapshot.OriginalPhysicalPartMap.Add(physicalPartIndex);
                if (transitionSet.Contains(segmentIndex + 1))
                {
                    physicalPartIndex++;
                }
            }

            foreach (var originalCenterlinePart in originalCenterlineParts ??
                Enumerable.Empty<PlanarBendBusbarSetting>())
            {
                if (originalCenterlinePart != null)
                {
                    snapshot.CenterlineParts.Add(originalCenterlinePart.Copy());
                }
            }

            for (var bendPointIndex = 1;
                bendPointIndex + 1 < snapshot.ManufacturableCenterline.Count;
                bendPointIndex++)
            {
                var precedingDirection = new Vector3d(
                    snapshot.ManufacturableCenterline[bendPointIndex - 1],
                    snapshot.ManufacturableCenterline[bendPointIndex]);
                var followingDirection = new Vector3d(
                    snapshot.ManufacturableCenterline[bendPointIndex],
                    snapshot.ManufacturableCenterline[bendPointIndex + 1]);
                if (precedingDirection.Norm > GeometryTolerance &&
                    followingDirection.Norm > GeometryTolerance &&
                    Math.Abs(precedingDirection.Normalized.Dot(
                        followingDirection.Normalized)) <
                        1d - GeometryTolerance)
                {
                    snapshot.RealBendPoints.Add(Copy(
                        snapshot.ManufacturableCenterline[bendPointIndex]));
                }
            }

            return snapshot;
        }

        /// <summary>
        /// 在候选进入实体、净距和搭接终检之前，按物理段一次性生成真实
        /// 始端搭接排、中间排、柜顶搭接排及其附件。该步骤属于候选转换，
        /// 不是成功方案发布后的外观后处理。
        /// </summary>
        internal static bool TryApplySectionSpecificBusbarGeometry(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason,
            bool applySingleStackCabinetTopSpecification = false)
        {
            failureReason = null;
            if (conversionResult == null || routeRequest == null ||
                routeGroupRequest == null)
            {
                failureReason = "分段排真实规格转换缺少候选结果或请求参数。";
                return false;
            }

            if (!routeGroupRequest.HasSectionSpecificBusbarSettings(routeRequest) ||
                conversionResult.UsesSectionSpecificBusbarGeometry)
            {
                return true;
            }

            if (!LinkageRouteSectionSpecificationPostProcessor.TryApply(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    out var sectionSpecificationProcessingState,
                    out failureReason,
                    applySingleStackCabinetTopSpecification))
            {
                return false;
            }

            if (!ValidateStackedUShapePreviewMinimumSegments(
                    conversionResult.StackMembers,
                    routeRequest.MinimumSegmentLength,
                    out failureReason))
            {
                return false;
            }

            if (!LinkageRouteTerminalAccessoryBuilder.TryBuild(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    out failureReason,
                    true))
            {
                return false;
            }

            if (!LinkageRouteSectionSpecificationPostProcessor
                    .TryAddInternalSupportBlocks(
                        conversionResult,
                        routeRequest,
                        routeGroupRequest,
                        sectionSpecificationProcessingState,
                        out failureReason))
            {
                return false;
            }

            conversionResult.ActualCopperLength = conversionResult.StackMembers
                    .SelectMany(stackMember => stackMember.PlanarParts)
                    .Sum(planarPart => planarPart.GetDevelopedLength()) +
                LinkageRouteCopperLength.GetBridgeLength(conversionResult.Accessories);
            conversionResult.UsesSectionSpecificBusbarGeometry = true;
            return true;
        }

        private static PlanarConversionInvariantContext CreateInvariantContext(
            IList<Point3d> centerlinePoints,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            var invariantContext = new PlanarConversionInvariantContext();
            if (centerlinePoints == null || centerlinePoints.Count < 2 ||
                routeRequest == null || routeGroupRequest == null)
            {
                return invariantContext;
            }

            var normalizedCenterline = RemoveRepeatedPoints(centerlinePoints);
            if (normalizedCenterline.Count < 2 ||
                !HasValidSegments(normalizedCenterline))
            {
                return invariantContext;
            }

            var singleBusbarThickness =
                routeGroupRequest.GetBusbarThickness(routeRequest);
            invariantContext.StartStackPlacementResolved =
                TryResolveTerminalStackPlacement(
                    normalizedCenterline[0],
                    routeGroupRequest.StartTerminal,
                    routeGroupRequest,
                    routeRequest,
                    singleBusbarThickness,
                    true,
                    out var startStackPlacement,
                    out var startStackPlacementFailureReason,
                    routeRequest.EnableSingleStartConnectionBar &&
                    routeGroupRequest.StartTerminal.Members.Count == 1 &&
                    Math.Abs(new Vector3d(normalizedCenterline[0], normalizedCenterline[1]).Normalized
                        .Dot(new Vector3d(routeGroupRequest.StartTerminal.Members[0].FixedBusbar.PointList[0],
                            routeGroupRequest.StartTerminal.Members[0].FixedBusbar.PointList[1]).Normalized)) >=
                        1d - GeometryTolerance);
            invariantContext.StartStackPlacement = startStackPlacement;
            invariantContext.StartStackPlacementFailureReason =
                startStackPlacementFailureReason;
            invariantContext.EndStackPlacementResolved =
                TryResolveTerminalStackPlacement(
                    normalizedCenterline[normalizedCenterline.Count - 1],
                    routeGroupRequest.EndTerminal,
                    routeGroupRequest,
                    routeRequest,
                    singleBusbarThickness,
                    false,
                    out var endStackPlacement,
                    out var endStackPlacementFailureReason);
            invariantContext.EndStackPlacement = endStackPlacement;
            invariantContext.EndStackPlacementFailureReason =
                endStackPlacementFailureReason;
            return invariantContext;
        }

        private static Point3d TranslateWithTerminalLongitudinalAnchor(
            Point3d sourceSegmentEnd,
            Vector3d accumulatedTransitionOffset,
            Vector3d segmentDirection,
            bool preserveTerminalLongitudinalAnchor)
        {
            var translatedSegmentEnd = Translate(
                sourceSegmentEnd,
                accumulatedTransitionOffset,
                1d);
            if (!preserveTerminalLongitudinalAnchor)
            {
                return translatedSegmentEnd;
            }

            // 跨平面拆排产生的单排厚错层仍须保留在最终端段的横向/厚向上，
            // 但其沿最终排长方向的分量不能移动固定端搭接锚点。否则在末端
            // 垂直搭接时，端头会比固定排宽边界额外伸出一个或多个排厚。
            var normalizedSegmentDirection = segmentDirection.Normalized;
            var accumulatedLongitudinalOffset = accumulatedTransitionOffset.Dot(
                normalizedSegmentDirection);
            return Translate(
                translatedSegmentEnd,
                normalizedSegmentDirection,
                -accumulatedLongitudinalOffset);
        }

        private static int GetTerminalLapCoverageScore(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly terminalAssembly,
            double busbarWidth,
            double singleBusbarThickness,
            bool useStartBoundary)
        {
            if (conversionResult?.IsValid != true || terminalAssembly?.Members == null ||
                terminalAssembly.Members.Count == 0)
            {
                return 0;
            }

            var fixedCoverage = new bool[terminalAssembly.Members.Count];
            var generatedCoverage = new bool[conversionResult.StackMembers.Count];
            var generatedLapSegments = conversionResult.StackMembers
                .Select(stackedRouteMember => useStartBoundary
                    ? stackedRouteMember.PlanarParts.FirstOrDefault()
                    : stackedRouteMember.PlanarParts.LastOrDefault())
                .Select(generatedSetting => generatedSetting == null
                    ? null
                    : CreateBoundarySegment(
                        generatedSetting,
                        useStartBoundary))
                .ToList();
            for (var fixedMemberIndex = 0;
                fixedMemberIndex < terminalAssembly.Members.Count;
                fixedMemberIndex++)
            {
                var fixedSetting = terminalAssembly.Members[fixedMemberIndex]?.FixedBusbar;
                if (fixedSetting?.PointList == null || fixedSetting.PointList.Count < 2)
                {
                    continue;
                }

                var fixedLapSegment = CreateBoundarySegment(
                    fixedSetting,
                    useStartBoundary);
                for (var generatedMemberIndex = 0;
                    generatedMemberIndex < conversionResult.StackMembers.Count;
                    generatedMemberIndex++)
                {
                    var generatedLapSegment =
                        generatedLapSegments[generatedMemberIndex];
                    if (generatedLapSegment == null)
                    {
                        continue;
                    }

                    if (LinkageRouteLapValidator.Evaluate(
                            fixedLapSegment,
                            generatedLapSegment,
                            busbarWidth).IsValid)
                    {
                        fixedCoverage[fixedMemberIndex] = true;
                        generatedCoverage[generatedMemberIndex] = true;
                    }
                }
            }

            return fixedCoverage.Count(isCovered => isCovered) +
                generatedCoverage.Count(isCovered => isCovered) +
                (fixedCoverage.All(isCovered => isCovered) &&
                 generatedCoverage.All(isCovered => isCovered)
                    ? CompleteTerminalCoveragePreference
                    : 0);
        }

        private static void AddPart(
            LinkageRouteCenterlineConversionResult conversionResult,
            IList<Point3d> partPoints,
            Vector3d startThicknessDirection,
            Vector3d widthDirection,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            int physicalSectionIndex,
            int physicalSectionCount,
            bool useUniformCabinetTopSpecification = false)
        {
            var physicalSectionWidth = useUniformCabinetTopSpecification
                ? routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest)
                : routeGroupRequest.GetSectionBusbarWidth(
                    routeRequest,
                    physicalSectionIndex,
                    physicalSectionCount);
            var physicalSectionThickness = useUniformCabinetTopSpecification
                ? routeGroupRequest.GetCabinetTopLapBusbarThickness(routeRequest)
                : routeGroupRequest.GetSectionBusbarThickness(
                    routeRequest,
                    physicalSectionIndex,
                    physicalSectionCount);
            var planarSetting = new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = routeGroupRequest.GroupKey,
                StartBusbarVector = Copy(startThicknessDirection.Normalized),
                WidthVector = Copy(widthDirection.Normalized),
                PointList = partPoints.Select(Copy).ToList(),
                BarBendingRadius = routeRequest.BendingRadius,
                BendAllowance = routeRequest.GetEffectiveBendAllowance(),
                BusBarWidth = physicalSectionWidth,
                BusBarThickness = physicalSectionThickness,
                StartLapWidth = physicalSectionWidth,
                EndLapWidth = physicalSectionWidth,
                // 普通统一规格候选也必须保留实际物理分段索引。最终输出
                // 闭合校验会按该索引建立相邻分段关系；缺少索引会把统一
                // 规格误判为无法识别的异规格输出。
                PhysicalSectionIndex = physicalSectionIndex,
                SpecificationSectionIndex = physicalSectionIndex
            };
            planarSetting.RemoveInvalidPoints();
            if (planarSetting.PointList.Count >= 2)
            {
                conversionResult.CenterlineParts.Add(planarSetting);
            }
        }

        private static bool ValidatePartLaps(
            IList<PlanarBendBusbarSetting> centerlineParts,
            double busbarWidth,
            double stackBendAllowance,
            out string failureReason,
            ISet<int> deferredPositiveAreaOverlapSections = null,
            double maximumPerpendicularLapCoverageRepair = 0d,
            LinkageMultiGroupRouteRequest reserveRequest = null)
        {
            failureReason = null;
            for (var partIndex = 0; partIndex < centerlineParts.Count; partIndex++)
            {
                var physicalSectionIndex = centerlineParts[partIndex]
                    ?.PhysicalSectionIndex >= 0
                        ? centerlineParts[partIndex].PhysicalSectionIndex
                        : partIndex;
                if (deferredPositiveAreaOverlapSections?.Contains(
                        physicalSectionIndex) == true)
                {
                    // 双始端合并连接排的首段可能暂时与外侧实体排重叠；
                    // 它必须先经过连接排/端部联动后处理，不能在普通候选
                    // 转换阶段提前淘汰。最终实体门禁仍会严格复核。
                    continue;
                }

                if (!LinkageRouteTerminalLapRegionValidator.HasNoPositiveAreaOverlap(
                        centerlineParts[partIndex],
                        out var terminalLapRegionFailureReason))
                {
                    if (centerlineParts[partIndex]?.PointList?.Count == 2)
                    {
                        // 直 I 排的首末搭接区域重叠由端部联动、I→N
                        // 条件转换或最终垫块处理；不能在候选阶段把短 I
                        // 直接淘汰。最终实体门禁仍会严格判定是否可发布。
                        continue;
                    }

                    failureReason = string.Format(
                        "第 {0} 根实际铜排的首末搭接区域校验失败：{1}",
                        partIndex + 1,
                        terminalLapRegionFailureReason);
                    return false;
                }
            }

            for (var partIndex = 0; partIndex < centerlineParts.Count - 1; partIndex++)
            {
                var firstPart = centerlineParts[partIndex];
                var secondPart = centerlineParts[partIndex + 1];
                var firstLapSegment = CreateBoundarySegment(firstPart, false);
                var secondLapSegment = CreateBoundarySegment(secondPart, true);
                var lapEvaluation = LinkageRouteLapValidator.Evaluate(
                    firstLapSegment,
                    secondLapSegment,
                    busbarWidth);
                if (!lapEvaluation.IsValid)
                {
                    if (maximumPerpendicularLapCoverageRepair > GeometryTolerance &&
                        TryRepairPerpendicularLapCoverage(
                            centerlineParts,
                            partIndex,
                            lapEvaluation,
                            maximumPerpendicularLapCoverageRepair,
                            out var repairedLapEvaluation) &&
                        repairedLapEvaluation.IsValid)
                    {
                        continue;
                    }

                    failureReason = "平面过渡满搭接校验失败：" + lapEvaluation.Message;
                    return false;
                }

                if (!HasPerpendicularLapContinuationReserve(
                        firstPart,
                        secondPart,
                        firstLapSegment,
                        secondLapSegment,
                        lapEvaluation,
                        busbarWidth,
                        stackBendAllowance,
                        out failureReason,
                        reserveRequest))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ValidateStackedUShapePreviewMinimumSegments(
            IList<LinkageStackedRouteMember> stackedRouteMembers,
            double minimumSegmentLength,
            out string failureReason)
        {
            failureReason = null;
            if (stackedRouteMembers == null || stackedRouteMembers.Count == 0)
            {
                return true;
            }

            foreach (var stackedRouteMember in stackedRouteMembers)
            {
                for (var planarPartIndex = 0;
                    planarPartIndex < stackedRouteMember.PlanarParts.Count;
                    planarPartIndex++)
                {
                    var planarPart = stackedRouteMember.PlanarParts[planarPartIndex];
                    var centerlinePoints = planarPart?.PointList;
                    if (centerlinePoints == null || centerlinePoints.Count < 4)
                    {
                        continue;
                    }

                    var uShapeReturnSegmentIndices = new List<int>();
                    for (var segmentIndex = 1;
                        segmentIndex < centerlinePoints.Count - 2;
                        segmentIndex++)
                    {
                        var localFourPointShape = centerlinePoints
                            .Skip(segmentIndex - 1)
                            .Take(4)
                            .ToList();
                        if (!string.Equals(
                                LinkageRouteBendGeometry
                                    .ClassifyFourPointShape(
                                        localFourPointShape),
                                "U",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        uShapeReturnSegmentIndices.Add(segmentIndex);
                    }

                    if (uShapeReturnSegmentIndices.Count == 0)
                    {
                        continue;
                    }

                    // 只有实际包含 U 型同向回程的叠排才生成预览侧线，
                    // 避免给普通 I/L/S 路径增加无意义的转换成本或新门禁。
                    var previewPoints = planarPart.GetPreviewPointList();
                    if (previewPoints == null ||
                        previewPoints.Count != centerlinePoints.Count)
                    {
                        failureReason = string.Format(
                            "叠排第 {0} 层第 {1} 根铜排无法生成完整的 U 型预览点集。",
                            stackedRouteMember.MemberIndex + 1,
                            planarPartIndex + 1);
                        return false;
                    }

                    foreach (var uShapeReturnSegmentIndex in
                        uShapeReturnSegmentIndices)
                    {
                        var firstUShapeBendTangentLength =
                            GetPlanarBendTangentLength(
                                planarPart,
                                uShapeReturnSegmentIndex);
                        var secondUShapeBendTangentLength =
                            GetPlanarBendTangentLength(
                                planarPart,
                                uShapeReturnSegmentIndex + 1);
                        var previousCenterlineSegmentLength =
                            centerlinePoints[uShapeReturnSegmentIndex - 1]
                                .DistanceTo(
                                    centerlinePoints[uShapeReturnSegmentIndex]);
                        var currentCenterlineSegmentLength =
                            centerlinePoints[uShapeReturnSegmentIndex]
                                .DistanceTo(
                                    centerlinePoints[uShapeReturnSegmentIndex + 1]);
                        var followingCenterlineSegmentLength =
                            centerlinePoints[uShapeReturnSegmentIndex + 1]
                                .DistanceTo(
                                    centerlinePoints[uShapeReturnSegmentIndex + 2]);
                        var requiredUShapeReturnSegmentLength = Math.Max(
                            firstUShapeBendTangentLength,
                            secondUShapeBendTangentLength);
                        var requiredUShapeMiddleDistance =
                            planarPart.BusBarThickness > GeometryTolerance &&
                            planarPart.BarBendingRadius > GeometryTolerance
                                ? LinkageRouteStackGeometry
                                    .GetUShapeMinimumMiddleDistance(
                                        planarPart.BusBarThickness,
                                        planarPart.BarBendingRadius)
                                : 0d;
                        if (previousCenterlineSegmentLength + GeometryTolerance <
                                firstUShapeBendTangentLength ||
                            currentCenterlineSegmentLength + GeometryTolerance <
                                requiredUShapeReturnSegmentLength ||
                            followingCenterlineSegmentLength + GeometryTolerance <
                                secondUShapeBendTangentLength)
                        {
                            failureReason = string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "铜排 {0} 第 {1} 段 U 型折弯直段预留不足：前段 {2:F3} mm、中段 {3:F3} mm、后段 {4:F3} mm，折弯切线至少为 {5:F3}/{6:F3} mm。",
                                stackedRouteMember.MemberIndex + 1,
                                planarPartIndex + 1,
                                previousCenterlineSegmentLength,
                                currentCenterlineSegmentLength,
                                followingCenterlineSegmentLength,
                                firstUShapeBendTangentLength,
                                secondUShapeBendTangentLength);
                            return false;
                        }

                        if (requiredUShapeMiddleDistance > GeometryTolerance &&
                            currentCenterlineSegmentLength <=
                                requiredUShapeMiddleDistance +
                                    GeometryTolerance)
                        {
                            failureReason = string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "叠排第 {0} 层第 {1} 根铜排的 U 型中间距离仅 {2:F3} mm，必须大于 {6:G}×(排厚 {3:F3} mm+折弯半径 {4:F3} mm)={5:F3} mm。",
                                stackedRouteMember.MemberIndex + 1,
                                planarPartIndex + 1,
                                currentCenterlineSegmentLength,
                                planarPart.BusBarThickness,
                                planarPart.BarBendingRadius,
                                requiredUShapeMiddleDistance,
                                LinkageRouteStackGeometry
                                    .UShapeMiddleDistanceBendUnitMultiplier);
                            return false;
                        }

                        var actualPreviewSegmentLength =
                            previewPoints[uShapeReturnSegmentIndex]
                                .DistanceTo(
                                    previewPoints[uShapeReturnSegmentIndex + 1]);
                        if (actualPreviewSegmentLength + GeometryTolerance >=
                            minimumSegmentLength)
                        {
                            continue;
                        }

                        failureReason = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "叠排第 {0} 层第 {1} 根铜排的 U 型回程预览段实际长度仅 {2:F3} mm，小于最短长度 {3:F3} mm。",
                            stackedRouteMember.MemberIndex + 1,
                            planarPartIndex + 1,
                            actualPreviewSegmentLength,
                            minimumSegmentLength);
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryRepairPerpendicularLapCoverage(
            IList<PlanarBendBusbarSetting> centerlineParts,
            int precedingPartIndex,
            LinkageRouteLapEvaluation sourceLapEvaluation,
            double maximumRepairLength,
            out LinkageRouteLapEvaluation repairedLapEvaluation)
        {
            repairedLapEvaluation = sourceLapEvaluation;
            if (sourceLapEvaluation?.IsPerpendicular != true ||
                sourceLapEvaluation.ProjectedIntersection == null ||
                precedingPartIndex < 0 ||
                precedingPartIndex + 1 >= centerlineParts.Count)
            {
                return false;
            }

            var precedingPart = centerlineParts[precedingPartIndex];
            var followingPart = centerlineParts[precedingPartIndex + 1];
            if (precedingPart?.PointList?.Count < 2 ||
                followingPart?.PointList?.Count < 2)
            {
                return false;
            }

            var originalPrecedingEndpoint = Copy(precedingPart.PointList[
                precedingPart.PointList.Count - 1]);
            var originalPrecedingPenultimate = Copy(precedingPart.PointList[
                precedingPart.PointList.Count - 2]);
            var originalFollowingStart = Copy(followingPart.PointList[0]);
            var originalFollowingSecond = Copy(followingPart.PointList[1]);
            var precedingEndpoint = precedingPart.PointList[
                precedingPart.PointList.Count - 1];
            var followingStart = followingPart.PointList[0];
            var precedingShortfall = sourceLapEvaluation.FirstRequiredEndDistance -
                Math.Min(
                    sourceLapEvaluation.ProjectedIntersection.DistanceTo(
                        precedingPart.PointList[precedingPart.PointList.Count - 2]),
                    sourceLapEvaluation.ProjectedIntersection.DistanceTo(
                        precedingEndpoint));
            var followingShortfall = sourceLapEvaluation.SecondRequiredEndDistance -
                Math.Min(
                    sourceLapEvaluation.ProjectedIntersection.DistanceTo(followingStart),
                    sourceLapEvaluation.ProjectedIntersection.DistanceTo(
                        followingPart.PointList[1]));
            if (Math.Max(precedingShortfall, followingShortfall) >
                maximumRepairLength + GeometryTolerance)
            {
                return false;
            }

            if (precedingShortfall > GeometryTolerance)
            {
                var precedingPenultimate = precedingPart.PointList[
                    precedingPart.PointList.Count - 2];
                var endpointDistance = precedingEndpoint.DistanceTo(
                    sourceLapEvaluation.ProjectedIntersection);
                var penultimateDistance = precedingPenultimate.DistanceTo(
                    sourceLapEvaluation.ProjectedIntersection);
                var precedingAdjustIndex = penultimateDistance <= endpointDistance &&
                    precedingPart.PointList.Count > 2
                    ? precedingPart.PointList.Count - 2
                    : precedingPart.PointList.Count - 1;
                var precedingAdjustPoint = precedingPart.PointList[
                    precedingAdjustIndex];
                var direction = new Vector3d(
                    sourceLapEvaluation.ProjectedIntersection,
                    precedingAdjustPoint);
                if (direction.Norm <= GeometryTolerance)
                {
                    return false;
                }

                precedingPart.PointList[precedingAdjustIndex] = Translate(
                    precedingAdjustPoint,
                    direction.Normalized,
                    precedingShortfall);
            }

            if (followingShortfall > GeometryTolerance)
            {
                var followingSecond = followingPart.PointList[1];
                var startDistance = followingStart.DistanceTo(
                    sourceLapEvaluation.ProjectedIntersection);
                var secondDistance = followingSecond.DistanceTo(
                    sourceLapEvaluation.ProjectedIntersection);
                var followingAdjustIndex = startDistance <= secondDistance
                    ? 0
                    : followingPart.PointList.Count > 2
                        ? 1
                        : 0;
                var followingAdjustPoint = followingPart.PointList[
                    followingAdjustIndex];
                var direction = new Vector3d(
                    sourceLapEvaluation.ProjectedIntersection,
                    followingAdjustPoint);
                if (direction.Norm <= GeometryTolerance)
                {
                    precedingPart.PointList[precedingPart.PointList.Count - 1] =
                        originalPrecedingEndpoint;
                    precedingPart.PointList[precedingPart.PointList.Count - 2] =
                        originalPrecedingPenultimate;
                    return false;
                }

                followingPart.PointList[followingAdjustIndex] = Translate(
                    followingAdjustPoint,
                    direction.Normalized,
                    followingShortfall);
            }

            repairedLapEvaluation = LinkageRouteLapValidator.Evaluate(
                CreateBoundarySegment(precedingPart, false),
                CreateBoundarySegment(followingPart, true),
                Math.Min(
                    precedingPart.BusBarWidth,
                    followingPart.BusBarWidth));
            if (repairedLapEvaluation.IsValid)
            {
                return true;
            }

            precedingPart.PointList[precedingPart.PointList.Count - 1] =
                originalPrecedingEndpoint;
            precedingPart.PointList[precedingPart.PointList.Count - 2] =
                originalPrecedingPenultimate;
            followingPart.PointList[0] = originalFollowingStart;
            followingPart.PointList[1] = originalFollowingSecond;
            repairedLapEvaluation = sourceLapEvaluation;
            return false;
        }

        private static bool ShouldDeferDoubleStartLapClosure(
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            // 双始端虚拟桥接阶段会临时关闭 AllowDoubleStartMiddleLap，
            // 但它仍然代表多始端的下游实体。只要物理始端存在多根固定排，
            // 首末搭接重叠都应延后到桥接合并后的最终实体门禁。
            if (routeGroupRequest?.StartTerminal?.Members?.Count >= 2)
            {
                return true;
            }

            return routeRequest?.AllowDoubleStartMiddleLap == false &&
                routeGroupRequest?.StartTerminal?.Members?.Any(member =>
                    member?.MemberKey?.IndexOf(
                        "S1",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    member?.MemberKey?.IndexOf(
                        "DOUBLE_START_BRIDGE",
                        StringComparison.OrdinalIgnoreCase) >= 0) == true;
        }

        private static double GetPlanarBendTangentLength(
            PlanarBendBusbarSetting planarPart,
            int cornerPointIndex)
        {
            if (planarPart?.PointList == null ||
                cornerPointIndex <= 0 ||
                cornerPointIndex >= planarPart.PointList.Count - 1 ||
                planarPart.BarBendingRadius <= GeometryTolerance ||
                planarPart.BusBarThickness <= GeometryTolerance)
            {
                return 0d;
            }

            var incomingDirection = new Vector3d(
                planarPart.PointList[cornerPointIndex - 1],
                planarPart.PointList[cornerPointIndex]);
            var outgoingDirection = new Vector3d(
                planarPart.PointList[cornerPointIndex],
                planarPart.PointList[cornerPointIndex + 1]);
            if (incomingDirection.Norm <= GeometryTolerance ||
                outgoingDirection.Norm <= GeometryTolerance)
            {
                return double.PositiveInfinity;
            }

            var interiorCosine = Math.Max(
                -1d,
                Math.Min(
                    1d,
                    new Vector3d(
                        -incomingDirection.X,
                        -incomingDirection.Y,
                        -incomingDirection.Z)
                        .Normalized
                        .Dot(outgoingDirection.Normalized)));
            var interiorAngle = Math.Acos(interiorCosine);
            var tangentDenominator = Math.Tan(interiorAngle / 2d);
            return tangentDenominator <= GeometryTolerance
                ? double.PositiveInfinity
                : (planarPart.BarBendingRadius + planarPart.BusBarThickness) /
                    tangentDenominator;
        }

        private static bool HasPerpendicularLapContinuationReserve(
            PlanarBendBusbarSetting precedingPart,
            PlanarBendBusbarSetting followingPart,
            LinkageRouteLapSegment previousPartLapSegment,
            LinkageRouteLapSegment followingPartLapSegment,
            LinkageRouteLapEvaluation lapEvaluation,
            double busbarWidth,
            double stackBendAllowance,
            out string failureReason,
            LinkageMultiGroupRouteRequest reserveRequest = null)
        {
            failureReason = null;
            if (lapEvaluation?.ProjectedIntersection == null)
            {
                return true;
            }

            var previousLapDirection = new Vector3d(
                previousPartLapSegment.Start,
                previousPartLapSegment.End).Normalized;
            var followingLapDirection = new Vector3d(
                followingPartLapSegment.Start,
                followingPartLapSegment.End).Normalized;
            if (Math.Abs(previousLapDirection.Dot(followingLapDirection)) > GeometryTolerance)
            {
                return true;
            }

            // 内部垂直搭接的交点位于两段排宽投影的公共区域，
            // 每一侧只需保留从交点到折弯点的半个生成排宽，
            // 再加叠排折弯余量。终端固定排的完整排宽要求不在这里校验，
            // 由固定端搭接门禁单独处理。
            var precedingPartBusbarWidth = precedingPart?.BusBarWidth >
                GeometryTolerance
                ? precedingPart.BusBarWidth
                : busbarWidth;
            var followingPartBusbarWidth = followingPart?.BusBarWidth >
                GeometryTolerance
                ? followingPart.BusBarWidth
                : busbarWidth;
            var precedingRequiredBoundarySegmentLength =
                precedingPartBusbarWidth / 2d + stackBendAllowance;
            var followingRequiredBoundarySegmentLength =
                followingPartBusbarWidth / 2d + stackBendAllowance;
            if (HasPartBendReserve(
                    precedingPart,
                    false,
                    precedingRequiredBoundarySegmentLength,
                    "前一根排",
                    out failureReason) &&
                HasPartBendReserve(
                    followingPart,
                    true,
                    followingRequiredBoundarySegmentLength,
                    "后一根排",
                    out failureReason))
            {
                return true;
            }

            if (reserveRequest == null ||
                (!reserveRequest.AllowBendGeometricLimit &&
                 !reserveRequest.AllowConstrainedBendAllowanceReduction)) return false;

            // 优选B构造不变；实际成员将因预留不足淘汰时，复核双方真实有限搭接区。
            // 不是把余量门槛归零，圆弧切距、完整搭接和后续实体终检仍须成立。
            if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                    precedingPart, false, followingPart, true,
                    Math.Min(precedingPartBusbarWidth, followingPartBusbarWidth),
                    out var physicalFootprint, LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
            {
                failureReason = "内部垂直搭接缺少真实有限搭接矩形，不能采用折弯极限。";
                return false;
            }
            return LinkageRouteRoundedLapGeometry.HasFlatLapCoverage(
                       precedingPart, false, physicalFootprint.Corners, out _, out failureReason) &&
                   LinkageRouteRoundedLapGeometry.HasFlatLapCoverage(
                       followingPart, true, physicalFootprint.Corners, out _, out failureReason);
        }

        private static bool HasPartBendReserve(
            PlanarBendBusbarSetting planarPart,
            bool lapAtStart,
            double requiredBoundarySegmentLength,
            string partLabel,
            out string failureReason)
        {
            failureReason = null;
            if (planarPart?.PointList == null || planarPart.PointList.Count < 3)
            {
                return true;
            }

            var boundarySegmentIndex = lapAtStart
                ? 0
                : planarPart.PointList.Count - 2;
            var actualBoundarySegmentLength = planarPart.PointList[boundarySegmentIndex]
                .DistanceTo(planarPart.PointList[boundarySegmentIndex + 1]);
            if (actualBoundarySegmentLength + GeometryTolerance >= requiredBoundarySegmentLength)
            {
                return true;
            }

            failureReason = string.Format(
                "内部垂直搭接{0}的实际端部线段仅 {1:F3}，至少需要半个排宽加叠排折弯余量 {2:F3}。",
                partLabel,
                actualBoundarySegmentLength,
                requiredBoundarySegmentLength);
            return false;
        }

        private static LinkageRouteLapSegment CreateBoundarySegment(
            PlanarBendBusbarSetting planarSetting,
            bool useStart)
        {
            var segmentIndex = useStart ? 0 : planarSetting.PointList.Count - 2;
            var segmentDirection = new Vector3d(
                planarSetting.PointList[segmentIndex],
                planarSetting.PointList[segmentIndex + 1]).Normalized;
            var localThicknessDirection = segmentDirection
                .Cross(planarSetting.WidthVector.Normalized).Normalized;
            return new LinkageRouteLapSegment(
                planarSetting.PointList[segmentIndex],
                planarSetting.PointList[segmentIndex + 1],
                planarSetting.WidthVector,
                localThicknessDirection,
                planarSetting.BusBarThickness,
                planarSetting.BusBarWidth);
        }

        private static bool ExpandSectionSpecificTemplateMembers(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageRouteGroupRequest routeGroupRequest,
            double middleSingleBusbarThickness,
            int middleStackCount,
            out string failureReason)
        {
            failureReason = null;
            if (conversionResult?.CenterlineParts == null ||
                conversionResult.CenterlineParts.Count == 0 ||
                routeGroupRequest == null ||
                middleSingleBusbarThickness <= GeometryTolerance ||
                middleStackCount <= 0)
            {
                failureReason = "异规格叠排模板缺少有效平面排或中间排规格。";
                return false;
            }

            if (!TryGetPartOffsetOrientationSigns(
                    conversionResult.CenterlineParts,
                    out var partOffsetOrientationSigns,
                    out failureReason))
            {
                return false;
            }

            var firstCenterlinePart = conversionResult.CenterlineParts[0];
            if (!TryGetLocalThicknessDirection(
                    firstCenterlinePart,
                    0,
                    out var firstPartLocalThicknessDirection))
            {
                failureReason = "异规格叠排模板无法建立首段局部排厚方向。";
                return false;
            }

            var middleStackOffsets = LinkageRouteStackGeometry
                .GetCenteredMemberOffsets(
                    middleSingleBusbarThickness,
                    middleStackCount,
                    routeGroupRequest.StackMiddleSpace)
                .ToList();
            if (ResolveReverseMemberOrder(
                    routeGroupRequest,
                    firstPartLocalThicknessDirection))
            {
                middleStackOffsets.Reverse();
            }

            conversionResult.StackMembers.Clear();
            conversionResult.ActualCopperLength = 0d;
            for (var memberIndex = 0;
                memberIndex < middleStackOffsets.Count;
                memberIndex++)
            {
                var templateStackMember = new LinkageStackedRouteMember
                {
                    MemberIndex = memberIndex,
                    CenterlineOffset = middleStackOffsets[memberIndex]
                };
                for (var sectionIndex = 0;
                    sectionIndex < conversionResult.CenterlineParts.Count;
                    sectionIndex++)
                {
                    var centerlinePart = conversionResult.CenterlineParts[
                        sectionIndex];
                    var sectionOffset = middleStackOffsets[memberIndex] *
                        partOffsetOrientationSigns[sectionIndex];
                    var segmentOffsetDistances = Enumerable.Repeat(
                            sectionOffset,
                            centerlinePart.PointList.Count - 1)
                        .ToList();
                    if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(
                            centerlinePart,
                            segmentOffsetDistances,
                            out var memberCenterlinePoints,
                            out var memberOffsetFailureReason))
                    {
                        failureReason = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "异规格叠排模板第 {0} 层第 {1} 段偏移失败：{2}",
                            memberIndex + 1,
                            sectionIndex + 1,
                            memberOffsetFailureReason);
                        return false;
                    }

                    var memberSetting = centerlinePart.Copy();
                    memberSetting.PointList = memberCenterlinePoints;
                    templateStackMember.PlanarParts.Add(memberSetting);
                    conversionResult.ActualCopperLength +=
                        memberSetting.GetDevelopedLength();
                }

                conversionResult.StackMembers.Add(templateStackMember);
            }

            return true;
        }

        private static bool ExpandStackMembers(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageRouteGroupRequest routeGroupRequest,
            double singleBusbarThickness,
            int stackCount,
            LinkageRouteStackTerminalPlacement startStackPlacement,
            LinkageRouteStackTerminalPlacement endStackPlacement,
            Point3d requestedStartBaseEndpoint,
            Point3d requestedEndBaseEndpoint,
            bool allowTerminalThicknessLayerSupport,
            out string failureReason)
        {
            failureReason = null;
            if (conversionResult?.CenterlineParts == null ||
                conversionResult.CenterlineParts.Count == 0 ||
                startStackPlacement == null || endStackPlacement == null)
            {
                failureReason = "叠排展开缺少基准铜排或端部夹层。";
                return false;
            }

            var firstCenterlinePart = conversionResult.CenterlineParts[0];
            var lastCenterlinePart = conversionResult.CenterlineParts[
                conversionResult.CenterlineParts.Count - 1];
            if (!TryGetLocalThicknessDirection(
                    firstCenterlinePart,
                    0,
                    out var firstPartLocalThicknessDirection) ||
                !TryGetLocalThicknessDirection(
                    lastCenterlinePart,
                    lastCenterlinePart.PointList.Count - 2,
                    out var lastPartLocalThicknessDirection))
            {
                failureReason = "叠排展开无法建立始末段局部排厚方向。";
                return false;
            }

            if (!TryGetPartOffsetOrientationSigns(
                    conversionResult.CenterlineParts,
                    out var partOffsetOrientationSigns,
                    out var partOrientationFailureReason))
            {
                failureReason = partOrientationFailureReason;
                return false;
            }

            var reverseMemberOrder = ResolveReverseMemberOrder(
                routeGroupRequest,
                firstPartLocalThicknessDirection);
            var mappedStartStackPlacement = MapPlacementToLocalMemberOrder(
                startStackPlacement,
                routeGroupRequest.StartTerminal.ThicknessDirection,
                firstPartLocalThicknessDirection,
                reverseMemberOrder);
            var mappedEndStackPlacement = MapPlacementToLocalMemberOrder(
                endStackPlacement,
                routeGroupRequest.EndTerminal.ThicknessDirection,
                lastPartLocalThicknessDirection,
                reverseMemberOrder !=
                    (partOffsetOrientationSigns[
                        partOffsetOrientationSigns.Count - 1] < 0d));
            if (mappedStartStackPlacement == null)
            {
                var startTerminalThicknessAlignment =
                    routeGroupRequest.StartTerminal.ThicknessDirection.Normalized.Dot(
                        firstPartLocalThicknessDirection.Normalized);
                failureReason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "始端实际排厚方向与固定排厚方向不平行，方向点积={0:F6}。",
                    startTerminalThicknessAlignment);
                return false;
            }

            if (mappedEndStackPlacement == null)
            {
                var endTerminalThicknessAlignment =
                    routeGroupRequest.EndTerminal.ThicknessDirection.Normalized.Dot(
                        lastPartLocalThicknessDirection.Normalized);
                failureReason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "柜顶实际排厚方向与固定排厚方向不平行，方向点积={0:F6}。",
                    endTerminalThicknessAlignment);
                return false;
            }

            if (mappedStartStackPlacement.GeneratedMemberOffsets.Count != stackCount ||
                mappedEndStackPlacement.GeneratedMemberOffsets.Count != stackCount)
            {
                failureReason = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "叠排层位数量不一致：生成层数={0}，始端层位={1}，柜顶层位={2}。",
                    stackCount,
                    mappedStartStackPlacement.GeneratedMemberOffsets.Count,
                    mappedEndStackPlacement.GeneratedMemberOffsets.Count);
                return false;
            }

            var actualStartBaseEndpoint = firstCenterlinePart.PointList[0];
            var actualEndBaseEndpoint = lastCenterlinePart.PointList[
                lastCenterlinePart.PointList.Count - 1];
            var startBaseThicknessDisplacement = new Vector3d(
                    requestedStartBaseEndpoint,
                    actualStartBaseEndpoint)
                .Dot(firstPartLocalThicknessDirection.Normalized);
            var endBaseThicknessDisplacement = new Vector3d(
                    requestedEndBaseEndpoint,
                    actualEndBaseEndpoint)
                .Dot(lastPartLocalThicknessDirection.Normalized);

            var middleStackOffsets = LinkageRouteStackGeometry.GetCenteredMemberOffsets(
                    singleBusbarThickness,
                    stackCount,
                    routeGroupRequest.StackMiddleSpace)
                .ToList();
            if (reverseMemberOrder)
            {
                middleStackOffsets.Reverse();
            }

            // A rigid terminal I must carry its actual terminal layer to the
            // first bend. A chain containing only I parts keeps the existing
            // terminal support policy because it cannot absorb two offsets.
            var canAbsorbTerminalLayerDifference = conversionResult.CenterlineParts.Any(part =>
            {
                GetTerminalRigidSegmentIndices(part, true, out var canAbsorbLayer);
                return canAbsorbLayer;
            });
            for (var memberIndex = 0; memberIndex < middleStackOffsets.Count; memberIndex++)
            {
                var stackedMember = new LinkageStackedRouteMember
                {
                    MemberIndex = memberIndex,
                    CenterlineOffset = middleStackOffsets[memberIndex]
                };
                for (var centerlinePartIndex = 0;
                    centerlinePartIndex < conversionResult.CenterlineParts.Count;
                    centerlinePartIndex++)
                {
                    var centerlinePart = conversionResult.CenterlineParts[
                        centerlinePartIndex];
                    var centerlinePartSegmentCount = centerlinePart.PointList.Count - 1;
                    var currentPartMiddleOffset =
                        middleStackOffsets[memberIndex] *
                        partOffsetOrientationSigns[centerlinePartIndex];
                    var segmentOffsetDistances = Enumerable.Repeat(
                            currentPartMiddleOffset,
                            centerlinePartSegmentCount)
                        .ToList();
                    var isFirstCenterlinePart = centerlinePartIndex == 0;
                    var isLastCenterlinePart = centerlinePartIndex ==
                        conversionResult.CenterlineParts.Count - 1;
                    var requiredStartOffset = isFirstCenterlinePart
                        ? mappedStartStackPlacement.GeneratedMemberOffsets[memberIndex] -
                            startBaseThicknessDisplacement
                        : currentPartMiddleOffset;
                    var requiredEndOffset = isLastCenterlinePart
                        ? mappedEndStackPlacement.GeneratedMemberOffsets[memberIndex] -
                            endBaseThicknessDisplacement
                        : currentPartMiddleOffset;
                    if (centerlinePartSegmentCount == 1 &&
                        Math.Abs(requiredStartOffset - requiredEndOffset) > GeometryTolerance)
                    {
                        if (!allowTerminalThicknessLayerSupport)
                        {
                            failureReason = string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "叠排第 {0} 层的第 {1} 根 I 排始末层位不同，必须先由 N/M 路径完成层位过渡。",
                                memberIndex + 1,
                                centerlinePartIndex + 1);
                            return false;
                        }

                        // 暂以内部边界构造刚性I排。完整成员建立后，端部真实
                        // 层位差沿同一I链传播并由非I吸收，不能只移动此接口。
                        // 没有转角的纯I路线保留端部支撑所需的原构造。
                        var rigidInternalOffset = isFirstCenterlinePart &&
                                !isLastCenterlinePart
                            ? requiredEndOffset
                            : isLastCenterlinePart &&
                                !isFirstCenterlinePart
                                ? requiredStartOffset
                                : (requiredStartOffset +
                                    requiredEndOffset) / 2d;
                        requiredStartOffset = rigidInternalOffset;
                        requiredEndOffset = rigidInternalOffset;
                    }

                    segmentOffsetDistances[0] = requiredStartOffset;
                    segmentOffsetDistances[centerlinePartSegmentCount - 1] =
                        requiredEndOffset;
                    var memberSetting = centerlinePart.Copy();
                    if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(
                            centerlinePart,
                            segmentOffsetDistances,
                            out var memberCenterlinePoints,
                            out var memberOffsetFailureReason))
                    {
                        failureReason = string.Format(
                            "叠排第 {0} 层的第 {1} 根铜排偏移失败：{2}",
                            memberIndex + 1,
                            centerlinePartIndex + 1,
                            memberOffsetFailureReason);
                        return false;
                    }

                    memberSetting.PointList = memberCenterlinePoints;
                    memberSetting.BusBarThickness = singleBusbarThickness;
                    stackedMember.PlanarParts.Add(memberSetting);

                }

                if (allowTerminalThicknessLayerSupport && canAbsorbTerminalLayerDifference)
                {
                    var memberStartPoint = stackedMember.PlanarParts.First().PointList.First();
                    var memberEndPoint = stackedMember.PlanarParts.Last().PointList.Last();
                    var actualStartLayerOffset = new Vector3d(actualStartBaseEndpoint, memberStartPoint)
                        .Dot(firstPartLocalThicknessDirection.Normalized);
                    var actualEndLayerOffset = new Vector3d(actualEndBaseEndpoint, memberEndPoint)
                        .Dot(lastPartLocalThicknessDirection.Normalized);
                    var terminalStartLayerDisplacement = Scale(firstPartLocalThicknessDirection.Normalized,
                        mappedStartStackPlacement.GeneratedMemberOffsets[memberIndex] -
                        startBaseThicknessDisplacement - actualStartLayerOffset);
                    var terminalEndLayerDisplacement = Scale(lastPartLocalThicknessDirection.Normalized,
                        mappedEndStackPlacement.GeneratedMemberOffsets[memberIndex] -
                        endBaseThicknessDisplacement - actualEndLayerOffset);
                    // A real materialized bridge has its own locked placement.
                    if (conversionResult.DoubleStartPhysicalBridgeSectionIndices.Contains(0))
                        terminalStartLayerDisplacement = new Vector3d(0d, 0d, 0d);
                    if (conversionResult.DoubleStartPhysicalBridgeSectionIndices.Contains(conversionResult.CenterlineParts.Count - 1))
                        terminalEndLayerDisplacement = new Vector3d(0d, 0d, 0d);
                    if (terminalStartLayerDisplacement.Norm > GeometryTolerance ||
                        terminalEndLayerDisplacement.Norm > GeometryTolerance)
                    {
                        if (!TryMoveOrdinaryMemberTerminalChain(stackedMember,
                            terminalStartLayerDisplacement, terminalEndLayerDisplacement,
                            out var terminalAlignedMember, out var terminalLayerFailureReason))
                        {
                            failureReason = "叠排端部实际厚差层位无法沿I链传递：" + terminalLayerFailureReason;
                            return false;
                        }
                        stackedMember = terminalAlignedMember;
                    }
                }
                conversionResult.ActualCopperLength += stackedMember.PlanarParts.Sum(part => part.GetDevelopedLength());
                conversionResult.StackMembers.Add(stackedMember);
            }

            conversionResult.StartStackPlacement = mappedStartStackPlacement;
            conversionResult.EndStackPlacement = mappedEndStackPlacement;
            return true;
        }

        private static bool TryGetPartOffsetOrientationSigns(
            IList<PlanarBendBusbarSetting> centerlineParts,
            out IReadOnlyList<double> partOffsetOrientationSigns,
            out string failureReason)
        {
            var resolvedOrientationSigns = new List<double>();
            partOffsetOrientationSigns = resolvedOrientationSigns;
            failureReason = null;
            if (centerlineParts == null || centerlineParts.Count == 0)
            {
                failureReason = "叠排展开缺少可传播层序的平面铜排。";
                return false;
            }

            resolvedOrientationSigns.Add(1d);
            for (var partIndex = 1; partIndex < centerlineParts.Count; partIndex++)
            {
                var precedingPart = centerlineParts[partIndex - 1];
                var followingPart = centerlineParts[partIndex];
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
                        "叠排无法建立第 {0} 个平面过渡的局部排厚方向。",
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
                        System.Globalization.CultureInfo.InvariantCulture,
                        "叠排第 {0} 个平面过渡的排厚方向不平行，方向点积={1:F6}。",
                        partIndex,
                        boundaryThicknessAlignment);
                    return false;
                }

                resolvedOrientationSigns.Add(
                    resolvedOrientationSigns[partIndex - 1] *
                    (boundaryThicknessAlignment < 0d ? -1d : 1d));
            }

            return true;
        }

        private static bool TryResolveTerminalStackPlacement(
            Point3d generatedBaseEndpoint,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageMultiGroupRouteRequest routeRequest,
            double singleBusbarThickness,
            bool useStartTerminal,
            out LinkageRouteStackTerminalPlacement matchedStackPlacement,
            out string failureReason,
            bool useHorizontalConnectionPlacements = false)
        {
            matchedStackPlacement = null;
            failureReason = null;
            if (!LinkageRouteStackPlacementSolver.TryMeasureBaseCenterOffset(
                    terminalAssembly,
                    generatedBaseEndpoint,
                    out var measuredBaseCenterOffset))
            {
                failureReason = (useStartTerminal ? "始端" : "柜顶") +
                    "无法测量叠排基准中心相对固定排的位置。";
                return false;
            }

            var terminalAdditionalSpace = useStartTerminal
                ? routeGroupRequest.StackStartSpace
                : routeGroupRequest.StackEndSpace;
            var terminalPlacements =
                LinkageRouteStackPlacementSolver.ResolveTerminalPlacements(
                    terminalAssembly,
                    routeGroupRequest.GetStackCount(routeRequest),
                    singleBusbarThickness,
                    terminalAdditionalSpace,
                    routeGroupRequest.StackMiddleSpace,
                    false,
                    useStartTerminal);
            if (useStartTerminal && useHorizontalConnectionPlacements)
                terminalPlacements = LinkageRouteTerminalAccessoryBuilder.PrepareSingleStartHorizontalConnectionPlacements(
                    terminalPlacements, routeRequest, routeGroupRequest, singleBusbarThickness);
            matchedStackPlacement =
                LinkageRouteStackPlacementSolver.FindMatchingPlacement(
                    terminalPlacements,
                    measuredBaseCenterOffset);
            if (matchedStackPlacement != null)
            {
                return true;
            }

            failureReason = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}叠排基准中心偏移 {1:F6} 未匹配任何连续夹层候选。",
                useStartTerminal ? "始端" : "柜顶",
                measuredBaseCenterOffset);
            return false;
        }

        private static LinkageRouteStackTerminalPlacement MapPlacementToLocalMemberOrder(
            LinkageRouteStackTerminalPlacement sourcePlacement,
            Vector3d terminalThicknessDirection,
            Vector3d localThicknessDirection,
            bool reverseMemberOrder)
        {
            if (sourcePlacement == null || terminalThicknessDirection == null ||
                localThicknessDirection == null ||
                sourcePlacement.GeneratedMemberOffsets == null)
            {
                return null;
            }

            var terminalToLocalDirectionAlignment = terminalThicknessDirection.Normalized.Dot(
                localThicknessDirection.Normalized);
            if (Math.Abs(terminalToLocalDirectionAlignment) < 1d - GeometryTolerance)
            {
                return null;
            }

            var generatedMemberCount = sourcePlacement.GeneratedMemberOffsets.Count;
            if ((sourcePlacement.SpacerlessMovedGeneratedMemberIndices ??
                    new List<int>())
                .Any(generatedMemberIndex => generatedMemberIndex < 0 ||
                    generatedMemberIndex >= generatedMemberCount))
            {
                return null;
            }

            var mappedOffsets = new double[generatedMemberCount];
            var physicalToMappedMemberIndex = new int[generatedMemberCount];
            for (var mappedMemberIndex = 0;
                mappedMemberIndex < generatedMemberCount;
                mappedMemberIndex++)
            {
                var localOrderIndex = reverseMemberOrder
                    ? generatedMemberCount - 1 - mappedMemberIndex
                    : mappedMemberIndex;
                var physicalTerminalMemberIndex =
                    terminalToLocalDirectionAlignment > 0d
                        ? localOrderIndex
                        : generatedMemberCount - 1 - localOrderIndex;
                mappedOffsets[mappedMemberIndex] =
                    sourcePlacement.GeneratedMemberOffsets[
                        physicalTerminalMemberIndex] *
                    (terminalToLocalDirectionAlignment > 0d ? 1d : -1d);
                physicalToMappedMemberIndex[physicalTerminalMemberIndex] =
                    mappedMemberIndex;
            }

            return new LinkageRouteStackTerminalPlacement
            {
                Signature = sourcePlacement.Signature,
                BaseCenterOffset = sourcePlacement.BaseCenterOffset,
                GeneratedMemberOffsets = mappedOffsets,
                SpacerlessMovedGeneratedMemberIndices = (sourcePlacement
                        .SpacerlessMovedGeneratedMemberIndices ??
                        new List<int>())
                    .Select(physicalGeneratedMemberIndex =>
                        physicalToMappedMemberIndex[
                            physicalGeneratedMemberIndex])
                    .OrderBy(mappedGeneratedMemberIndex =>
                        mappedGeneratedMemberIndex)
                    .ToList(),
                DoubleStartHorizontalBridgeGeneratedMemberIndex =
                    sourcePlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex.HasValue
                        ? (int?)physicalToMappedMemberIndex[
                            sourcePlacement
                                .DoubleStartHorizontalBridgeGeneratedMemberIndex.Value]
                        : null,
                DoubleStartHorizontalBridgeSideSign =
                    sourcePlacement.DoubleStartHorizontalBridgeSideSign *
                    (terminalToLocalDirectionAlignment > 0d ? 1 : -1),
                DoubleStartHorizontalBridgeThickness =
                    sourcePlacement.DoubleStartHorizontalBridgeThickness,
                DoubleStartHorizontalBridgePlacements =
                    (sourcePlacement.DoubleStartHorizontalBridgePlacements ??
                        new List<LinkageRouteTerminalBridgePlacement>())
                    .Where(bridgePlacement =>
                        bridgePlacement.GeneratedMemberIndex >= 0 &&
                        bridgePlacement.GeneratedMemberIndex <
                            physicalToMappedMemberIndex.Length)
                    .Select(bridgePlacement => new LinkageRouteTerminalBridgePlacement
                    {
                        GeneratedMemberIndex = physicalToMappedMemberIndex[
                            bridgePlacement.GeneratedMemberIndex],
                        SideSign = bridgePlacement.SideSign *
                            (terminalToLocalDirectionAlignment > 0d ? 1 : -1),
                        Thickness = bridgePlacement.Thickness
                    }).ToList(),
                Contacts = sourcePlacement.Contacts.Select(stackContact =>
                    new LinkageRouteStackTerminalContact
                    {
                        GeneratedMemberIndex = physicalToMappedMemberIndex[
                            stackContact.GeneratedMemberIndex],
                        FixedMemberIndex = stackContact.FixedMemberIndex
                    }).ToList()
            };
        }

        private static bool ResolveReverseMemberOrder(
            LinkageRouteGroupRequest routeGroupRequest,
            Vector3d firstPartLocalThicknessDirection)
        {
            var reverseMemberOrder = routeGroupRequest.StackReverse;
            if (routeGroupRequest.StackMoveVector == null ||
                routeGroupRequest.StackMoveVector.Norm <= GeometryTolerance)
            {
                return reverseMemberOrder;
            }

            var stackMoveDirectionAlignment = routeGroupRequest.StackMoveVector.Normalized.Dot(
                firstPartLocalThicknessDirection.Normalized);
            return stackMoveDirectionAlignment < -GeometryTolerance
                ? !reverseMemberOrder
                : reverseMemberOrder;
        }

        private static bool TryGetLocalThicknessDirection(
            PlanarBendBusbarSetting planarSetting,
            int segmentIndex,
            out Vector3d localThicknessDirection)
        {
            localThicknessDirection = null;
            if (planarSetting?.PointList == null || planarSetting.PointList.Count < 2 ||
                planarSetting.StartBusbarVector == null ||
                planarSetting.StartBusbarVector.Norm <= GeometryTolerance ||
                planarSetting.WidthVector == null ||
                planarSetting.WidthVector.Norm <= GeometryTolerance ||
                segmentIndex < 0 || segmentIndex + 1 >= planarSetting.PointList.Count)
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
                localThicknessDirection = Scale(localThicknessDirection, -1d);
            }

            return localThicknessDirection != null &&
                localThicknessDirection.Norm > GeometryTolerance;
        }

        private static bool HasValidSegments(IList<Point3d> points)
        {
            for (var pointIndex = 0; pointIndex < points.Count - 1; pointIndex++)
            {
                var segmentDirection = new Vector3d(points[pointIndex], points[pointIndex + 1]);
                if (segmentDirection.Norm <= GeometryTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static List<Point3d> RemoveRepeatedPoints(IEnumerable<Point3d> points)
        {
            var normalizedPoints = new List<Point3d>();
            foreach (var sourcePoint in points.Where(point => point != null))
            {
                AddPoint(normalizedPoints, sourcePoint);
            }

            return normalizedPoints;
        }

        private static void AddPoint(ICollection<Point3d> points, Point3d point)
        {
            var pointList = points as IList<Point3d>;
            if (pointList == null || pointList.Count == 0 ||
                pointList[pointList.Count - 1].DistanceTo(point) > GeometryTolerance)
            {
                points.Add(Copy(point));
            }
        }

    }

    internal static class LinkageRoutePlanarConverter
    {
        public static LinkageRouteCenterlineConversionResult Convert(
            IList<Point3d> centerlinePoints,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest)
        {
            return LinkageRouteCenterlineConverter.Convert(
                centerlinePoints, routeRequest, routeGroupRequest);
        }
    }
}
