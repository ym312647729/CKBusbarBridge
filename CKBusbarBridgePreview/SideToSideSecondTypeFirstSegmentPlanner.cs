using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    internal sealed class SideToSideSecondTypeFirstSegmentPlan
    {
        internal IReadOnlyList<PlanarBendBusbarSetting>
            BCabinetFirstSegmentBusbars { get; set; }
        internal IReadOnlyList<PlanarBendBusbarSetting>
            ACabinetFirstSegmentBusbars { get; set; }
        internal IReadOnlyList<PlanarBendBusbarSetting>
            MiddleConnectionBusbars { get; set; }
    }

    /// <summary>
    /// Stores the physical nearest-to-farthest phase order independently for
    /// the two cabinets.  A/B phase order can legitimately differ when the
    /// side-to-side route is a phase-change scene; reusing B's order for A in
    /// that case assigns the fold insulators to the wrong logical phase.
    /// </summary>
    internal sealed class SideToSideFoldPhaseIndexMapping
    {
        internal IReadOnlyList<int> BCabinetNearestToFarthestPhaseIndexes
        {
            get;
            set;
        }

        internal IReadOnlyList<int> ACabinetNearestToFarthestPhaseIndexes
        {
            get;
            set;
        }

    }

    /// <summary>
    /// 侧对侧前后开孔（基座2）B 柜柜顶首段路径规划器。
    /// 增高基座绝缘子只定位点2；普通基座绝缘子与柜顶连接中心线的
    /// 长度轴偏差超过一个排厚时独立生成点3、4，否则归并后由点2
    /// 直接进入点5、6、7。
    /// </summary>
    internal static class SideToSideSecondTypeFirstSegmentPlanner
    {
        private const int PhaseCount = 3;
        private const double GeometryTolerance = 0.001d;
        private const double ClearanceSearchStep = 1d;
        private const int ClearanceBinarySearchIterations = 18;
        private const double UpperStraightLengthAdditionMm = 100.0;

        internal static SideToSideSecondTypeFirstSegmentPlan Create(
            AssemblyPartCatalog partCatalog,
            BusbarBridgeAxisSetting routeAxisSetting,
            IReadOnlyList<ModeledBusbar> bCabinetExtensionBusbars,
            IReadOnlyList<ModeledBusbar> aCabinetExtensionBusbars,
            Component2 bCabinetOrdinaryBaseComponent,
            Component2 bCabinetElevatedBaseComponent,
            Component2 aCabinetOrdinaryBaseComponent,
            Component2 aCabinetElevatedBaseComponent,
            IReadOnlyList<InsulatorConnectionInfo> availableInsulators,
            double bFirstBreakpointModelCoordinate,
            double aLastBreakpointModelCoordinate,
            int singlePhaseStackCount,
            double bendingRadius,
            double bendAllowance,
            double insulationClearance,
            bool createStraightMiddleConnection = true)
        {
            if (partCatalog == null)
            {
                throw new ArgumentNullException(nameof(partCatalog));
            }
            if (routeAxisSetting == null)
            {
                throw new ArgumentNullException(nameof(routeAxisSetting));
            }
            if (bCabinetOrdinaryBaseComponent == null)
            {
                throw new ArgumentNullException(
                    nameof(bCabinetOrdinaryBaseComponent));
            }
            if (availableInsulators == null)
            {
                throw new ArgumentNullException(nameof(availableInsulators));
            }

            var phaseExtensionGroups =
                FaceToWallModelBusbarPlanner.GroupExtensionBusbarsByPhase(
                    bCabinetExtensionBusbars,
                    routeAxisSetting.WidthVector);
            var aCabinetPhaseExtensionGroups =
                FaceToWallModelBusbarPlanner.GroupExtensionBusbarsByPhase(
                    aCabinetExtensionBusbars,
                    routeAxisSetting.WidthVector);
            var foldPhaseIndexMapping = ResolveFoldPhaseIndexes(
                phaseExtensionGroups,
                aCabinetPhaseExtensionGroups,
                routeAxisSetting.WidthVector);
            var bCabinetFoldPhaseIndexes =
                foldPhaseIndexMapping.BCabinetNearestToFarthestPhaseIndexes;
            var aCabinetFoldPhaseIndexes =
                foldPhaseIndexMapping.ACabinetNearestToFarthestPhaseIndexes;
            var phaseWidthCoordinates = phaseExtensionGroups
                .Select(phaseGroup => phaseGroup.Average(busbar =>
                    BusbarGeometryUtility.Project(
                        busbar.TerminalSegment.P2,
                        routeAxisSetting.WidthVector)))
                .ToList();
            var elevatedBaseInsulators = bCabinetElevatedBaseComponent == null
                ? new List<InsulatorConnectionInfo>()
                : SelectStrictHostInsulators(
                    partCatalog,
                    bCabinetElevatedBaseComponent,
                    availableInsulators,
                    routeAxisSetting.LengthVector,
                    "B柜增高基座");
            if (bCabinetElevatedBaseComponent != null &&
                elevatedBaseInsulators.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    "B柜存在增高基座，但其内部必须且只能识别三相绝缘子，当前数量=" +
                    elevatedBaseInsulators.Count + "。");
            }
            var orderedElevatedBaseInsulators =
                elevatedBaseInsulators.Count == PhaseCount
                    ? FaceToWallModelBusbarPlanner.MatchInsulatorsToPhases(
                        elevatedBaseInsulators,
                        phaseWidthCoordinates,
                        routeAxisSetting.WidthVector)
                    : null;

            var ordinaryBaseInsulators = SelectStrictHostInsulators(
                partCatalog,
                bCabinetOrdinaryBaseComponent,
                availableInsulators,
                routeAxisSetting.LengthVector,
                "B柜普通基座");
            if (ordinaryBaseInsulators.Count != 0 &&
                ordinaryBaseInsulators.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    "B柜普通基座内部的绝缘子应为 0 个或三相 3 个，当前数量=" +
                    ordinaryBaseInsulators.Count + "。");
            }
            var orderedOrdinaryBaseInsulators =
                ordinaryBaseInsulators.Count == PhaseCount
                    ? MatchBCabinetFoldInsulatorsToPhases(
                        ordinaryBaseInsulators,
                        bCabinetFoldPhaseIndexes,
                        routeAxisSetting.WidthVector,
                        "普通基座",
                        false)
                    : null;
            var baseInsulatorNames = new HashSet<string>(
                elevatedBaseInsulators.Concat(ordinaryBaseInsulators)
                    .Select(insulator =>
                        BusbarGeometryUtility.SafeName(insulator.Component)),
                StringComparer.OrdinalIgnoreCase);
            var baseCenter =
                BusbarGeometryUtility.GetComponentCenter(
                    bCabinetOrdinaryBaseComponent);
            if (baseCenter == null)
            {
                throw new InvalidOperationException(
                    "无法读取侧对侧第二种 B 柜基座绝缘子宿主中心。");
            }

            var firstFrameComponent =
                FaceToWallModelBusbarPlanner
                    .SelectNearestFrameHostingInsulators(
                        partCatalog,
                        baseCenter,
                        availableInsulators,
                        routeAxisSetting.HeightVector,
                        baseInsulatorNames,
                        PhaseCount);
            if (firstFrameComponent == null)
            {
                throw new InvalidOperationException(
                    "侧对侧第二种 B 柜附近没有识别到包含三相绝缘子的第一个基架。");
            }

            var frameInsulators =
                FaceToWallModelBusbarPlanner.SelectHostInsulators(
                    partCatalog,
                    firstFrameComponent,
                    availableInsulators,
                    routeAxisSetting.HeightVector,
                    baseInsulatorNames,
                    "侧对侧第二种第一个基架",
                    PhaseCount);
            var orderedFrameInsulators =
                MatchBCabinetFoldInsulatorsToPhases(
                    frameInsulators,
                    bCabinetFoldPhaseIndexes,
                    routeAxisSetting.LengthVector,
                    "第一个基架",
                    true);
            TracePhaseInsulatorMapping(
                "B柜",
                phaseExtensionGroups,
                orderedElevatedBaseInsulators,
                orderedFrameInsulators,
                routeAxisSetting);

            var phaseTemplates = new List<FirstSegmentPhaseTemplate>();
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                phaseTemplates.Add(CreatePhaseTemplate(
                    phaseExtensionGroups[phaseIndex],
                    orderedElevatedBaseInsulators?[phaseIndex],
                    orderedOrdinaryBaseInsulators?[phaseIndex],
                    orderedFrameInsulators[phaseIndex],
                    routeAxisSetting,
                    bFirstBreakpointModelCoordinate,
                    singlePhaseStackCount,
                    bendingRadius,
                    bendAllowance,
                    phaseIndex));
            }

            EnsureLowestPhaseVerticalAvoidance(phaseTemplates);
            var generatedBusbars = ResolveShortestClearancePaths(
                phaseTemplates,
                insulationClearance);
            FaceToWallPreviewTrace.Info(
                "侧对侧第二种柜顶首段-绝缘净距",
                "要求=" + insulationClearance.ToString("0.###") +
                " mm，实际最小=" +
                PlanarBusbarClearanceValidator.Evaluate(
                        generatedBusbars,
                        GetPhaseClearanceGroup,
                        insulationClearance)
                    .MinimumDistance.ToString("0.###") + " mm");
            var aCabinetFirstSegmentBusbars =
                CreateCabinetFirstSegmentBusbars(
                    partCatalog,
                    aCabinetPhaseExtensionGroups,
                    aCabinetOrdinaryBaseComponent,
                    aCabinetElevatedBaseComponent,
                    availableInsulators,
                    aCabinetFoldPhaseIndexes,
                    routeAxisSetting,
                    aLastBreakpointModelCoordinate,
                    singlePhaseStackCount,
                    bendingRadius,
                    bendAllowance,
                    insulationClearance,
                    "A柜");
            var middleConnectionBusbars = createStraightMiddleConnection
                ? CreateMiddleConnectionBusbars(
                    generatedBusbars,
                    aCabinetFirstSegmentBusbars,
                    routeAxisSetting)
                : Array.Empty<PlanarBendBusbarSetting>();
            return new SideToSideSecondTypeFirstSegmentPlan
            {
                BCabinetFirstSegmentBusbars = generatedBusbars,
                ACabinetFirstSegmentBusbars = aCabinetFirstSegmentBusbars,
                MiddleConnectionBusbars = middleConnectionBusbars
            };
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateCabinetFirstSegmentBusbars(
            AssemblyPartCatalog partCatalog,
            IReadOnlyList<List<ModeledBusbar>> phaseExtensionGroups,
            Component2 ordinaryBaseComponent,
            Component2 elevatedBaseComponent,
            IReadOnlyList<InsulatorConnectionInfo> availableInsulators,
            IReadOnlyList<int> bCabinetFoldPhaseIndexes,
            BusbarBridgeAxisSetting routeAxisSetting,
            double cabinetBreakpointModelCoordinate,
            int singlePhaseStackCount,
            double bendingRadius,
            double bendAllowance,
            double insulationClearance,
            string cabinetLabel)
        {
            if (ordinaryBaseComponent == null)
            {
                throw new InvalidOperationException(
                    cabinetLabel + "缺少普通基座，无法生成柜顶首段。");
            }

            var phaseWidthCoordinates = phaseExtensionGroups
                .Select(phaseGroup => phaseGroup.Average(busbar =>
                    BusbarGeometryUtility.Project(
                        busbar.TerminalSegment.P2,
                        routeAxisSetting.WidthVector)))
                .ToList();
            var elevatedBaseInsulators = elevatedBaseComponent == null
                ? new List<InsulatorConnectionInfo>()
                : SelectStrictHostInsulators(
                    partCatalog,
                    elevatedBaseComponent,
                    availableInsulators,
                    routeAxisSetting.LengthVector,
                    cabinetLabel + "增高基座");
            if (elevatedBaseComponent != null &&
                elevatedBaseInsulators.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    cabinetLabel + "存在增高基座，但其内部必须且只能识别三相绝缘子，当前数量=" +
                    elevatedBaseInsulators.Count + "。");
            }
            var orderedElevatedBaseInsulators =
                elevatedBaseInsulators.Count == PhaseCount
                    ? FaceToWallModelBusbarPlanner.MatchInsulatorsToPhases(
                        elevatedBaseInsulators,
                        phaseWidthCoordinates,
                        routeAxisSetting.WidthVector)
                    : null;

            var ordinaryBaseInsulators = SelectStrictHostInsulators(
                partCatalog,
                ordinaryBaseComponent,
                availableInsulators,
                routeAxisSetting.LengthVector,
                cabinetLabel + "普通基座");
            if (ordinaryBaseInsulators.Count != 0 &&
                ordinaryBaseInsulators.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    cabinetLabel + "普通基座内部的绝缘子应为0个或3个，当前数量=" +
                    ordinaryBaseInsulators.Count + "。");
            }
            var orderedOrdinaryBaseInsulators =
                ordinaryBaseInsulators.Count == PhaseCount
                    ? MatchBCabinetFoldInsulatorsToPhases(
                        ordinaryBaseInsulators,
                        bCabinetFoldPhaseIndexes,
                        routeAxisSetting.WidthVector,
                        cabinetLabel + "普通基座",
                        false)
                    : null;
            var baseInsulatorNames = new HashSet<string>(
                elevatedBaseInsulators.Concat(ordinaryBaseInsulators)
                    .Select(insulator =>
                        BusbarGeometryUtility.SafeName(insulator.Component)),
                StringComparer.OrdinalIgnoreCase);
            var baseCenter =
                BusbarGeometryUtility.GetComponentCenter(
                    ordinaryBaseComponent);
            if (baseCenter == null)
            {
                throw new InvalidOperationException(
                    "无法读取" + cabinetLabel + "普通基座中心。");
            }
            var firstFrameComponent = FaceToWallModelBusbarPlanner
                .SelectNearestFrameHostingInsulators(
                    partCatalog,
                    baseCenter,
                    availableInsulators,
                    routeAxisSetting.HeightVector,
                    baseInsulatorNames,
                    PhaseCount);
            if (firstFrameComponent == null)
            {
                throw new InvalidOperationException(
                    cabinetLabel + "附近没有识别到包含三相绝缘子的第一个基架。");
            }
            var frameInsulators =
                FaceToWallModelBusbarPlanner.SelectHostInsulators(
                    partCatalog,
                    firstFrameComponent,
                    availableInsulators,
                    routeAxisSetting.HeightVector,
                    baseInsulatorNames,
                    cabinetLabel + "第一个基架",
                    PhaseCount);
            var orderedFrameInsulators =
                MatchBCabinetFoldInsulatorsToPhases(
                    frameInsulators,
                    bCabinetFoldPhaseIndexes,
                    routeAxisSetting.LengthVector,
                    cabinetLabel + "第一个基架",
                    true);
            TracePhaseInsulatorMapping(
                cabinetLabel,
                phaseExtensionGroups,
                orderedElevatedBaseInsulators,
                orderedFrameInsulators,
                routeAxisSetting);

            var phaseTemplates = new List<FirstSegmentPhaseTemplate>();
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                phaseTemplates.Add(CreatePhaseTemplate(
                    phaseExtensionGroups[phaseIndex],
                    orderedElevatedBaseInsulators?[phaseIndex],
                    orderedOrdinaryBaseInsulators?[phaseIndex],
                    orderedFrameInsulators[phaseIndex],
                    routeAxisSetting,
                    cabinetBreakpointModelCoordinate,
                    singlePhaseStackCount,
                    bendingRadius,
                    bendAllowance,
                    phaseIndex));
            }
            EnsureLowestPhaseVerticalAvoidance(phaseTemplates);
            return ResolveShortestClearancePaths(
                    phaseTemplates,
                    insulationClearance)
                .AsReadOnly();
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateMiddleConnectionBusbars(
            IReadOnlyList<PlanarBendBusbarSetting> bCabinetFirstSegments,
            IReadOnlyList<PlanarBendBusbarSetting> aCabinetFirstSegments,
            BusbarBridgeAxisSetting routeAxisSetting)
        {
            var bMembersByKey = bCabinetFirstSegments.ToDictionary(
                setting => setting.GroupKey,
                StringComparer.OrdinalIgnoreCase);
            var aMembersByKey = aCabinetFirstSegments.ToDictionary(
                setting => setting.GroupKey,
                StringComparer.OrdinalIgnoreCase);
            if (bMembersByKey.Count != aMembersByKey.Count ||
                bMembersByKey.Keys.Any(key => !aMembersByKey.ContainsKey(key)))
            {
                throw new InvalidOperationException(
                    "侧对侧第二种 A/B 柜首段的三相叠排成员无法一一对应。");
            }

            var middleConnectionBusbars =
                new List<PlanarBendBusbarSetting>();
            foreach (var bMemberPair in bMembersByKey.OrderBy(pair => pair.Key))
            {
                var bMember = bMemberPair.Value;
                var aMember = aMembersByKey[bMemberPair.Key];
                if (bMember.PointList == null || bMember.PointList.Count < 2 ||
                    aMember.PointList == null || aMember.PointList.Count < 2)
                {
                    throw new InvalidOperationException(
                        bMemberPair.Key + "首段缺少可用于中间垂直搭接的6、7点。");
                }

                var bTerminalPoint = bMember.PointList.Last();
                var aTerminalPoint = aMember.PointList.Last();
                var bTerminalDirection = new Vector3d(
                    bMember.PointList[bMember.PointList.Count - 2],
                    bTerminalPoint).Normalized;
                var aTerminalDirection = new Vector3d(
                    aMember.PointList[aMember.PointList.Count - 2],
                    aTerminalPoint).Normalized;
                if (!BusbarGeometryUtility.AreParallel(
                        bTerminalDirection,
                        routeAxisSetting.LengthVector) ||
                    !BusbarGeometryUtility.AreParallel(
                        aTerminalDirection,
                        routeAxisSetting.LengthVector))
                {
                    throw new InvalidOperationException(
                        bMemberPair.Key + "两侧首段的6→7段没有沿长度轴，无法垂直搭接。");
                }

                var busbarWidth = bMember.BusBarWidth;
                var busbarThickness = bMember.BusBarThickness;
                var bLapCenter = PlanarBendBusbarSetting.Translate(
                    bTerminalPoint,
                    bTerminalDirection,
                    -busbarWidth / 2d);
                var aLapCenter = PlanarBendBusbarSetting.Translate(
                    aTerminalPoint,
                    aTerminalDirection,
                    -busbarWidth / 2d);
                var bLapLengthCoordinate = BusbarGeometryUtility.Project(
                    bLapCenter,
                    routeAxisSetting.LengthVector);
                var aLapLengthCoordinate = BusbarGeometryUtility.Project(
                    aLapCenter,
                    routeAxisSetting.LengthVector);
                if (Math.Abs(
                        bLapLengthCoordinate -
                        aLapLengthCoordinate) > 2d)
                {
                    throw new InvalidOperationException(
                        bMemberPair.Key +
                        "两侧首段对应的基架搭接中心未位于同一相位长度位置，偏差=" +
                        Math.Abs(bLapLengthCoordinate - aLapLengthCoordinate)
                            .ToString("0.###") + " mm。");
                }
                var commonLapLengthCoordinate =
                    (bLapLengthCoordinate + aLapLengthCoordinate) / 2d;
                bLapCenter = BusbarGeometryUtility.WithProjection(
                    bLapCenter,
                    routeAxisSetting.LengthVector,
                    commonLapLengthCoordinate);
                aLapCenter = BusbarGeometryUtility.WithProjection(
                    aLapCenter,
                    routeAxisSetting.LengthVector,
                    commonLapLengthCoordinate);

                var signedCabinetWidthSpan = BusbarGeometryUtility.Project(
                        aLapCenter,
                        routeAxisSetting.WidthVector) -
                    BusbarGeometryUtility.Project(
                        bLapCenter,
                        routeAxisSetting.WidthVector);
                var cabinetWidthTravelSign = Math.Sign(signedCabinetWidthSpan);
                if (cabinetWidthTravelSign == 0d)
                {
                    throw new InvalidOperationException(
                        bMemberPair.Key + "两侧首段没有形成有效的柜间宽度距离。");
                }
                var middleStartPoint = PlanarBendBusbarSetting.Translate(
                    bLapCenter,
                    routeAxisSetting.WidthVector,
                    -cabinetWidthTravelSign * busbarWidth / 2d);
                var middleEndPoint = PlanarBendBusbarSetting.Translate(
                    aLapCenter,
                    routeAxisSetting.WidthVector,
                    cabinetWidthTravelSign * busbarWidth / 2d);
                middleStartPoint = PlanarBendBusbarSetting.Translate(
                    middleStartPoint,
                    routeAxisSetting.HeightVector,
                    -busbarThickness);
                middleEndPoint = PlanarBendBusbarSetting.Translate(
                    middleEndPoint,
                    routeAxisSetting.HeightVector,
                    -busbarThickness);

                var middlePathDirection = new Vector3d(
                    middleStartPoint,
                    middleEndPoint).Normalized;
                var middleThicknessDirection = middlePathDirection
                    .Cross(routeAxisSetting.LengthVector)
                    .Normalized;
                var middleSetting = bMember.Copy();
                middleSetting.GroupKey = bMemberPair.Key + "-中间";
                middleSetting.StartBusbarVector =
                    middleThicknessDirection.Copy();
                middleSetting.WidthVector =
                    routeAxisSetting.LengthVector.Copy();
                middleSetting.PointList = new List<Point3d>
                {
                    middleStartPoint,
                    middleEndPoint
                };
                middleSetting.PreviewBendPointKinds.Clear();
                middleConnectionBusbars.Add(middleSetting);
            }
            return middleConnectionBusbars.AsReadOnly();
        }

        internal static IReadOnlyList<SideToSideCopperPiece> CreateIndependentCabinetConnections(SideToSideCopperPlanningInput input)
        {
            if (input == null || input.FixedPhaseChange || input.A == null || input.B == null)
                throw new ArgumentException("七点首段只接受侧对侧基座2柜端快照。");
            FaceToWallModelBusbarPlanner.ValidateSinglePhaseStackCount(input.RootCount);
            var axis = BusbarBridgeAxisSetting.CreateFromVectors(new Vector3d(0, 0, -1),
                new Vector3d(0, 1, 0), new Vector3d(1, 0, 0));
            Func<SideToSideCabinetGeometry, List<List<ModeledBusbar>>> groups = cabinet =>
                Enumerable.Range(0, PhaseCount).Select(phase => (cabinet.Extensions ?? Array.Empty<SideToSideExtensionGeometry>())
                    .Where(e => e.Phase.ToString() == GetPhaseName(phase)).Select(e => e.ToBar()).ToList()).ToList();
            var bGroups = groups(input.B); var aGroups = groups(input.A);
            if (bGroups.Concat(aGroups).Any(g => g.Count != input.RootCount - 1 || g.Count == 0))
                throw new InvalidOperationException("七点首段实际伸出排数量必须满足每相E=N−1。");
            var order = ResolveFoldPhaseIndexes(bGroups, aGroups, axis.WidthVector);
            var result = new List<SideToSideCopperPiece>();
            for (var side = 0; side < 2; side++)
            {
                var cabinet = side == 0 ? input.B : input.A;
                var phaseGroups = side == 0 ? bGroups : aGroups;
                var foldingOrder = side == 0 ? order.BCabinetNearestToFarthestPhaseIndexes : order.ACabinetNearestToFarthestPhaseIndexes;
                var elevated = cabinet.ElevatedConnections?.Select(c => c.ToConnection()).ToList();
                var ordinary = cabinet.OrdinaryConnections?.Select(c => c.ToConnection()).ToList();
                if (elevated == null || ordinary == null || (elevated.Count != 0 && elevated.Count != 3) ||
                    (ordinary.Count != 0 && ordinary.Count != 3) || elevated.Count + ordinary.Count == 0)
                    throw new InvalidOperationException("基座2普通/增高基座必须各自具有零个或完整三相实际连接接口，且不能同时为空。");
                var elevatedByPhase = elevated.Count == 0 ? null : FaceToWallModelBusbarPlanner.MatchInsulatorsToPhases(
                    elevated, phaseGroups.Select(g => g.Average(bar => BusbarGeometryUtility.Project(bar.TerminalSegment.P2, axis.WidthVector))).ToList(), axis.WidthVector);
                // 规范宽度轴为B→A：B柜最近接口取最大X，A柜最近接口取最小X。
                var ordinaryByPhase = ordinary.Count == 0 ? null : MatchBCabinetFoldInsulatorsToPhases(
                    ordinary, foldingOrder, axis.WidthVector, side == 0 ? "B普通基座" : "A普通基座", side == 1);
                var contactHeight = cabinet.CalculateConnectionHeight();
                var targets = Enumerable.Range(0, PhaseCount).Select(phase =>
                {
                    var thickness = phaseGroups[phase][0].CenterlineSetting.BusBarThickness;
                    var envelope = (2d * input.RootCount - 1d) * thickness;
                    // 沿用七点首段叠排补偿：接触面→叠层中心→端部搭接上一排厚。
                    return new Point3d(cabinet.Opening.X, contactHeight + envelope / 2d + thickness,
                        cabinet.Opening.Z + (1 - foldingOrder.ToList().IndexOf(phase)) * input.PhaseSpacing);
                }).ToArray();
                foreach (var shape in CreateFromCopperLapCenters(phaseGroups, elevatedByPhase, ordinaryByPhase,
                    axis, targets, input.RootCount, Math.Max(10d, input.Thickness), 20d, input.Clearance,
                    cabinet.ElevatedOrdinaryJoint?.Y))
                    result.Add(new SideToSideCopperPiece(side == 0 ? "B" : "A", shape.GroupKey.Substring(0, 1), shape));
            }
            return result.AsReadOnly();
        }

        /// <summary>按已经独立确定的铜排搭接中心计算单柜七点首段，不读取或构造基架/架内绝缘子。</summary>
        internal static IReadOnlyList<PlanarBendBusbarSetting> CreateFromCopperLapCenters(
            IReadOnlyList<List<ModeledBusbar>> phaseGroups,
            IReadOnlyList<InsulatorConnectionInfo> elevatedByPhase,
            IReadOnlyList<InsulatorConnectionInfo> ordinaryByPhase,
            BusbarBridgeAxisSetting axis,
            IReadOnlyList<Point3d> copperLapCenters,
            int rootCount, double bendingRadius, double bendAllowance, double clearance,
            double? jointBendHeight)
        {
            if (phaseGroups == null || phaseGroups.Count != PhaseCount || copperLapCenters == null ||
                copperLapCenters.Count != PhaseCount || copperLapCenters.Any(p => p == null) || axis == null ||
                (elevatedByPhase != null && elevatedByPhase.Count != PhaseCount) ||
                (ordinaryByPhase != null && ordinaryByPhase.Count != PhaseCount) ||
                (elevatedByPhase == null && ordinaryByPhase == null))
                throw new ArgumentException("七点首段需要完整三相伸出排、柜端连接接口和独立铜排搭接中心。");
            FaceToWallModelBusbarPlanner.ValidateSinglePhaseStackCount(rootCount);
            if (new[] { bendingRadius, bendAllowance, clearance }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                bendingRadius <= 0 || bendAllowance < 0 || clearance < 125)
                throw new ArgumentException("七点首段折弯或净距参数无效。");
            if (elevatedByPhase != null && (!jointBendHeight.HasValue ||
                double.IsNaN(jointBendHeight.Value) || double.IsInfinity(jointBendHeight.Value)))
                throw new InvalidOperationException("增高基座与基座2的已核验连接高度缺失，无法定位三相共同折弯起点。");
            var templates = Enumerable.Range(0, PhaseCount).Select(phase => CreatePhaseTemplate(
                phaseGroups[phase], elevatedByPhase?[phase], ordinaryByPhase?[phase], null,
                axis, double.NaN, rootCount, bendingRadius, bendAllowance, phase,
                copperLapCenters[phase], jointBendHeight)).ToList();
            EnsureLowestPhaseVerticalAvoidance(templates);
            return ResolveShortestClearancePaths(templates, clearance).AsReadOnly();
        }

        private static FirstSegmentPhaseTemplate CreatePhaseTemplate(
            IReadOnlyList<ModeledBusbar> sourcePhaseBusbars,
            InsulatorConnectionInfo elevatedBaseInsulator,
            InsulatorConnectionInfo ordinaryBaseInsulator,
            InsulatorConnectionInfo frameInsulator,
            BusbarBridgeAxisSetting routeAxisSetting,
            double cabinetBreakpointModelCoordinate,
            int singlePhaseStackCount,
            double bendingRadius,
            double bendAllowance,
            int phaseIndex,
            Point3d plannedCopperLapCenter = null,
            double? jointBendHeight = null)
        {
            if (elevatedBaseInsulator != null)
            {
                FaceToWallModelBusbarPlanner.ValidateInsulatorConnectionGeometry(
                    elevatedBaseInsulator,
                    "增高基座",
                    phaseIndex);
            }
            if (ordinaryBaseInsulator != null)
            {
                FaceToWallModelBusbarPlanner.ValidateInsulatorConnectionGeometry(
                    ordinaryBaseInsulator,
                    "普通基座",
                    phaseIndex);
            }
            if (plannedCopperLapCenter == null) FaceToWallModelBusbarPlanner.ValidateInsulatorConnectionGeometry(frameInsulator, "第一个基架", phaseIndex);
            else if (new[] { plannedCopperLapCenter.X, plannedCopperLapCenter.Y, plannedCopperLapCenter.Z }
                .Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("独立铜排搭接中心必须是有限坐标。", nameof(plannedCopperLapCenter));
            if (sourcePhaseBusbars == null || sourcePhaseBusbars.Count == 0)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相缺少柜顶伸出排，无法生成第二种柜顶首段。");
            }

            var busbarThickness = sourcePhaseBusbars.Average(busbar =>
                busbar.CenterlineSetting.BusBarThickness);
            var busbarWidth = sourcePhaseBusbars.Average(busbar =>
                busbar.CenterlineSetting.BusBarWidth);
            if (busbarThickness <= GeometryTolerance ||
                busbarWidth <= busbarThickness)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相柜顶伸出排规格无效。");
            }
            if (elevatedBaseInsulator != null &&
                sourcePhaseBusbars.Count != singlePhaseStackCount - 1)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相增高基座夹接要求柜顶已有排数量等于单相根数减1，" +
                    "当前柜顶=" + sourcePhaseBusbars.Count +
                    "根，单相根数=" + singlePhaseStackCount + "。");
            }

            var startThicknessDirection = FaceToWallModelBusbarPlanner.AverageAlignedVector(
                sourcePhaseBusbars.Select(busbar =>
                    busbar.CenterlineSetting.StartBusbarVector));
            var startWidthDirection = FaceToWallModelBusbarPlanner.AverageAlignedVector(
                sourcePhaseBusbars.Select(busbar =>
                    busbar.CenterlineSetting.WidthVector));
            if (startThicknessDirection == null ||
                startWidthDirection == null)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相柜顶伸出排的排厚或排宽方向无效。");
            }
            if (!BusbarGeometryUtility.AreParallel(
                    startWidthDirection,
                    routeAxisSetting.WidthVector))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相柜顶伸出排排宽方向与侧对侧宽度轴不平行。");
            }

            var fixedExtensionPoint = BusbarGeometryUtility.Average(
                sourcePhaseBusbars.Select(busbar =>
                    busbar.TerminalSegment.P2));
            if (fixedExtensionPoint == null)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相柜顶伸出排没有可用的端头中心点。");
            }
            var stackEnvelopeThickness =
                (2d * singlePhaseStackCount - 1d) * busbarThickness;
            var lapSurfaceOffset = FaceToWallModelBusbarPlanner
                .CalculateCabinetTopLapReferenceOffset(
                    singlePhaseStackCount,
                    sourcePhaseBusbars.Count,
                    busbarThickness);
            var cabinetTopLapReferencePoint =
                PlanarBendBusbarSetting.Translate(
                    fixedExtensionPoint,
                    startThicknessDirection,
                    lapSurfaceOffset);

            // 用户确认的折弯伸出余量：单根排厚 + 折弯半径。
            var bendExtension = busbarThickness + bendingRadius;
            var firstPoint = PlanarBendBusbarSetting.Translate(
                cabinetTopLapReferencePoint,
                routeAxisSetting.HeightVector,
                -busbarWidth);
            var secondPoint = PlanarBendBusbarSetting.Translate(
                cabinetTopLapReferencePoint,
                routeAxisSetting.HeightVector,
                bendExtension);

            var frameOutwardNormal = plannedCopperLapCenter == null
                ? FaceToWallModelBusbarPlanner.ResolveConnectionOutwardNormal(frameInsulator) : routeAxisSetting.HeightVector.Copy();
            var elevatedBaseTransitionLengthSign = 0d;
            var elevatedBaseTerminalLengthSign = 0d;
            var elevatedLapExitBoundaryHeightCoordinate = double.NaN;
            var elevatedInsideDirectionSign = 0d;
            Point3d elevatedEntryExtensionExit = null;
            Point3d elevatedEntryInsulatorStart = null;
            if (elevatedBaseInsulator != null)
            {
                var elevatedBaseOutwardNormal = FaceToWallModelBusbarPlanner.ResolveConnectionOutwardNormal(
                    elevatedBaseInsulator);
                if (!BusbarGeometryUtility.AreParallel(
                        elevatedBaseOutwardNormal,
                        routeAxisSetting.LengthVector))
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseIndex) +
                        "相增高基座绝缘子面法向与排长方向不平行，无法确定3、4点折向。");
                }
                // 增高基座处和顶部搭接处必须分别取向：
                // 3/4点沿绝缘子配合面法向的反向偏移，使4→5从最近侧接入；
                // 6→7保持沿当前法向侧搭接，不得被3/4点的取向联动翻转。
                elevatedBaseTransitionLengthSign =
                    elevatedBaseOutwardNormal.Negate().Dot(
                        routeAxisSetting.LengthVector) >= 0d ? 1d : -1d;
                elevatedBaseTerminalLengthSign =
                    elevatedBaseOutwardNormal.Dot(
                        routeAxisSetting.LengthVector) >= 0d ? 1d : -1d;
                var elevatedStackCenterPoint =
                    PlanarBendBusbarSetting.Translate(
                        elevatedBaseInsulator.ConnectionCircleCenter,
                        elevatedBaseOutwardNormal,
                        stackEnvelopeThickness / 2d);
                var extensionLengthCoordinate = BusbarGeometryUtility.Project(
                    cabinetTopLapReferencePoint,
                    routeAxisSetting.LengthVector);
                var extensionWidthCoordinate = BusbarGeometryUtility.Project(
                    cabinetTopLapReferencePoint,
                    routeAxisSetting.WidthVector);
                var elevatedInsulatorLengthCoordinate =
                    BusbarGeometryUtility.Project(
                        elevatedStackCenterPoint,
                        routeAxisSetting.LengthVector);
                var elevatedInsulatorWidthCoordinate =
                    BusbarGeometryUtility.Project(
                        elevatedBaseInsulator.ConnectionCircleCenter,
                        routeAxisSetting.WidthVector);
                var centerlineLapOffset = Math.Abs(
                    extensionLengthCoordinate -
                    elevatedInsulatorLengthCoordinate);
                // cabinetTopLapReferencePoint 已经按柜顶叠排的搭接面偏移过；
                // elevatedStackCenterPoint 也已经按增高基座叠排半包络补偿到连接排中心。
                // 因此两者在长度轴上应当重合。实际的一个排厚间距由中间搭接排本身形成，
                // 这里若再次要求中心线相差一个排厚，会把合法的 8 mm 贴面搭接重复计算一次。
                var elevatedWidthDeviation = Math.Abs(
                    extensionWidthCoordinate - elevatedInsulatorWidthCoordinate);
                if ((centerlineLapOffset > 2d && plannedCopperLapCenter == null) ||
                    elevatedWidthDeviation > 2d)
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseIndex) +
                        "相增高基座绝缘子补偿后的叠层中心与柜顶连接排中心线" +
                        "未重合（容差 2 mm），中心线距离=" +
                        centerlineLapOffset.ToString("0.0") +
                        " mm，宽度偏差=" +
                        elevatedWidthDeviation.ToString("0.0") + " mm。");
                }

                if (plannedCopperLapCenter != null && centerlineLapOffset > 0.05d)
                    elevatedEntryExtensionExit = secondPoint.Copy();
                else
                    firstPoint = BusbarGeometryUtility.WithProjection(
                        firstPoint,
                        routeAxisSetting.LengthVector,
                        elevatedInsulatorLengthCoordinate);
                secondPoint = BusbarGeometryUtility.WithProjection(
                    secondPoint,
                    routeAxisSetting.LengthVector,
                    elevatedInsulatorLengthCoordinate);

                var elevatedCircleHeightCoordinate =
                    BusbarGeometryUtility.Project(
                        elevatedBaseInsulator.ConnectionCircleCenter,
                        routeAxisSetting.HeightVector);
                var frameCircleHeightCoordinate =
                    BusbarGeometryUtility.Project(
                        plannedCopperLapCenter ?? frameInsulator.ConnectionCircleCenter,
                        routeAxisSetting.HeightVector);
                elevatedInsideDirectionSign = Math.Sign(
                    frameCircleHeightCoordinate -
                    elevatedCircleHeightCoordinate);
                if (elevatedInsideDirectionSign == 0d)
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseIndex) +
                        "相无法根据增高基座与第一个基架判断柜内侧方向。");
                }
                elevatedLapExitBoundaryHeightCoordinate =
                    elevatedCircleHeightCoordinate +
                    elevatedInsideDirectionSign * busbarWidth / 2d;
                if (elevatedEntryExtensionExit != null)
                {
                    var entryHeight = elevatedCircleHeightCoordinate -
                        elevatedInsideDirectionSign *
                        (busbarWidth / 2d + bendExtension);
                    var extensionExitHeight = BusbarGeometryUtility.Project(
                        elevatedEntryExtensionExit,
                        routeAxisSetting.HeightVector);
                    if (elevatedInsideDirectionSign *
                        (entryHeight - extensionExitHeight) +
                        GeometryTolerance < bendExtension)
                        throw new InvalidOperationException(
                            GetPhaseName(phaseIndex) +
                            "相柜顶伸出排与增高基座绝缘子不平齐，接入折弯空间不足。");
                    elevatedEntryInsulatorStart = BusbarGeometryUtility.WithProjection(
                        secondPoint,
                        routeAxisSetting.HeightVector,
                        entryHeight);
                }
                secondPoint = BusbarGeometryUtility.WithProjection(
                    secondPoint,
                    routeAxisSetting.HeightVector,
                    elevatedLapExitBoundaryHeightCoordinate +
                    elevatedInsideDirectionSign * bendExtension);
                FaceToWallPreviewTrace.Info(
                    "侧对侧第二种-增高基座夹接",
                    "相=" + GetPhaseName(phaseIndex) +
                    "，单相根数=" + singlePhaseStackCount +
                    "，柜顶已有=" + sourcePhaseBusbars.Count +
                    "，夹接中心偏移=" +
                    lapSurfaceOffset.ToString("0.###") +
                    " mm，柜内方向=" +
                    elevatedInsideDirectionSign.ToString("0") +
                    "，点2超过搭接区=" +
                    bendExtension.ToString("0.###") + " mm");
                var expectedStartThicknessDirection =
                    new Vector3d(elevatedEntryInsulatorStart ?? firstPoint,
                        secondPoint).Normalized
                        .Cross(startWidthDirection);
                if (!BusbarGeometryUtility.AreParallel(
                        expectedStartThicknessDirection,
                        elevatedBaseOutwardNormal))
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseIndex) +
                        "相柜顶首段排厚方向与增高基座绝缘子配合面法向不平行。");
                }
                if (plannedCopperLapCenter != null)
                {
                    var exitHeight = BusbarGeometryUtility.Project(
                        secondPoint, routeAxisSetting.HeightVector);
                    if (!jointBendHeight.HasValue ||
                        elevatedInsideDirectionSign * (jointBendHeight.Value - exitHeight) +
                        GeometryTolerance < bendExtension)
                        throw new InvalidOperationException(GetPhaseName(phaseIndex) +
                            "相增高基座与基座2连接点未在绝缘子出口后保留折弯余量。");
                    secondPoint = BusbarGeometryUtility.WithProjection(
                        secondPoint, routeAxisSetting.HeightVector, jointBendHeight.Value);
                }
            }

            Point3d thirdPoint = null;
            Point3d fourthPoint = null;
            var transitionStartPoint = secondPoint;
            var fifthReferencePoint = secondPoint;
            var hasOrdinaryBaseLapPoints = false;
            if (ordinaryBaseInsulator != null)
            {
                var ordinaryBaseOutwardNormal = FaceToWallModelBusbarPlanner.ResolveConnectionOutwardNormal(
                    ordinaryBaseInsulator);
                var baseStackCenterPoint = PlanarBendBusbarSetting.Translate(
                    ordinaryBaseInsulator.ConnectionCircleCenter,
                    ordinaryBaseOutwardNormal,
                    stackEnvelopeThickness / 2d);
                var secondLengthCoordinate = BusbarGeometryUtility.Project(
                    secondPoint,
                    routeAxisSetting.LengthVector);
                var ordinaryBaseLengthCoordinate =
                    BusbarGeometryUtility.Project(
                        baseStackCenterPoint,
                        routeAxisSetting.LengthVector);
                var ordinaryBaseLengthDeviation = Math.Abs(
                    ordinaryBaseLengthCoordinate - secondLengthCoordinate);
                hasOrdinaryBaseLapPoints =
                    ShouldCreateOrdinaryBaseLapPoints(
                        ordinaryBaseLengthDeviation,
                        busbarThickness);
                FaceToWallPreviewTrace.Info(
                    "侧对侧第二种-普通基座3/4点判定",
                    "相=" + GetPhaseName(phaseIndex) +
                    "，柜顶点2与普通基座长度轴偏差=" +
                    ordinaryBaseLengthDeviation.ToString("0.###") +
                    " mm，排厚=" + busbarThickness.ToString("0.###") +
                    " mm，处理=" +
                    (hasOrdinaryBaseLapPoints
                        ? "独立生成点3、4"
                        : "点2与基座定位归并"));
                if (hasOrdinaryBaseLapPoints)
                {
                    thirdPoint = BusbarGeometryUtility.WithProjection(
                        secondPoint,
                        routeAxisSetting.LengthVector,
                        ordinaryBaseLengthCoordinate);
                    thirdPoint = BusbarGeometryUtility.WithProjection(
                        thirdPoint,
                        routeAxisSetting.WidthVector,
                        BusbarGeometryUtility.Project(
                            ordinaryBaseInsulator.ConnectionCircleCenter,
                            routeAxisSetting.WidthVector));
                    var baseCircleHalfSpan = FaceToWallModelBusbarPlanner.GetConnectionCircleHalfSpan(
                        ordinaryBaseInsulator,
                        thirdPoint,
                        routeAxisSetting.WidthVector,
                        "普通基座",
                        phaseIndex);
                    var thirdHeightCoordinate = BusbarGeometryUtility.Project(
                        thirdPoint,
                        routeAxisSetting.HeightVector);
                    var baseCircleHeightCoordinate =
                        BusbarGeometryUtility.Project(
                            ordinaryBaseInsulator.ConnectionCircleCenter,
                            routeAxisSetting.HeightVector);
                    var baseTravelSign = baseCircleHeightCoordinate >=
                        thirdHeightCoordinate ? 1d : -1d;
                    var baseCircleEntryHeightCoordinate =
                        baseCircleHeightCoordinate -
                        baseTravelSign * baseCircleHalfSpan;
                    var baseEntryReserve = Math.Abs(
                        baseCircleEntryHeightCoordinate -
                        thirdHeightCoordinate);
                    if (baseEntryReserve + GeometryTolerance < bendExtension)
                    {
                        FaceToWallPreviewTrace.Warning(
                            "侧对侧第二种-固定型式预览警告",
                            GetPhaseName(phaseIndex) +
                            "相柜顶到普通基座绝缘子搭接区前仅余 " +
                            baseEntryReserve.ToString("0.###") +
                            " mm，小于排厚+折弯半径 " +
                            bendExtension.ToString("0.###") +
                            " mm；固定型式继续预览。");
                    }

                    var fourthHeightCoordinate =
                        baseCircleHeightCoordinate +
                        baseTravelSign *
                        (baseCircleHalfSpan + bendExtension);
                    fourthPoint = BusbarGeometryUtility.WithProjection(
                        thirdPoint,
                        routeAxisSetting.HeightVector,
                        fourthHeightCoordinate);
                    var ordinaryLapThicknessDirection =
                        new Vector3d(thirdPoint, fourthPoint).Normalized
                            .Cross(startWidthDirection);
                    if (!BusbarGeometryUtility.AreParallel(
                            ordinaryLapThicknessDirection,
                            ordinaryBaseOutwardNormal))
                    {
                        throw new InvalidOperationException(
                            GetPhaseName(phaseIndex) +
                            "相点3→4排厚方向与普通基座绝缘子配合面法向不平行。");
                    }
                    transitionStartPoint = fourthPoint;
                    fifthReferencePoint = fourthPoint;
                }
            }

            var frameStackCenterPoint = plannedCopperLapCenter?.Copy() ?? PlanarBendBusbarSetting.Translate(
                frameInsulator.ConnectionCircleCenter, frameOutwardNormal, stackEnvelopeThickness / 2d);
            var finalRouteHeightCoordinate = BusbarGeometryUtility.Project(
                frameStackCenterPoint,
                routeAxisSetting.HeightVector) + (plannedCopperLapCenter == null ? busbarThickness : 0d);
            if (ordinaryBaseInsulator == null)
            {
                if (elevatedBaseTransitionLengthSign == 0d)
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseIndex) +
                        "相缺少增高基座绝缘子面法向，无法确定3、4点折向。");
                }

                var secondLengthCoordinate = BusbarGeometryUtility.Project(
                    secondPoint,
                    routeAxisSetting.LengthVector);
                var secondHeightCoordinate = BusbarGeometryUtility.Project(
                    secondPoint,
                    routeAxisSetting.HeightVector);
                var signedAvailableRise = finalRouteHeightCoordinate -
                    secondHeightCoordinate;
                if (Math.Abs(signedAvailableRise) <= GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseIndex) +
                        "相绝缘子到中间排没有可用于七点路径的高度距离。");
                }

                // 客户图七点路径：3、4点统一沿增高基座绝缘子配合面法向
                // 错开一个排宽；从点2到点6的高度按1:2:2:1分配，得到
                // 点3/4/5的累计高度1/6、3/6、5/6。
                thirdPoint = BusbarGeometryUtility.WithProjection(
                    secondPoint,
                    routeAxisSetting.LengthVector,
                    secondLengthCoordinate +
                    elevatedBaseTransitionLengthSign * busbarWidth);
                thirdPoint = BusbarGeometryUtility.WithProjection(
                    thirdPoint,
                    routeAxisSetting.HeightVector,
                    secondHeightCoordinate + signedAvailableRise / 6d);
                fourthPoint = BusbarGeometryUtility.WithProjection(
                    thirdPoint,
                    routeAxisSetting.HeightVector,
                    secondHeightCoordinate + signedAvailableRise * 3d / 6d);
                fifthReferencePoint = BusbarGeometryUtility.WithProjection(
                    fourthPoint,
                    routeAxisSetting.HeightVector,
                    secondHeightCoordinate + signedAvailableRise * 5d / 6d);
                var directJointRoute = plannedCopperLapCenter != null &&
                    elevatedBaseInsulator != null;
                transitionStartPoint = directJointRoute ? secondPoint : fourthPoint;
                if (directJointRoute)
                    fifthReferencePoint = secondPoint;
                hasOrdinaryBaseLapPoints = !directJointRoute;
            }
            if (finalRouteHeightCoordinate <=
                BusbarGeometryUtility.Project(
                    transitionStartPoint,
                    routeAxisSetting.HeightVector) + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相铜排 6/7 点高度 " +
                    finalRouteHeightCoordinate.ToString("0.###") +
                    " mm 未位于前一定位点的高度轴上升方向；无法形成上升避让段。");
            }
            var transitionStartLengthCoordinate = BusbarGeometryUtility.Project(
                transitionStartPoint,
                routeAxisSetting.LengthVector);
            var frameLapCenterLengthCoordinate = BusbarGeometryUtility.Project(
                frameStackCenterPoint,
                routeAxisSetting.LengthVector);
            // 侧对侧首段的基架顺序沿宽度方向判断，但 6→7 段仍沿
            // 绝缘子配合面法向所在的实际长度轴生成。不能再用总装全局
            // 断点长度坐标覆盖首个基架绝缘子的实际配合圆心，否则 A/B
            // 两侧会被强行推到不同的长度位置，形成固定的虚假偏差。
            FaceToWallPreviewTrace.Info(
                "侧对侧第二种-基架绝缘子实际搭接定位",
                "相=" + GetPhaseName(phaseIndex) +
                "，配合圆心长度坐标=" +
                frameLapCenterLengthCoordinate.ToString("0.###") +
                "，配合圆心宽度坐标=" +
                BusbarGeometryUtility.Project(
                    plannedCopperLapCenter ?? frameInsulator.ConnectionCircleCenter,
                    routeAxisSetting.WidthVector).ToString("0.###") +
                "，忽略全局断点长度坐标=" +
                (double.IsNaN(cabinetBreakpointModelCoordinate) ||
                 double.IsInfinity(cabinetBreakpointModelCoordinate)
                    ? "无效"
                    : cabinetBreakpointModelCoordinate.ToString("0.###")));
            var lengthTravelSign = elevatedBaseTerminalLengthSign != 0d
                ? elevatedBaseTerminalLengthSign
                : (frameLapCenterLengthCoordinate >=
                    transitionStartLengthCoordinate ? 1d : -1d);

            // 先把首个基架绝缘子上的横向铜排作为模拟搭接排；6→7 段沿长度轴
            // 与它垂直相交。6点在搭接区近侧边缘外保留一个折弯余量，
            // 7点正好终止在远侧的排宽边，保证满搭接且端头不突出。
            var seventhPoint = BusbarGeometryUtility.WithProjection(
                transitionStartPoint,
                routeAxisSetting.HeightVector,
                finalRouteHeightCoordinate);
            seventhPoint = BusbarGeometryUtility.WithProjection(
                seventhPoint,
                routeAxisSetting.LengthVector,
                frameLapCenterLengthCoordinate +
                lengthTravelSign * busbarWidth / 2d);

            var seventhLengthCoordinate = BusbarGeometryUtility.Project(
                seventhPoint,
                routeAxisSetting.LengthVector);
            var requiredTerminalOverlapLength =
                busbarWidth + bendExtension;
            var requiredSixthLengthCoordinate =
                seventhLengthCoordinate -
                lengthTravelSign * requiredTerminalOverlapLength;
            var upperDiagonalLengthSpan = Math.Abs(
                requiredSixthLengthCoordinate -
                transitionStartLengthCoordinate);
            if (upperDiagonalLengthSpan + GeometryTolerance < bendExtension)
            {
                FaceToWallPreviewTrace.Warning(
                    "侧对侧第二种-固定型式预览警告",
                    GetPhaseName(phaseIndex) +
                    "相按增高基座绝缘子面法向生成6→7后，4→5仅余 " +
                    upperDiagonalLengthSpan.ToString("0.###") +
                    " mm，小于折弯余量 " +
                    bendExtension.ToString("0.###") +
                    " mm；固定型式继续预览。");
            }
            var upperDiagonalSignedLength =
                requiredSixthLengthCoordinate -
                transitionStartLengthCoordinate;
            var approachLengthGrowthCoordinateSign = -lengthTravelSign;
            var approachGrowthExtendsUpperDiagonal =
                upperDiagonalSignedLength *
                approachLengthGrowthCoordinateSign >= 0d;
            // 换向前，增加6→7中心线长度会缩短4→5，所以需要受折弯
            // 余量限制；整组三相换向后，增加长度反而会把4→5继续拉长，
            // 此时不能继续沿用旧上限。额外放行一个叠排包络厚度，只用于
            // 补偿各叠排成员沿排厚方向偏移造成的端长差，最终仍取所需最短值。
            var availableMaximumApproachLength =
                approachGrowthExtendsUpperDiagonal
                    ? requiredTerminalOverlapLength +
                      stackEnvelopeThickness
                    : requiredTerminalOverlapLength +
                      Math.Max(
                          0d,
                          upperDiagonalLengthSpan - bendExtension);

            FaceToWallPreviewTrace.Info(
                "侧对侧第二种-换向后端长余量",
                "相=" + GetPhaseName(phaseIndex) +
                "，基座过渡折向=" +
                elevatedBaseTransitionLengthSign.ToString("0") +
                "，末端搭接折向=" +
                lengthTravelSign.ToString("0") +
                "，增加6→7是否延长4→5=" +
                approachGrowthExtendsUpperDiagonal +
                "，中心线最短=" +
                requiredTerminalOverlapLength.ToString("0.###") +
                " mm，可校正上限=" +
                availableMaximumApproachLength.ToString("0.###") +
                " mm，叠排包络=" +
                stackEnvelopeThickness.ToString("0.###") + " mm");

            var expectedEndThicknessDirection = routeAxisSetting.LengthVector
                .Cross(startWidthDirection);
            if (!BusbarGeometryUtility.AreParallel(
                    expectedEndThicknessDirection,
                    frameOutwardNormal))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相 6→7 段排厚方向与第一个基架绝缘子配合面法向不平行。");
            }

            return new FirstSegmentPhaseTemplate
            {
                PhaseIndex = phaseIndex,
                StartThicknessDirection = startThicknessDirection.Copy(),
                WidthDirection = startWidthDirection.Copy(),
                LengthDirection = routeAxisSetting.LengthVector.Copy(),
                HeightDirection = routeAxisSetting.HeightVector.Copy(),
                FirstPoint = firstPoint,
                SecondPoint = secondPoint,
                ThirdPoint = thirdPoint,
                FourthPoint = fourthPoint,
                TransitionStartPoint = transitionStartPoint,
                FifthReferencePoint = fifthReferencePoint,
                UsesSyntheticHeightDistribution =
                    ordinaryBaseInsulator == null,
                ElevatedLapExitBoundaryHeightCoordinate =
                    elevatedLapExitBoundaryHeightCoordinate,
                ElevatedInsideDirectionSign = elevatedInsideDirectionSign,
                HasOrdinaryBaseLapPoints = hasOrdinaryBaseLapPoints,
                ElevatedEntryExtensionExit = elevatedEntryExtensionExit,
                ElevatedEntryInsulatorStart = elevatedEntryInsulatorStart,
                DirectElevatedToUpperLap = plannedCopperLapCenter != null &&
                    elevatedBaseInsulator != null &&
                    ordinaryBaseInsulator == null,
                FixedJointBendStart = plannedCopperLapCenter != null &&
                    elevatedBaseInsulator != null,
                SeventhPoint = seventhPoint,
                LengthTravelSign = lengthTravelSign,
                MinimumApproachLength = requiredTerminalOverlapLength,
                // 客户图要求三相顶部搭接段等长，禁止绝缘净距搜索单独拉长
                // 某一相；相间避让由1:2:2:1七点高度路径承担。
                MaximumApproachLength = requiredTerminalOverlapLength,
                AvailableMaximumApproachLength =
                    availableMaximumApproachLength,
                BusbarWidth = busbarWidth,
                BusbarThickness = busbarThickness,
                BendingRadius = bendingRadius,
                BendAllowance = bendAllowance,
                StackMemberOffsets = FaceToWallModelBusbarPlanner
                    .CalculateStackMemberOffsets(
                        singlePhaseStackCount,
                        busbarThickness)
            };
        }

        /// <summary>
        /// 侧对侧第二种专用的3、4点判定；第一种固定换相不得调用。
        /// </summary>
        internal static bool ShouldCreateOrdinaryBaseLapPoints(
            double cabinetTopToOrdinaryBaseLengthDeviation,
            double busbarThickness)
        {
            if (double.IsNaN(cabinetTopToOrdinaryBaseLengthDeviation) ||
                double.IsInfinity(cabinetTopToOrdinaryBaseLengthDeviation) ||
                cabinetTopToOrdinaryBaseLengthDeviation < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cabinetTopToOrdinaryBaseLengthDeviation));
            }
            if (double.IsNaN(busbarThickness) ||
                double.IsInfinity(busbarThickness) ||
                busbarThickness <= GeometryTolerance)
            {
                throw new ArgumentOutOfRangeException(nameof(busbarThickness));
            }

            return cabinetTopToOrdinaryBaseLengthDeviation >
                   busbarThickness + GeometryTolerance;
        }

        private static void EnsureLowestPhaseVerticalAvoidance(
            IReadOnlyList<FirstSegmentPhaseTemplate> phaseTemplates)
        {
            var lowestPhaseTemplate = phaseTemplates
                .OrderBy(template => BusbarGeometryUtility.Project(
                    template.SeventhPoint,
                    template.HeightDirection))
                .First();
            var transitionStartHeightCoordinate = BusbarGeometryUtility.Project(
                lowestPhaseTemplate.FifthReferencePoint,
                lowestPhaseTemplate.HeightDirection);
            var seventhHeightCoordinate = BusbarGeometryUtility.Project(
                lowestPhaseTemplate.SeventhPoint,
                lowestPhaseTemplate.HeightDirection);
            var verticalAvoidanceLength = Math.Abs(
                seventhHeightCoordinate - transitionStartHeightCoordinate);
            if (verticalAvoidanceLength + GeometryTolerance <
                lowestPhaseTemplate.BusbarWidth)
            {
                FaceToWallPreviewTrace.Warning(
                    "侧对侧第二种-固定型式预览警告",
                    GetPhaseName(lowestPhaseTemplate.PhaseIndex) +
                    "相是高度方向最下侧铜排，但 5→6 避让段仅 " +
                    verticalAvoidanceLength.ToString("0.###") +
                    " mm，小于一个排宽 " +
                    lowestPhaseTemplate.BusbarWidth.ToString("0.###") +
                    " mm；固定七点型式继续预览。");
            }
        }

        private static List<PlanarBendBusbarSetting>
            ResolveShortestClearancePaths(
            IReadOnlyList<FirstSegmentPhaseTemplate> phaseTemplates,
            double insulationClearance)
        {
            var resolvedPhaseMembers =
                new Dictionary<int, IReadOnlyList<PlanarBendBusbarSetting>>();
            foreach (var phaseTemplate in phaseTemplates.OrderBy(template =>
                         BusbarGeometryUtility.Project(
                             template.SeventhPoint,
                             template.HeightDirection)))
            {
                var minimumCandidateMembers =
                    CreatePhaseMembers(
                        phaseTemplate,
                        phaseTemplate.MinimumApproachLength);
                if (resolvedPhaseMembers.Count == 0 ||
                    IsClearWithResolvedPhases(
                        resolvedPhaseMembers,
                        minimumCandidateMembers,
                        insulationClearance))
                {
                    resolvedPhaseMembers.Add(
                        phaseTemplate.PhaseIndex,
                        minimumCandidateMembers);
                    continue;
                }

                IReadOnlyList<PlanarBendBusbarSetting> firstClearMembers = null;
                var lastBlockedApproachLength =
                    phaseTemplate.MinimumApproachLength;
                var firstClearApproachLength = double.NaN;
                for (var candidateApproachLength =
                         phaseTemplate.MinimumApproachLength +
                         ClearanceSearchStep;
                     candidateApproachLength <=
                         phaseTemplate.MaximumApproachLength +
                         GeometryTolerance;
                     candidateApproachLength += ClearanceSearchStep)
                {
                    var boundedCandidateApproachLength = Math.Min(
                        candidateApproachLength,
                        phaseTemplate.MaximumApproachLength);
                    var candidateMembers = CreatePhaseMembers(
                        phaseTemplate,
                        boundedCandidateApproachLength);
                    if (IsClearWithResolvedPhases(
                            resolvedPhaseMembers,
                            candidateMembers,
                            insulationClearance))
                    {
                        firstClearMembers = candidateMembers;
                        firstClearApproachLength =
                            boundedCandidateApproachLength;
                        break;
                    }

                    lastBlockedApproachLength =
                        boundedCandidateApproachLength;
                    if (boundedCandidateApproachLength >=
                        phaseTemplate.MaximumApproachLength -
                        GeometryTolerance)
                    {
                        break;
                    }
                }

                if (firstClearMembers == null)
                {
                    FaceToWallPreviewTrace.Warning(
                        "侧对侧第二种-固定型式预览警告",
                        GetPhaseName(phaseTemplate.PhaseIndex) +
                        "相柜顶首段在 6→7 可用长度范围 " +
                        phaseTemplate.MinimumApproachLength.ToString("0.###") +
                        "～" +
                        phaseTemplate.MaximumApproachLength.ToString("0.###") +
                        " mm 内均无法满足 " +
                        insulationClearance.ToString("0.###") +
                        " mm 相间绝缘距离；保留固定型式最短路径继续预览。");
                    resolvedPhaseMembers.Add(
                        phaseTemplate.PhaseIndex,
                        minimumCandidateMembers);
                    continue;
                }

                var blockedBoundary = lastBlockedApproachLength;
                var clearBoundary = firstClearApproachLength;
                for (var searchIndex = 0;
                    searchIndex < ClearanceBinarySearchIterations;
                    searchIndex++)
                {
                    var middleApproachLength =
                        (blockedBoundary + clearBoundary) / 2d;
                    var middleCandidateMembers = CreatePhaseMembers(
                        phaseTemplate,
                        middleApproachLength);
                    if (IsClearWithResolvedPhases(
                            resolvedPhaseMembers,
                            middleCandidateMembers,
                            insulationClearance))
                    {
                        clearBoundary = middleApproachLength;
                        firstClearMembers = middleCandidateMembers;
                    }
                    else
                    {
                        blockedBoundary = middleApproachLength;
                    }
                }

                resolvedPhaseMembers.Add(
                    phaseTemplate.PhaseIndex,
                    firstClearMembers);
            }

            var result = phaseTemplates
                .OrderBy(template => template.PhaseIndex)
                .SelectMany(template =>
                    resolvedPhaseMembers[template.PhaseIndex])
                .ToList();
            var finalEvaluation = PlanarBusbarClearanceValidator.Evaluate(
                result.Select(CreateTerminalLapExcludedClearanceSetting)
                    .ToList(),
                GetPhaseClearanceGroup,
                insulationClearance);
            if (!finalEvaluation.IsClear)
            {
                FaceToWallPreviewTrace.Warning(
                    "侧对侧第二种-固定型式预览警告",
                    "侧对侧第二种柜顶首段相间最小实体净距仅 " +
                    finalEvaluation.MinimumDistance.ToString("0.###") +
                    " mm，小于要求的 " +
                    insulationClearance.ToString("0.###") + " mm。冲突=" +
                    finalEvaluation.FirstGroupKey + " / " +
                    finalEvaluation.SecondGroupKey +
                    "；不中断固定型式预览。");
            }

            foreach (var phaseTemplate in phaseTemplates.OrderBy(template =>
                         template.PhaseIndex))
            {
                var resolvedReferenceBusbar =
                    resolvedPhaseMembers[phaseTemplate.PhaseIndex][0];
                var resolvedPointCount =
                    resolvedReferenceBusbar.PointList.Count;
                FaceToWallPreviewTrace.Info(
                    "侧对侧第二种柜顶首段-最终路径",
                    "相=" + GetPhaseName(phaseTemplate.PhaseIndex) +
                    "，点数=" + resolvedPointCount +
                    "，末段长度=" +
                    resolvedReferenceBusbar.PointList[resolvedPointCount - 2]
                        .DistanceTo(
                            resolvedReferenceBusbar.PointList[
                                resolvedPointCount - 1])
                        .ToString("0.###") + " mm" +
                    "，路径=" + string.Join(
                        "/",
                        resolvedReferenceBusbar.PointList.Select(
                            FormatPoint)));
            }

            return result;
        }

        private static bool IsClearWithResolvedPhases(
            IReadOnlyDictionary<int,
                IReadOnlyList<PlanarBendBusbarSetting>> resolvedPhaseMembers,
            IReadOnlyList<PlanarBendBusbarSetting> candidateMembers,
            double insulationClearance)
        {
            var comparedMembers = resolvedPhaseMembers.Values
                .SelectMany(phaseMembers => phaseMembers)
                .Concat(candidateMembers)
                .Select(CreateTerminalLapExcludedClearanceSetting)
                .ToList();
            return PlanarBusbarClearanceValidator.Evaluate(
                comparedMembers,
                GetPhaseClearanceGroup,
                insulationClearance).IsClear;
        }

        /// <summary>
        /// 侧对侧第二种的6→7段是与中间排的固定搭接段：该段优先
        /// 保证“排宽+折弯余量”，不参与相间绝缘距离优化。其余路径仍按
        /// 长方体实体距离校验。
        /// </summary>
        private static PlanarBendBusbarSetting
            CreateTerminalLapExcludedClearanceSetting(
            PlanarBendBusbarSetting sourceSetting)
        {
            if (sourceSetting?.PointList == null ||
                sourceSetting.PointList.Count < 3)
            {
                return sourceSetting;
            }

            var clearanceSetting = sourceSetting.Copy();
            clearanceSetting.PointList = sourceSetting.PointList
                .Take(sourceSetting.PointList.Count - 1)
                .Select(point => point.Copy())
                .ToList();
            if (clearanceSetting.PreviewBendPointKinds != null &&
                clearanceSetting.PreviewBendPointKinds.Count > 0)
            {
                clearanceSetting.PreviewBendPointKinds.Clear();
            }
            return clearanceSetting;
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreatePhaseMembers(
            FirstSegmentPhaseTemplate phaseTemplate,
            double approachLength)
        {
            var correctedCenterlineApproachLength = approachLength;
            var correctedElevatedLapExitHeight = 0d;
            var correctedUpperStraightLength = 0d;
            IReadOnlyList<PlanarBendBusbarSetting> lastMemberSettings = null;
            for (var correctionIndex = 0;
                correctionIndex < (phaseTemplate.DirectElevatedToUpperLap ? 24 : 6);
                correctionIndex++)
            {
                var memberSettings = CreatePhaseMembersAtApproachLength(
                    phaseTemplate,
                    correctedCenterlineApproachLength,
                    correctedElevatedLapExitHeight,
                    correctedUpperStraightLength);
                lastMemberSettings = memberSettings;
                var expectedPointCount = (phaseTemplate.DirectElevatedToUpperLap
                    ? 5 : 7) +
                    (phaseTemplate.ElevatedEntryExtensionExit == null ? 0 : 2);
                if (memberSettings.Any(memberSetting =>
                        memberSetting.PointList == null ||
                        memberSetting.PointList.Count != expectedPointCount))
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseTemplate.PhaseIndex) +
                        "相未生成柜顶首段要求的完整点列。");
                }

                if (!double.IsNaN(
                        phaseTemplate
                            .ElevatedLapExitBoundaryHeightCoordinate))
                {
                    var minimumMemberLapExitReserve = memberSettings.Min(
                        memberSetting =>
                            phaseTemplate.ElevatedInsideDirectionSign *
                            (BusbarGeometryUtility.Project(
                                 memberSetting.PointList[
                                     phaseTemplate.ElevatedEntryExtensionExit == null ? 1 : 3],
                                 phaseTemplate.HeightDirection) -
                             phaseTemplate
                                 .ElevatedLapExitBoundaryHeightCoordinate));
                    var missingLapExitReserve =
                        phaseTemplate.BendingRadius +
                        phaseTemplate.BusbarThickness -
                        minimumMemberLapExitReserve;
                    if (missingLapExitReserve > GeometryTolerance)
                    {
                        if (phaseTemplate.FixedJointBendStart)
                            throw new InvalidOperationException(GetPhaseName(phaseTemplate.PhaseIndex) +
                                "相在已核验的增高基座与基座2连接处缺少折弯余量。");
                        correctedElevatedLapExitHeight +=
                            missingLapExitReserve;
                        continue;
                    }
                }

                if (phaseTemplate.DirectElevatedToUpperLap)
                {
                    var minimumBetweenBends = 2d *
                        (phaseTemplate.BendingRadius + phaseTemplate.BusbarThickness);
                    var shortestUpperStraight = memberSettings.Min(setting =>
                    {
                        var points = setting.PointList;
                        return points[points.Count - 3].DistanceTo(points[points.Count - 2]);
                    });
                    var missingUpperStraight = minimumBetweenBends + UpperStraightLengthAdditionMm - shortestUpperStraight;
                    if (missingUpperStraight > 1.0)
                    {
                        correctedUpperStraightLength += missingUpperStraight;
                        continue;
                    }
                    if (memberSettings.Any(setting =>
                    {
                        var points = setting.PointList;
                        return points[points.Count - 4].DistanceTo(points[points.Count - 3]) +
                            GeometryTolerance < minimumBetweenBends;
                    }))
                        throw new InvalidOperationException(GetPhaseName(phaseTemplate.PhaseIndex) +
                            "相基座连接处到上方直排的斜段不足以容纳相邻折弯。");
                }

                var minimumMemberTerminalLength = memberSettings.Min(
                    memberSetting => memberSetting.PointList[
                            memberSetting.PointList.Count - 2]
                        .DistanceTo(memberSetting.PointList[
                            memberSetting.PointList.Count - 1]));
                var missingTerminalLength =
                    phaseTemplate.MinimumApproachLength -
                    minimumMemberTerminalLength;
                if (missingTerminalLength <= GeometryTolerance)
                {
                    return memberSettings;
                }

                correctedCenterlineApproachLength += missingTerminalLength;
                if (correctedCenterlineApproachLength >
                    phaseTemplate.AvailableMaximumApproachLength +
                    GeometryTolerance)
                {
                    FaceToWallPreviewTrace.Warning(
                        "侧对侧第二种-固定型式预览警告",
                        GetPhaseName(phaseTemplate.PhaseIndex) +
                        "相保持四叠排平行间距后，最短6→7仅 " +
                        minimumMemberTerminalLength.ToString("0.###") +
                        " mm；原可用空间不足以整体延长到排宽+折弯余量 " +
                        phaseTemplate.MinimumApproachLength
                            .ToString("0.###") +
                        " mm；固定搭接段不受绝缘距离限制，继续整体延长。");
                }
            }

            if (phaseTemplate.DirectElevatedToUpperLap)
                throw new InvalidOperationException(GetPhaseName(phaseTemplate.PhaseIndex) +
                    "相无法同时满足上方直排折弯余量与完整搭接长度，请检查基座连接位置和搭接空间。");
            FaceToWallPreviewTrace.Warning(
                "侧对侧第二种-固定型式预览警告",
                GetPhaseName(phaseTemplate.PhaseIndex) +
                "相整体修正中心路径后，仍无法同时保证内侧点2折弯余量和6→7搭接长度；显示最后一次固定型式计算结果。");
            return lastMemberSettings ??
                   CreatePhaseMembersAtApproachLength(
                       phaseTemplate,
                       approachLength,
                       0d,
                       0d);
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreatePhaseMembersAtApproachLength(
            FirstSegmentPhaseTemplate phaseTemplate,
            double centerlineApproachLength,
            double elevatedLapExitHeightCorrection,
            double upperStraightLengthCorrection)
        {
            var secondPoint = phaseTemplate.SecondPoint.Copy();
            var thirdPoint = phaseTemplate.ThirdPoint.Copy();
            var fourthPoint = phaseTemplate.FourthPoint.Copy();
            var fifthReferencePoint =
                phaseTemplate.FifthReferencePoint.Copy();
            if (elevatedLapExitHeightCorrection > GeometryTolerance)
            {
                var correctedSecondHeightCoordinate =
                    BusbarGeometryUtility.Project(
                        secondPoint,
                        phaseTemplate.HeightDirection) +
                    phaseTemplate.ElevatedInsideDirectionSign *
                    elevatedLapExitHeightCorrection;
                secondPoint = BusbarGeometryUtility.WithProjection(
                    secondPoint,
                    phaseTemplate.HeightDirection,
                    correctedSecondHeightCoordinate);
                if (phaseTemplate.UsesSyntheticHeightDistribution)
                {
                    var finalRouteHeightCoordinate =
                        BusbarGeometryUtility.Project(
                            phaseTemplate.SeventhPoint,
                            phaseTemplate.HeightDirection);
                    var signedAvailableRise = finalRouteHeightCoordinate -
                        correctedSecondHeightCoordinate;
                    if (signedAvailableRise <= GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            GetPhaseName(phaseTemplate.PhaseIndex) +
                            "相按内侧排修正点2后，已没有可用的上升高度。");
                    }

                    thirdPoint = BusbarGeometryUtility.WithProjection(
                        thirdPoint,
                        phaseTemplate.HeightDirection,
                        correctedSecondHeightCoordinate +
                        signedAvailableRise / 6d);
                    fourthPoint = BusbarGeometryUtility.WithProjection(
                        fourthPoint,
                        phaseTemplate.HeightDirection,
                        correctedSecondHeightCoordinate +
                        signedAvailableRise * 3d / 6d);
                    fifthReferencePoint =
                        BusbarGeometryUtility.WithProjection(
                            fifthReferencePoint,
                            phaseTemplate.HeightDirection,
                            correctedSecondHeightCoordinate +
                            signedAvailableRise * 5d / 6d);
                }
            }

            var seventhLengthCoordinate = BusbarGeometryUtility.Project(
                phaseTemplate.SeventhPoint,
                phaseTemplate.LengthDirection);
            var fifthAndSixthLengthCoordinate =
                seventhLengthCoordinate -
                phaseTemplate.LengthTravelSign * centerlineApproachLength;
            var fifthPoint = BusbarGeometryUtility.WithProjection(
                fifthReferencePoint,
                phaseTemplate.LengthDirection,
                fifthAndSixthLengthCoordinate);
            var sixthPoint = BusbarGeometryUtility.WithProjection(
                phaseTemplate.SeventhPoint,
                phaseTemplate.LengthDirection,
                fifthAndSixthLengthCoordinate);
            Point3d upperStraightStart = null;
            if (phaseTemplate.DirectElevatedToUpperLap)
            {
                var minimumBetweenBends = 2d *
                    (phaseTemplate.BendingRadius + phaseTemplate.BusbarThickness);
                var availableRise = BusbarGeometryUtility.Project(
                    sixthPoint, phaseTemplate.HeightDirection) -
                    BusbarGeometryUtility.Project(secondPoint, phaseTemplate.HeightDirection);
                if (availableRise + 1.0 < 2d * minimumBetweenBends + UpperStraightLengthAdditionMm)
                    throw new InvalidOperationException(GetPhaseName(phaseTemplate.PhaseIndex) +
                        "相基座连接处到上方搭接排缺少斜段和上方直排的折弯余量。");
                var upperStraightLength = minimumBetweenBends + UpperStraightLengthAdditionMm +
                    upperStraightLengthCorrection;
                if (upperStraightLength > availableRise - minimumBetweenBends +
                    1.0)
                    throw new InvalidOperationException(GetPhaseName(phaseTemplate.PhaseIndex) +
                        "相上方直排校正后挤占了连接处斜段的折弯余量。");
                upperStraightStart = BusbarGeometryUtility.WithProjection(
                    sixthPoint,
                    phaseTemplate.HeightDirection,
                    BusbarGeometryUtility.Project(sixthPoint, phaseTemplate.HeightDirection) -
                    upperStraightLength);
                if (secondPoint.DistanceTo(upperStraightStart) + GeometryTolerance <
                    minimumBetweenBends)
                    throw new InvalidOperationException(GetPhaseName(phaseTemplate.PhaseIndex) +
                        "相基座连接处到上方直排的斜段不足以容纳相邻折弯。");
            }
            var pointList = new List<Point3d>
            {
                phaseTemplate.FirstPoint.Copy()
            };
            if (phaseTemplate.ElevatedEntryExtensionExit != null)
            {
                pointList.Add(phaseTemplate.ElevatedEntryExtensionExit.Copy());
                pointList.Add(phaseTemplate.ElevatedEntryInsulatorStart.Copy());
            }
            pointList.Add(secondPoint);
            if (upperStraightStart != null)
                pointList.Add(upperStraightStart);
            if (!phaseTemplate.DirectElevatedToUpperLap &&
                phaseTemplate.HasOrdinaryBaseLapPoints)
            {
                pointList.Add(thirdPoint);
                pointList.Add(fourthPoint);
            }
            if (!phaseTemplate.DirectElevatedToUpperLap)
                pointList.Add(fifthPoint);
            pointList.Add(sixthPoint);
            pointList.Add(phaseTemplate.SeventhPoint.Copy());

            var centerlineSetting = new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = GetPhaseName(phaseTemplate.PhaseIndex),
                StartBusbarVector =
                    phaseTemplate.StartThicknessDirection.Copy(),
                WidthVector = phaseTemplate.WidthDirection.Copy(),
                PointList = pointList,
                BusBarWidth = phaseTemplate.BusbarWidth,
                BusBarThickness = phaseTemplate.BusbarThickness,
                BarBendingRadius = phaseTemplate.BendingRadius,
                BendAllowance = phaseTemplate.BendAllowance,
                StartLapWidth = phaseTemplate.BusbarWidth,
                EndLapWidth = phaseTemplate.BusbarWidth
            };
            var memberSettings = FaceToWallModelBusbarPlanner
                .CreateStackMemberSettings(
                    centerlineSetting,
                    phaseTemplate.StackMemberOffsets,
                    phaseTemplate.PhaseIndex);
            return memberSettings;
        }

        private static string GetPhaseClearanceGroup(
            PlanarBendBusbarSetting busbar)
        {
            var groupKey = busbar?.GroupKey ?? string.Empty;
            var separatorIndex = groupKey.IndexOf('-');
            return separatorIndex > 0
                ? groupKey.Substring(0, separatorIndex)
                : groupKey;
        }

        private static List<InsulatorConnectionInfo>
            SelectStrictHostInsulators(
            AssemblyPartCatalog partCatalog,
            Component2 hostComponent,
            IReadOnlyList<InsulatorConnectionInfo> availableInsulators,
            Vector3d expectedConnectionNormal,
            string hostLabel)
        {
            if (hostComponent == null)
            {
                return new List<InsulatorConnectionInfo>();
            }

            var descendantNames = new HashSet<string>(
                partCatalog.GetAllDescendants(hostComponent)
                    .Select(BusbarGeometryUtility.SafeName),
                StringComparer.OrdinalIgnoreCase);
            var selectedInsulators = availableInsulators
                .Where(insulator => insulator?.Component != null &&
                    insulator.ConnectionPlane != null &&
                    descendantNames.Contains(
                        BusbarGeometryUtility.SafeName(insulator.Component)) &&
                    BusbarGeometryUtility.AreParallel(
                        insulator.ConnectionPlane.Normal,
                        expectedConnectionNormal))
                .ToList();
            FaceToWallPreviewTrace.Info(
                "侧对侧第二种-基座绝缘子严格读取",
                "宿主=" + hostLabel + "/" +
                BusbarGeometryUtility.SafeName(hostComponent) +
                "，宿主后代数量=" + descendantNames.Count +
                "，有效绝缘子数量=" + selectedInsulators.Count +
                "，绝缘子=" + string.Join(
                    ",",
                    selectedInsulators.Select(insulator =>
                        BusbarGeometryUtility.SafeName(insulator.Component))));
            return selectedInsulators;
        }

        /// <summary>
        /// B 柜按各相端头到 A 柜三相中心的实际距离由近到远排序。
        /// 最近相折向宽度正向（前/内侧），最远相折向宽度负向（外侧）；
        /// A 柜物理距离顺序相反，但沿用同一逻辑相的折向，保证两端同相同向。
        /// </summary>
        private static SideToSideFoldPhaseIndexMapping ResolveFoldPhaseIndexes(
            IReadOnlyList<List<ModeledBusbar>> bCabinetPhaseGroups,
            IReadOnlyList<List<ModeledBusbar>> aCabinetPhaseGroups,
            Vector3d widthDirection)
        {
            var bReferencePoints = FaceToFaceModelBusbarPlanner.GetPhaseTerminalReferencePoints(
                bCabinetPhaseGroups,
                "B柜");
            var aReferencePoints = FaceToFaceModelBusbarPlanner.GetPhaseTerminalReferencePoints(
                aCabinetPhaseGroups,
                "A柜");
            var bWidthOrder = Enumerable.Range(0, PhaseCount)
                .OrderBy(phaseIndex => BusbarGeometryUtility.Project(
                    bReferencePoints[phaseIndex],
                    widthDirection))
                .ToList();
            var aWidthOrder = Enumerable.Range(0, PhaseCount)
                .OrderBy(phaseIndex => BusbarGeometryUtility.Project(
                    aReferencePoints[phaseIndex],
                    widthDirection))
                .ToList();
            var hasWidthOrderMismatch = !bWidthOrder.SequenceEqual(aWidthOrder);
            if (hasWidthOrderMismatch)
            {
                FaceToWallPreviewTrace.Info(
                    "side-to-side second type phase order validation",
                    "A/B cabinet width phase order differs; treating as phase change. B=" +
                    FormatPhaseIndexes(bWidthOrder) + "; A=" +
                    FormatPhaseIndexes(aWidthOrder));
            }

            var bCabinetPhaseCenter = BusbarGeometryUtility.Average(
                bReferencePoints);
            var aCabinetPhaseCenter = BusbarGeometryUtility.Average(
                aReferencePoints);
            var phaseDistances = Enumerable.Range(0, PhaseCount)
                .Select(phaseIndex => new
                {
                    PhaseIndex = phaseIndex,
                    Distance = bReferencePoints[phaseIndex].DistanceTo(
                        aCabinetPhaseCenter)
                })
                .OrderBy(item => item.Distance)
                .ToList();
            var aNearestToFarthestPhaseIndexes = Enumerable.Range(
                    0,
                    PhaseCount)
                .OrderBy(phaseIndex =>
                    aReferencePoints[phaseIndex].DistanceTo(
                        bCabinetPhaseCenter))
                .ToList();
            if (phaseDistances.Zip(
                    phaseDistances.Skip(1),
                    (nearer, farther) => farther.Distance - nearer.Distance)
                .Any(distanceDifference => distanceDifference <= 2d))
            {
                throw new InvalidOperationException(
                    "侧对侧第二种同相端头距离差不超过 2 mm，无法唯一判断内外折向。" +
                    string.Join(
                        "，",
                        phaseDistances.Select(item =>
                            GetPhaseName(item.PhaseIndex) + "=" +
                            item.Distance.ToString("0.###") + " mm")) + "。");
            }

            if (hasWidthOrderMismatch)
            {
                var aPhaseDistances = aNearestToFarthestPhaseIndexes
                    .Select(phaseIndex => aReferencePoints[phaseIndex]
                        .DistanceTo(bCabinetPhaseCenter))
                    .ToList();
                if (aPhaseDistances.Zip(
                        aPhaseDistances.Skip(1),
                        (nearer, farther) => farther - nearer)
                    .Any(distanceDifference => distanceDifference <= 2d))
                {
                    throw new InvalidOperationException(
                        "侧对侧第二种 A 柜同相端头距离差不超过 2 mm，无法唯一判断独立折向。");
                }
            }

            var orderedPhaseIndexes = phaseDistances
                .Select(item => item.PhaseIndex)
                .ToList()
                .AsReadOnly();
            if (!hasWidthOrderMismatch &&
                !orderedPhaseIndexes.Reverse().SequenceEqual(
                    aNearestToFarthestPhaseIndexes))
            {
                throw new InvalidOperationException(
                    "侧对侧第二种 A/B 柜离对柜由近到远的相序不是反向关系，" +
                    "B柜=" + FormatPhaseIndexes(orderedPhaseIndexes) +
                    "，A柜=" +
                    FormatPhaseIndexes(aNearestToFarthestPhaseIndexes) + "。");
            }
            FaceToWallPreviewTrace.Info(
                "侧对侧第二种-同相折向",
                "B柜距A柜由近到远=" +
                FormatPhaseIndexes(orderedPhaseIndexes) +
                "；A柜距B柜由近到远=" +
                FormatPhaseIndexes(aNearestToFarthestPhaseIndexes) +
                "；B柜折向=最近:+宽(前/内),中间:0,最远:-宽(外)" +
                "；A柜距离顺序相反并沿用同相折向，保证同相同向");
            return new SideToSideFoldPhaseIndexMapping
            {
                BCabinetNearestToFarthestPhaseIndexes = orderedPhaseIndexes,
                ACabinetNearestToFarthestPhaseIndexes = hasWidthOrderMismatch
                    ? aNearestToFarthestPhaseIndexes.AsReadOnly()
                    : orderedPhaseIndexes
            };
        }

        private static List<InsulatorConnectionInfo>
            MatchBCabinetFoldInsulatorsToPhases(
            IReadOnlyList<InsulatorConnectionInfo> hostInsulators,
            IReadOnlyList<int> nearestToFarthestPhaseIndexes,
            Vector3d orderingDirection,
            string hostLabel,
            bool nearestUsesMinimumCoordinate)
        {
            var directionOrderedInsulators = hostInsulators
                .OrderBy(insulator => BusbarGeometryUtility.Project(
                    insulator.ConnectionCircleCenter,
                    orderingDirection))
                .ToList();
            if (directionOrderedInsulators.Count != PhaseCount ||
                nearestToFarthestPhaseIndexes == null ||
                nearestToFarthestPhaseIndexes.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    "侧对侧第二种无法建立 B柜" + hostLabel +
                    "三相内外折向映射。");
            }

            var result = new InsulatorConnectionInfo[PhaseCount];
            var nearestInsulatorIndex = nearestUsesMinimumCoordinate
                ? 0
                : PhaseCount - 1;
            var farthestInsulatorIndex = nearestUsesMinimumCoordinate
                ? PhaseCount - 1
                : 0;
            result[nearestToFarthestPhaseIndexes[0]] =
                directionOrderedInsulators[nearestInsulatorIndex];
            result[nearestToFarthestPhaseIndexes[1]] =
                directionOrderedInsulators[1];
            result[nearestToFarthestPhaseIndexes[2]] =
                directionOrderedInsulators[farthestInsulatorIndex];
            FaceToWallPreviewTrace.Info(
                "侧对侧第二种-B柜" + hostLabel + "折向分配",
                string.Join(
                    "；",
                    Enumerable.Range(0, PhaseCount).Select(phaseIndex =>
                         GetPhaseName(phaseIndex) + "相->" +
                         BusbarGeometryUtility.SafeName(
                             result[phaseIndex].Component) + "@排序轴" +
                         BusbarGeometryUtility.Project(
                             result[phaseIndex].ConnectionCircleCenter,
                             orderingDirection).ToString("0.###"))));
            return result.ToList();
        }

        private static string FormatPhaseIndexes(
            IEnumerable<int> phaseIndexes)
        {
            return string.Join(
                "-",
                phaseIndexes.Select(GetPhaseName));
        }

        private static string GetPhaseName(int phaseIndex)
        {
            return FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex);
        }

        private static void TracePhaseInsulatorMapping(
            string cabinetLabel,
            IReadOnlyList<List<ModeledBusbar>> phaseGroups,
            IReadOnlyList<InsulatorConnectionInfo> orderedBaseInsulators,
            IReadOnlyList<InsulatorConnectionInfo> orderedFrameInsulators,
            BusbarBridgeAxisSetting routeAxisSetting)
        {
            var mappingItems = Enumerable.Range(0, PhaseCount)
                .Select(phaseIndex =>
                {
                    var phaseWidthCoordinate = phaseGroups[phaseIndex]
                        .Average(busbar => BusbarGeometryUtility.Project(
                            busbar.TerminalSegment.P2,
                            routeAxisSetting.WidthVector));
                    var baseInsulator = orderedBaseInsulators?[phaseIndex];
                    var frameInsulator = orderedFrameInsulators?[phaseIndex];
                    return GetPhaseName(phaseIndex) +
                        "相:柜顶宽坐标=" +
                        phaseWidthCoordinate.ToString("0.###") +
                        "，基座=" +
                        BusbarGeometryUtility.SafeName(baseInsulator?.Component) +
                        "@" + FormatPoint(baseInsulator?.ConnectionCircleCenter) +
                        "，基架=" +
                        BusbarGeometryUtility.SafeName(frameInsulator?.Component) +
                        "@" + FormatPoint(frameInsulator?.ConnectionCircleCenter);
                });
            FaceToWallPreviewTrace.Info(
                "侧对侧第二种-本柜基座基架绝缘子对应",
                cabinetLabel + "：" + string.Join("；", mappingItems));
        }

        private static string FormatPoint(Point3d point)
        {
            return point == null
                ? "null"
                : string.Format(
                    "({0:0.###},{1:0.###},{2:0.###})",
                    point.X,
                    point.Y,
                    point.Z);
        }

        private sealed class FirstSegmentPhaseTemplate
        {
            internal int PhaseIndex { get; set; }
            internal Vector3d StartThicknessDirection { get; set; }
            internal Vector3d WidthDirection { get; set; }
            internal Vector3d LengthDirection { get; set; }
            internal Vector3d HeightDirection { get; set; }
            internal Point3d FirstPoint { get; set; }
            internal Point3d SecondPoint { get; set; }
            internal Point3d ThirdPoint { get; set; }
            internal Point3d FourthPoint { get; set; }
            internal Point3d TransitionStartPoint { get; set; }
            internal Point3d FifthReferencePoint { get; set; }
            internal Point3d SeventhPoint { get; set; }
            internal bool UsesSyntheticHeightDistribution { get; set; }
            internal double ElevatedLapExitBoundaryHeightCoordinate
            {
                get;
                set;
            }
            internal double ElevatedInsideDirectionSign { get; set; }
            internal bool HasOrdinaryBaseLapPoints { get; set; }
            internal Point3d ElevatedEntryExtensionExit { get; set; }
            internal Point3d ElevatedEntryInsulatorStart { get; set; }
            internal bool DirectElevatedToUpperLap { get; set; }
            internal bool FixedJointBendStart { get; set; }
            internal double LengthTravelSign { get; set; }
            internal double MinimumApproachLength { get; set; }
            internal double MaximumApproachLength { get; set; }
            internal double AvailableMaximumApproachLength { get; set; }
            internal double BusbarWidth { get; set; }
            internal double BusbarThickness { get; set; }
            internal double BendingRadius { get; set; }
            internal double BendAllowance { get; set; }
            internal IReadOnlyList<double> StackMemberOffsets { get; set; }
        }
    }
}
