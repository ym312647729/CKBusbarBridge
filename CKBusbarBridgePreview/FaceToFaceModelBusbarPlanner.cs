using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    public enum FaceToFaceBusbarGroupKind_e
    {
        BCabinetConnection = 0,
        LengthwiseOrdinary = 1,
        LargeOffsetCrosswise = 2,
        ACabinetConnection = 3,
        PhaseChange = 4
    }

    public enum FaceToFaceSmallOffsetTransitionKind_e
    {
        VerticalBend = 0,
        MilledBar = 1,
        MiddleParallelConnection = 2
    }

    /// <summary>面对面按 B 柜到 A 柜物理顺序生成的一个铜排子装配组。</summary>
    public sealed class FaceToFaceGeneratedBusbarGroup
    {
        public string DisplayName { get; set; }
        public FaceToFaceBusbarGroupKind_e Kind { get; set; }
        public int RouteGroupIndex { get; set; } = -1;
        public bool IsBridge { get; set; }
        public string ClearanceWarning { get; set; }
        public List<PlanarBendBusbarSetting> Busbars { get; } =
            new List<PlanarBendBusbarSetting>();
    }

    /// <summary>按实际 Box 中点得到的单个基架包围盒。</summary>
    public sealed class FaceToFaceFrameBox
    {
        public int PhysicalIndex { get; set; }
        public string ComponentName { get; set; }
        public Component2 Component { get; set; }
        public Point3d Minimum { get; set; }
        public Point3d Maximum { get; set; }
        public double LengthMinimum { get; set; }
        public double LengthMaximum { get; set; }
        public double LengthCenter => (LengthMinimum + LengthMaximum) / 2d;
        public double WidthMinimum { get; set; }
        public double WidthMaximum { get; set; }
        public double WidthCenter => (WidthMinimum + WidthMaximum) / 2d;
        public double HeightMinimum { get; set; }
        public double HeightMaximum { get; set; }
        public double HeightCenter => (HeightMinimum + HeightMaximum) / 2d;
    }

    /// <summary>
    /// 面对面时，由两柜实际三相宽度顺序自动识别出的换相区域。
    /// 默认断点使用相对首基架长度起点的坐标，规划阶段允许采用客户
    /// 调整后的中间换相段边界。
    /// </summary>
    public sealed class FaceToFacePhaseChangeRegionPlan
    {
        public FaceToFaceFrameBox MiddleFrame { get; set; }
        public double LeftFixedBreakpointPosition { get; set; }
        public double RightFixedBreakpointPosition { get; set; }
        public double LeftFixedBreakpointCoordinate { get; set; }
        public double RightFixedBreakpointCoordinate { get; set; }
        public int SuppressedBreakpointIntervalIndex { get; set; } = -1;
        public double InsulationClearance { get; set; }
        public double RoutingClearance { get; set; }
        public double MinimumRecommendedBreakpointSpan { get; set; }
        public double BusbarWidth { get; set; }
        public double BusbarThickness { get; set; }
        public double BendAllowance { get; set; }
        public double AvailableWidthMinimum { get; set; }
        public double AvailableWidthMaximum { get; set; }
        public double AvailableHeightMinimum { get; set; }
        public double AvailableHeightMaximum { get; set; }
        public bool UsesLargeOffsetBSide { get; set; }
    }

    /// <summary>
    /// 面对面小偏移中由实际 Box 中心识别出的 B 侧轨迹、唯一特殊基架、
    /// A 侧轨迹以及不进入正式生成组、但会补绘特殊过渡排的固定空窗。
    /// </summary>
    public sealed class FaceToFaceSmallOffsetRegionPlan
    {
        public FaceToFaceFrameBox BCabinetReferenceFrame { get; set; }
        public FaceToFaceFrameBox SpecialFrame { get; set; }
        public FaceToFaceFrameBox ACabinetReferenceFrame { get; set; }
        public double SuppressedIntervalStartCoordinate { get; set; }
        public double SuppressedIntervalEndCoordinate { get; set; }
        public int SuppressedBreakpointIntervalIndex { get; set; } = -1;
        /// <summary>覆盖固定禁区的实际左断点模型坐标。</summary>
        public double TransitionLeftBreakpointCoordinate { get; set; }
        /// <summary>覆盖固定禁区的实际右断点模型坐标。</summary>
        public double TransitionRightBreakpointCoordinate { get; set; }
        public double SignedBusbarWidthOffset { get; set; }
        public double InsulationClearance { get; set; }
        public bool UsesMiddleParallelConnection { get; set; }
        public FaceToFaceSmallOffsetTransitionKind_e TransitionKind
            { get; set; }
    }

    /// <summary>大偏移中单相两个垂直搭接转角和三段物理直排的纯几何结果。</summary>
    public sealed class FaceToFaceLargeOffsetPhaseRoute
    {
        public BusbarPhase_e Phase { get; set; }
        public int TurnOrder { get; set; }
        public double TurnLengthCoordinate { get; set; }
        public double BSideWidthCoordinate { get; set; }
        public double ASideWidthCoordinate { get; set; }
        public double BSideLengthStartCoordinate { get; set; }
        public double BSideLengthEndCoordinate { get; set; }
        public double CrosswiseStartWidthCoordinate { get; set; }
        public double CrosswiseEndWidthCoordinate { get; set; }
        public double ASideLengthStartCoordinate { get; set; }
        public double ASideLengthEndCoordinate { get; set; }
        public Point3d BSideTurnCenter { get; set; }
        public Point3d ASideTurnCenter { get; set; }
    }

    internal sealed class FaceToFaceLargeOffsetCrosswiseSegmentRange
    {
        public double StartCoordinate { get; set; }
        public double EndCoordinate { get; set; }
    }

    /// <summary>唯一连续大偏移区及其 B 长度向、宽度向、A 长度向三组规划。</summary>
    public sealed class FaceToFaceLargeOffsetRegionPlan
    {
        public int FirstChangedEdgeIndex { get; set; }
        public int LastChangedEdgeIndex { get; set; }
        public int FirstPhysicalFrameIndex { get; set; }
        public int LastPhysicalFrameIndex { get; set; }
        public double TransitionLengthMinimum { get; set; }
        public double TransitionLengthMaximum { get; set; }
        public double SignedWidthOffset { get; set; }
        public int SourceBreakpointIntervalIndex { get; set; } = -1;
        public int LastSourceBreakpointIntervalIndex { get; set; } = -1;
        public int BSideLengthGroupIndex { get; set; } = -1;
        public int CrosswiseGroupIndex { get; set; } = -1;
        public int ASideLengthGroupIndex { get; set; } = -1;
        public List<double> CrosswiseBreakpointFractions { get; } =
            new List<double>();
        public List<int> CrosswiseGroupIndexes { get; } =
            new List<int>();
        public List<FaceToFaceLargeOffsetPhaseRoute> PhaseRoutes { get; } =
            new List<FaceToFaceLargeOffsetPhaseRoute>();
    }

    public sealed class FaceToFaceModelBusbarPlan
    {
        public List<FaceToFaceGeneratedBusbarGroup> GeneratedBusbarGroups { get; } =
            new List<FaceToFaceGeneratedBusbarGroup>();
        /// <summary>
        /// 预览使用的扁平铜排集合；包含正式生成组以及小偏移空窗内的
        /// 立弯/铣排专用预览排。
        /// </summary>
        public List<PlanarBendBusbarSetting> GeneratedBusbars { get; } =
            new List<PlanarBendBusbarSetting>();
        public IReadOnlyList<ModeledBusbar> CabinetTopExtensionBusbars { get; set; }
        public IReadOnlyList<ModeledBusbar> BCabinetTopExtensionBusbars { get; set; }
        public IReadOnlyList<ModeledBusbar> ACabinetTopExtensionBusbars { get; set; }
        public IReadOnlyList<FaceToFaceFrameBox> OrderedFrameBoxes { get; set; }
        public FaceToFaceLargeOffsetRegionPlan LargeOffsetRegion { get; set; }
        public FaceToFaceSmallOffsetRegionPlan SmallOffsetRegion { get; set; }
        public FaceToFacePhaseChangeRegionPlan PhaseChangeRegion { get; set; }
        /// <summary>
        /// 由两端基座接口直接得到的连续路径与分段结果。为空表示旧调用尚未提供
        /// 两侧端部几何；基架仅作为后续支撑，不参与此结果的坐标计算。
        /// </summary>
        public MiddleBusbarPathPlan MiddlePathPlan { get; set; }
        public FaceToFaceUnifiedPlanningResult UnifiedPlanning { get; set; }
#if DEBUG
        public SideToSideFixedPhaseChangePlanningSnapshot
            SideToSideFixedPhaseChangePlanning { get; set; }
        public string SideToSideFixedPhaseChangePlanningCaptureWarning
            { get; set; }
#endif
        public List<PlanarBendBusbarSetting>
            SmallOffsetTransitionPreviewBusbars { get; } =
                new List<PlanarBendBusbarSetting>();
    }

    /// <summary>
    /// 单侧柜体端部排的基座定位输入。开孔高度是从基座顶部向下的孔口
    /// 长度；绝缘子高度包含其自带25 mm支架，基座结构支架另行传入。
    /// </summary>
    public sealed class FaceToFaceCabinetEndpointGeometry
    {
        public FaceToFaceCabinetEndpointGeometry(
            double baseOpeningHeight,
            double insulatorWithBracketHeight,
            double baseSupportBracketHeight,
            double breakpointOffsetFromBaseBridgeEdge)
        {
            ValidatePositiveFinite(baseOpeningHeight, nameof(baseOpeningHeight));
            ValidatePositiveFinite(
                insulatorWithBracketHeight,
                nameof(insulatorWithBracketHeight));
            ValidatePositiveFinite(
                baseSupportBracketHeight,
                nameof(baseSupportBracketHeight));
            if (double.IsNaN(breakpointOffsetFromBaseBridgeEdge) ||
                double.IsInfinity(breakpointOffsetFromBaseBridgeEdge))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(breakpointOffsetFromBaseBridgeEdge),
                    breakpointOffsetFromBaseBridgeEdge,
                    "基座出桥断点偏移必须为有限数值。");
            }

            BaseOpeningHeight = baseOpeningHeight;
            InsulatorWithBracketHeight = insulatorWithBracketHeight;
            BaseSupportBracketHeight = baseSupportBracketHeight;
            BreakpointOffsetFromBaseBridgeEdge =
                breakpointOffsetFromBaseBridgeEdge;
        }

        public double BaseOpeningHeight { get; }
        public double InsulatorWithBracketHeight { get; }
        public double BaseSupportBracketHeight { get; }
        public double BreakpointOffsetFromBaseBridgeEdge { get; }

        public double CalculateInsulatorConnectionHeight(
            double baseTopHeightCoordinate)
        {
            if (double.IsNaN(baseTopHeightCoordinate) ||
                double.IsInfinity(baseTopHeightCoordinate))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(baseTopHeightCoordinate));
            }

            return baseTopHeightCoordinate - BaseOpeningHeight +
                BaseSupportBracketHeight + InsulatorWithBracketHeight;
        }

        public double CalculateBreakpointCoordinate(
            double baseBridgeEdgeCoordinate)
        {
            if (double.IsNaN(baseBridgeEdgeCoordinate) ||
                double.IsInfinity(baseBridgeEdgeCoordinate))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(baseBridgeEdgeCoordinate));
            }

            return baseBridgeEdgeCoordinate +
                BreakpointOffsetFromBaseBridgeEdge;
        }

        private static void ValidatePositiveFinite(double value, string name)
        {
            if (!(value > 0d) || double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    name + "必须为有限正数。");
            }
        }
    }

    /// <summary>
    /// 面对面固定铜排规划器。默认从长度轴较小侧的 B 柜开始，到较大侧 A 柜结束。
    /// 柜顶两端复用面对墙五点搭接；中间普通组始终按 B→A 全局 0 基索引偶 I 奇桥。
    /// </summary>
    public static partial class FaceToFaceModelBusbarPlanner
    {
        private const int PhaseCount = 3;
        private const double GeometryTolerance = 0.001d;
        private const double ModelAlignmentTolerance = 0.2d;
        private const double FrameLengthOrderTolerance = 1d;
        // 转角基架旋转后实际 Box 中点会有小量偏差；10 mm 内按同一直线处理。
        private const double FrameLengthCenterClusterTolerance = 10d;
        private const double FrameWidthCenterChangeTolerance = 1d;
        private const double SmallOffsetWidthAlignmentTolerance = 2d;
        /// <summary>
        /// 换相路径优先尝试的绝缘距离附加量。规划、默认最小长度和界面
        /// 校验必须共用同一个值，避免界面提示与实际生成路径使用不同标准。
        /// </summary>
        public const double PhaseChangePreferredClearanceAddition = 5d;
        public const double DefaultPhaseChangeInsulationClearance = 125d;
        /// <summary>桥式换相首次布置时每侧额外保留的断点安全余量。</summary>
        private const double PhaseChangeInitialBridgeBoundarySideAddition = 5d;
        private const double SideToSidePhysicalAlignmentTolerance = 2d;
        private const string ExtensionBusbarPhaseSequencePropertyName =
            "伸出排相序";
        private const double SmallOffsetMilledBarWidthThreshold = 80d;
        private const double MilledPlateAdditionalLength = 20d;
        private const double TransitionFrameLengthCenterMergeTolerance =
            FrameLengthCenterClusterTolerance;

        private sealed class FaceToFaceStandaloneBaseAssignment
        {
            public Component2 BCabinetBaseComponent { get; set; }
            public Component2 ACabinetBaseComponent { get; set; }
            public Component2 BCabinetElevatedBaseComponent { get; set; }
            public Component2 ACabinetElevatedBaseComponent { get; set; }
            public Point3d BCabinetBaseCenter { get; set; }
            public Point3d ACabinetBaseCenter { get; set; }
        }

        private sealed class SideToSideFixedPhaseSideAssignment
        {
            internal Component2 FirstFrameComponent { get; set; }
            internal IReadOnlyList<InsulatorConnectionInfo>
                BaseInsulatorsByPhase { get; set; }
            internal IReadOnlyList<InsulatorConnectionInfo>
                FrameInsulatorsByPhase { get; set; }
        }

        private sealed class SideToSideFixedPhaseAssignments
        {
            internal SideToSideFixedPhaseSideAssignment BCabinet { get; set; }
            internal SideToSideFixedPhaseSideAssignment ACabinet { get; set; }
            // B 柜相序到 A 柜同名相序的映射。相序只能采用柜顶伸出排
            // 组件名中间字段，空间位置不得改写实际相位。
            internal IReadOnlyList<int>
                ACabinetPhysicalPhaseIndexesByLogicalPhase { get; set; }
            internal BusbarPhase_e NearestOuterPhase { get; set; }
            internal double PhaseAPairDistance { get; set; }
            internal double PhaseCPairDistance { get; set; }
#if DEBUG
            internal IReadOnlyList<IReadOnlyList<InsulatorConnectionInfo>>
                FrameHeightLaneGroups { get; set; }
#endif
        }

        private sealed class SideToSideFixedMiddleLapAlignment
        {
            internal Point3d BCabinetTerminalCenter { get; set; }
            internal Point3d ACabinetTerminalCenter { get; set; }
            internal Vector3d RouteThicknessDirection { get; set; }
            internal double StraightRouteThicknessCoordinate { get; set; }
            internal double InsulatorRouteThicknessCoordinate { get; set; }
            internal double BCabinetTerminalThicknessCoordinate { get; set; }
            internal double ACabinetTerminalThicknessCoordinate { get; set; }
            internal double BBridgeOffsetDistance { get; set; }
            internal double ABridgeOffsetDistance { get; set; }
            internal bool BCabinetStraightRouteCanLapInsulator { get; set; }
            internal bool ACabinetStraightRouteCanLapInsulator { get; set; }
            internal bool BaseAndFrameInsulatorNormalsAligned { get; set; }
            internal bool RequiresBridge { get; set; }
        }

        /// <summary>
        /// 侧对侧左右开孔（基座1）的固定换相规划。柜体和两个独立基座
    /// 仍按总装顶层及实际 Box 中心唯一归属；业务轴由 B 柜柜顶伸出排
    /// 解析，基座绝缘子只提供端点定位平面，首末基架的三相绝缘子按
    /// 高度组成共享的固定换相层。
        /// </summary>
        public static FaceToFaceModelBusbarPlan
            CreateSideToSideFixedPhaseChange(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            IReadOnlyList<double> breakpointPositions,
            int singlePhaseStackCount,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            IReadOnlyList<InsulatorConnectionInfo>
                preReadConnectionInsulators = null)
        {
            if (partCatalog == null)
            {
                throw new ArgumentNullException(nameof(partCatalog));
            }
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            if (extensionBusbars == null)
            {
                throw new ArgumentNullException(nameof(extensionBusbars));
            }

            FaceToWallModelBusbarPlanner.ValidateSinglePhaseStackCount(
                singlePhaseStackCount);
            ValidatePositiveFinite(bendingRadius, nameof(bendingRadius));
            ValidateNonNegativeFinite(bendAllowance, nameof(bendAllowance));

            var bCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("B柜");
            var aCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("A柜");
            EnsureDistinctCabinetHostComponents(
                bCabinetHostComponent,
                aCabinetHostComponent);
            var bCabinetPhaseSequence = ReadRequiredCabinetPhaseSequence(
                partCatalog,
                bCabinetHostComponent,
                "B柜");
            var aCabinetPhaseSequence = ReadRequiredCabinetPhaseSequence(
                partCatalog,
                aCabinetHostComponent,
                "A柜");
            var requiresPhaseChange = RequiresPhaseChangeByCabinetPlacement(
                bCabinetPhaseSequence,
                aCabinetPhaseSequence,
                true);
            FaceToWallPreviewTrace.Info(
                "柜体属性相序判定",
                "场景=侧对侧；B柜=" + bCabinetPhaseSequence +
                "；A柜=" + aCabinetPhaseSequence +
                "；换相=" + requiresPhaseChange);
            if (!requiresPhaseChange)
            {
                throw new InvalidOperationException(
                    "侧对侧固定换相要求 A/B 柜伸出排相序互为倒序；" +
                    "B柜=" + bCabinetPhaseSequence +
                    "，A柜=" + aCabinetPhaseSequence + "。 ");
            }
            var standaloneBaseAssignment = MatchStandaloneBasesToCabinets(
                partCatalog,
                bCabinetHostComponent,
                aCabinetHostComponent,
                axisSetting);
            var initialCabinetExtensionSides =
                SplitLargeOffsetCabinetExtensionBusbars(
                    partCatalog,
                    extensionBusbars,
                    bCabinetHostComponent,
                    aCabinetHostComponent);
            var routeAxisSetting = ResolveSideToSideAxisSetting(
                initialCabinetExtensionSides.BCabinetBusbars,
                standaloneBaseAssignment.BCabinetBaseCenter);
            var cabinetSpanDirection = ResolveSideToSideCabinetSpanDirection(
                routeAxisSetting,
                standaloneBaseAssignment.BCabinetBaseCenter,
                standaloneBaseAssignment.ACabinetBaseCenter);
            FaceToWallPreviewTrace.Info(
                "侧对侧固定换相-柜顶伸出排业务轴",
                "高=" + routeAxisSetting.HeightVector +
                "（柜顶伸出排长方向，正向指向 B 柜基座）" +
                "；宽=" + routeAxisSetting.WidthVector +
                "（B柜 C→A）" +
                "；长=" + routeAxisSetting.LengthVector +
                "（高 Cross 宽）");

            // 首次读取只用于按顶层柜体归属并解析业务轴；TerminalSegment
            // 必须用解析后的高度轴重新读取，否则会把默认 Y 轴误当成端头方向。
            var resolvedExtensionBusbars = CabinetTopExtensionBusbarReader.Read(
                partCatalog,
                swApp,
                routeAxisSetting);
            var cabinetExtensionSides =
                SplitLargeOffsetCabinetExtensionBusbars(
                    partCatalog,
                    resolvedExtensionBusbars,
                    bCabinetHostComponent,
                    aCabinetHostComponent);
            var orderedFrameBoxes = ReadSideToSideTopLevelFrameBoxes(
                partCatalog,
                routeAxisSetting,
                cabinetSpanDirection,
                standaloneBaseAssignment.BCabinetBaseCenter,
                standaloneBaseAssignment.ACabinetBaseCenter);
            if (orderedFrameBoxes.Count < 2)
            {
                throw new InvalidOperationException(
                    "侧对侧固定换相至少需要首、末两个不同的垂直基架。");
            }
            EnsureStraightFrameChain(orderedFrameBoxes);

            var availableConnectionInsulators =
                preReadConnectionInsulators?.ToList() ??
                FaceToWallModelBusbarPlanner.ReadAvailableConnectionInsulators(
                    partCatalog,
                    swApp);
            var fixedPhaseAssignments =
                CreateSideToSideFixedPhaseAssignments(
                    partCatalog,
                    routeAxisSetting,
                    cabinetExtensionSides,
                standaloneBaseAssignment,
                orderedFrameBoxes,
                availableConnectionInsulators);

            // 柜顶首段的折弯定位仍沿业务长度轴（绝缘子配合面法向）；
            // 中间排的 B→A 路径则沿独立柜间轴。两个坐标绝不能混用。
            var bConnectionLapCenterCoordinates = fixedPhaseAssignments.BCabinet
                .FrameInsulatorsByPhase
                .Select(frameInsulator => BusbarGeometryUtility.Project(
                    frameInsulator.ConnectionCircleCenter,
                    routeAxisSetting.LengthVector))
                .ToList()
                .AsReadOnly();
            var aConnectionLapCenterCoordinates = fixedPhaseAssignments.ACabinet
                .FrameInsulatorsByPhase
                .Select(frameInsulator => BusbarGeometryUtility.Project(
                    frameInsulator.ConnectionCircleCenter,
                    routeAxisSetting.LengthVector))
                .ToList()
                .AsReadOnly();
            var middleWidthDirection = cabinetSpanDirection
                .Cross(routeAxisSetting.HeightVector)
                .Normalized;
            var middleRouteAxisSetting = BusbarBridgeAxisSetting.CreateFromVectors(
                cabinetSpanDirection,
                routeAxisSetting.HeightVector,
                middleWidthDirection);

            var bCabinetContext = CreateSidePlanningContext(
                "B柜",
                partCatalog,
                routeAxisSetting,
                cabinetExtensionSides.BCabinetBusbars,
                availableConnectionInsulators,
                0d,
                singlePhaseStackCount,
                bendingRadius,
                bendAllowance,
                0,
                standaloneBaseAssignment.BCabinetBaseComponent,
                fixedPhaseAssignments.BCabinet.FirstFrameComponent,
                fixedPhaseAssignments.BCabinet.BaseInsulatorsByPhase,
                fixedPhaseAssignments.BCabinet.FrameInsulatorsByPhase,
                null,
                bConnectionLapCenterCoordinates,
                true);
            var reversedAxisSetting =
                CreateLengthReversedAxisSetting(routeAxisSetting);
            var aCabinetContext = CreateSidePlanningContext(
                "A柜",
                partCatalog,
                reversedAxisSetting,
                cabinetExtensionSides.ACabinetBusbars,
                availableConnectionInsulators,
                0d,
                singlePhaseStackCount,
                bendingRadius,
                bendAllowance,
                0,
                standaloneBaseAssignment.ACabinetBaseComponent,
                fixedPhaseAssignments.ACabinet.FirstFrameComponent,
                fixedPhaseAssignments.ACabinet.BaseInsulatorsByPhase,
                fixedPhaseAssignments.ACabinet.FrameInsulatorsByPhase,
                fixedPhaseAssignments
                    .ACabinetPhysicalPhaseIndexesByLogicalPhase,
                aConnectionLapCenterCoordinates.Select(
                    phaseLapCenterCoordinate =>
                        -phaseLapCenterCoordinate).ToList().AsReadOnly(),
                true);

            EnsureDistinctCabinetHosts(
                bCabinetContext,
                aCabinetContext);
            ValidateSideToSideFixedPhaseRouteCompatibility(
                bCabinetContext,
                aCabinetContext,
                middleRouteAxisSetting);

            var useBridgeMiddleBusbar =
                ShouldUseSideToSideFixedBridgeMiddleBusbar(
                    bCabinetContext,
                    aCabinetContext,
                    fixedPhaseAssignments,
                    middleRouteAxisSetting);
            var routeDefinitions = new List<FaceToFaceRouteGroupDefinition>
            {
                CreateSideToSideFixedMiddleRouteDefinition(
                    bCabinetContext,
                    aCabinetContext,
                    fixedPhaseAssignments,
                    middleRouteAxisSetting,
                    useBridgeMiddleBusbar)
            }.AsReadOnly();

            var result = new FaceToFaceModelBusbarPlan
            {
                CabinetTopExtensionBusbars = resolvedExtensionBusbars,
                BCabinetTopExtensionBusbars =
                    cabinetExtensionSides.BCabinetBusbars.AsReadOnly(),
                ACabinetTopExtensionBusbars =
                    cabinetExtensionSides.ACabinetBusbars.AsReadOnly(),
                OrderedFrameBoxes = orderedFrameBoxes
            };
            // 两侧柜顶连接段分别以各自柜体伸出排为独立定位基准，
            // 中间排最后生成，避免 A 侧从中间排继承同源姿态。
            result.GeneratedBusbarGroups.Add(CreateCabinetConnectionGroup(
                "侧对侧-B柜柜顶换相连接段",
                FaceToFaceBusbarGroupKind_e.BCabinetConnection,
                bCabinetContext));
            result.GeneratedBusbarGroups.Add(CreateCabinetConnectionGroup(
                "侧对侧-A柜柜顶换相连接段",
                FaceToFaceBusbarGroupKind_e.ACabinetConnection,
                aCabinetContext));
            foreach (var routeDefinition in routeDefinitions)
            {
                result.GeneratedBusbarGroups.Add(CreateGeneratedRouteGroup(
                    routeDefinition,
                    bCabinetContext,
                    middleRouteAxisSetting));
            }
            result.GeneratedBusbars.AddRange(
                result.GeneratedBusbarGroups.SelectMany(
                    generatedBusbarGroup => generatedBusbarGroup.Busbars));
#if DEBUG
            try
            {
                result.SideToSideFixedPhaseChangePlanning =
                    CreateSideToSideFixedPhaseChangePlanningSnapshot(
                        bCabinetHostComponent,
                        aCabinetHostComponent,
                        standaloneBaseAssignment,
                        orderedFrameBoxes,
                        fixedPhaseAssignments,
                        middleRouteAxisSetting);
            }
            catch (Exception planningCaptureException)
            {
                result.SideToSideFixedPhaseChangePlanningCaptureWarning =
                    planningCaptureException.ToString();
                FaceToWallPreviewTrace.Error(
                    "侧对侧固定换相-Debug规划快照整理失败",
                    planningCaptureException);
            }
#endif

            FaceToWallPreviewTrace.Info(
                "侧对侧固定换相模型规划-完成",
                "A相两柜端距=" +
                fixedPhaseAssignments.PhaseAPairDistance.ToString("0.###") +
                "，C相两柜端距=" +
                fixedPhaseAssignments.PhaseCPairDistance.ToString("0.###") +
                "，高度最近层相序=" +
                fixedPhaseAssignments.NearestOuterPhase +
                "，B相=中间层，另一外相=最外层" +
                "，铜排组=" + result.GeneratedBusbarGroups.Count +
                "，铜排路径=" + result.GeneratedBusbars.Count);
            return result;
        }

        /// <summary>
        /// 侧对侧前后开孔（基座2）的完整规划。两柜柜顶首段、首段之间的
        /// 中间搭接排以及基架间路径都在同一计划中返回。
        /// </summary>
        public static FaceToFaceModelBusbarPlan
            CreateSideToSideSecondTypeFirstSegment(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            IReadOnlyList<double> breakpointPositions,
            int singlePhaseStackCount,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            double insulationClearance = 125d,
            IReadOnlyList<InsulatorConnectionInfo>
                preReadConnectionInsulators = null,
            bool isLargeOffset = false,
            bool isSmallOffset = false,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            int? requestedPhaseChangeIntervalIndex = null,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                largeOffsetBreakpointProtectionRanges = null)
        {
            if (partCatalog == null)
            {
                throw new ArgumentNullException(nameof(partCatalog));
            }
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            if (extensionBusbars == null)
            {
                throw new ArgumentNullException(nameof(extensionBusbars));
            }

            FaceToWallModelBusbarPlanner.ValidateSinglePhaseStackCount(
                singlePhaseStackCount);
            ValidatePositiveFinite(bendingRadius, nameof(bendingRadius));
            ValidateNonNegativeFinite(bendAllowance, nameof(bendAllowance));
            ValidateNonNegativeFinite(
                insulationClearance,
                nameof(insulationClearance));

            var normalizedBreakpointPositions = NormalizeBreakpointPositions(
                breakpointPositions);
            var bCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("B柜");
            var aCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("A柜");
            EnsureDistinctCabinetHostComponents(
                bCabinetHostComponent,
                aCabinetHostComponent);
            var standaloneBaseAssignment =
                MatchStandaloneBasesToCabinets(
                    partCatalog,
                    bCabinetHostComponent,
                    aCabinetHostComponent,
                    axisSetting);
            var initialCabinetExtensionSides =
                SplitLargeOffsetCabinetExtensionBusbars(
                    partCatalog,
                    extensionBusbars,
                    bCabinetHostComponent,
                    aCabinetHostComponent);
            var routeAxisSetting = ResolveSideToSideAxisSetting(
                initialCabinetExtensionSides.BCabinetBusbars,
                standaloneBaseAssignment.BCabinetBaseCenter);
            routeAxisSetting = OrientSideToSideAxisFromBCabinetToACabinet(
                routeAxisSetting,
                standaloneBaseAssignment.BCabinetBaseCenter,
                standaloneBaseAssignment.ACabinetBaseCenter);
            var resolvedExtensionBusbars = CabinetTopExtensionBusbarReader.Read(
                partCatalog,
                swApp,
                routeAxisSetting);
            var cabinetExtensionSides =
                SplitLargeOffsetCabinetExtensionBusbars(
                    partCatalog,
                    resolvedExtensionBusbars,
                    bCabinetHostComponent,
                    aCabinetHostComponent);
            // Physical boxes are ordered by the actual B->A cabinet span, but
            // the generic planner consumes Length/Width projections in the
            // final business axis. Reuse that final axis here instead of
            // letting the physical reader derive a second one.
            var cabinetSpanDirection = ResolveSideToSideCabinetSpanDirection(
                routeAxisSetting,
                standaloneBaseAssignment.BCabinetBaseCenter,
                standaloneBaseAssignment.ACabinetBaseCenter);
            var orderedPhysicalFrameBoxes = ReadSideToSideTopLevelFrameBoxes(
                partCatalog,
                routeAxisSetting,
                cabinetSpanDirection,
                standaloneBaseAssignment.BCabinetBaseCenter,
                standaloneBaseAssignment.ACabinetBaseCenter);
            var middleRouteAxisSetting =
                CreateSideToSideMiddleRouteAxisSetting(
                    routeAxisSetting,
                    cabinetSpanDirection);
            var routeProjectedFrameBoxes =
                ReprojectSideToSideFrameBoxesToRouteAxis(
                    orderedPhysicalFrameBoxes,
                    middleRouteAxisSetting,
                    !isLargeOffset);
            var orderedFrameBoxes = isLargeOffset
                ? OrderLargeOffsetFrameBoxesByGeometry(
                    routeProjectedFrameBoxes,
                    BusbarGeometryUtility.Project(
                        standaloneBaseAssignment.BCabinetBaseCenter,
                        middleRouteAxisSetting.LengthVector),
                    BusbarGeometryUtility.Project(
                        standaloneBaseAssignment.ACabinetBaseCenter,
                        middleRouteAxisSetting.LengthVector))
                : routeProjectedFrameBoxes;
            if (orderedFrameBoxes.Count == 0)
            {
                throw new InvalidOperationException(
                    "侧对侧第二种没有识别到可用于首段定位的基架。");
            }

            var globalFrameLengthOrigin = orderedFrameBoxes[0].LengthMinimum;
            var bFirstBreakpointModelCoordinate =
                orderedFrameBoxes[0].LengthMinimum +
                normalizedBreakpointPositions[0];
            var aLastBreakpointModelCoordinate =
                globalFrameLengthOrigin +
                normalizedBreakpointPositions[
                    normalizedBreakpointPositions.Count - 1];
            var availableConnectionInsulators =
                preReadConnectionInsulators?.ToList() ??
                FaceToWallModelBusbarPlanner
                    .ReadAvailableConnectionInsulators(partCatalog, swApp);
            var firstSegmentPlan =
                SideToSideSecondTypeFirstSegmentPlanner.Create(
                    partCatalog,
                    routeAxisSetting,
                    cabinetExtensionSides.BCabinetBusbars,
                    cabinetExtensionSides.ACabinetBusbars,
                    standaloneBaseAssignment.BCabinetBaseComponent,
                    standaloneBaseAssignment.BCabinetElevatedBaseComponent,
                    standaloneBaseAssignment.ACabinetBaseComponent,
                    standaloneBaseAssignment.ACabinetElevatedBaseComponent,
                    availableConnectionInsulators,
                    bFirstBreakpointModelCoordinate,
                    aLastBreakpointModelCoordinate,
                    singlePhaseStackCount,
                    bendingRadius,
                    bendAllowance,
                    insulationClearance,
                    !isLargeOffset && !isSmallOffset);
            var bCabinetMiddleContext =
                CreateSideToSideSecondTypeMiddlePlanningContext(
                    "B柜",
                    firstSegmentPlan.BCabinetFirstSegmentBusbars,
                    true,
                    standaloneBaseAssignment.BCabinetBaseComponent,
                    orderedFrameBoxes.First().Component,
                    middleRouteAxisSetting);
            var aCabinetMiddleContext =
                CreateSideToSideSecondTypeMiddlePlanningContext(
                    "A柜",
                    firstSegmentPlan.ACabinetFirstSegmentBusbars,
                    false,
                    standaloneBaseAssignment.ACabinetBaseComponent,
                    orderedFrameBoxes.Last().Component,
                    middleRouteAxisSetting);

            // 首段采用侧对侧第二种专用七点路径；其余区间统一复用面对面
            // 规划器，确保常规、大偏移、小偏移及换相场景都生成完整中间段。
            var completeMiddlePlan = CreateCore(
                partCatalog,
                swApp,
                routeAxisSetting,
                resolvedExtensionBusbars,
                breakpointPositions,
                singlePhaseStackCount,
                isLargeOffset,
                bendingRadius,
                bendAllowance,
                isSmallOffset,
                largeOffsetBreakpointProtectionRanges,
                insulationClearance,
                false,
                generatedBusbarWidth,
                generatedBusbarThickness,
                requestedPhaseChangeIntervalIndex,
                true,
                orderedFrameBoxes,
                false,
                true,
                middleRouteAxisSetting,
                bCabinetMiddleContext,
                aCabinetMiddleContext,
                null,
                null);

            var result = new FaceToFaceModelBusbarPlan
            {
                CabinetTopExtensionBusbars = resolvedExtensionBusbars,
                BCabinetTopExtensionBusbars =
                    cabinetExtensionSides.BCabinetBusbars.AsReadOnly(),
                ACabinetTopExtensionBusbars =
                    cabinetExtensionSides.ACabinetBusbars.AsReadOnly(),
                OrderedFrameBoxes = orderedFrameBoxes,
                LargeOffsetRegion = completeMiddlePlan.LargeOffsetRegion,
                SmallOffsetRegion = completeMiddlePlan.SmallOffsetRegion,
                PhaseChangeRegion = completeMiddlePlan.PhaseChangeRegion
            };
            var bCabinetFirstSegmentGroup =
                new FaceToFaceGeneratedBusbarGroup
                {
                    DisplayName = "侧对侧第二种-B柜基座排",
                    Kind = FaceToFaceBusbarGroupKind_e.BCabinetConnection,
                    RouteGroupIndex = -1,
                    IsBridge = false
                };
            bCabinetFirstSegmentGroup.Busbars.AddRange(
                firstSegmentPlan.BCabinetFirstSegmentBusbars);
            var aCabinetFirstSegmentGroup =
                new FaceToFaceGeneratedBusbarGroup
                {
                    DisplayName = "侧对侧第二种-A柜基座排",
                    Kind = FaceToFaceBusbarGroupKind_e.ACabinetConnection,
                    RouteGroupIndex = -1,
                    IsBridge = false
                };
            aCabinetFirstSegmentGroup.Busbars.AddRange(
                firstSegmentPlan.ACabinetFirstSegmentBusbars);
            result.GeneratedBusbarGroups.Add(bCabinetFirstSegmentGroup);
            result.GeneratedBusbarGroups.Add(aCabinetFirstSegmentGroup);

            // 七点首段的末端并不是通用 B→A 长度路径的一部分。它们之间
            // 的跨柜宽度搭接排由专用首段规划器计算，必须单独保留，否则
            // 预览虽有两侧首段，中间会出现断口。
            if (!isLargeOffset && !isSmallOffset)
            {
                var middleConnectionGroup =
                    new FaceToFaceGeneratedBusbarGroup
                    {
                        DisplayName = "侧对侧第二种-中间搭接排",
                        Kind = FaceToFaceBusbarGroupKind_e.LengthwiseOrdinary,
                        RouteGroupIndex = -1,
                        IsBridge = false
                    };
                middleConnectionGroup.Busbars.AddRange(
                    firstSegmentPlan.MiddleConnectionBusbars ??
                    Enumerable.Empty<PlanarBendBusbarSetting>());
                result.GeneratedBusbarGroups.Add(middleConnectionGroup);
            }

            var useDedicatedMiddleConnectionOnly =
                !isLargeOffset &&
                !isSmallOffset &&
                completeMiddlePlan.PhaseChangeRegion == null;
            foreach (var generatedGroup in completeMiddlePlan.GeneratedBusbarGroups)
            {
                if (generatedGroup.Kind == FaceToFaceBusbarGroupKind_e.BCabinetConnection ||
                    generatedGroup.Kind == FaceToFaceBusbarGroupKind_e.ACabinetConnection)
                {
                    continue;
                }
                if (useDedicatedMiddleConnectionOnly &&
                    generatedGroup.Kind ==
                        FaceToFaceBusbarGroupKind_e.LengthwiseOrdinary)
                {
                    // 普通侧对侧的两柜跨度已由上面的专用中间搭接排完成。
                    // 面对面普通段沿长度轴生成，只会在 B 柜三相位置重复
                    // 形成横排，不能再次合入侧对侧结果。
                    continue;
                }

                result.GeneratedBusbarGroups.Add(generatedGroup);
            }
            result.SmallOffsetTransitionPreviewBusbars.AddRange(
                completeMiddlePlan.SmallOffsetTransitionPreviewBusbars);
            result.GeneratedBusbars.AddRange(
                result.GeneratedBusbarGroups.SelectMany(
                    generatedBusbarGroup => generatedBusbarGroup.Busbars));
            result.GeneratedBusbars.AddRange(
                result.SmallOffsetTransitionPreviewBusbars);
            FaceToWallPreviewTrace.Info(
                "侧对侧第二种柜顶首段规划-完成",
                "B柜首段=" +
                firstSegmentPlan.BCabinetFirstSegmentBusbars.Count +
                "，A柜首段=" +
                firstSegmentPlan.ACabinetFirstSegmentBusbars.Count +
                "，通用规划组=" +
                completeMiddlePlan.GeneratedBusbarGroups.Count +
                "，最终生成组=" + result.GeneratedBusbarGroups.Count +
                "，最终铜排=" + result.GeneratedBusbars.Count +
                "，绝缘距离=" + insulationClearance.ToString("0.###") +
                " mm，折弯伸出余量=单根排厚+折弯半径");
            return result;
        }

        public static FaceToFaceModelBusbarPlan Create(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            IReadOnlyList<double> breakpointPositions,
            int singlePhaseStackCount,
            bool isLargeOffset,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            bool isSmallOffset = false,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                largeOffsetBreakpointProtectionRanges = null,
            double phaseChangeInsulationClearance = 125d,
            bool useMilledBarForLargeSmallOffset = false,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            int? requestedPhaseChangeIntervalIndex = null,
            bool axisAlreadyOriented = false,
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxesOverride = null,
            bool allowInsufficientPhaseChangeClearancePreview = false,
            FaceToFaceCabinetEndpointGeometry bCabinetEndpoint = null,
            FaceToFaceCabinetEndpointGeometry aCabinetEndpoint = null)
        {
            return CreateCore(
                partCatalog,
                swApp,
                axisSetting,
                extensionBusbars,
                breakpointPositions,
                singlePhaseStackCount,
                isLargeOffset,
                bendingRadius,
                bendAllowance,
                isSmallOffset,
                largeOffsetBreakpointProtectionRanges,
                phaseChangeInsulationClearance,
                useMilledBarForLargeSmallOffset,
                generatedBusbarWidth,
                generatedBusbarThickness,
                requestedPhaseChangeIntervalIndex,
                axisAlreadyOriented,
                orderedFrameBoxesOverride,
                allowInsufficientPhaseChangeClearancePreview,
                false,
                null,
                null,
                null,
                bCabinetEndpoint,
                aCabinetEndpoint);
        }

        private static FaceToFaceModelBusbarPlan CreateCore(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            IReadOnlyList<double> breakpointPositions,
            int singlePhaseStackCount,
            bool isLargeOffset,
            double bendingRadius,
            double bendAllowance,
            bool isSmallOffset,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                largeOffsetBreakpointProtectionRanges,
            double phaseChangeInsulationClearance,
            bool useMilledBarForLargeSmallOffset,
            double? generatedBusbarWidth,
            double? generatedBusbarThickness,
            int? requestedPhaseChangeIntervalIndex,
            bool axisAlreadyOriented,
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxesOverride,
            bool allowInsufficientPhaseChangeClearancePreview,
            bool useWidthAxisForCabinetSeparation,
            BusbarBridgeAxisSetting routeGeometryAxisSettingOverride,
            FaceToFaceSidePlanningContext bCabinetContextOverride,
            FaceToFaceSidePlanningContext aCabinetContextOverride,
            FaceToFaceCabinetEndpointGeometry bCabinetEndpoint,
            FaceToFaceCabinetEndpointGeometry aCabinetEndpoint)
        {
            if (partCatalog == null) throw new ArgumentNullException(nameof(partCatalog));
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            if (axisSetting == null) throw new ArgumentNullException(nameof(axisSetting));
            if (extensionBusbars == null) throw new ArgumentNullException(nameof(extensionBusbars));
            if ((bCabinetEndpoint == null) != (aCabinetEndpoint == null))
                throw new ArgumentException("A/B柜端部几何必须同时提供，不能只规划单侧L排接口。");
            FaceToWallModelBusbarPlanner.ValidateSinglePhaseStackCount(singlePhaseStackCount);
            ValidatePositiveFinite(bendingRadius, nameof(bendingRadius));
            ValidateNonNegativeFinite(bendAllowance, nameof(bendAllowance));
            ValidateNonNegativeFinite(
                phaseChangeInsulationClearance,
                nameof(phaseChangeInsulationClearance));
            if (phaseChangeInsulationClearance <= GeometryTolerance)
            {
                phaseChangeInsulationClearance =
                    DefaultPhaseChangeInsulationClearance;
                FaceToWallPreviewTrace.Info(
                    "换相绝缘距离归一化",
                    "输入净距为 0，已按默认 125 mm 规划。 ");
            }
            ValidateOptionalGeneratedBusbarSpecification(
                generatedBusbarWidth,
                generatedBusbarThickness);
            if (isLargeOffset && isSmallOffset)
            {
                throw new InvalidOperationException(
                    "面对面大偏移与小偏移标记不能同时启用。");
            }

            var usesEndpointDrivenMiddlePath =
                bCabinetEndpoint != null && aCabinetEndpoint != null;
            var normalizedBreakpointPositions = usesEndpointDrivenMiddlePath
                ? new List<double> { 0d, 1d }.AsReadOnly()
                : NormalizeBreakpointPositions(breakpointPositions);
            var bCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("B柜");
            var aCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("A柜");
            EnsureDistinctCabinetHostComponents(
                bCabinetHostComponent,
                aCabinetHostComponent);
            var bCabinetPhaseSequence = ReadRequiredCabinetPhaseSequence(
                partCatalog,
                bCabinetHostComponent,
                "B柜");
            var aCabinetPhaseSequence = ReadRequiredCabinetPhaseSequence(
                partCatalog,
                aCabinetHostComponent,
                "A柜");
            var requiresPhaseChange = RequiresPhaseChangeByCabinetPlacement(
                bCabinetPhaseSequence,
                aCabinetPhaseSequence,
                useWidthAxisForCabinetSeparation);
            FaceToWallPreviewTrace.Info(
                "柜体属性相序判定",
                "场景=" +
                (useWidthAxisForCabinetSeparation ? "侧对侧" : "面对面") +
                "；B柜=" + bCabinetPhaseSequence +
                "；A柜=" + aCabinetPhaseSequence +
                "；换相=" + requiresPhaseChange);
            var standaloneBaseAssignment = MatchStandaloneBasesToCabinets(
                partCatalog,
                bCabinetHostComponent,
                aCabinetHostComponent,
                axisSetting);
            var bCabinetBaseComponent =
                standaloneBaseAssignment.BCabinetBaseComponent;
            var aCabinetBaseComponent =
                standaloneBaseAssignment.ACabinetBaseComponent;
            var bCabinetBaseCenter =
                standaloneBaseAssignment.BCabinetBaseCenter;
            var aCabinetBaseCenter =
                standaloneBaseAssignment.ACabinetBaseCenter;
            var routeAxisSetting = axisAlreadyOriented
                ? axisSetting
                : OrientLengthAxisFromBCabinetToACabinet(
                    axisSetting,
                    bCabinetBaseCenter,
                    aCabinetBaseCenter);
            var routeGeometryAxisSetting =
                routeGeometryAxisSettingOverride ?? routeAxisSetting;
            var cabinetExtensionSides =
                SplitLargeOffsetCabinetExtensionBusbars(
                    partCatalog,
                    extensionBusbars,
                    bCabinetHostComponent,
                    aCabinetHostComponent);
            MiddleBusbarPathPlan middlePathPlan = null;
            FaceToFaceUnifiedPlanningResult unifiedPlanningResult = null;
            double? endpointBreakpointOrigin = null;
            if (bCabinetEndpoint != null && aCabinetEndpoint != null)
            {
                if (!BusbarGeometryUtility.TryGetComponentProjectionRange(
                        bCabinetBaseComponent,
                        routeGeometryAxisSetting.LengthVector,
                        out var ignoredBBaseMinimum,
                        out var bBaseBridgeEdge) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        aCabinetBaseComponent,
                        routeGeometryAxisSetting.LengthVector,
                        out var aBaseBridgeEdge,
                        out var ignoredABaseMaximum))
                {
                    throw new InvalidOperationException(
                        "无法读取A/B基座出桥边界，不能规划中间铜排路径。");
                }

                var bBreakpointCoordinate = bBaseBridgeEdge -
                    bCabinetEndpoint.BreakpointOffsetFromBaseBridgeEdge;
                var aBreakpointCoordinate = aBaseBridgeEdge +
                    aCabinetEndpoint.BreakpointOffsetFromBaseBridgeEdge;
                var bWidthCoordinate = BusbarGeometryUtility.Project(
                    bCabinetBaseCenter, routeGeometryAxisSetting.WidthVector);
                var aWidthCoordinate = BusbarGeometryUtility.Project(
                    aCabinetBaseCenter, routeGeometryAxisSetting.WidthVector);
                var planningBusbarWidth = generatedBusbarWidth ?? 80d;
                var planningBusbarThickness = generatedBusbarThickness ?? 10d;
                var phaseSpacing = ReadCommonPhaseSpacingFromEndpoints(
                    cabinetExtensionSides.BCabinetBusbars,
                    cabinetExtensionSides.ACabinetBusbars,
                    routeGeometryAxisSetting.WidthVector);
                var absoluteWidthOffset = Math.Abs(
                    aWidthCoordinate - bWidthCoordinate);
                if (!useWidthAxisForCabinetSeparation)
                {
                    var unifiedPlan = FaceToFaceUnifiedPlanner.Create(
                        new FaceToFaceUnifiedPlanningInput
                        {
                            ACabinetBaseWidth = ReadConnectedBaseWidth(aCabinetBaseComponent),
                            BCabinetBaseWidth = ReadConnectedBaseWidth(bCabinetBaseComponent),
                            StartX = bBreakpointCoordinate,
                            StartY = bWidthCoordinate,
                            EndX = aBreakpointCoordinate,
                            EndY = aWidthCoordinate,
                            PhaseSpacing = phaseSpacing,
                            BusbarWidth = planningBusbarWidth,
                            BusbarThickness = planningBusbarThickness,
                            RequiresPhaseChange = requiresPhaseChange,
                            UseMilledBarForLargeSmallOffset =
                                useMilledBarForLargeSmallOffset,
                            PhaseChangeInsulationClearance =
                                phaseChangeInsulationClearance
                        });
                    middlePathPlan = unifiedPlan.MiddlePath;
                    unifiedPlanningResult = unifiedPlan;
                    isLargeOffset = unifiedPlan.IsLargeOffset;
                    isSmallOffset = unifiedPlan.HasOffset
                        && !unifiedPlan.IsLargeOffset;
                }
                else
                {
                    // 侧对侧仍保留原有入口；本期统一改造范围只是面对面。
                    middlePathPlan = MiddleBusbarPathPlanner.Create(
                        new MiddleBusbarPathPlanningInput
                        {
                            StartX = bBreakpointCoordinate,
                            StartY = bWidthCoordinate,
                            EndX = aBreakpointCoordinate,
                            EndY = aWidthCoordinate,
                            OverlapLength = planningBusbarWidth,
                            PreferredBreakpointSpacing = 2000d,
                            MaximumDevelopedLength = 3000d,
                            MinimumOffsetTransitionSpan = Math.Max(
                                600d,
                                2d * (bendingRadius + planningBusbarWidth)),
                            PhaseChangeInsulationClearance =
                                phaseChangeInsulationClearance,
                            RequiresPhaseChange = requiresPhaseChange
                        });
                }
                endpointBreakpointOrigin = bBreakpointCoordinate;
                normalizedBreakpointPositions = middlePathPlan.Points
                    .Select(point => point.X - bBreakpointCoordinate)
                    .ToList()
                    .AsReadOnly();
                requestedPhaseChangeIntervalIndex = requiresPhaseChange
                    ? 0
                    : (int?)null;
                if (!middlePathPlan.Segments.Any(segment =>
                    (segment.Role & MiddleBusbarSegmentRole.Offset) != 0))
                {
                    isLargeOffset = false;
                    isSmallOffset = false;
                }
                FaceToWallPreviewTrace.Info(
                    "面对面中间路径-端部接口规划",
                    "B端=" + bBreakpointCoordinate.ToString("0.###") +
                    "，A端=" + aBreakpointCoordinate.ToString("0.###") +
                    "，分段=" + middlePathPlan.Segments.Count +
                    "，最佳断点间距=2000 mm，最大展开长度=3000 mm" +
                    "，换相=" + (requiresPhaseChange ? "第一段" : "否") +
                    "，偏移折弯=" +
                    (Math.Abs(aWidthCoordinate - bWidthCoordinate) > GeometryTolerance
                        ? "中间段"
                        : "无"));
            }
            var rawCabinetSeparationDirection =
                useWidthAxisForCabinetSeparation
                    ? routeAxisSetting.WidthVector
                    : routeAxisSetting.LengthVector;
            var bCabinetBoxCenter = ReadRequiredComponentBoxCenter(
                bCabinetHostComponent,
                "B柜");
            var aCabinetBoxCenter = ReadRequiredComponentBoxCenter(
                aCabinetHostComponent,
                "A柜");
            var rawBCabinetSeparationCoordinate =
                BusbarGeometryUtility.Project(
                    bCabinetBoxCenter,
                    rawCabinetSeparationDirection);
            var rawACabinetSeparationCoordinate =
                BusbarGeometryUtility.Project(
                    aCabinetBoxCenter,
                    rawCabinetSeparationDirection);
            var cabinetSeparationDirection =
                rawACabinetSeparationCoordinate <
                rawBCabinetSeparationCoordinate
                    ? rawCabinetSeparationDirection.Negate()
                    : rawCabinetSeparationDirection;
            FaceToWallPreviewTrace.Info(
                "柜间分离方向验证",
                "方向=" +
                (useWidthAxisForCabinetSeparation ? "宽度轴（侧对侧）" : "长度轴（面对面）") +
                "，B柜BOX中心=" + FormatPoint(bCabinetBoxCenter) +
                "，B柜方向坐标=" +
                BusbarGeometryUtility.Project(
                    bCabinetBoxCenter,
                    cabinetSeparationDirection).ToString("0.###") +
                "，A柜BOX中心=" + FormatPoint(aCabinetBoxCenter) +
                "，A柜方向坐标=" +
                BusbarGeometryUtility.Project(
                    aCabinetBoxCenter,
                    cabinetSeparationDirection).ToString("0.###"));
            cabinetExtensionSides.SeparationLengthCoordinate =
                CalculateCabinetSeparationLengthCoordinate(
                    cabinetExtensionSides,
                    cabinetSeparationDirection);
            FaceToFaceSmallOffsetRegionPlan smallOffsetRegion = null;
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion = null;
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxes;
            if (orderedFrameBoxesOverride != null)
            {
                var suppliedFrameBoxes = orderedFrameBoxesOverride
                    .Where(frameBox => frameBox != null)
                    .ToList();
                if (suppliedFrameBoxes.Count != orderedFrameBoxesOverride.Count ||
                    suppliedFrameBoxes.Count == 0)
                {
                    throw new InvalidOperationException(
                        "提供的基架Box集合不能为空或包含空项。");
                }

                orderedFrameBoxes = suppliedFrameBoxes.AsReadOnly();
                if (isSmallOffset)
                {
                    smallOffsetRegion = middlePathPlan != null
                        ? CreateSmallOffsetRegionFromPath(middlePathPlan)
                        : AnalyzeSmallOffsetRegion(
                            orderedFrameBoxes,
                            BusbarGeometryUtility.Project(
                                bCabinetBaseCenter,
                                routeGeometryAxisSetting.LengthVector),
                            BusbarGeometryUtility.Project(
                                bCabinetBaseCenter,
                                routeGeometryAxisSetting.WidthVector),
                            BusbarGeometryUtility.Project(
                                aCabinetBaseCenter,
                                routeGeometryAxisSetting.LengthVector),
                            BusbarGeometryUtility.Project(
                                aCabinetBaseCenter,
                                routeGeometryAxisSetting.WidthVector));
                    phaseChangeRegion = AnalyzeNoOffsetPhaseChangeRegion(
                        orderedFrameBoxes,
                        cabinetExtensionSides.BCabinetBusbars,
                        cabinetExtensionSides.ACabinetBusbars,
                        routeAxisSetting,
                        requiresPhaseChange,
                        phaseChangeInsulationClearance,
                        generatedBusbarWidth,
                        generatedBusbarThickness,
                        smallOffsetRegion.BCabinetReferenceFrame,
                        singlePhaseStackCount,
                        bendAllowance,
                        requestedPhaseChangeIntervalIndex.HasValue
                            ? IsBridgeRouteGroup(
                                requestedPhaseChangeIntervalIndex.Value)
                            : false);
                }
                else if (isLargeOffset)
                {
                    phaseChangeRegion = AnalyzeNoOffsetPhaseChangeRegion(
                        orderedFrameBoxes,
                        cabinetExtensionSides.BCabinetBusbars,
                        cabinetExtensionSides.ACabinetBusbars,
                        routeAxisSetting,
                        requiresPhaseChange,
                        phaseChangeInsulationClearance,
                        generatedBusbarWidth,
                        generatedBusbarThickness,
                        orderedFrameBoxes.First(),
                        singlePhaseStackCount,
                        bendAllowance,
                        requestedPhaseChangeIntervalIndex.HasValue
                            ? IsBridgeRouteGroup(
                                requestedPhaseChangeIntervalIndex.Value)
                            : false);
                }
                else if (!isLargeOffset)
                {
                    phaseChangeRegion = AnalyzeNoOffsetPhaseChangeRegion(
                        orderedFrameBoxes,
                        cabinetExtensionSides.BCabinetBusbars,
                        cabinetExtensionSides.ACabinetBusbars,
                        routeAxisSetting,
                        requiresPhaseChange,
                        phaseChangeInsulationClearance,
                        generatedBusbarWidth,
                        generatedBusbarThickness,
                        null,
                        singlePhaseStackCount,
                        bendAllowance,
                        requestedPhaseChangeIntervalIndex.HasValue
                            ? IsBridgeRouteGroup(
                                requestedPhaseChangeIntervalIndex.Value)
                            : true);
                }
            }
            else if (isLargeOffset)
            {
                orderedFrameBoxes = ReadLargeOffsetOrderedFrameBoxes(
                    partCatalog,
                    routeGeometryAxisSetting,
                    bCabinetBaseCenter,
                    aCabinetBaseCenter);
                phaseChangeRegion = AnalyzeNoOffsetPhaseChangeRegion(
                    orderedFrameBoxes,
                    cabinetExtensionSides.BCabinetBusbars,
                    cabinetExtensionSides.ACabinetBusbars,
                    routeAxisSetting,
                    requiresPhaseChange,
                    phaseChangeInsulationClearance,
                    generatedBusbarWidth,
                    generatedBusbarThickness,
                    orderedFrameBoxes.First(),
                    singlePhaseStackCount,
                    bendAllowance,
                    requestedPhaseChangeIntervalIndex.HasValue
                        ? IsBridgeRouteGroup(
                            requestedPhaseChangeIntervalIndex.Value)
                        : false);
            }
            else if (isSmallOffset)
            {
                orderedFrameBoxes = ReadSmallOffsetOrderedFrameBoxes(
                    partCatalog,
                    routeGeometryAxisSetting);
                smallOffsetRegion = middlePathPlan != null
                    ? CreateSmallOffsetRegionFromPath(middlePathPlan)
                    : AnalyzeSmallOffsetRegion(
                        orderedFrameBoxes,
                        BusbarGeometryUtility.Project(
                            bCabinetBaseCenter,
                            routeGeometryAxisSetting.LengthVector),
                        BusbarGeometryUtility.Project(
                            bCabinetBaseCenter,
                            routeGeometryAxisSetting.WidthVector),
                        BusbarGeometryUtility.Project(
                            aCabinetBaseCenter,
                            routeGeometryAxisSetting.LengthVector),
                        BusbarGeometryUtility.Project(
                            aCabinetBaseCenter,
                            routeGeometryAxisSetting.WidthVector));
                phaseChangeRegion = AnalyzeNoOffsetPhaseChangeRegion(
                    orderedFrameBoxes,
                    cabinetExtensionSides.BCabinetBusbars,
                    cabinetExtensionSides.ACabinetBusbars,
                    routeAxisSetting,
                    requiresPhaseChange,
                    phaseChangeInsulationClearance,
                    generatedBusbarWidth,
                    generatedBusbarThickness,
                    smallOffsetRegion.BCabinetReferenceFrame,
                    singlePhaseStackCount,
                    bendAllowance,
                    requestedPhaseChangeIntervalIndex.HasValue
                        ? IsBridgeRouteGroup(
                            requestedPhaseChangeIntervalIndex.Value)
                        : false);
            }
            else
            {
                orderedFrameBoxes = ReadOrderedFrameBoxes(
                    partCatalog,
                    routeGeometryAxisSetting,
                    standaloneBaseAssignment.BCabinetBaseCenter);
                phaseChangeRegion = AnalyzeNoOffsetPhaseChangeRegion(
                    orderedFrameBoxes,
                    cabinetExtensionSides.BCabinetBusbars,
                    cabinetExtensionSides.ACabinetBusbars,
                    routeAxisSetting,
                    requiresPhaseChange,
                    phaseChangeInsulationClearance,
                    generatedBusbarWidth,
                    generatedBusbarThickness,
                    null,
                    singlePhaseStackCount,
                    bendAllowance,
                    requestedPhaseChangeIntervalIndex.HasValue
                        ? IsBridgeRouteGroup(
                            requestedPhaseChangeIntervalIndex.Value)
                        : true);
            }
            if (requestedPhaseChangeIntervalIndex.HasValue &&
                phaseChangeRegion == null)
            {
                throw new InvalidOperationException(
                    "请求的换相段未识别到可用换相区域，不能按该断点生成。 ");
            }
            var useDefaultLargeOffsetPhaseChangeSegment =
                isLargeOffset &&
                phaseChangeRegion != null &&
                !requestedPhaseChangeIntervalIndex.HasValue;
            var effectiveBreakpointPositions =
                normalizedBreakpointPositions.ToList();
            if (phaseChangeRegion != null &&
                effectiveBreakpointPositions.Count == 2 &&
                !useDefaultLargeOffsetPhaseChangeSegment)
            {
                // 常规无偏移界面只显示首、尾两个默认断点。需要自动换相时，
                // 规划器在内部补入中间基架两侧的虚拟边界，不反向修改界面断点数。
                effectiveBreakpointPositions.Add(
                    phaseChangeRegion.LeftFixedBreakpointPosition);
                effectiveBreakpointPositions.Add(
                    phaseChangeRegion.RightFixedBreakpointPosition);
                effectiveBreakpointPositions = effectiveBreakpointPositions
                    .OrderBy(position => position)
                    .Distinct()
                    .ToList();
            }
            var globalFrameLengthOrigin = endpointBreakpointOrigin ??
                orderedFrameBoxes[0].LengthMinimum;
            var modelBreakpointCoordinates = effectiveBreakpointPositions
                .Select(relativeBreakpointPosition =>
                    globalFrameLengthOrigin + relativeBreakpointPosition)
                .ToList()
                .AsReadOnly();
            var modelLargeOffsetBreakpointProtectionRanges =
                largeOffsetBreakpointProtectionRanges?
                    .Select(protectionRange =>
                        FaceToFaceLargeOffsetBreakpointProtectionRange.Create(
                            globalFrameLengthOrigin +
                            protectionRange.MinimumCornerFrameCoordinate,
                            globalFrameLengthOrigin +
                            protectionRange.MaximumCornerFrameCoordinate,
                            protectionRange.BusbarWidth))
                    .ToList()
                    .AsReadOnly();
            var modelLengthBreakpointCoordinates =
                isLargeOffset &&
                modelLargeOffsetBreakpointProtectionRanges != null
                    ? MapLargeOffsetPathCoordinatesToLengthAxis(
                        modelBreakpointCoordinates,
                        modelLargeOffsetBreakpointProtectionRanges)
                    : modelBreakpointCoordinates;
            var hasExplicitLargeOffsetTurnBreakpoints =
                isLargeOffset &&
                TryFindExplicitLargeOffsetTurnBreakpointIndexes(
                    modelBreakpointCoordinates,
                    modelLargeOffsetBreakpointProtectionRanges,
                    out _,
                    out _);
            if (phaseChangeRegion != null &&
                !useDefaultLargeOffsetPhaseChangeSegment)
            {
                FaceToWallPreviewTrace.Info(
                    "面对面换相断点输入",
                    "相对断点=" +
                    string.Join(",", effectiveBreakpointPositions
                        .Select(position => position.ToString("0.###"))) +
                    "；模型断点=" +
                    string.Join(",", modelLengthBreakpointCoordinates
                        .Select(position => position.ToString("0.###"))) +
                    "；请求换相区段序号=" +
                    (requestedPhaseChangeIntervalIndex?.ToString() ?? "自动"));
                var effectivePhaseChangeIntervalIndex =
                    requestedPhaseChangeIntervalIndex ?? 0;
                CompletePhaseChangeRouteGeometry(
                    phaseChangeRegion,
                    modelLengthBreakpointCoordinates,
                    effectivePhaseChangeIntervalIndex);
            }

            var useBaseOnlyEndpointInsulators =
                bCabinetEndpoint != null && aCabinetEndpoint != null;
            var availableConnectionInsulators =
                useBaseOnlyEndpointInsulators
                    ? FaceToWallModelBusbarPlanner.ReadConnectionInsulators(
                        partCatalog
                            .GetDescendants(
                                bCabinetBaseComponent,
                                SystemPartType_e.Insulator)
                            .Concat(partCatalog.GetDescendants(
                                aCabinetBaseComponent,
                                SystemPartType_e.Insulator)),
                        swApp,
                        "面对面端部排-仅基座绝缘子候选")
                    : FaceToWallModelBusbarPlanner
                        .ReadAvailableConnectionInsulators(
                            partCatalog,
                            swApp);
            var bCabinetConnectionInsulators =
                bCabinetContextOverride != null
                    ? Array.Empty<InsulatorConnectionInfo>()
                    : SelectConnectionInsulatorsForCabinetSide(
                    availableConnectionInsulators,
                    cabinetSeparationDirection,
                    cabinetExtensionSides.SeparationLengthCoordinate,
                    true,
                    "B柜",
                    bCabinetEndpoint == null
                        ? PhaseCount * 2
                        : PhaseCount);
            var aCabinetConnectionInsulators =
                aCabinetContextOverride != null
                    ? Array.Empty<InsulatorConnectionInfo>()
                    : SelectConnectionInsulatorsForCabinetSide(
                    availableConnectionInsulators,
                    cabinetSeparationDirection,
                    cabinetExtensionSides.SeparationLengthCoordinate,
                    false,
                    "A柜",
                    aCabinetEndpoint == null
                        ? PhaseCount * 2
                        : PhaseCount);
            var logicalRouteGroupCount =
                effectiveBreakpointPositions.Count - 1 +
                (isLargeOffset && !hasExplicitLargeOffsetTurnBreakpoints
                    ? 2
                    : 0);
            // The special-window interval is completed after both side
            // contexts are available; initialize the combined decision here
            // and finalize it below.
            bool combinedSmallOffsetPhaseChange = false;
            var generatedRouteGroupCount = logicalRouteGroupCount;
            if (generatedRouteGroupCount <= 0)
            {
                throw new InvalidOperationException(
                    "面对面可生成的铜排区间数量不足。");
            }
            var lastLogicalRouteGroupIndex = logicalRouteGroupCount - 1;
            var aCabinetTerminalLayerOffset =
                lastLogicalRouteGroupIndex % 2 == 1 ? -1 : 0;

            var bCabinetContext = bCabinetContextOverride ??
                CreateSidePlanningContext(
                "B柜",
                partCatalog,
                routeAxisSetting,
                cabinetExtensionSides.BCabinetBusbars,
                bCabinetConnectionInsulators,
                modelLengthBreakpointCoordinates[0],
                singlePhaseStackCount,
                bendingRadius,
                bendAllowance,
                0,
                bCabinetBaseComponent,
                smallOffsetRegion?.BCabinetReferenceFrame?.Component,
                generatedBusbarWidth: generatedBusbarWidth,
                generatedBusbarThickness: generatedBusbarThickness,
                useActualInsulatorPositions: true,
                baseInsulatorHostComponent:
                    useWidthAxisForCabinetSeparation
                        ? standaloneBaseAssignment
                            .BCabinetElevatedBaseComponent
                        : null,
                endpointGeometry: bCabinetEndpoint);
            var reversedAxisSetting =
                CreateLengthReversedAxisSetting(routeAxisSetting);
            var aCabinetContext = aCabinetContextOverride ??
                CreateSidePlanningContext(
                "A柜",
                partCatalog,
                reversedAxisSetting,
                cabinetExtensionSides.ACabinetBusbars,
                aCabinetConnectionInsulators,
                -modelLengthBreakpointCoordinates[
                    modelLengthBreakpointCoordinates.Count - 1],
                singlePhaseStackCount,
                bendingRadius,
                bendAllowance,
                aCabinetTerminalLayerOffset,
                aCabinetBaseComponent,
                smallOffsetRegion?.ACabinetReferenceFrame?.Component,
                generatedBusbarWidth: generatedBusbarWidth,
                generatedBusbarThickness: generatedBusbarThickness,
                useActualInsulatorPositions: true,
                baseInsulatorHostComponent:
                    useWidthAxisForCabinetSeparation
                        ? standaloneBaseAssignment
                            .ACabinetElevatedBaseComponent
                        : null,
                endpointGeometry: aCabinetEndpoint);

            EnsureDistinctCabinetHosts(bCabinetContext, aCabinetContext);
            ValidateSideRouteCompatibility(
                bCabinetContext,
                aCabinetContext,
                routeGeometryAxisSetting,
                isLargeOffset || isSmallOffset ||
                phaseChangeRegion != null ||
                useWidthAxisForCabinetSeparation,
                phaseChangeRegion != null);

            var commonBusbarWidth = bCabinetContext.PhaseRouteContexts[0].BusbarWidth;
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion = null;
            if (isLargeOffset)
            {
                var largeOffsetAnalysisFrames = middlePathPlan != null
                    ? CreateLargeOffsetAnalysisFramesFromPath(
                        middlePathPlan,
                        GetPhaseWidthCoordinates(
                            bCabinetContext,
                            routeGeometryAxisSetting.WidthVector),
                        GetPhaseWidthCoordinates(
                            aCabinetContext,
                            routeGeometryAxisSetting.WidthVector),
                        commonBusbarWidth)
                    : orderedFrameBoxes;
                largeOffsetRegion = AnalyzeLargeOffsetRegion(
                    largeOffsetAnalysisFrames,
                    GetPhaseWidthCoordinates(
                        bCabinetContext,
                        routeGeometryAxisSetting.WidthVector),
                    GetPhaseWidthCoordinates(
                        aCabinetContext,
                        routeGeometryAxisSetting.WidthVector),
                    commonBusbarWidth,
                    phaseChangeRegion != null &&
                    !requestedPhaseChangeIntervalIndex.HasValue);
                CompleteLargeOffsetRouteGeometry(
                    largeOffsetRegion,
                    modelBreakpointCoordinates,
                    commonBusbarWidth,
                    modelLargeOffsetBreakpointProtectionRanges,
                    modelLengthBreakpointCoordinates);
                if (useDefaultLargeOffsetPhaseChangeSegment)
                {
                    CompleteLargeOffsetPhaseChangeRouteGeometry(
                        phaseChangeRegion,
                        largeOffsetRegion,
                        commonBusbarWidth);
                }
                if (phaseChangeRegion != null &&
                    modelLargeOffsetBreakpointProtectionRanges != null)
                {
                    var phaseChangeIntervalIndex =
                        phaseChangeRegion.SuppressedBreakpointIntervalIndex;
                    var phaseChangeIntervalStartCoordinate =
                        modelBreakpointCoordinates[phaseChangeIntervalIndex];
                    var phaseChangeIntervalEndCoordinate =
                        modelBreakpointCoordinates[phaseChangeIntervalIndex + 1];
                    if (!useDefaultLargeOffsetPhaseChangeSegment &&
                        modelLargeOffsetBreakpointProtectionRanges.Any(
                        protectionRange =>
                            phaseChangeIntervalStartCoordinate <
                                protectionRange.MaximumProtectedCoordinate -
                                GeometryTolerance &&
                            phaseChangeIntervalEndCoordinate >
                                protectionRange.MinimumProtectedCoordinate +
                                GeometryTolerance))
                    {
                        throw new InvalidOperationException(
                            "大偏移转角保护禁区不能作为换相段，请选择 B 柜侧首个可用段。 ");
                    }
                }
            }
            else if (isSmallOffset)
            {
                CompleteSmallOffsetRouteGeometry(
                    smallOffsetRegion,
                    modelBreakpointCoordinates,
                    commonBusbarWidth);
                if (phaseChangeRegion != null &&
                    phaseChangeRegion.SuppressedBreakpointIntervalIndex ==
                    smallOffsetRegion.SuppressedBreakpointIntervalIndex)
                {
                    throw new InvalidOperationException(
                        "小偏移特殊基架禁区段不能作为换相段，请选择 B 柜侧首个可用段。 ");
                }
            }
            else
            {
                // 侧对侧的首、末基架本来就沿柜间宽度方向分开，不能套用
                // 面对面“所有基架宽度中心同轴”的直线链校验。
                if (!useWidthAxisForCabinetSeparation)
                {
                    EnsureStraightFrameChain(orderedFrameBoxes);
                }
            }

            // CompleteSmallOffsetRouteGeometry has now resolved the exact
            // scanned interval. Re-evaluate the combination only after that
            // step so a custom phase interval cannot be mistaken for the
            // small-offset window.
            combinedSmallOffsetPhaseChange =
                IsCombinedSmallOffsetPhaseChange(
                    smallOffsetRegion,
                    phaseChangeRegion);
            var suppressedSpecialGroupCount =
                combinedSmallOffsetPhaseChange
                    ? 1
                    : (isSmallOffset ? 1 : 0) +
                      (phaseChangeRegion != null ? 1 : 0);
            generatedRouteGroupCount = logicalRouteGroupCount -
                                       suppressedSpecialGroupCount;
            if (generatedRouteGroupCount <= 0)
            {
                throw new InvalidOperationException(
                    "可生成的铜排区域数量不足。 ");
            }

            var routeDefinitions = BuildRouteDefinitions(
                modelLengthBreakpointCoordinates,
                bCabinetContext,
                aCabinetContext,
                routeGeometryAxisSetting,
                largeOffsetRegion,
                smallOffsetRegion,
                phaseChangeRegion);
            IReadOnlyList<double> phaseChangeStartWidthCoordinates = null;
            IReadOnlyList<double> phaseChangeEndWidthCoordinates =
                useDefaultLargeOffsetPhaseChangeSegment
                    ? largeOffsetRegion.PhaseRoutes
                        .Select(phaseRoute =>
                            phaseRoute.BSideWidthCoordinate)
                        .ToList()
                    : null;
            IReadOnlyList<double> phaseChangeEndLengthCoordinates =
                useDefaultLargeOffsetPhaseChangeSegment
                    ? largeOffsetRegion.PhaseRoutes
                        .Select(phaseRoute =>
                            phaseRoute.BSideLengthEndCoordinate)
                        .ToList()
                    : null;
            FaceToFaceSidePlanningContext
                smallOffsetPhaseChangeGeometricSideContext = null;
            if (smallOffsetRegion != null && phaseChangeRegion != null)
            {
                var phaseChangeOccursBeforeSmallOffset =
                    phaseChangeRegion.SuppressedBreakpointIntervalIndex <
                    smallOffsetRegion.SuppressedBreakpointIntervalIndex;
                if (phaseChangeOccursBeforeSmallOffset)
                {
                    // 换相在小偏移前完成：换相段仍位于 B 柜几何侧，
                    // 其出口按 B 柜物理相位反序落点。
                    phaseChangeEndWidthCoordinates =
                        GetPhaseWidthCoordinates(
                                bCabinetContext,
                                routeGeometryAxisSetting.WidthVector)
                            .Reverse()
                            .ToList();
                    smallOffsetPhaseChangeGeometricSideContext =
                        bCabinetContext;
                }
                else
                {
                    // 换相在小偏移后完成：进入换相段时已经位于 A 柜侧，
                    // 入口按 A 柜物理相位反序，出口恢复逻辑相位落点。
                    phaseChangeStartWidthCoordinates =
                        GetPhaseWidthCoordinates(
                                aCabinetContext,
                                routeGeometryAxisSetting.WidthVector)
                            .Reverse()
                            .ToList();
                    smallOffsetPhaseChangeGeometricSideContext =
                        aCabinetContext;
                }

                FaceToWallPreviewTrace.Info(
                    "面对面小偏移换相-段序映射",
                    "换相逻辑段=" +
                    (phaseChangeRegion.SuppressedBreakpointIntervalIndex + 1) +
                    "，小偏移逻辑段=" +
                    (smallOffsetRegion.SuppressedBreakpointIntervalIndex + 1) +
                    "，换相位置=" +
                    (phaseChangeOccursBeforeSmallOffset
                        ? "小偏移之前"
                        : "小偏移之后"));
            }
            if (useWidthAxisForCabinetSeparation &&
                (isLargeOffset || isSmallOffset))
            {
                ApplySideToSidePhaseEdgeExtensions(
                    routeDefinitions,
                    bCabinetContext.PhaseRouteContexts,
                    aCabinetContext.PhaseRouteContexts,
                    routeGeometryAxisSetting);
            }
            if (routeDefinitions.Count != generatedRouteGroupCount)
            {
                throw new InvalidOperationException(
                    "面对面固定普通段物理组数量与规划结果不一致。");
            }
            var lastRouteDefinition =
                routeDefinitions[routeDefinitions.Count - 1];
            if (lastRouteDefinition.RouteGroupIndex !=
                lastLogicalRouteGroupIndex)
            {
                throw new InvalidOperationException(
                    "面对面固定普通段末端逻辑序号与规划结果不一致。");
            }

            var useHalfBridgeAtACabinetTerminal =
                isLargeOffset &&
                normalizedBreakpointPositions.Count % 2 == 1 &&
                lastRouteDefinition.IsBridge;
            if (useHalfBridgeAtACabinetTerminal)
            {
                ConfigureHalfBridgeAtACabinetTerminal(
                    lastRouteDefinition,
                    aCabinetContext,
                    axisSetting.HeightVector);
            }
            else
            {
                ValidateACabinetTerminalLayer(
                    aCabinetContext,
                    bCabinetContext,
                    axisSetting.HeightVector,
                    lastRouteDefinition.IsBridge);
            }

            var result = new FaceToFaceModelBusbarPlan
            {
                CabinetTopExtensionBusbars = extensionBusbars,
                BCabinetTopExtensionBusbars =
                    cabinetExtensionSides.BCabinetBusbars.AsReadOnly(),
                ACabinetTopExtensionBusbars =
                    cabinetExtensionSides.ACabinetBusbars.AsReadOnly(),
                OrderedFrameBoxes = orderedFrameBoxes,
                LargeOffsetRegion = largeOffsetRegion,
                SmallOffsetRegion = smallOffsetRegion,
                PhaseChangeRegion = phaseChangeRegion,
                MiddlePathPlan = middlePathPlan,
                UnifiedPlanning = unifiedPlanningResult
            };
            result.GeneratedBusbarGroups.Add(CreateCabinetConnectionGroup(
                "B柜基座排",
                FaceToFaceBusbarGroupKind_e.BCabinetConnection,
                bCabinetContext));
            if (isLargeOffset || isSmallOffset)
            {
                // 偏移方案的 A/B 两侧必须先各自按柜顶伸出排完成定位，
                // 中间普通段和特殊过渡排统一放到最后生成。
                result.GeneratedBusbarGroups.Add(
                    CreateCabinetConnectionGroup(
                        "A柜基座排",
                        FaceToFaceBusbarGroupKind_e.ACabinetConnection,
                        aCabinetContext));
            }
            foreach (var routeDefinition in routeDefinitions)
            {
                if (phaseChangeRegion != null &&
                    routeDefinition.RouteGroupIndex >
                        phaseChangeRegion
                            .SuppressedBreakpointIntervalIndex &&
                    result.GeneratedBusbarGroups.All(
                        generatedBusbarGroup =>
                            generatedBusbarGroup.Kind !=
                            FaceToFaceBusbarGroupKind_e.PhaseChange))
                {
                    result.GeneratedBusbarGroups.Add(
                        CreatePhaseChangeBusbarGroup(
                            phaseChangeRegion,
                            bCabinetContext,
                            aCabinetContext,
                            routeGeometryAxisSetting,
                            allowInsufficientPhaseChangeClearancePreview,
                            combinedSmallOffsetPhaseChange
                                ? smallOffsetRegion
                                : null,
                            useMilledBarForLargeSmallOffset,
                            phaseChangeEndWidthCoordinates,
                            phaseChangeEndLengthCoordinates,
                            phaseChangeStartWidthCoordinates,
                            smallOffsetPhaseChangeGeometricSideContext));
                }

                result.GeneratedBusbarGroups.Add(CreateGeneratedRouteGroup(
                    routeDefinition,
                    bCabinetContext,
                    routeGeometryAxisSetting));
            }

            if (phaseChangeRegion != null &&
                result.GeneratedBusbarGroups.All(
                    generatedBusbarGroup => generatedBusbarGroup.Kind !=
                        FaceToFaceBusbarGroupKind_e.PhaseChange))
            {
                result.GeneratedBusbarGroups.Add(
                    CreatePhaseChangeBusbarGroup(
                        phaseChangeRegion,
                        bCabinetContext,
                        aCabinetContext,
                        routeGeometryAxisSetting,
                        allowInsufficientPhaseChangeClearancePreview,
                        combinedSmallOffsetPhaseChange
                            ? smallOffsetRegion
                            : null,
                        useMilledBarForLargeSmallOffset,
                        phaseChangeEndWidthCoordinates,
                        phaseChangeEndLengthCoordinates,
                        phaseChangeStartWidthCoordinates,
                        smallOffsetPhaseChangeGeometricSideContext));
            }

            if (smallOffsetRegion != null &&
                !combinedSmallOffsetPhaseChange)
            {
                result.SmallOffsetTransitionPreviewBusbars.AddRange(
                    CreateSmallOffsetTransitionPreviewBusbars(
                        smallOffsetRegion,
                        bCabinetContext,
                        aCabinetContext,
                        routeGeometryAxisSetting,
                        phaseChangeInsulationClearance,
                        useMilledBarForLargeSmallOffset,
                        phaseChangeRegion));
            }

            if (!isLargeOffset && !isSmallOffset)
            {
                result.GeneratedBusbarGroups.Add(
                    CreateCabinetConnectionGroup(
                        "A柜基座排",
                        FaceToFaceBusbarGroupKind_e.ACabinetConnection,
                        aCabinetContext));
            }
            result.GeneratedBusbars.AddRange(
                result.GeneratedBusbarGroups.SelectMany(
                    generatedBusbarGroup => generatedBusbarGroup.Busbars));
            result.GeneratedBusbars.AddRange(
                result.SmallOffsetTransitionPreviewBusbars);

            FaceToWallPreviewTrace.Info(
                "面对面固定铜排规划-完成",
                "大偏移=" + isLargeOffset +
                "，小偏移=" + isSmallOffset +
                "，自动换相=" + (phaseChangeRegion != null) +
                "，中间排规格=" +
                bCabinetContext.PhaseRouteContexts[0].BusbarWidth
                    .ToString("0.###") +
                "x" +
                bCabinetContext.PhaseRouteContexts[0].BusbarThickness
                    .ToString("0.###") +
                "，断点=" + string.Join(
                    "/",
                    modelBreakpointCoordinates.Select(
                        breakpointCoordinate =>
                            breakpointCoordinate.ToString("0.###"))) +
                "，物理组=" + result.GeneratedBusbarGroups.Count +
                "，逻辑普通段=" + logicalRouteGroupCount +
                "，生成普通段=" + generatedRouteGroupCount +
                "，小偏移过渡预览排=" +
                result.SmallOffsetTransitionPreviewBusbars.Count +
                (smallOffsetRegion == null
                    ? string.Empty
                    : "（" + smallOffsetRegion.TransitionKind +
                      "，宽向偏移=" +
                      smallOffsetRegion.SignedBusbarWidthOffset.ToString(
                          "0.###") + " mm）") +
                "，铜排路径=" + result.GeneratedBusbars.Count +
                "，A端终层=" +
                (lastLogicalRouteGroupIndex % 2 == 1
                    ? "末段桥式对应下层"
                    : "末段I排对应上层"));
            return result;
        }

        internal static IReadOnlyList<FaceToFaceFrameBox> ReadOrderedFrameBoxes(
            AssemblyPartCatalog partCatalog,
            BusbarBridgeAxisSetting axisSetting,
            Point3d ordinaryBaseCenter = null)
        {
            var orderedFrameComponents =
                partCatalog.GetGeneratedFrameComponentsInOrder();
            if (orderedFrameComponents.Count == 0)
            {
                throw new InvalidOperationException("面对面没有可用的有序基架。");
            }

            if (ordinaryBaseCenter != null)
            {
                var nearestFrameComponent = orderedFrameComponents
                    .Select(frameComponent => new
                    {
                        FrameComponent = frameComponent,
                        FrameCenter = BusbarGeometryUtility.GetComponentCenter(
                            frameComponent)
                    })
                    .Where(candidate => candidate.FrameCenter != null)
                    .OrderBy(candidate => candidate.FrameCenter.DistanceTo(
                        ordinaryBaseCenter))
                    .Select(candidate => candidate.FrameComponent)
                    .FirstOrDefault();
                if (nearestFrameComponent == null)
                {
                    throw new InvalidOperationException(
                        "面对面无法读取基架 Box 中心，无法确定主体基座最近的首个基架。 ");
                }

                var firstConfiguredFrameComponent = orderedFrameComponents[0];
                if (!string.Equals(
                        BusbarGeometryUtility.SafeName(nearestFrameComponent),
                        BusbarGeometryUtility.SafeName(
                            firstConfiguredFrameComponent),
                        StringComparison.OrdinalIgnoreCase))
                {
                    FaceToWallPreviewTrace.Warning(
                        "面对面基架-忽略非最近首基架顺序",
                        "主体基座最近基架=" + BusbarGeometryUtility.SafeName(
                            nearestFrameComponent) +
                        "，原模型对照首基架=" + BusbarGeometryUtility.SafeName(
                            firstConfiguredFrameComponent) +
                        "；按 Box 中心距离重新确定首基架。 ");
                    orderedFrameComponents = new[] { nearestFrameComponent }
                        .Concat(orderedFrameComponents.Where(frameComponent =>
                            !string.Equals(
                                BusbarGeometryUtility.SafeName(frameComponent),
                                BusbarGeometryUtility.SafeName(
                                    nearestFrameComponent),
                                StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }
            }

            var orderedFrameBoxes = new List<FaceToFaceFrameBox>();
            for (var frameIndex = 0;
                frameIndex < orderedFrameComponents.Count;
                frameIndex++)
            {
                var frameComponent = orderedFrameComponents[frameIndex];
                if (!BusbarGeometryUtility.TryGetComponentBoundingBox(
                        frameComponent,
                        out var frameMinimum,
                        out var frameMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.LengthVector,
                        out var lengthMinimum,
                        out var lengthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.WidthVector,
                        out var widthMinimum,
                        out var widthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.HeightVector,
                        out var heightMinimum,
                        out var heightMaximum))
                {
                    throw new InvalidOperationException(
                        "无法读取基架“" +
                        BusbarGeometryUtility.SafeName(frameComponent) +
                        "”的实际包围盒。");
                }

                orderedFrameBoxes.Add(new FaceToFaceFrameBox
                {
                    PhysicalIndex = frameIndex,
                    ComponentName = BusbarGeometryUtility.SafeName(frameComponent),
                    Component = frameComponent,
                    Minimum = frameMinimum,
                    Maximum = frameMaximum,
                    LengthMinimum = lengthMinimum,
                    LengthMaximum = lengthMaximum,
                    WidthMinimum = widthMinimum,
                    WidthMaximum = widthMaximum,
                    HeightMinimum = heightMinimum,
                    HeightMaximum = heightMaximum
                });
            }

            for (var orderedFrameIndex = 1;
                orderedFrameIndex < orderedFrameBoxes.Count;
                orderedFrameIndex++)
            {
                if (orderedFrameBoxes[orderedFrameIndex].LengthCenter <
                    orderedFrameBoxes[orderedFrameIndex - 1].LengthCenter -
                    FrameLengthOrderTolerance)
                {
                    throw new InvalidOperationException(
                        "总装配体“模型对照映射”中的基架物理顺序不是从 B 柜到 A 柜；无法按 B 柜为起点规划母排。");
                }
            }

            return orderedFrameBoxes.AsReadOnly();
        }

        /// <summary>
        /// 侧对侧使用独立的物理识别：业务轴由 B 柜柜顶伸出排确定，长度轴再定向为 B→A；
        /// 中间基架只从总装配体顶层 RD 类型读取，不依赖面对面的配合链或基架映射顺序。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceFrameBox>
            ResolveSideToSideOrderedPhysicalFrameBoxes(
            AssemblyPartCatalog partCatalog,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            BusbarBridgeAxisSetting sourceAxisSetting)
        {
            if (partCatalog == null)
            {
                throw new ArgumentNullException(nameof(partCatalog));
            }
            if (extensionBusbars == null)
            {
                throw new ArgumentNullException(nameof(extensionBusbars));
            }
            if (sourceAxisSetting == null)
            {
                throw new ArgumentNullException(nameof(sourceAxisSetting));
            }

            var bCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("B柜");
            var aCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("A柜");
            EnsureDistinctCabinetHostComponents(
                bCabinetHostComponent,
                aCabinetHostComponent);
            var standaloneBaseAssignment = MatchStandaloneBasesToCabinets(
                partCatalog,
                bCabinetHostComponent,
                aCabinetHostComponent,
                sourceAxisSetting);
            var cabinetExtensionSides = SplitLargeOffsetCabinetExtensionBusbars(
                partCatalog,
                extensionBusbars,
                bCabinetHostComponent,
                aCabinetHostComponent);
            var resolvedSideToSideAxisSetting = ResolveSideToSideAxisSetting(
                cabinetExtensionSides.BCabinetBusbars,
                standaloneBaseAssignment.BCabinetBaseCenter);
            var cabinetSpanDirection = ResolveSideToSideCabinetSpanDirection(
                resolvedSideToSideAxisSetting,
                standaloneBaseAssignment.BCabinetBaseCenter,
                standaloneBaseAssignment.ACabinetBaseCenter);
            return ReadSideToSideTopLevelFrameBoxes(
                partCatalog,
                resolvedSideToSideAxisSetting,
                cabinetSpanDirection,
                standaloneBaseAssignment.BCabinetBaseCenter,
                standaloneBaseAssignment.ACabinetBaseCenter);
        }

        private static IReadOnlyList<FaceToFaceFrameBox>
            ReadSideToSideTopLevelFrameBoxes(
            AssemblyPartCatalog partCatalog,
            BusbarBridgeAxisSetting sideToSideAxisSetting,
            Vector3d cabinetSpanDirection,
            Point3d bCabinetBaseCenter = null,
            Point3d aCabinetBaseCenter = null)
        {
            var frameTransverseDirection = cabinetSpanDirection
                .Cross(sideToSideAxisSetting.HeightVector)
                .Normalized;
            var topLevelFrameComponents = partCatalog
                .GetTopLevelComponentsByConfiguredRdType(SystemPartType_e.Frame)
                .ToList();
            if (topLevelFrameComponents.Count == 0)
            {
                throw new InvalidOperationException(
                    "侧对侧总装配体顶层没有识别到 RD类型=基架 的中间基架。");
            }

            var physicalFrameBoxes = new List<FaceToFaceFrameBox>();
            for (var physicalFrameIndex = 0;
                physicalFrameIndex < topLevelFrameComponents.Count;
                physicalFrameIndex++)
            {
                var physicalFrameComponent =
                    topLevelFrameComponents[physicalFrameIndex];
                if (!BusbarGeometryUtility.TryGetComponentBoundingBox(
                        physicalFrameComponent,
                        out var frameMinimum,
                        out var frameMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        physicalFrameComponent,
                        cabinetSpanDirection,
                        out var lengthMinimum,
                        out var lengthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        physicalFrameComponent,
                        frameTransverseDirection,
                        out var widthMinimum,
                        out var widthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        physicalFrameComponent,
                        sideToSideAxisSetting.HeightVector,
                        out var heightMinimum,
                        out var heightMaximum))
                {
                    throw new InvalidOperationException(
                        "无法读取侧对侧顶层基架“" +
                        BusbarGeometryUtility.SafeName(physicalFrameComponent) +
                        "”的实际 Box 投影范围。");
                }

                physicalFrameBoxes.Add(new FaceToFaceFrameBox
                {
                    PhysicalIndex = physicalFrameIndex,
                    ComponentName =
                        BusbarGeometryUtility.SafeName(physicalFrameComponent),
                    Component = physicalFrameComponent,
                    Minimum = frameMinimum,
                    Maximum = frameMaximum,
                    LengthMinimum = lengthMinimum,
                    LengthMaximum = lengthMaximum,
                    WidthMinimum = widthMinimum,
                    WidthMaximum = widthMaximum,
                    HeightMinimum = heightMinimum,
                    HeightMaximum = heightMaximum
                });
            }

            var orderedPhysicalFrameBoxes = physicalFrameBoxes
                .OrderBy(frameBox => frameBox.LengthCenter)
                .ThenBy(frameBox => frameBox.PhysicalIndex)
                .ToList();
            if (bCabinetBaseCenter != null || aCabinetBaseCenter != null)
            {
                if (bCabinetBaseCenter == null || aCabinetBaseCenter == null)
                {
                    throw new InvalidOperationException(
                        "侧对侧基架排序必须同时提供 A柜、B柜主体基座 Box 中心。 ");
                }

                var nearestBCabinetFrame = orderedPhysicalFrameBoxes
                    .OrderBy(frameBox => GetFrameBoxCenter(frameBox).DistanceTo(
                        bCabinetBaseCenter))
                    .First();
                var nearestACabinetFrame = orderedPhysicalFrameBoxes
                    .OrderBy(frameBox => GetFrameBoxCenter(frameBox).DistanceTo(
                        aCabinetBaseCenter))
                    .First();
                var firstOrderedFrame = orderedPhysicalFrameBoxes.First();
                var lastOrderedFrame = orderedPhysicalFrameBoxes.Last();
                if (!string.Equals(
                        nearestBCabinetFrame.ComponentName,
                        firstOrderedFrame.ComponentName,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        nearestACabinetFrame.ComponentName,
                        lastOrderedFrame.ComponentName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "侧对侧基架的投影顺序与主体基座 Box 中心最近规则冲突：" +
                        "B柜最近基架=" + nearestBCabinetFrame.ComponentName +
                        "，当前首基架=" + firstOrderedFrame.ComponentName +
                        "；A柜最近基架=" + nearestACabinetFrame.ComponentName +
                        "，当前末基架=" + lastOrderedFrame.ComponentName + "。 ");
                }
            }
            for (var orderedFrameIndex = 0;
                orderedFrameIndex < orderedPhysicalFrameBoxes.Count;
                orderedFrameIndex++)
            {
                orderedPhysicalFrameBoxes[orderedFrameIndex].PhysicalIndex =
                    orderedFrameIndex;
            }

            FaceToWallPreviewTrace.Info(
                "侧对侧基架-顶层RD类型按B到A排序",
                string.Join(
                    "；",
                    orderedPhysicalFrameBoxes.Select(frameBox =>
                        (frameBox.PhysicalIndex + 1) + "=" +
                        frameBox.ComponentName +
                        "，中心=" + frameBox.LengthCenter.ToString("0.###") +
                        "，长度=" +
                        (frameBox.LengthMaximum - frameBox.LengthMinimum)
                            .ToString("0.###") +
                        "，范围=[" +
                        frameBox.LengthMinimum.ToString("0.###") + "," +
                        frameBox.LengthMaximum.ToString("0.###") + "]")));
            return orderedPhysicalFrameBoxes.AsReadOnly();
        }

        private static Point3d GetFrameBoxCenter(FaceToFaceFrameBox frameBox)
        {
            if (frameBox?.Minimum == null || frameBox.Maximum == null)
            {
                throw new InvalidOperationException(
                    "基架 Box 范围不完整，无法计算 Box 中心。 ");
            }

            return new Point3d(
                (frameBox.Minimum.X + frameBox.Maximum.X) / 2d,
                (frameBox.Minimum.Y + frameBox.Maximum.Y) / 2d,
                (frameBox.Minimum.Z + frameBox.Maximum.Z) / 2d);
        }

        /// <summary>
        /// 将侧对侧读取的物理基架 Box 投影到通用规划器使用的业务轴。
        /// 物理读取阶段必须按真实 B→A 柜间方向排序；这里仅重算每个
        /// Box 在业务长、宽、高轴上的范围，不能再次按投影结果重排，
        /// 否则断点序号会与界面显示的物理基架顺序脱节。
        /// </summary>
        private static IReadOnlyList<FaceToFaceFrameBox>
            ReprojectSideToSideFrameBoxesToRouteAxis(
            IReadOnlyList<FaceToFaceFrameBox> orderedPhysicalFrameBoxes,
            BusbarBridgeAxisSetting routeAxisSetting,
            bool requireMonotonicLengthOrder = true)
        {
            if (orderedPhysicalFrameBoxes == null)
            {
                throw new ArgumentNullException(
                    nameof(orderedPhysicalFrameBoxes));
            }
            if (routeAxisSetting == null)
            {
                throw new ArgumentNullException(nameof(routeAxisSetting));
            }
            if (orderedPhysicalFrameBoxes.Count == 0)
            {
                throw new InvalidOperationException(
                    "侧对侧没有可投影到业务轴的基架 Box。 ");
            }

            var routeFrameBoxes = new List<FaceToFaceFrameBox>();
            foreach (var physicalFrameBox in orderedPhysicalFrameBoxes)
            {
                if (physicalFrameBox == null ||
                    physicalFrameBox.Component == null)
                {
                    throw new InvalidOperationException(
                        "侧对侧物理基架 Box 集合包含无效项，无法按业务轴重新投影。 ");
                }

                if (!BusbarGeometryUtility.TryGetComponentProjectionRange(
                        physicalFrameBox.Component,
                        routeAxisSetting.LengthVector,
                        out var lengthMinimum,
                        out var lengthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        physicalFrameBox.Component,
                        routeAxisSetting.WidthVector,
                        out var widthMinimum,
                        out var widthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        physicalFrameBox.Component,
                        routeAxisSetting.HeightVector,
                        out var heightMinimum,
                        out var heightMaximum))
                {
                    throw new InvalidOperationException(
                        "无法按侧对侧业务长宽高轴读取基架“" +
                        BusbarGeometryUtility.SafeName(
                            physicalFrameBox.Component) +
                        "”的 Box 投影范围。 ");
                }

                if (double.IsNaN(lengthMinimum) ||
                    double.IsInfinity(lengthMinimum) ||
                    double.IsNaN(lengthMaximum) ||
                    double.IsInfinity(lengthMaximum) ||
                    double.IsNaN(widthMinimum) ||
                    double.IsInfinity(widthMinimum) ||
                    double.IsNaN(widthMaximum) ||
                    double.IsInfinity(widthMaximum) ||
                    double.IsNaN(heightMinimum) ||
                    double.IsInfinity(heightMinimum) ||
                    double.IsNaN(heightMaximum) ||
                    double.IsInfinity(heightMaximum) ||
                    lengthMaximum < lengthMinimum ||
                    widthMaximum < widthMinimum ||
                    heightMaximum < heightMinimum)
                {
                    throw new InvalidOperationException(
                        "侧对侧基架“" +
                        BusbarGeometryUtility.SafeName(
                            physicalFrameBox.Component) +
                        "”的业务轴投影范围无效。 ");
                }

                routeFrameBoxes.Add(new FaceToFaceFrameBox
                {
                    // PhysicalIndex is assigned by the physical reader and
                    // is deliberately retained for diagnostics and region
                    // lookup; only the projection ranges change here.
                    PhysicalIndex = physicalFrameBox.PhysicalIndex,
                    ComponentName = physicalFrameBox.ComponentName,
                    Component = physicalFrameBox.Component,
                    Minimum = physicalFrameBox.Minimum,
                    Maximum = physicalFrameBox.Maximum,
                    LengthMinimum = lengthMinimum,
                    LengthMaximum = lengthMaximum,
                    WidthMinimum = widthMinimum,
                    WidthMaximum = widthMaximum,
                    HeightMinimum = heightMinimum,
                    HeightMaximum = heightMaximum
                });
            }

            // 普通/小偏移仍须保持物理 B→A 单调顺序。大偏移是 Z 型路径，
            // 同一宽度轴位置可能存在多个转向基架，必须先保留全部投影，再交给
            // OrderLargeOffsetFrameBoxesByGeometry 按二维路径排序。
            if (requireMonotonicLengthOrder)
            {
                for (var routeFrameIndex = 1;
                    routeFrameIndex < routeFrameBoxes.Count;
                    routeFrameIndex++)
                {
                    if (routeFrameBoxes[routeFrameIndex].LengthCenter <
                        routeFrameBoxes[routeFrameIndex - 1].LengthCenter -
                        FrameLengthOrderTolerance)
                    {
                        throw new InvalidOperationException(
                            "侧对侧基架物理 B→A 顺序与换轴后的路径长度投影不一致，无法安全对应断点。 ");
                    }
                }
            }

            FaceToWallPreviewTrace.Info(
                "侧对侧基架-业务轴投影",
                "基架数量=" + routeFrameBoxes.Count +
                "，长度中心=" + string.Join(
                    "/",
                    routeFrameBoxes.Select(frameBox =>
                        frameBox.LengthCenter.ToString("0.###"))));
            return routeFrameBoxes.AsReadOnly();
        }

        /// <summary>
        /// 侧对侧复用面对面偏移算法时交换水平业务轴：柜顶伸出排的排宽方向
        /// 作为算法长度轴，排厚方向作为算法宽度轴，高度轴保持不变。
        /// B→A 柜间连线只用于校正轴正负，不能直接作为轴向；大偏移时该连线
        /// 是长宽合成的对角线，直接使用会抹掉 Z 型路径的转向基架簇。
        /// </summary>
        internal static BusbarBridgeAxisSetting
            CreateSideToSideMiddleRouteAxisSetting(
            BusbarBridgeAxisSetting sideToSideBusinessAxisSetting,
            Vector3d cabinetSpanDirection)
        {
            if (sideToSideBusinessAxisSetting == null)
            {
                throw new ArgumentNullException(
                    nameof(sideToSideBusinessAxisSetting));
            }
            if (cabinetSpanDirection == null ||
                cabinetSpanDirection.Norm <= GeometryTolerance)
            {
                throw new ArgumentException(
                    "侧对侧中间排缺少有效的 B→A 柜间方向。",
                    nameof(cabinetSpanDirection));
            }

            var normalizedCabinetSpanDirection =
                cabinetSpanDirection.Normalized;
            var signedBusinessWidthSpan =
                sideToSideBusinessAxisSetting.WidthVector.Normalized.Dot(
                    normalizedCabinetSpanDirection);
            if (Math.Abs(signedBusinessWidthSpan) <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "侧对侧 B/A 柜中心连线在排宽方向上没有有效分量，无法按宽度轴规划偏移路径。");
            }
            var middleLengthDirection = BusbarGeometryUtility.Align(
                sideToSideBusinessAxisSetting.WidthVector.Normalized,
                normalizedCabinetSpanDirection);
            var rawMiddleWidthDirection = middleLengthDirection.Cross(
                sideToSideBusinessAxisSetting.HeightVector);
            if (rawMiddleWidthDirection.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "侧对侧柜间方向与高度方向平行，无法确定铜排偏移方向。");
            }

            var middleWidthDirection = rawMiddleWidthDirection.Normalized;
            var result = BusbarBridgeAxisSetting.CreateFromVectors(
                middleLengthDirection,
                sideToSideBusinessAxisSetting.HeightVector,
                middleWidthDirection);
            FaceToWallPreviewTrace.Info(
                "侧对侧偏移规划-轴映射",
                "面对面算法长度轴=侧对侧排宽方向（按B→A定向）=" +
                result.LengthVector +
                "；面对面算法宽度轴=侧对侧排厚方向=" +
                result.WidthVector +
                "；高度轴=" + result.HeightVector +
                "；仅用于定向的柜间B→A连线=" +
                normalizedCabinetSpanDirection);
            return result;
        }

        /// <summary>
        /// 七点首段已经完成基座和首基架绝缘子定位；中间偏移段必须从
        /// 这些首段的实际搭接端继续，不能再按面对面五点路径重复定位。
        /// </summary>
        private static FaceToFaceSidePlanningContext
            CreateSideToSideSecondTypeMiddlePlanningContext(
            string cabinetLabel,
            IReadOnlyList<PlanarBendBusbarSetting> cabinetFirstSegments,
            bool isBCabinetSide,
            Component2 ordinaryBaseComponent,
            Component2 firstFrameComponent,
            BusbarBridgeAxisSetting middleRouteAxisSetting)
        {
            if (cabinetFirstSegments == null)
            {
                throw new ArgumentNullException(nameof(cabinetFirstSegments));
            }
            if (ordinaryBaseComponent == null || firstFrameComponent == null)
            {
                throw new InvalidOperationException(
                    cabinetLabel + "缺少主体基座或首个基架，无法建立侧对侧中间排接续基准。");
            }

            var result = new FaceToFaceSidePlanningContext
            {
                SideLabel = cabinetLabel,
                BaseComponent = ordinaryBaseComponent,
                FirstFrameComponent = firstFrameComponent,
                UseActualInsulatorPositions = true,
                ExtensionBusbars = new List<ModeledBusbar>()
            };
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var phaseName = FaceToWallModelBusbarPlanner.GetPhaseName(
                    phaseIndex);
                var phaseKeyPrefix = phaseName + "-";
                var firstSegmentMembers = cabinetFirstSegments
                    .Where(setting => setting != null &&
                        !string.IsNullOrWhiteSpace(setting.GroupKey) &&
                        setting.GroupKey.StartsWith(
                            phaseKeyPrefix,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(setting => setting.GroupKey)
                    .ToList();
                if (firstSegmentMembers.Count == 0 ||
                    firstSegmentMembers.Any(setting =>
                        setting.PointList == null ||
                        setting.PointList.Count < 2))
                {
                    throw new InvalidOperationException(
                        cabinetLabel + phaseName +
                        "相七点首段与中间搭接排成员无法一一对应。");
                }

                var routeReferencePoints = firstSegmentMembers
                    .Select(setting =>
                    {
                        var terminalPoint = setting.PointList[
                            setting.PointList.Count - 1];
                        var terminalDirection = new Vector3d(
                            setting.PointList[setting.PointList.Count - 2],
                            terminalPoint).Normalized;
                        if (!BusbarGeometryUtility.AreParallel(
                                terminalDirection,
                                middleRouteAxisSetting.WidthVector))
                        {
                            throw new InvalidOperationException(
                                cabinetLabel + phaseName +
                                "相七点首段6→7未沿侧对侧偏移方向，无法接续中间排。");
                        }

                        var lapCenter = PlanarBendBusbarSetting.Translate(
                            terminalPoint,
                            terminalDirection,
                            -setting.BusBarWidth / 2d);
                        var routePoint = PlanarBendBusbarSetting.Translate(
                            lapCenter,
                            middleRouteAxisSetting.LengthVector,
                            (isBCabinetSide ? -1d : 1d) *
                            setting.BusBarWidth / 2d);
                        return PlanarBendBusbarSetting.Translate(
                            routePoint,
                            middleRouteAxisSetting.HeightVector,
                            -setting.BusBarThickness);
                    })
                    .ToList();
                var routeReferencePoint = BusbarGeometryUtility.Average(
                    routeReferencePoints);
                var routeThicknessDirection = AverageAlignedDirections(
                    new[]
                    {
                        middleRouteAxisSetting.LengthVector.Cross(
                            middleRouteAxisSetting.WidthVector)
                    },
                    middleRouteAxisSetting.HeightVector);
                var routeWidthDirection = AverageAlignedDirections(
                    firstSegmentMembers.Select(setting =>
                        new Vector3d(
                            setting.PointList[setting.PointList.Count - 2],
                            setting.PointList[setting.PointList.Count - 1])),
                    middleRouteAxisSetting.WidthVector);
                if (routeReferencePoint == null ||
                    routeThicknessDirection == null ||
                    routeWidthDirection == null)
                {
                    throw new InvalidOperationException(
                        cabinetLabel + phaseName +
                        "相中间搭接排缺少有效的端点或排厚/排宽方向。");
                }

                var centerThicknessCoordinate = BusbarGeometryUtility.Project(
                    routeReferencePoint,
                    routeThicknessDirection);
                var stackMemberOffsets = routeReferencePoints
                    .Select(point => BusbarGeometryUtility.Project(
                        point,
                        routeThicknessDirection) - centerThicknessCoordinate)
                    .OrderBy(offset => offset)
                    .ToList()
                    .AsReadOnly();
                var referenceSetting = firstSegmentMembers[0];
                result.PhaseRouteContexts.Add(
                    new FaceToWallPhaseRouteContext
                    {
                        PhaseIndex = phaseIndex,
                        OrdinaryRouteReferencePoint = routeReferencePoint,
                        RouteWidthDirection = routeWidthDirection,
                        RouteThicknessDirection = routeThicknessDirection,
                        FrameOutwardNormal = routeThicknessDirection.Copy(),
                        BusbarWidth = referenceSetting.BusBarWidth,
                        BusbarThickness = referenceSetting.BusBarThickness,
                        BendingRadius = referenceSetting.BarBendingRadius,
                        BendAllowance = referenceSetting.BendAllowance,
                        StackMemberOffsets = stackMemberOffsets
                    });
                result.CabinetConnectionBusbarsByPhase.Add(
                    firstSegmentMembers);
            }

            return result;
        }

        private static Vector3d AverageAlignedDirections(
            IEnumerable<Vector3d> sourceDirections,
            Vector3d preferredDirection)
        {
            var validDirections = (sourceDirections ??
                    Enumerable.Empty<Vector3d>())
                .Where(direction => direction != null &&
                    direction.Norm > GeometryTolerance)
                .Select(direction => direction.Normalized)
                .ToList();
            if (validDirections.Count == 0 || preferredDirection == null ||
                preferredDirection.Norm <= GeometryTolerance)
            {
                return null;
            }

            var normalizedPreferredDirection = preferredDirection.Normalized;
            var alignedDirections = validDirections
                .Select(direction => BusbarGeometryUtility.Align(
                    direction,
                    normalizedPreferredDirection))
                .ToList();
            var averageDirection = new Vector3d(
                alignedDirections.Average(direction => direction.X),
                alignedDirections.Average(direction => direction.Y),
                alignedDirections.Average(direction => direction.Z));
            return averageDirection.Norm <= GeometryTolerance
                ? null
                : averageDirection.Normalized;
        }

        private static IReadOnlyList<FaceToFaceFrameBox>
            ReadLargeOffsetOrderedFrameBoxes(
            AssemblyPartCatalog partCatalog,
            BusbarBridgeAxisSetting axisSetting,
            Point3d bCabinetBaseCenter,
            Point3d aCabinetBaseCenter)
        {
            var frameComponents =
                partCatalog.GetTopLevelComponentsByConfiguredRdType(
                    SystemPartType_e.Frame);
            if (frameComponents.Count == 0)
            {
                throw new InvalidOperationException("面对面没有识别到可用基架。");
            }

            var frameBoxes = new List<FaceToFaceFrameBox>();
            for (var frameIndex = 0;
                frameIndex < frameComponents.Count;
                frameIndex++)
            {
                var frameComponent = frameComponents[frameIndex];
                if (!BusbarGeometryUtility.TryGetComponentBoundingBox(
                        frameComponent,
                        out var frameMinimum,
                        out var frameMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.LengthVector,
                        out var lengthMinimum,
                        out var lengthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.WidthVector,
                        out var widthMinimum,
                        out var widthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.HeightVector,
                        out var heightMinimum,
                        out var heightMaximum))
                {
                    throw new InvalidOperationException(
                        "无法读取基架“" +
                        BusbarGeometryUtility.SafeName(frameComponent) +
                        "”的实际包围盒。");
                }

                frameBoxes.Add(new FaceToFaceFrameBox
                {
                    PhysicalIndex = frameIndex,
                    ComponentName = BusbarGeometryUtility.SafeName(frameComponent),
                    Component = frameComponent,
                    Minimum = frameMinimum,
                    Maximum = frameMaximum,
                    LengthMinimum = lengthMinimum,
                    LengthMaximum = lengthMaximum,
                    WidthMinimum = widthMinimum,
                    WidthMaximum = widthMaximum,
                    HeightMinimum = heightMinimum,
                    HeightMaximum = heightMaximum
                });
            }

            FaceToWallPreviewTrace.Info(
                "面对面基架-实际Box中点",
                string.Join(
                    "；",
                    frameBoxes.Select(frameBox =>
                        frameBox.ComponentName +
                        "，L=" + frameBox.LengthCenter.ToString("0.###") +
                        "[" + frameBox.LengthMinimum.ToString("0.###") +
                        "," + frameBox.LengthMaximum.ToString("0.###") +
                        "]，W=" + frameBox.WidthCenter.ToString("0.###") +
                        "[" + frameBox.WidthMinimum.ToString("0.###") +
                        "," + frameBox.WidthMaximum.ToString("0.###") + "]")));

            var orderedFrameBoxes = OrderLargeOffsetFrameBoxesByGeometry(
                frameBoxes,
                BusbarGeometryUtility.Project(
                    bCabinetBaseCenter,
                    axisSetting.LengthVector),
                BusbarGeometryUtility.Project(
                    aCabinetBaseCenter,
                    axisSetting.LengthVector));
            FaceToWallPreviewTrace.Info(
                "面对面基架-Box中点几何顺序",
                string.Join(
                    "；",
                    orderedFrameBoxes.Select(frameBox =>
                        (frameBox.PhysicalIndex + 1) + "=" +
                        frameBox.ComponentName +
                        "@L" + frameBox.LengthCenter.ToString("0.###") +
                        "/W" + frameBox.WidthCenter.ToString("0.###"))));

            return orderedFrameBoxes;
        }

        /// <summary>
        /// 小偏移仍沿长度轴从 B 到 A 排列，因此读取总装配体中的全部基架 Box 后，
        /// 只按实际长度中心排序；宽度中心留给后续 B 侧/特殊/A 侧归属判断。
        /// </summary>
        private static IReadOnlyList<FaceToFaceFrameBox>
            ReadSmallOffsetOrderedFrameBoxes(
            AssemblyPartCatalog partCatalog,
            BusbarBridgeAxisSetting axisSetting)
        {
            var frameComponents =
                partCatalog.GetTopLevelComponentsByConfiguredRdType(
                    SystemPartType_e.Frame);
            if (frameComponents.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对面小偏移没有识别到可用基架。");
            }

            var frameBoxes = new List<FaceToFaceFrameBox>();
            for (var physicalFrameIndex = 0;
                physicalFrameIndex < frameComponents.Count;
                physicalFrameIndex++)
            {
                var frameComponent = frameComponents[physicalFrameIndex];
                if (!BusbarGeometryUtility.TryGetComponentBoundingBox(
                        frameComponent,
                        out var frameMinimum,
                        out var frameMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.LengthVector,
                        out var lengthMinimum,
                        out var lengthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.WidthVector,
                        out var widthMinimum,
                        out var widthMaximum) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        frameComponent,
                        axisSetting.HeightVector,
                        out var heightMinimum,
                        out var heightMaximum))
                {
                    throw new InvalidOperationException(
                        "无法读取小偏移基架“" +
                        BusbarGeometryUtility.SafeName(frameComponent) +
                        "”的实际包围盒。");
                }

                frameBoxes.Add(new FaceToFaceFrameBox
                {
                    PhysicalIndex = physicalFrameIndex,
                    ComponentName =
                        BusbarGeometryUtility.SafeName(frameComponent),
                    Component = frameComponent,
                    Minimum = frameMinimum,
                    Maximum = frameMaximum,
                    LengthMinimum = lengthMinimum,
                    LengthMaximum = lengthMaximum,
                    WidthMinimum = widthMinimum,
                    WidthMaximum = widthMaximum,
                    HeightMinimum = heightMinimum,
                    HeightMaximum = heightMaximum
                });
            }

            var orderedFrameBoxes = frameBoxes
                .OrderBy(frameBox => frameBox.LengthCenter)
                .ThenBy(frameBox => frameBox.PhysicalIndex)
                .ToList()
                .AsReadOnly();
            FaceToWallPreviewTrace.Info(
                "面对面小偏移基架-实际Box中点顺序",
                string.Join(
                    "；",
                    orderedFrameBoxes.Select(frameBox =>
                        frameBox.ComponentName +
                        "@L" + frameBox.LengthCenter.ToString("0.###") +
                        "/W" + frameBox.WidthCenter.ToString("0.###"))));
            return orderedFrameBoxes;
        }

        /// <summary>
        /// 长度轴只用于 B→A 排序；宽度轴按 2 mm 容差将基架归入 B/A 柜轨迹。
        /// 同时不属于两条轨迹的唯一基架就是小偏移特殊基架。
        /// </summary>
        internal static FaceToFaceSmallOffsetRegionPlan AnalyzeSmallOffsetRegion(
            IReadOnlyList<FaceToFaceFrameBox> frameBoxes,
            double bCabinetBaseLengthCoordinate,
            double bCabinetBaseWidthCoordinate,
            double aCabinetBaseLengthCoordinate,
            double aCabinetBaseWidthCoordinate)
        {
            if (frameBoxes == null || frameBoxes.Count < 3)
            {
                throw new InvalidOperationException(
                    "面对面小偏移至少需要 B 侧基架、特殊基架和 A 侧基架。");
            }

            var cabinetCoordinates = new[]
            {
                bCabinetBaseLengthCoordinate,
                bCabinetBaseWidthCoordinate,
                aCabinetBaseLengthCoordinate,
                aCabinetBaseWidthCoordinate
            };
            if (cabinetCoordinates.Any(coordinate =>
                    double.IsNaN(coordinate) || double.IsInfinity(coordinate)))
            {
                throw new InvalidOperationException(
                    "面对面小偏移 B/A 柜基座中心坐标无效。");
            }

            if (aCabinetBaseLengthCoordinate <=
                bCabinetBaseLengthCoordinate + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "面对面小偏移长度轴必须从 B 柜指向 A 柜。");
            }

            if (Math.Abs(aCabinetBaseWidthCoordinate -
                bCabinetBaseWidthCoordinate) <=
                SmallOffsetWidthAlignmentTolerance * 2d)
            {
                throw new InvalidOperationException(
                    "面对面小偏移 B/A 柜基座宽度中心过近，无法按 2 mm 容差唯一归属基架。");
            }

            var orderedFrameBoxes = frameBoxes
                .OrderBy(frameBox => frameBox?.LengthCenter ?? double.MaxValue)
                .ThenBy(frameBox => frameBox?.PhysicalIndex ?? int.MaxValue)
                .ToList();
            if (orderedFrameBoxes.Any(frameBox => frameBox == null))
            {
                throw new InvalidOperationException(
                    "面对面小偏移基架 Box 集合不能包含空项。");
            }

            var bAlignedFrames = new List<FaceToFaceFrameBox>();
            var aAlignedFrames = new List<FaceToFaceFrameBox>();
            var specialFrameCandidates = new List<FaceToFaceFrameBox>();
            foreach (var orderedFrameBox in orderedFrameBoxes)
            {
                var alignsWithBCabinet = Math.Abs(
                    orderedFrameBox.WidthCenter -
                    bCabinetBaseWidthCoordinate) <=
                    SmallOffsetWidthAlignmentTolerance;
                var alignsWithACabinet = Math.Abs(
                    orderedFrameBox.WidthCenter -
                    aCabinetBaseWidthCoordinate) <=
                    SmallOffsetWidthAlignmentTolerance;
                if (alignsWithBCabinet && alignsWithACabinet)
                {
                    throw new InvalidOperationException(
                        "小偏移基架“" + orderedFrameBox.ComponentName +
                        "”同时落入 B/A 柜 2 mm 容差，无法唯一归属。");
                }

                if (alignsWithBCabinet)
                {
                    bAlignedFrames.Add(orderedFrameBox);
                }
                else if (alignsWithACabinet)
                {
                    aAlignedFrames.Add(orderedFrameBox);
                }
                else
                {
                    specialFrameCandidates.Add(orderedFrameBox);
                }
            }

            if (bAlignedFrames.Count == 0 ||
                aAlignedFrames.Count == 0 ||
                specialFrameCandidates.Count != 1)
            {
                throw new InvalidOperationException(
                    "面对面小偏移必须识别到至少一个 B 侧基架、至少一个 A 侧基架以及唯一一个特殊基架；" +
                    "当前 B/A/特殊数量=" + bAlignedFrames.Count + "/" +
                    aAlignedFrames.Count + "/" + specialFrameCandidates.Count + "。");
            }

            var specialFrame = specialFrameCandidates[0];
            if (bAlignedFrames.Any(frameBox =>
                    frameBox.LengthCenter >=
                    specialFrame.LengthCenter - GeometryTolerance) ||
                aAlignedFrames.Any(frameBox =>
                    frameBox.LengthCenter <=
                    specialFrame.LengthCenter + GeometryTolerance))
            {
                throw new InvalidOperationException(
                    "面对面小偏移基架顺序必须是 B 侧轨迹、唯一特殊基架、A 侧轨迹。");
            }

            var bReferenceFrame = bAlignedFrames
                .OrderBy(frameBox => CalculatePlanarDistanceSquared(
                    frameBox.LengthCenter,
                    frameBox.WidthCenter,
                    bCabinetBaseLengthCoordinate,
                    bCabinetBaseWidthCoordinate))
                .First();
            var aReferenceFrame = aAlignedFrames
                .OrderBy(frameBox => CalculatePlanarDistanceSquared(
                    frameBox.LengthCenter,
                    frameBox.WidthCenter,
                    aCabinetBaseLengthCoordinate,
                    aCabinetBaseWidthCoordinate))
                .First();
            return new FaceToFaceSmallOffsetRegionPlan
            {
                BCabinetReferenceFrame = bReferenceFrame,
                SpecialFrame = specialFrame,
                ACabinetReferenceFrame = aReferenceFrame
            };
        }

        /// <summary>
        /// 供界面初始化使用：严格按顶层 A/B 柜、两个独立基座和全局基架
        /// 解析无偏移换相，并返回相对首基架起点的两个默认断点。
        /// 正常 A-A、C-C 对齐时返回 null。
        /// </summary>
        public static FaceToFacePhaseChangeRegionPlan
            CreateNoOffsetPhaseChangeLayout(
            AssemblyPartCatalog partCatalog,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            double insulationClearance = 125d,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            int singlePhaseStackCount = 1,
            double bendAllowance = 20d,
            bool preferBCabinetFirstSegment = false)
        {
            if (partCatalog == null)
            {
                throw new ArgumentNullException(nameof(partCatalog));
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            if (extensionBusbars == null)
            {
                throw new ArgumentNullException(nameof(extensionBusbars));
            }
            ValidateNonNegativeFinite(
                insulationClearance,
                nameof(insulationClearance));

            var bCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("B柜");
            var aCabinetHostComponent =
                partCatalog.FindUniqueTopLevelComponentContaining("A柜");
            EnsureDistinctCabinetHostComponents(
                bCabinetHostComponent,
                aCabinetHostComponent);
            var bCabinetPhaseSequence = ReadRequiredCabinetPhaseSequence(
                partCatalog,
                bCabinetHostComponent,
                "B柜");
            var aCabinetPhaseSequence = ReadRequiredCabinetPhaseSequence(
                partCatalog,
                aCabinetHostComponent,
                "A柜");
            var requiresPhaseChange = RequiresPhaseChangeByCabinetPlacement(
                bCabinetPhaseSequence,
                aCabinetPhaseSequence,
                false);
            var standaloneBaseAssignment = MatchStandaloneBasesToCabinets(
                partCatalog,
                bCabinetHostComponent,
                aCabinetHostComponent,
                axisSetting);
            var routeAxisSetting = OrientLengthAxisFromBCabinetToACabinet(
                axisSetting,
                standaloneBaseAssignment.BCabinetBaseCenter,
                standaloneBaseAssignment.ACabinetBaseCenter);
            var cabinetExtensionSides =
                SplitLargeOffsetCabinetExtensionBusbars(
                    partCatalog,
                    extensionBusbars,
                    bCabinetHostComponent,
                    aCabinetHostComponent);
            var orderedFrameBoxes = ReadOrderedFrameBoxes(
                partCatalog,
                routeAxisSetting,
                standaloneBaseAssignment.BCabinetBaseCenter);
            return AnalyzeNoOffsetPhaseChangeRegion(
                orderedFrameBoxes,
                cabinetExtensionSides.BCabinetBusbars,
                cabinetExtensionSides.ACabinetBusbars,
                routeAxisSetting,
                requiresPhaseChange,
                insulationClearance,
                generatedBusbarWidth,
                generatedBusbarThickness,
                preferBCabinetFirstSegment
                    ? orderedFrameBoxes.First()
                    : null,
                singlePhaseStackCount,
                bendAllowance,
                preferBCabinetFirstSegment ? false : true);
        }

        internal static FaceToFacePhaseChangeRegionPlan
            AnalyzeNoOffsetPhaseChangeRegion(
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxes,
            IReadOnlyList<ModeledBusbar> bCabinetExtensionBusbars,
            IReadOnlyList<ModeledBusbar> aCabinetExtensionBusbars,
            BusbarBridgeAxisSetting axisSetting,
            bool requiresPhaseChange,
            double insulationClearance,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            FaceToFaceFrameBox preferredMiddleFrame = null,
            int singlePhaseStackCount = 1,
            double bendAllowance = 20d,
            // 自动换相默认使用四断点布局的中间逻辑段（序号 1，桥式）；
            // 客制段由 Create/Complete 在选定实际区间后重新计算。
            bool phaseChangeTargetIsBridge = true)
        {
            if (orderedFrameBoxes == null || orderedFrameBoxes.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对面无偏移没有识别到可用基架。");
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            ValidateNonNegativeFinite(
                insulationClearance,
                nameof(insulationClearance));
            FaceToWallModelBusbarPlanner.ValidateSinglePhaseStackCount(
                singlePhaseStackCount);
            ValidateNonNegativeFinite(bendAllowance, nameof(bendAllowance));

            if (!requiresPhaseChange)
            {
                FaceToWallPreviewTrace.Info(
                    "面对面换相判定",
                    "已按两柜伸出排配置属性及柜体放置方向判定为不换相。");
                return null;
            }
            if (preferredMiddleFrame == null &&
                orderedFrameBoxes.Count % 2 == 0)
            {
                throw new InvalidOperationException(
                    "面对面换相需要唯一的最中间基架；当前按 B→A 排序后的基架数量为偶数。 ");
            }

            var middleFrame = preferredMiddleFrame ?? orderedFrameBoxes[
                orderedFrameBoxes.Count / 2];
            if (!orderedFrameBoxes.Contains(middleFrame))
            {
                throw new InvalidOperationException(
                    "换相中间基架必须来自当前按轴排序的基架Box集合。");
            }
            var allExtensionBusbars = bCabinetExtensionBusbars
                .Concat(aCabinetExtensionBusbars)
                .Where(busbar => busbar?.CenterlineSetting != null)
                .ToList();
            var commonCabinetTopBusbarWidth = allExtensionBusbars.Average(
                busbar => busbar.CenterlineSetting.BusBarWidth);
            if (commonCabinetTopBusbarWidth <= GeometryTolerance ||
                allExtensionBusbars.Any(busbar => Math.Abs(
                    busbar.CenterlineSetting.BusBarWidth -
                    commonCabinetTopBusbarWidth) > ModelAlignmentTolerance))
            {
                throw new InvalidOperationException(
                    "面对面换相要求 A/B 柜三相使用一致且有效的排宽。");
            }
            var effectiveGeneratedBusbarWidth = generatedBusbarWidth ??
                commonCabinetTopBusbarWidth;
            ValidatePositiveFinite(
                effectiveGeneratedBusbarWidth,
                nameof(generatedBusbarWidth));

            var commonCabinetTopBusbarThickness = allExtensionBusbars.Average(
                busbar => busbar.CenterlineSetting.BusBarThickness);
            if (commonCabinetTopBusbarThickness <= GeometryTolerance ||
                allExtensionBusbars.Any(busbar => Math.Abs(
                    busbar.CenterlineSetting.BusBarThickness -
                    commonCabinetTopBusbarThickness) >
                    ModelAlignmentTolerance))
            {
                throw new InvalidOperationException(
                    "面对面换相要求 A/B 柜三相使用一致且有效的排厚。");
            }
            var effectiveGeneratedBusbarThickness = generatedBusbarThickness ??
                commonCabinetTopBusbarThickness;
            ValidatePositiveFinite(
                effectiveGeneratedBusbarThickness,
                nameof(generatedBusbarThickness));
            var effectiveGeneratedStackEnvelopeThickness =
                LinkageRouteStackGeometry.GetStackEnvelopeThickness(
                    effectiveGeneratedBusbarThickness,
                    singlePhaseStackCount);
            var middleFrameTargetIsBridge = phaseChangeTargetIsBridge;

            // 初始断点必须为固定的“输入绝缘距离 + 5 mm”换相核心预留完整长度。
            // 生成阶段不允许降低净距或压缩路径。
            var preferredPhaseChangeClearance =
                insulationClearance + PhaseChangePreferredClearanceAddition;
            var minimumRecommendedBreakpointSpan =
                CalculatePhaseChangeMinimumRequiredBreakpointSpan(
                    preferredPhaseChangeClearance,
                    effectiveGeneratedBusbarWidth,
                    effectiveGeneratedStackEnvelopeThickness,
                    bendAllowance,
                    middleFrameTargetIsBridge);
            var middleFrameBreakpointSpan = middleFrame.LengthMaximum -
                middleFrame.LengthMinimum;
            // 桥式换相首次打开界面时，在推荐最小长度两侧各留 5 mm，
            // 避免断点舍入或实体包络误差导致初始状态立即提示过短。
            var initialBridgeBoundarySideAddition =
                middleFrameTargetIsBridge
                    ? PhaseChangeInitialBridgeBoundarySideAddition
                    : 0d;
            var minimumInitialBreakpointSpan =
                minimumRecommendedBreakpointSpan +
                2d * initialBridgeBoundarySideAddition;
            var effectiveBreakpointSpan = Math.Max(
                middleFrameBreakpointSpan,
                minimumInitialBreakpointSpan);
            var leftFixedBreakpointCoordinate = middleFrame.LengthCenter -
                effectiveBreakpointSpan / 2d;
            var rightFixedBreakpointCoordinate = middleFrame.LengthCenter +
                effectiveBreakpointSpan / 2d;

            // 此处只记录客户输入的绝缘距离。额外 5 mm 由换相核心统一增加，
            // 不改变基架本身的几何边界。
            var routingClearance = insulationClearance;
            var availableWidthMinimum = middleFrame.WidthMinimum;
            var availableWidthMaximum = middleFrame.WidthMaximum;
            var availableHeightMinimum = middleFrame.HeightMinimum;
            var availableHeightMaximum = middleFrame.HeightMaximum;
            if (availableWidthMaximum <=
                    availableWidthMinimum + GeometryTolerance ||
                availableHeightMaximum <=
                    availableHeightMinimum + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "最中间基架 Box 没有有效换相空间。");
            }

            var globalFrameLengthOrigin =
                orderedFrameBoxes[0].LengthMinimum;
            FaceToWallPreviewTrace.Info(
                "面对面换相判定",
                "已按两柜伸出排配置属性及柜体放置方向判定为换相；" +
                "中间基架=" + middleFrame.ComponentName +
                "，默认断点=" +
                (leftFixedBreakpointCoordinate -
                    globalFrameLengthOrigin).ToString("0.###") +
                "/" +
                (rightFixedBreakpointCoordinate -
                    globalFrameLengthOrigin).ToString("0.###") +
                " mm，输入绝缘距离=" +
                insulationClearance.ToString("0.###") +
                " mm；生成换相组固定按净距额外增加 5 mm。 ");
            return new FaceToFacePhaseChangeRegionPlan
            {
                MiddleFrame = middleFrame,
                LeftFixedBreakpointPosition =
                    Math.Round(
                        leftFixedBreakpointCoordinate -
                        globalFrameLengthOrigin,
                        1),
                RightFixedBreakpointPosition =
                    Math.Round(
                        rightFixedBreakpointCoordinate -
                        globalFrameLengthOrigin,
                        1),
                LeftFixedBreakpointCoordinate =
                    leftFixedBreakpointCoordinate,
                RightFixedBreakpointCoordinate =
                    rightFixedBreakpointCoordinate,
                InsulationClearance = insulationClearance,
                RoutingClearance = routingClearance,
                MinimumRecommendedBreakpointSpan =
                    minimumRecommendedBreakpointSpan,
                BusbarWidth = effectiveGeneratedBusbarWidth,
                BusbarThickness = effectiveGeneratedStackEnvelopeThickness,
                BendAllowance = bendAllowance,
                AvailableWidthMinimum = availableWidthMinimum,
                AvailableWidthMaximum = availableWidthMaximum,
                AvailableHeightMinimum = availableHeightMinimum,
                AvailableHeightMaximum = availableHeightMaximum
            };
        }

        /// <summary>
        /// 仅根据两柜“伸出排相序”配置属性和柜体放置方向判断换相。
        /// 面对面两柜反向放置：属性相同需要换相，互为倒序不换相；
        /// 侧对侧两柜同向放置：属性相同不换相，互为倒序需要换相。
        /// </summary>
        public static bool RequiresPhaseChangeByCabinetPlacement(
            string bCabinetPhaseSequence,
            string aCabinetPhaseSequence,
            bool isSideToSideScenario)
        {
            var normalizedBCabinetPhaseSequence =
                NormalizeCabinetPhaseSequence(
                    bCabinetPhaseSequence,
                    "B柜");
            var normalizedACabinetPhaseSequence =
                NormalizeCabinetPhaseSequence(
                    aCabinetPhaseSequence,
                    "A柜");
            var hasSamePhaseSequence = string.Equals(
                normalizedBCabinetPhaseSequence,
                normalizedACabinetPhaseSequence,
                StringComparison.Ordinal);
            return isSideToSideScenario
                ? !hasSamePhaseSequence
                : hasSamePhaseSequence;
        }

        /// <summary>
        /// 保留旧的枚举序列入口；输入必须是两柜配置属性表达的逻辑相序，
        /// 不得传入按模型坐标重新排序的结果。
        /// </summary>
        public static bool RequiresPhaseChangeBetweenOpposingCabinets(
            IReadOnlyList<BusbarPhase_e> bCabinetGlobalWidthOrder,
            IReadOnlyList<BusbarPhase_e> aCabinetGlobalWidthOrder)
        {
            if (bCabinetGlobalWidthOrder == null)
            {
                throw new ArgumentNullException(
                    nameof(bCabinetGlobalWidthOrder));
            }
            if (aCabinetGlobalWidthOrder == null)
            {
                throw new ArgumentNullException(
                    nameof(aCabinetGlobalWidthOrder));
            }

            if (bCabinetGlobalWidthOrder.SequenceEqual(
                    aCabinetGlobalWidthOrder))
            {
                return true;
            }
            if (bCabinetGlobalWidthOrder.SequenceEqual(
                    aCabinetGlobalWidthOrder.Reverse()))
            {
                return false;
            }

            throw new InvalidOperationException(
                "面对面两柜配置相序既不相同也不互为倒序，无法判断换相：B柜相序=" +
                string.Join("-", bCabinetGlobalWidthOrder) +
                "，A柜相序=" +
                string.Join("-", aCabinetGlobalWidthOrder) + "。");
        }

        /// <summary>
        /// 兼容 9.8 设置窗体的最小换相断点长度提示。
        /// </summary>
        public static double CalculateMinimumRecommendedPhaseChangeBreakpointSpan(
            double busbarWidth)
        {
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            return 3d * busbarWidth;
        }

        /// <summary>
        /// 按换相实体的实际长度向包络计算最小断点间距。
        /// C 相横排、B 相上升段、A 相横排和 B/C 相下降段依次错开；
        /// 三个动作中心按“绝缘距离 + 半排宽 + 叠排折弯扫描半跨度”布置，
        /// 与最终逐根折弯实体净距检查使用同一包络。C 相入口抬升点到横排中心另保留“半排宽 +
        /// 半叠排厚 + 折弯余量”，加上横排另一侧半排宽搭接后形成完整直段；
        /// 入口、出口两端对称保留“排宽 + 半叠排组排厚 + 折弯余量”，
        /// 确保抬高前、下降后的直段长度一致并能与下一段母排完整搭接；
        /// 桥式段另加两端固定“排宽 + 40 mm + 50 mm”端头的长度轴投影。
        /// </summary>
        public static double
            CalculatePhaseChangeMinimumRequiredBreakpointSpan(
            double insulationClearance,
            double busbarWidth,
            double busbarThickness,
            double bendAllowance = 20d,
            bool isBridge = false)
        {
            ValidateNonNegativeFinite(
                insulationClearance,
                nameof(insulationClearance));
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            ValidatePositiveFinite(busbarThickness, nameof(busbarThickness));
            ValidateNonNegativeFinite(bendAllowance, nameof(bendAllowance));
            var minimumPostBendStraightLength =
                CalculatePhaseChangePerpendicularBendReserve(
                    busbarWidth,
                    busbarThickness,
                    bendAllowance);
            var centerToCenterSpacing = CalculatePhaseChangeActionCenterSpacing(
                insulationClearance, busbarWidth, busbarThickness, bendAllowance);
            var cPhasePostBendStraightAdditionalLength =
                PlanarBusbarClearanceValidator.CalculateBendSweepHalfSpan(
                    busbarThickness, bendAllowance);
            var endSegmentMinimumLength =
                minimumPostBendStraightLength;
            var minimumTransitionLength = 3d * centerToCenterSpacing +
                busbarWidth / 2d +
                cPhasePostBendStraightAdditionalLength +
                2d * endSegmentMinimumLength;
            if (isBridge)
            {
                minimumTransitionLength +=
                    2d * CalculatePhaseChangeBridgeTerminalLength(
                        busbarWidth);
            }
            return minimumTransitionLength - busbarWidth;
        }

        /// <summary>
        /// 以单根排厚和单相叠排根数计算换相段最小断点间距，供设置界面
        /// 在尚未构造完整规划上下文时使用。内部先换算成叠排总包络，避免
        /// 界面显示的“最小长度”比实际生成阶段少一截。
        /// </summary>
        public static double
            CalculatePhaseChangeMinimumRequiredBreakpointSpan(
            double insulationClearance,
            double busbarWidth,
            double singleBusbarThickness,
            int singlePhaseStackCount,
            double bendAllowance = 20d,
            bool isBridge = false)
        {
            FaceToWallModelBusbarPlanner.ValidateSinglePhaseStackCount(
                singlePhaseStackCount);
            var stackEnvelopeThickness =
                LinkageRouteStackGeometry.GetStackEnvelopeThickness(
                    singleBusbarThickness,
                    singlePhaseStackCount);
            return CalculatePhaseChangeMinimumRequiredBreakpointSpan(
                insulationClearance,
                busbarWidth,
                stackEnvelopeThickness,
                bendAllowance,
                isBridge);
        }

        internal sealed class PhaseChangeLongitudinalLayout
        {
            internal double CPhaseRiseLengthCoordinate { get; set; }

            internal double CPhaseCrossLengthCoordinate { get; set; }

            internal double BPhaseRiseLengthCoordinate { get; set; }

            internal double APhaseCrossLengthCoordinate { get; set; }

            internal double SharedDropLengthCoordinate { get; set; }
        }

        /// <summary>
        /// 从 A/B 柜组件子树内的“柜内伸出排”装配体 Comp 读取引用配置
        /// 自定义属性“伸出排相序”。不使用文件路径，也不根据空间坐标推算。
        /// 参数 <paramref name="isSideToSideScenario"/> 只用于记录场景上下文。
        /// </summary>
        public static bool TryReadCabinetPhaseWidthOrderTexts(
            ISldWorks swApp,
            bool isSideToSideScenario,
            out string bCabinetPhaseWidthOrderText,
            out string aCabinetPhaseWidthOrderText,
            out string validationMessage)
        {
            bCabinetPhaseWidthOrderText = null;
            aCabinetPhaseWidthOrderText = null;
            validationMessage = null;
            if (swApp == null || !(swApp.IActiveDoc2 is AssemblyDoc))
            {
                validationMessage =
                    "柜体相序读取需要在目标总装配体环境中执行。";
                return false;
            }

            try
            {
                var partCatalog = AssemblyPartCatalog.Create(swApp);
                var bCabinetHostComponent =
                    partCatalog.FindUniqueTopLevelComponentContaining("B柜");
                var aCabinetHostComponent =
                    partCatalog.FindUniqueTopLevelComponentContaining("A柜");
                EnsureDistinctCabinetHostComponents(
                    bCabinetHostComponent,
                    aCabinetHostComponent);
                bCabinetPhaseWidthOrderText =
                    ReadRequiredCabinetPhaseSequence(
                        partCatalog,
                        bCabinetHostComponent,
                        "B柜");
                aCabinetPhaseWidthOrderText =
                    ReadRequiredCabinetPhaseSequence(
                        partCatalog,
                        aCabinetHostComponent,
                        "A柜");
                FaceToWallPreviewTrace.Info(
                    "母排设置界面-柜体相序读取",
                    "场景=" +
                    (isSideToSideScenario ? "侧对侧" : "面对面") +
                    "；B柜=" + bCabinetPhaseWidthOrderText +
                    "；A柜=" + aCabinetPhaseWidthOrderText +
                    "；来源=柜内伸出排Comp引用配置属性");
                return true;
            }
            catch (Exception phaseOrderReadException)
            {
                FaceToWallPreviewTrace.Error(
                    "母排设置界面-柜体相序读取",
                    phaseOrderReadException,
                    "侧对侧=" + isSideToSideScenario);
                validationMessage =
                    "柜体相序读取失败：" + phaseOrderReadException.Message;
                return false;
            }
        }

        private static string ReadRequiredCabinetPhaseSequence(
            AssemblyPartCatalog partCatalog,
            Component2 cabinetHostComponent,
            string cabinetLabel)
        {
            const string assemblyNameToken = "伸出排";
            var matchingComponents = partCatalog
                .GetAllDescendants(cabinetHostComponent)
                .Where(component =>
                    BusbarGeometryUtility.SafeName(component).IndexOf(
                        assemblyNameToken,
                        StringComparison.OrdinalIgnoreCase) >= 0 &&
                    IsAssemblyComponent(component))
                .ToList();
            if (matchingComponents.Count != 1)
            {
                throw new InvalidOperationException(
                    cabinetLabel + "组件子树内名称包含“伸出排”的装配体 " +
                    "Comp 数量应为1，实际=" +
                    matchingComponents.Count + "。 ");
            }

            var phaseSequence = ReadComponentConfigurationCustomProperty(
                matchingComponents[0],
                ExtensionBusbarPhaseSequencePropertyName);
            return NormalizeCabinetPhaseSequence(
                phaseSequence,
                cabinetLabel);
        }

        private static bool IsAssemblyComponent(Component2 component)
        {
            try
            {
                return component?.GetModelDoc2() is AssemblyDoc;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadComponentConfigurationCustomProperty(
            Component2 component,
            string propertyName)
        {
            var componentModel = component?.GetModelDoc2() as ModelDoc2;
            if (componentModel == null)
            {
                throw new InvalidOperationException(
                    "组件“" + BusbarGeometryUtility.SafeName(component) +
                    "”没有可读取的装配体模型。 ");
            }

            string referencedConfiguration;
            try
            {
                referencedConfiguration = component.ReferencedConfiguration;
            }
            catch (Exception configurationReadException)
            {
                throw new InvalidOperationException(
                    "组件“" + BusbarGeometryUtility.SafeName(component) +
                    "”无法读取引用配置。",
                    configurationReadException);
            }

            var configurationNames = new[]
                {
                    referencedConfiguration,
                    string.Empty
                }
                .Where(configurationName => configurationName != null)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var configurationName in configurationNames)
            {
                try
                {
                    var propertyManager = componentModel.Extension
                        .CustomPropertyManager[configurationName];
                    if (propertyManager == null)
                    {
                        continue;
                    }

                    propertyManager.Get4(
                        propertyName,
                        false,
                        out var rawPropertyValue,
                        out var resolvedPropertyValue);
                    var propertyValue = string.IsNullOrWhiteSpace(
                        resolvedPropertyValue)
                        ? rawPropertyValue
                        : resolvedPropertyValue;
                    if (!string.IsNullOrWhiteSpace(propertyValue))
                    {
                        return propertyValue;
                    }
                }
                catch (Exception propertyReadException)
                {
                    FaceToWallPreviewTrace.Error(
                        "柜内伸出排-读取相序属性",
                        propertyReadException,
                        "组件=" +
                        BusbarGeometryUtility.SafeName(component) +
                        "；引用配置=" + configurationName +
                        "；属性=" + propertyName);
                }
            }

            throw new InvalidOperationException(
                "组件“" + BusbarGeometryUtility.SafeName(component) +
                "”的引用配置“" + (referencedConfiguration ?? string.Empty) +
                "”未读取到属性“" + propertyName + "”。 ");
        }

        private static string NormalizeCabinetPhaseSequence(
            string phaseSequence,
            string cabinetLabel)
        {
            var normalizedPhaseSequence = (phaseSequence ?? string.Empty)
                .Trim()
                .ToUpperInvariant();
            if (string.Equals(
                    normalizedPhaseSequence,
                    "ABC",
                    StringComparison.Ordinal) ||
                string.Equals(
                    normalizedPhaseSequence,
                    "CBA",
                    StringComparison.Ordinal))
            {
                return normalizedPhaseSequence;
            }

            throw new InvalidOperationException(
                cabinetLabel + "伸出排相序仅支持 ABC 或 CBA，当前值=" +
                (string.IsNullOrWhiteSpace(phaseSequence)
                    ? "空"
                    : phaseSequence) + "。 ");
        }

        private static double CalculatePlanarDistanceSquared(
            double firstLengthCoordinate,
            double firstWidthCoordinate,
            double secondLengthCoordinate,
            double secondWidthCoordinate)
        {
            var lengthDifference =
                firstLengthCoordinate - secondLengthCoordinate;
            var widthDifference = firstWidthCoordinate - secondWidthCoordinate;
            return lengthDifference * lengthDifference +
                   widthDifference * widthDifference;
        }

        /// <summary>
        /// 大偏移专用：完全忽略基架名称和引用文件，把实际 Box 中点投影到
        /// 长度-宽度二维平面，再按 B 长度腿、宽向腿、A 长度腿的 Z 型路径里程排序。
        /// </summary>
        internal static IReadOnlyList<FaceToFaceFrameBox>
            OrderLargeOffsetFrameBoxesByGeometry(
            IReadOnlyList<FaceToFaceFrameBox> frameBoxes,
            double bCabinetLengthCoordinate,
            double aCabinetLengthCoordinate)
        {
            if (frameBoxes == null || frameBoxes.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对面没有可用于几何排序的基架 Box。");
            }

            var cabinetCoordinates = new[]
            {
                bCabinetLengthCoordinate,
                aCabinetLengthCoordinate
            };
            if (cabinetCoordinates.Any(coordinate =>
                    double.IsNaN(coordinate) || double.IsInfinity(coordinate)))
            {
                throw new InvalidOperationException(
                    "B柜/A柜基座 Box 中点包含无效坐标。");
            }

            var signedCabinetLengthDistance =
                aCabinetLengthCoordinate - bCabinetLengthCoordinate;
            if (Math.Abs(signedCabinetLengthDistance) <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "B柜与A柜基座的长度轴中点重合，无法确定 B柜 到 A柜 的方向。");
            }

            var invalidFrameBox = frameBoxes.FirstOrDefault(frameBox =>
                frameBox == null ||
                double.IsNaN(frameBox.LengthMinimum) ||
                double.IsInfinity(frameBox.LengthMinimum) ||
                double.IsNaN(frameBox.LengthMaximum) ||
                double.IsInfinity(frameBox.LengthMaximum) ||
                double.IsNaN(frameBox.WidthMinimum) ||
                double.IsInfinity(frameBox.WidthMinimum) ||
                double.IsNaN(frameBox.WidthMaximum) ||
                double.IsInfinity(frameBox.WidthMaximum) ||
                frameBox.LengthMaximum <= frameBox.LengthMinimum ||
                frameBox.WidthMaximum <= frameBox.WidthMinimum);
            if (invalidFrameBox != null)
            {
                throw new InvalidOperationException(
                    "面对面基架包含无效的实际 Box 范围：" +
                    (invalidFrameBox.ComponentName ?? "未命名基架") + "。");
            }

            var lengthDirectionSign =
                signedCabinetLengthDistance > 0d ? 1d : -1d;
            var frameBoxCandidates = frameBoxes
                .Select((frameBox, inputIndex) =>
                    new FrameBoxGeometryCandidate
                    {
                        FrameBox = CloneFrameBox(frameBox),
                        InputIndex = inputIndex
                    })
                .ToList();
            var lengthOrderedCandidates = frameBoxCandidates
                .OrderBy(frameBoxCandidate =>
                    lengthDirectionSign *
                    frameBoxCandidate.FrameBox.LengthCenter)
                .ThenBy(frameBoxCandidate => frameBoxCandidate.InputIndex)
                .ToList();
            var lengthCenterClusters =
                new List<List<FrameBoxGeometryCandidate>>();
            foreach (var frameBoxCandidate in lengthOrderedCandidates)
            {
                var startsNewLengthCenterCluster =
                    lengthCenterClusters.Count == 0;
                if (!startsNewLengthCenterCluster)
                {
                    var activeLengthCenterCluster =
                        lengthCenterClusters[lengthCenterClusters.Count - 1];
                    var previousLengthOrderedCandidate =
                        activeLengthCenterCluster[
                            activeLengthCenterCluster.Count - 1];
                    startsNewLengthCenterCluster = Math.Abs(
                        frameBoxCandidate.FrameBox.LengthCenter -
                        previousLengthOrderedCandidate.FrameBox.LengthCenter) >
                        FrameLengthCenterClusterTolerance;
                }

                if (startsNewLengthCenterCluster)
                {
                    lengthCenterClusters.Add(
                        new List<FrameBoxGeometryCandidate>());
                }

                lengthCenterClusters[lengthCenterClusters.Count - 1].Add(
                    frameBoxCandidate);
            }

            var transitionLengthClusters = lengthCenterClusters
                .Where(lengthCenterCluster =>
                    lengthCenterCluster.Count >= 2 &&
                    lengthCenterCluster.Max(frameBoxCandidate =>
                        frameBoxCandidate.FrameBox.WidthCenter) -
                    lengthCenterCluster.Min(frameBoxCandidate =>
                        frameBoxCandidate.FrameBox.WidthCenter) >
                    FrameWidthCenterChangeTolerance)
                .ToList();
            if (transitionLengthClusters.Count != 1)
            {
                throw new InvalidOperationException(
                    "无法在基架 Box 中点的长度-宽度平面上唯一识别 Z 型宽向基架簇，识别数量=" +
                    transitionLengthClusters.Count + "。");
            }

            var transitionLengthCluster = transitionLengthClusters[0];
            var transitionLengthCoordinate =
                transitionLengthCluster.Average(
                    frameBoxCandidate =>
                        frameBoxCandidate.FrameBox.LengthCenter);
            var transitionCandidateIndexes = new HashSet<int>(
                transitionLengthCluster.Select(
                    frameBoxCandidate => frameBoxCandidate.InputIndex));
            var bLengthLegCandidates = frameBoxCandidates
                .Where(frameBoxCandidate =>
                    !transitionCandidateIndexes.Contains(
                        frameBoxCandidate.InputIndex) &&
                    lengthDirectionSign *
                    (frameBoxCandidate.FrameBox.LengthCenter -
                     transitionLengthCoordinate) <
                    -FrameLengthCenterClusterTolerance)
                .ToList();
            var aLengthLegCandidates = frameBoxCandidates
                .Where(frameBoxCandidate =>
                    !transitionCandidateIndexes.Contains(
                        frameBoxCandidate.InputIndex) &&
                    lengthDirectionSign *
                    (frameBoxCandidate.FrameBox.LengthCenter -
                     transitionLengthCoordinate) >
                    FrameLengthCenterClusterTolerance)
                .ToList();
            if (bLengthLegCandidates.Count == 0 ||
                aLengthLegCandidates.Count == 0)
            {
                throw new InvalidOperationException(
                    "Z 型基架 Box 中点没有同时形成 B柜侧长度腿和 A柜侧长度腿。");
            }

            var bFrameLegWidthCoordinate = bLengthLegCandidates.Average(
                frameBoxCandidate =>
                    frameBoxCandidate.FrameBox.WidthCenter);
            var aFrameLegWidthCoordinate = aLengthLegCandidates.Average(
                frameBoxCandidate =>
                    frameBoxCandidate.FrameBox.WidthCenter);
            var signedFrameLegWidthDistance =
                aFrameLegWidthCoordinate - bFrameLegWidthCoordinate;
            if (Math.Abs(signedFrameLegWidthDistance) <=
                FrameWidthCenterChangeTolerance)
            {
                throw new InvalidOperationException(
                    "大偏移基架两侧长度腿的 Box 中点没有形成有效宽度偏移。");
            }

            var widthDirectionSign =
                signedFrameLegWidthDistance > 0d ? 1d : -1d;
            foreach (var frameBoxCandidate in frameBoxCandidates)
            {
                AssignLargeOffsetPathPosition(
                    frameBoxCandidate,
                    bCabinetLengthCoordinate,
                    bFrameLegWidthCoordinate,
                    aCabinetLengthCoordinate,
                    aFrameLegWidthCoordinate,
                    transitionLengthCoordinate,
                    lengthDirectionSign,
                    widthDirectionSign);
            }

            var orderedFrameBoxes = frameBoxCandidates
                .OrderBy(frameBoxCandidate =>
                    frameBoxCandidate.PathProgress)
                .ThenBy(frameBoxCandidate =>
                    frameBoxCandidate.PathDistance)
                .ThenBy(frameBoxCandidate =>
                    frameBoxCandidate.InputIndex)
                .Select(frameBoxCandidate => frameBoxCandidate.FrameBox)
                .ToList();
            for (var orderedFrameIndex = 0;
                orderedFrameIndex < orderedFrameBoxes.Count;
                orderedFrameIndex++)
            {
                orderedFrameBoxes[orderedFrameIndex].PhysicalIndex =
                    orderedFrameIndex;
            }

            return orderedFrameBoxes.AsReadOnly();
        }

        /// <summary>
        /// 把 Box 中点投影到长度-宽度二维平面，并计算其在
        /// B 长度腿 → 宽向腿 → A 长度腿这条 Z 型折线路径上的里程。
        /// 高度坐标完全不参与基架排序。
        /// </summary>
        private static void AssignLargeOffsetPathPosition(
            FrameBoxGeometryCandidate frameBoxCandidate,
            double bCabinetLengthCoordinate,
            double bCabinetWidthCoordinate,
            double aCabinetLengthCoordinate,
            double aCabinetWidthCoordinate,
            double transitionLengthCoordinate,
            double lengthDirectionSign,
            double widthDirectionSign)
        {
            var totalLengthDistance = Math.Abs(
                aCabinetLengthCoordinate - bCabinetLengthCoordinate);
            var totalWidthDistance = Math.Abs(
                aCabinetWidthCoordinate - bCabinetWidthCoordinate);
            var transitionLengthDistance = lengthDirectionSign *
                (transitionLengthCoordinate - bCabinetLengthCoordinate);
            if (transitionLengthDistance < -
                    FrameLengthCenterClusterTolerance ||
                transitionLengthDistance > totalLengthDistance +
                    FrameLengthCenterClusterTolerance)
            {
                throw new InvalidOperationException(
                    "Z 型宽向基架簇的长度中点不在 B柜 与 A柜 基座之间。");
            }

            transitionLengthDistance = ClampCoordinate(
                transitionLengthDistance,
                0d,
                totalLengthDistance);
            var frameLengthDistance = lengthDirectionSign *
                (frameBoxCandidate.FrameBox.LengthCenter -
                 bCabinetLengthCoordinate);
            var frameWidthDistance = widthDirectionSign *
                (frameBoxCandidate.FrameBox.WidthCenter -
                 bCabinetWidthCoordinate);

            var firstLegNearestLength = ClampCoordinate(
                frameLengthDistance,
                0d,
                transitionLengthDistance);
            var firstLegDistance = CalculatePlaneDistance(
                frameLengthDistance,
                frameWidthDistance,
                firstLegNearestLength,
                0d);
            var selectedPathProgress = firstLegNearestLength;
            var selectedPathDistance = firstLegDistance;

            var middleLegNearestWidth = ClampCoordinate(
                frameWidthDistance,
                0d,
                totalWidthDistance);
            var middleLegDistance = CalculatePlaneDistance(
                frameLengthDistance,
                frameWidthDistance,
                transitionLengthDistance,
                middleLegNearestWidth);
            if (middleLegDistance < selectedPathDistance - GeometryTolerance)
            {
                selectedPathProgress =
                    transitionLengthDistance + middleLegNearestWidth;
                selectedPathDistance = middleLegDistance;
            }

            var lastLegNearestLength = ClampCoordinate(
                frameLengthDistance,
                transitionLengthDistance,
                totalLengthDistance);
            var lastLegDistance = CalculatePlaneDistance(
                frameLengthDistance,
                frameWidthDistance,
                lastLegNearestLength,
                totalWidthDistance);
            if (lastLegDistance < selectedPathDistance - GeometryTolerance)
            {
                selectedPathProgress =
                    transitionLengthDistance + totalWidthDistance +
                    lastLegNearestLength - transitionLengthDistance;
                selectedPathDistance = lastLegDistance;
            }

            frameBoxCandidate.PathProgress = selectedPathProgress;
            frameBoxCandidate.PathDistance = selectedPathDistance;
        }

        private static double ClampCoordinate(
            double sourceCoordinate,
            double minimumCoordinate,
            double maximumCoordinate)
        {
            return Math.Max(
                minimumCoordinate,
                Math.Min(maximumCoordinate, sourceCoordinate));
        }

        private static double CalculatePlaneDistance(
            double firstLengthCoordinate,
            double firstWidthCoordinate,
            double secondLengthCoordinate,
            double secondWidthCoordinate)
        {
            var lengthDistance =
                firstLengthCoordinate - secondLengthCoordinate;
            var widthDistance =
                firstWidthCoordinate - secondWidthCoordinate;
            return Math.Sqrt(
                lengthDistance * lengthDistance +
                widthDistance * widthDistance);
        }

        private static FaceToFaceFrameBox CloneFrameBox(
            FaceToFaceFrameBox sourceFrameBox)
        {
            return new FaceToFaceFrameBox
            {
                PhysicalIndex = sourceFrameBox.PhysicalIndex,
                ComponentName = sourceFrameBox.ComponentName,
                Component = sourceFrameBox.Component,
                Minimum = sourceFrameBox.Minimum?.Copy(),
                Maximum = sourceFrameBox.Maximum?.Copy(),
                LengthMinimum = sourceFrameBox.LengthMinimum,
                LengthMaximum = sourceFrameBox.LengthMaximum,
                WidthMinimum = sourceFrameBox.WidthMinimum,
                WidthMaximum = sourceFrameBox.WidthMaximum,
                HeightMinimum = sourceFrameBox.HeightMinimum,
                HeightMaximum = sourceFrameBox.HeightMaximum
            };
        }

        /// <summary>
        /// 仅根据有序实际基架 Box 与两端三相中心识别唯一连续宽度偏移区。
        /// 转弯顺序从偏移内侧相开始，并按模型中的实际相间中心距沿长度轴依次错开。
        /// </summary>
        internal static FaceToFaceLargeOffsetRegionPlan AnalyzeLargeOffsetRegion(
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxes,
            IReadOnlyList<double> bPhaseWidthCoordinates,
            IReadOnlyList<double> aPhaseWidthCoordinates,
            double busbarWidth,
            bool phaseChangeBeforeLargeOffset = false)
        {
            if (orderedFrameBoxes == null || orderedFrameBoxes.Count < 3)
            {
                throw new InvalidOperationException(
                    "面对面大偏移至少需要三个有序物理基架。");
            }

            if (orderedFrameBoxes.Any(frameBox => frameBox == null ||
                    double.IsNaN(frameBox.LengthMinimum) ||
                    double.IsInfinity(frameBox.LengthMinimum) ||
                    double.IsNaN(frameBox.LengthMaximum) ||
                    double.IsInfinity(frameBox.LengthMaximum) ||
                    double.IsNaN(frameBox.WidthMinimum) ||
                    double.IsInfinity(frameBox.WidthMinimum) ||
                    double.IsNaN(frameBox.WidthMaximum) ||
                    double.IsInfinity(frameBox.WidthMaximum) ||
                    frameBox.LengthMaximum <= frameBox.LengthMinimum ||
                    frameBox.WidthMaximum <= frameBox.WidthMinimum))
            {
                throw new InvalidOperationException(
                    "面对面大偏移包含无效的基架 Box 范围。");
            }

            ValidatePhaseCoordinateLists(
                bPhaseWidthCoordinates,
                aPhaseWidthCoordinates,
                phaseChangeBeforeLargeOffset);
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            var changedEdgeIndexes = GetChangedWidthCenterEdgeIndexes(
                orderedFrameBoxes);
            if (changedEdgeIndexes.Count == 0)
            {
                throw new InvalidOperationException(
                    "当前有序基架 Box 没有形成面对面大偏移宽度变化区。");
            }

            var firstChangedEdgeIndex = changedEdgeIndexes[0];
            var lastChangedEdgeIndex = changedEdgeIndexes[changedEdgeIndexes.Count - 1];
            if (changedEdgeIndexes.Count !=
                lastChangedEdgeIndex - firstChangedEdgeIndex + 1)
            {
                throw new InvalidOperationException(
                    "面对面只允许一个连续大偏移区；当前基架 Box 出现了两个或更多分离的宽度变化区。");
            }

            var changedRegionFrameBoxes = orderedFrameBoxes
                .Skip(firstChangedEdgeIndex)
                .Take(lastChangedEdgeIndex - firstChangedEdgeIndex + 2)
                .ToList();
            var involvedFrameBoxes = SelectLargeOffsetTransitionFrameRun(
                changedRegionFrameBoxes);
            var transitionLengthMinimum = involvedFrameBoxes.Max(
                frameBox => frameBox.LengthMinimum);
            var transitionLengthMaximum = involvedFrameBoxes.Min(
                frameBox => frameBox.LengthMaximum);
            if (transitionLengthMaximum <=
                transitionLengthMinimum + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "大偏移连续基架 Box 在长度轴上没有共同的转弯范围。");
            }

            var frameRunSignedWidthOffset =
                involvedFrameBoxes[involvedFrameBoxes.Count - 1]
                    .WidthCenter - involvedFrameBoxes[0].WidthCenter;
            if (Math.Abs(frameRunSignedWidthOffset) <=
                    FrameWidthCenterChangeTolerance)
            {
                throw new InvalidOperationException(
                    "大偏移基架没有形成有效的宽度向位移。");
            }

            // 换相在大偏移前完成时，转向段 B 侧接收的是换相后的物理相位。
            // 先把 A 柜相位坐标沿基架实际偏移量反投影到 B 侧，避免把相序
            // 变化误算成三相各不相同的几何偏移。
            var bSideTurnWidthCoordinates = phaseChangeBeforeLargeOffset
                ? aPhaseWidthCoordinates
                    .Select(aPhaseWidthCoordinate =>
                        aPhaseWidthCoordinate - frameRunSignedWidthOffset)
                    .ToList()
                : bPhaseWidthCoordinates.ToList();
            var phaseWidthOffsets = Enumerable.Range(0, PhaseCount)
                .Select(phaseIndex =>
                    aPhaseWidthCoordinates[phaseIndex] -
                    bSideTurnWidthCoordinates[phaseIndex])
                .ToList();
            var signedWidthOffset = phaseWidthOffsets.Average();
            if (Math.Abs(signedWidthOffset) <=
                FrameWidthCenterChangeTolerance)
            {
                throw new InvalidOperationException(
                    "两柜三相中心没有形成有效的大偏移宽度差。");
            }

            if (phaseWidthOffsets.Any(phaseWidthOffset =>
                    Math.Abs(phaseWidthOffset - signedWidthOffset) >
                    ModelAlignmentTolerance))
            {
                throw new InvalidOperationException(
                    "A/B 柜对应相的宽度偏移量不一致，无法保持原相间距生成大偏移。");
            }

            if (Math.Sign(frameRunSignedWidthOffset) !=
                    Math.Sign(signedWidthOffset))
            {
                throw new InvalidOperationException(
                    "柜顶三相中心指示的大偏移方向与实际拐角/连接基架 Box 的宽度偏移方向不一致。");
            }

            var offsetDirectionSign = signedWidthOffset > 0d ? 1d : -1d;
            var innerToOuterPhaseIndexes = Enumerable.Range(0, PhaseCount)
                .OrderByDescending(phaseIndex =>
                    offsetDirectionSign *
                    bSideTurnWidthCoordinates[phaseIndex])
                .ToList();
            var innerPhaseWidthCoordinate =
                bSideTurnWidthCoordinates[innerToOuterPhaseIndexes[0]];
            var turnCenterSpan = bSideTurnWidthCoordinates.Max() -
                                 bSideTurnWidthCoordinates.Min();
            var availableTurnLength = transitionLengthMaximum -
                                      transitionLengthMinimum;
            if (availableTurnLength + GeometryTolerance <
                turnCenterSpan + busbarWidth)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "大偏移共同转弯范围 {0:0.###} mm 不足以容纳三相实际中心跨度 {1:0.###} mm 和一个排宽 {2:0.###} mm。",
                        availableTurnLength,
                        turnCenterSpan,
                        busbarWidth));
            }

            var firstTurnLengthCoordinate = transitionLengthMinimum +
                (availableTurnLength - turnCenterSpan) / 2d;
            var result = new FaceToFaceLargeOffsetRegionPlan
            {
                FirstChangedEdgeIndex = firstChangedEdgeIndex,
                LastChangedEdgeIndex = lastChangedEdgeIndex,
                FirstPhysicalFrameIndex =
                    involvedFrameBoxes[0].PhysicalIndex,
                LastPhysicalFrameIndex =
                    involvedFrameBoxes[involvedFrameBoxes.Count - 1]
                        .PhysicalIndex,
                TransitionLengthMinimum = transitionLengthMinimum,
                TransitionLengthMaximum = transitionLengthMaximum,
                SignedWidthOffset = signedWidthOffset
            };
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var turnOrder = innerToOuterPhaseIndexes.IndexOf(phaseIndex);
                var turnSpacingFromInner = Math.Abs(
                    bSideTurnWidthCoordinates[phaseIndex] -
                    innerPhaseWidthCoordinate);
                result.PhaseRoutes.Add(new FaceToFaceLargeOffsetPhaseRoute
                {
                    Phase = GetPhase(phaseIndex),
                    TurnOrder = turnOrder,
                    TurnLengthCoordinate =
                        firstTurnLengthCoordinate + turnSpacingFromInner,
                    BSideWidthCoordinate =
                        bSideTurnWidthCoordinates[phaseIndex],
                    ASideWidthCoordinate =
                        aPhaseWidthCoordinates[phaseIndex]
                });
            }

            return result;
        }

        /// <summary>
        /// 变化边两侧可能包含与拐角仅相邻、但长度范围并不重合的常规基架。
        /// 大偏移的两个拐角和中间连接基架沿长度轴使用同一中心，故从变化区中
        /// 选择唯一最长的“长度中心一致”连续簇，再取其共同长度范围作为转弯区。
        /// </summary>
        private static List<FaceToFaceFrameBox>
            SelectLargeOffsetTransitionFrameRun(
            IReadOnlyList<FaceToFaceFrameBox> changedRegionFrameBoxes)
        {
            var candidateRuns = new List<List<FaceToFaceFrameBox>>();
            foreach (var changedRegionFrameBox in changedRegionFrameBoxes)
            {
                if (candidateRuns.Count == 0 ||
                    Math.Abs(candidateRuns[candidateRuns.Count - 1]
                                 [candidateRuns[candidateRuns.Count - 1].Count - 1]
                                 .LengthCenter -
                             changedRegionFrameBox.LengthCenter) >
                    TransitionFrameLengthCenterMergeTolerance)
                {
                    candidateRuns.Add(new List<FaceToFaceFrameBox>());
                }

                candidateRuns[candidateRuns.Count - 1].Add(
                    changedRegionFrameBox);
            }

            var maximumRunCount = candidateRuns.Max(
                candidateRun => candidateRun.Count);
            var longestRuns = candidateRuns
                .Where(candidateRun =>
                    candidateRun.Count == maximumRunCount)
                .ToList();
            if (maximumRunCount < 2 || longestRuns.Count != 1)
            {
                throw new InvalidOperationException(
                    "无法从连续宽度变化区唯一识别长度中心一致的拐角/连接基架簇。");
            }

            return longestRuns[0];
        }

        /// <summary>
        /// 将沿 B→A Z 型展开路径的断点里程转换为模型长度轴坐标。
        /// B 侧保持原坐标，连接基架区投影到共同转弯位置，A 侧扣除
        /// 两个转角之间在展开路径中新增的宽度向路程。
        /// </summary>
        internal static IReadOnlyList<double>
            MapLargeOffsetPathCoordinatesToLengthAxis(
                IReadOnlyList<double> pathBreakpointCoordinates,
                IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                    cornerBreakpointProtectionRanges)
        {
            if (pathBreakpointCoordinates == null)
            {
                throw new ArgumentNullException(
                    nameof(pathBreakpointCoordinates));
            }

            var orderedProtectionRanges =
                (cornerBreakpointProtectionRanges ??
                 Array.Empty<FaceToFaceLargeOffsetBreakpointProtectionRange>())
                    .Where(protectionRange => protectionRange != null)
                    .OrderBy(protectionRange =>
                        protectionRange.MinimumCornerFrameCoordinate)
                    .ToList();
            if (orderedProtectionRanges.Count != 2)
            {
                throw new InvalidOperationException(
                    "大偏移路径坐标转换必须提供两个转角基架范围。");
            }

            var bSideCornerRange = orderedProtectionRanges[0];
            var aSideCornerRange = orderedProtectionRanges[1];
            var additionalCrosswisePathLength =
                aSideCornerRange.MaximumCornerFrameCoordinate -
                bSideCornerRange.MaximumCornerFrameCoordinate;
            if (additionalCrosswisePathLength <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "大偏移 A 侧转角必须位于 B 侧转角之后。");
            }

            return pathBreakpointCoordinates
                .Select(pathCoordinate =>
                {
                    if (pathCoordinate <=
                        bSideCornerRange.MaximumCornerFrameCoordinate)
                    {
                        return pathCoordinate;
                    }

                    if (pathCoordinate >=
                        aSideCornerRange.MinimumCornerFrameCoordinate)
                    {
                        return pathCoordinate -
                               additionalCrosswisePathLength;
                    }

                    // 连接基架上的断点只用于拆分宽度向铜排；其长度轴投影
                    // 固定在大偏移逻辑段的 B 侧终点，不参与普通直段生成。
                    return bSideCornerRange.MaximumCornerFrameCoordinate;
                })
                .ToList()
                .AsReadOnly();
        }

        private static bool TryFindExplicitLargeOffsetTurnBreakpointIndexes(
            IReadOnlyList<double> breakpointCoordinates,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                cornerBreakpointProtectionRanges,
            out int bSideTurnBreakpointIndex,
            out int aSideTurnBreakpointIndex)
        {
            bSideTurnBreakpointIndex = -1;
            aSideTurnBreakpointIndex = -1;
            if (breakpointCoordinates == null ||
                cornerBreakpointProtectionRanges == null ||
                cornerBreakpointProtectionRanges.Count != 2)
            {
                return false;
            }

            var orderedRanges = cornerBreakpointProtectionRanges
                .Where(range => range != null)
                .OrderBy(range => range.MinimumCornerFrameCoordinate)
                .ToList();
            if (orderedRanges.Count != 2)
            {
                return false;
            }

            const double roundedCoordinateTolerance = 0.11d;
            var firstTurnCoordinate =
                (orderedRanges[0].MinimumProtectedCoordinate +
                 orderedRanges[0].MaximumProtectedCoordinate) / 2d;
            var secondTurnCoordinate =
                (orderedRanges[1].MinimumProtectedCoordinate +
                 orderedRanges[1].MaximumProtectedCoordinate) / 2d;
            bSideTurnBreakpointIndex = breakpointCoordinates
                .Select((coordinate, index) => new { coordinate, index })
                .Where(item => Math.Abs(item.coordinate - firstTurnCoordinate) <=
                               roundedCoordinateTolerance)
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            aSideTurnBreakpointIndex = breakpointCoordinates
                .Select((coordinate, index) => new { coordinate, index })
                .Where(item => Math.Abs(item.coordinate - secondTurnCoordinate) <=
                               roundedCoordinateTolerance)
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();

            return bSideTurnBreakpointIndex > 0 &&
                   aSideTurnBreakpointIndex > bSideTurnBreakpointIndex &&
                   aSideTurnBreakpointIndex < breakpointCoordinates.Count - 1;
        }

        /// <summary>
        /// 把一个包含转角的逻辑断点区间展开成三段物理直排端点。
        /// 两个垂直搭接均以转角中心前后各半个排宽构成 W×W 区域。
        /// </summary>
        internal static int CompleteLargeOffsetRouteGeometry(
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            IReadOnlyList<double> modelBreakpointCoordinates,
            double busbarWidth,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                cornerBreakpointProtectionRanges = null,
            IReadOnlyList<double> lengthAxisBreakpointCoordinates = null)
        {
            if (largeOffsetRegion == null)
            {
                throw new ArgumentNullException(nameof(largeOffsetRegion));
            }

            if (largeOffsetRegion.PhaseRoutes.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    "大偏移纯几何结果必须包含完整三相转角。");
            }

            if (modelBreakpointCoordinates == null ||
                modelBreakpointCoordinates.Count < 2)
            {
                throw new InvalidOperationException(
                    "大偏移至少需要一个相邻断点区间。");
            }

            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            var normalizedLengthAxisBreakpointCoordinates =
                lengthAxisBreakpointCoordinates ?? modelBreakpointCoordinates;
            if (normalizedLengthAxisBreakpointCoordinates.Count !=
                modelBreakpointCoordinates.Count)
            {
                throw new InvalidOperationException(
                    "大偏移路径断点与长度轴投影断点数量不一致。");
            }
            for (var breakpointIndex = 0;
                breakpointIndex < modelBreakpointCoordinates.Count;
                breakpointIndex++)
            {
                var breakpointCoordinate =
                    modelBreakpointCoordinates[breakpointIndex];
                if (double.IsNaN(breakpointCoordinate) ||
                    double.IsInfinity(breakpointCoordinate) ||
                    breakpointIndex > 0 && breakpointCoordinate <=
                    modelBreakpointCoordinates[breakpointIndex - 1] +
                    GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "大偏移模型断点必须是严格递增的有效数值。");
                }
            }

            var sourceIntervalIndex = -1;
            var lastSourceIntervalIndex = -1;
            largeOffsetRegion.CrosswiseBreakpointFractions.Clear();
            var orderedCornerProtectionRanges =
                (cornerBreakpointProtectionRanges ??
                 Array.Empty<FaceToFaceLargeOffsetBreakpointProtectionRange>())
                    .Where(protectionRange => protectionRange != null)
                    .OrderBy(protectionRange =>
                        protectionRange.MinimumCornerFrameCoordinate)
                    .ToList();
            var hasExplicitTurnBreakpoints =
                TryFindExplicitLargeOffsetTurnBreakpointIndexes(
                    modelBreakpointCoordinates,
                    orderedCornerProtectionRanges,
                    out var bSideTurnBreakpointIndex,
                    out var aSideTurnBreakpointIndex);
            if (orderedCornerProtectionRanges.Count == 0)
            {
                // 兼容纯几何调用：没有界面物理基架里程时仍按原共同转弯范围校验。
                var transitionProtectionRange =
                    FaceToFaceLargeOffsetBreakpointProtectionRange.Create(
                        largeOffsetRegion.TransitionLengthMinimum,
                        largeOffsetRegion.TransitionLengthMaximum,
                        busbarWidth);
                var matchingIntervalIndexes = Enumerable.Range(
                        0,
                        modelBreakpointCoordinates.Count - 1)
                    .Where(intervalIndex =>
                        transitionProtectionRange.IsStrictlySpannedBy(
                            modelBreakpointCoordinates[intervalIndex],
                            modelBreakpointCoordinates[intervalIndex + 1]))
                    .ToList();
                if (matchingIntervalIndexes.Count != 1)
                {
                    throw new InvalidOperationException(
                        "大偏移三相转角必须完整落在同一个相邻断点区间内，不能在转角区内增加断点。");
                }

                sourceIntervalIndex = matchingIntervalIndexes[0];
                lastSourceIntervalIndex = sourceIntervalIndex;
            }
            else
            {
                if (orderedCornerProtectionRanges.Count != 2)
                {
                    throw new InvalidOperationException(
                        "大偏移必须提供两个且仅两个转角基架断点禁区。");
                }

                var bSideCornerProtectionRange =
                    orderedCornerProtectionRanges[0];
                var aSideCornerProtectionRange =
                    orderedCornerProtectionRanges[1];
                if (Math.Abs(bSideCornerProtectionRange.BusbarWidth -
                        busbarWidth) > ModelAlignmentTolerance ||
                    Math.Abs(aSideCornerProtectionRange.BusbarWidth -
                        busbarWidth) > ModelAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        "大偏移转角禁区使用的排宽与实际铜排排宽不一致。");
                }

                if (bSideCornerProtectionRange.MaximumProtectedCoordinate >=
                    aSideCornerProtectionRange.MinimumProtectedCoordinate -
                    GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "大偏移两个转角基架外扩一个排宽后没有剩余可搭接区。");
                }

                var nonTurnBreakpoints = modelBreakpointCoordinates
                    .Where((breakpointCoordinate, breakpointIndex) =>
                        !hasExplicitTurnBreakpoints ||
                        breakpointIndex != bSideTurnBreakpointIndex &&
                        breakpointIndex != aSideTurnBreakpointIndex)
                    .ToList();
                var breakpointInsideCornerProtection =
                    nonTurnBreakpoints.FirstOrDefault(
                        breakpointCoordinate =>
                            orderedCornerProtectionRanges.Any(
                                protectionRange =>
                                    protectionRange.Contains(
                                        breakpointCoordinate)));
                if (nonTurnBreakpoints.Any(breakpointCoordinate =>
                        orderedCornerProtectionRanges.Any(protectionRange =>
                            protectionRange.Contains(breakpointCoordinate))))
                {
                    throw new InvalidOperationException(
                        "断点不能位于两个浅橙色转角基架本体及其左右各一个排宽的独立禁区内；" +
                        "中间连接基架扣除两端排宽后的剩余区间允许搭接。违规断点=" +
                        breakpointInsideCornerProtection.ToString("0.###") +
                        " mm。");
                }

                if (hasExplicitTurnBreakpoints)
                {
                    sourceIntervalIndex = bSideTurnBreakpointIndex - 1;
                    lastSourceIntervalIndex = aSideTurnBreakpointIndex;
                }
                else
                {
                    var bSideMatchingIntervals = Enumerable.Range(
                            0,
                            modelBreakpointCoordinates.Count - 1)
                        .Where(intervalIndex =>
                            bSideCornerProtectionRange.IsStrictlySpannedBy(
                                modelBreakpointCoordinates[intervalIndex],
                                modelBreakpointCoordinates[intervalIndex + 1]))
                        .ToList();
                    var aSideMatchingIntervals = Enumerable.Range(
                            0,
                            modelBreakpointCoordinates.Count - 1)
                        .Where(intervalIndex =>
                            aSideCornerProtectionRange.IsStrictlySpannedBy(
                                modelBreakpointCoordinates[intervalIndex],
                                modelBreakpointCoordinates[intervalIndex + 1]))
                        .ToList();
                    if (bSideMatchingIntervals.Count != 1 ||
                        aSideMatchingIntervals.Count != 1)
                    {
                        throw new InvalidOperationException(
                            "大偏移两个转角基架禁区必须分别完整落在相邻断点区间内。");
                    }

                    sourceIntervalIndex = bSideMatchingIntervals[0];
                    lastSourceIntervalIndex = aSideMatchingIntervals[0];
                }
                if (lastSourceIntervalIndex < sourceIntervalIndex)
                {
                    throw new InvalidOperationException(
                        "大偏移断点顺序与 B→A 两个转角基架顺序不一致。");
                }

                var connectorStartCoordinate =
                    bSideCornerProtectionRange.MaximumCornerFrameCoordinate;
                var connectorEndCoordinate =
                    aSideCornerProtectionRange.MinimumCornerFrameCoordinate;
                var connectorLength = connectorEndCoordinate -
                                      connectorStartCoordinate;
                if (connectorLength <= GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "大偏移两个转角基架之间没有有效连接基架长度。");
                }

                var firstCrosswiseBreakpointIndex = hasExplicitTurnBreakpoints
                    ? bSideTurnBreakpointIndex + 1
                    : sourceIntervalIndex + 1;
                var lastCrosswiseBreakpointIndex = hasExplicitTurnBreakpoints
                    ? aSideTurnBreakpointIndex - 1
                    : lastSourceIntervalIndex;
                for (var internalBreakpointIndex =
                        firstCrosswiseBreakpointIndex;
                    internalBreakpointIndex <= lastCrosswiseBreakpointIndex;
                    internalBreakpointIndex++)
                {
                    var internalBreakpointCoordinate =
                        modelBreakpointCoordinates[internalBreakpointIndex];
                    if (internalBreakpointCoordinate <=
                            bSideCornerProtectionRange
                                .MaximumProtectedCoordinate +
                            GeometryTolerance ||
                        internalBreakpointCoordinate >=
                            aSideCornerProtectionRange
                                .MinimumProtectedCoordinate -
                            GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            "大偏移中间断点只能位于连接基架扣除两端各一个排宽后的可搭接区内。");
                    }

                    largeOffsetRegion.CrosswiseBreakpointFractions.Add(
                        (internalBreakpointCoordinate -
                         connectorStartCoordinate) / connectorLength);
                }
            }

            var sourceIntervalStart =
                normalizedLengthAxisBreakpointCoordinates[
                    sourceIntervalIndex];
            var sourceIntervalEnd =
                normalizedLengthAxisBreakpointCoordinates[
                    lastSourceIntervalIndex + 1];
            var offsetDirectionSign =
                largeOffsetRegion.SignedWidthOffset > 0d ? 1d : -1d;
            foreach (var phaseRoute in largeOffsetRegion.PhaseRoutes)
            {
                phaseRoute.BSideLengthStartCoordinate =
                    sourceIntervalStart - busbarWidth / 2d;
                phaseRoute.BSideLengthEndCoordinate =
                    phaseRoute.TurnLengthCoordinate + busbarWidth / 2d;
                phaseRoute.CrosswiseStartWidthCoordinate =
                    phaseRoute.BSideWidthCoordinate -
                    offsetDirectionSign * busbarWidth / 2d;
                phaseRoute.CrosswiseEndWidthCoordinate =
                    phaseRoute.ASideWidthCoordinate +
                    offsetDirectionSign * busbarWidth / 2d;
                phaseRoute.ASideLengthStartCoordinate =
                    phaseRoute.TurnLengthCoordinate - busbarWidth / 2d;
                phaseRoute.ASideLengthEndCoordinate =
                    sourceIntervalEnd + busbarWidth / 2d;
            }

            largeOffsetRegion.SourceBreakpointIntervalIndex =
                sourceIntervalIndex;
            largeOffsetRegion.LastSourceBreakpointIntervalIndex =
                lastSourceIntervalIndex;
            AssignLargeOffsetRouteGroupIndexes(
                largeOffsetRegion,
                sourceIntervalIndex);
            return sourceIntervalIndex;
        }

        /// <summary>
        /// 大偏移换相默认占用转向前的 B 侧长度向段。三相转弯位置并不相同，
        /// 因此换相区间必须取三相中最短长度向段的公共范围；转向搭接及后续
        /// 宽度向、A 侧长度向路径仍由大偏移规划器原样生成。
        /// </summary>
        internal static void CompleteLargeOffsetPhaseChangeRouteGeometry(
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion,
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            double busbarWidth)
        {
            if (phaseChangeRegion == null)
            {
                throw new ArgumentNullException(nameof(phaseChangeRegion));
            }
            if (largeOffsetRegion == null ||
                largeOffsetRegion.SourceBreakpointIntervalIndex < 0 ||
                largeOffsetRegion.PhaseRoutes.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    "大偏移换相前必须先完成大偏移长度向路径规划。");
            }

            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            var commonTransitionStartCoordinate = largeOffsetRegion.PhaseRoutes
                .Max(phaseRoute => phaseRoute.BSideLengthStartCoordinate) +
                busbarWidth / 2d;
            var commonTransitionEndCoordinate = largeOffsetRegion.PhaseRoutes
                .Min(phaseRoute => phaseRoute.BSideLengthEndCoordinate) -
                busbarWidth / 2d;
            if (commonTransitionEndCoordinate <=
                commonTransitionStartCoordinate + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "大偏移转向前的最短长度向段没有可用换相空间。");
            }

            phaseChangeRegion.LeftFixedBreakpointCoordinate =
                commonTransitionStartCoordinate;
            phaseChangeRegion.RightFixedBreakpointCoordinate =
                commonTransitionEndCoordinate;
            phaseChangeRegion.SuppressedBreakpointIntervalIndex =
                largeOffsetRegion.SourceBreakpointIntervalIndex;
            phaseChangeRegion.UsesLargeOffsetBSide = true;
            var selectedBreakpointSpan = commonTransitionEndCoordinate -
                commonTransitionStartCoordinate;
            phaseChangeRegion.MinimumRecommendedBreakpointSpan =
                CalculatePhaseChangeMinimumRequiredBreakpointSpan(
                    phaseChangeRegion.InsulationClearance +
                    PhaseChangePreferredClearanceAddition,
                    phaseChangeRegion.BusbarWidth,
                    phaseChangeRegion.BusbarThickness,
                    phaseChangeRegion.BendAllowance,
                    false);

            FaceToWallPreviewTrace.Info(
                "大偏移换相默认段",
                "逻辑段=" +
                (largeOffsetRegion.SourceBreakpointIntervalIndex + 1) +
                "；公共起点=" +
                commonTransitionStartCoordinate.ToString("0.###") +
                "；最短转向前终点=" +
                commonTransitionEndCoordinate.ToString("0.###") +
                "；可用长度=" + selectedBreakpointSpan.ToString("0.###") +
                "；换相位于转向前长度段，转向搭接保持原规划");
        }

        /// <summary>
        /// 小偏移特殊基架本体两侧各外扩一个排宽，形成唯一不生成铜排的固定区间。
        /// 断点只需在区间外侧覆盖该禁区，禁区边界不绑定到断点；禁区内部禁止出现断点。
        /// </summary>
        internal static int CompleteSmallOffsetRouteGeometry(
            FaceToFaceSmallOffsetRegionPlan smallOffsetRegion,
            IReadOnlyList<double> modelBreakpointCoordinates,
            double busbarWidth)
        {
            if (smallOffsetRegion?.SpecialFrame == null)
            {
                throw new ArgumentNullException(nameof(smallOffsetRegion));
            }

            if (modelBreakpointCoordinates == null ||
                modelBreakpointCoordinates.Count < 4)
            {
                throw new InvalidOperationException(
                    "面对面小偏移至少需要 B 端、特殊段左右固定点和 A 端四个断点。");
            }

            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            for (var breakpointIndex = 0;
                breakpointIndex < modelBreakpointCoordinates.Count;
                breakpointIndex++)
            {
                var breakpointCoordinate =
                    modelBreakpointCoordinates[breakpointIndex];
                if (double.IsNaN(breakpointCoordinate) ||
                    double.IsInfinity(breakpointCoordinate) ||
                    breakpointIndex > 0 && breakpointCoordinate <=
                    modelBreakpointCoordinates[breakpointIndex - 1] +
                    GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "小偏移模型断点必须是严格递增的有效数值。");
                }
            }

            // 禁区由特殊基架 Box 外扩一个排宽固定计算，不能因为客户移动
            // 断点而改变。只要求断点区间完整覆盖禁区，禁区内部不允许再有断点。
            var suppressedIntervalStart =
                smallOffsetRegion.SpecialFrame.LengthMinimum - busbarWidth;
            var suppressedIntervalEnd =
                smallOffsetRegion.SpecialFrame.LengthMaximum + busbarWidth;
            var coveringIntervalIndexes = Enumerable.Range(
                    0,
                    modelBreakpointCoordinates.Count - 1)
                .Where(intervalIndex =>
                    modelBreakpointCoordinates[intervalIndex] <=
                        suppressedIntervalStart + GeometryTolerance &&
                    modelBreakpointCoordinates[intervalIndex + 1] >=
                        suppressedIntervalEnd - GeometryTolerance)
                .ToList();
            if (coveringIntervalIndexes.Count != 1)
            {
                throw new InvalidOperationException(
                    "面对面小偏移固定禁区必须由唯一一段母线覆盖；请将断点移到禁区外。");
            }

            var suppressedIntervalIndex = coveringIntervalIndexes[0];
            var breakpointInsideForbiddenZone = modelBreakpointCoordinates
                .Where((coordinate, coordinateIndex) =>
                    coordinateIndex > 0 &&
                    coordinateIndex < modelBreakpointCoordinates.Count - 1)
                .Any(coordinate =>
                    coordinate > suppressedIntervalStart + GeometryTolerance &&
                    coordinate < suppressedIntervalEnd - GeometryTolerance);
            if (breakpointInsideForbiddenZone)
            {
                throw new InvalidOperationException(
                    "面对面小偏移固定禁区内不能设置断点。");
            }

            smallOffsetRegion.SuppressedIntervalStartCoordinate =
                suppressedIntervalStart;
            smallOffsetRegion.SuppressedIntervalEndCoordinate =
                suppressedIntervalEnd;
            smallOffsetRegion.SuppressedBreakpointIntervalIndex =
                suppressedIntervalIndex;
            smallOffsetRegion.TransitionLeftBreakpointCoordinate =
                modelBreakpointCoordinates[suppressedIntervalIndex];
            smallOffsetRegion.TransitionRightBreakpointCoordinate =
                modelBreakpointCoordinates[suppressedIntervalIndex + 1];
            FaceToWallPreviewTrace.Info(
                "面对面小偏移固定空窗-对齐",
                "固定禁区=" +
                suppressedIntervalStart.ToString("0.###") + "～" +
                suppressedIntervalEnd.ToString("0.###") +
                " mm，覆盖断点段=" +
                (suppressedIntervalIndex + 1).ToString() +
                "，过渡段断点=" +
                smallOffsetRegion.TransitionLeftBreakpointCoordinate
                    .ToString("0.###") + "～" +
                smallOffsetRegion.TransitionRightBreakpointCoordinate
                    .ToString("0.###") + " mm");
            return suppressedIntervalIndex;
        }

        /// <summary>
        /// 从自动固定断点与特殊基架实际 Box 的左右净距中检测对齐误差。
        /// 只有唯一相邻区间完整包住特殊基架、区间中心与基架中心一致，并且
        /// 左右净距误差对称时才认可；检测出的误差供本次边界校验作为动态容差。
        /// </summary>
        internal static bool TryDetectSmallOffsetBreakpointAlignmentError(
            FaceToFaceFrameBox specialFrame,
            IReadOnlyList<double> modelBreakpointCoordinates,
            double busbarWidth,
            out int matchedIntervalIndex,
            out double detectedAlignmentError)
        {
            matchedIntervalIndex = -1;
            detectedAlignmentError = double.NaN;
            if (specialFrame == null ||
                modelBreakpointCoordinates == null ||
                modelBreakpointCoordinates.Count < 2 ||
                double.IsNaN(busbarWidth) ||
                double.IsInfinity(busbarWidth) ||
                busbarWidth <= 0d ||
                double.IsNaN(specialFrame.LengthMinimum) ||
                double.IsInfinity(specialFrame.LengthMinimum) ||
                double.IsNaN(specialFrame.LengthMaximum) ||
                double.IsInfinity(specialFrame.LengthMaximum) ||
                specialFrame.LengthMaximum <=
                specialFrame.LengthMinimum + GeometryTolerance)
            {
                return false;
            }

            var specialFrameLengthCenter = specialFrame.LengthCenter;
            var alignmentCandidates = Enumerable.Range(
                    0,
                    modelBreakpointCoordinates.Count - 1)
                .Select(intervalIndex =>
                {
                    var intervalStart =
                        modelBreakpointCoordinates[intervalIndex];
                    var intervalEnd =
                        modelBreakpointCoordinates[intervalIndex + 1];
                    var leftClearance =
                        specialFrame.LengthMinimum - intervalStart;
                    var rightClearance =
                        intervalEnd - specialFrame.LengthMaximum;
                    return new
                    {
                        IntervalIndex = intervalIndex,
                        LeftClearance = leftClearance,
                        RightClearance = rightClearance,
                        IntervalCenter = (intervalStart + intervalEnd) / 2d
                    };
                })
                .Where(alignmentCandidate =>
                    alignmentCandidate.LeftClearance > GeometryTolerance &&
                    alignmentCandidate.RightClearance > GeometryTolerance &&
                    Math.Abs(alignmentCandidate.IntervalCenter -
                        specialFrameLengthCenter) <= ModelAlignmentTolerance &&
                    Math.Abs(alignmentCandidate.LeftClearance -
                        alignmentCandidate.RightClearance) <=
                    ModelAlignmentTolerance)
                .ToList();
            if (alignmentCandidates.Count != 1)
            {
                return false;
            }

            var matchedAlignmentCandidate = alignmentCandidates[0];
            matchedIntervalIndex = matchedAlignmentCandidate.IntervalIndex;
            detectedAlignmentError = Math.Max(
                Math.Abs(matchedAlignmentCandidate.LeftClearance - busbarWidth),
                Math.Abs(matchedAlignmentCandidate.RightClearance - busbarWidth));
            return !double.IsNaN(detectedAlignmentError) &&
                   !double.IsInfinity(detectedAlignmentError);
        }

        internal static void CompletePhaseChangeRouteGeometry(
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion,
            IReadOnlyList<double> modelBreakpointCoordinates,
            int? requestedPhaseChangeIntervalIndex = null)
        {
            if (phaseChangeRegion == null)
            {
                throw new ArgumentNullException(nameof(phaseChangeRegion));
            }
            if (modelBreakpointCoordinates == null ||
                modelBreakpointCoordinates.Count < 4)
            {
                throw new InvalidOperationException(
                    "面对面换相至少需要 B/A 端断点和中间基架内的两个固定断点。");
            }
            if (requestedPhaseChangeIntervalIndex.HasValue &&
                (requestedPhaseChangeIntervalIndex.Value < 0 ||
                 requestedPhaseChangeIntervalIndex.Value >
                 modelBreakpointCoordinates.Count - 2))
            {
                throw new InvalidOperationException(
                    "客制换相段必须落在基架排对应的断点区间内。");
            }

            var phaseChangeIntervalIndex = requestedPhaseChangeIntervalIndex ??
                Enumerable.Range(
                        0,
                        modelBreakpointCoordinates.Count - 2)
                    .OrderBy(intervalIndex => Math.Abs(
                        (modelBreakpointCoordinates[intervalIndex] +
                         modelBreakpointCoordinates[intervalIndex + 1]) / 2d -
                        phaseChangeRegion.MiddleFrame.LengthCenter))
                    .First();
            var selectedLeftBreakpointCoordinate =
                modelBreakpointCoordinates[phaseChangeIntervalIndex];
            var selectedRightBreakpointCoordinate =
                modelBreakpointCoordinates[phaseChangeIntervalIndex + 1];
            FaceToWallPreviewTrace.Info(
                "面对面换相断点选择",
                "区段序号=" + (phaseChangeIntervalIndex + 1) +
                "；左断点=" + selectedLeftBreakpointCoordinate
                    .ToString("0.###") +
                "；右断点=" + selectedRightBreakpointCoordinate
                    .ToString("0.###") +
                "；区段长=" +
                (selectedRightBreakpointCoordinate -
                 selectedLeftBreakpointCoordinate).ToString("0.###") +
                "；最低推荐长度=" +
                phaseChangeRegion.MinimumRecommendedBreakpointSpan
                    .ToString("0.###"));
            if (selectedRightBreakpointCoordinate <=
                selectedLeftBreakpointCoordinate + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "面对面换相段的两个中间断点顺序无效。");
            }

            var globalFrameLengthOrigin =
                phaseChangeRegion.LeftFixedBreakpointCoordinate -
                phaseChangeRegion.LeftFixedBreakpointPosition;
            phaseChangeRegion.LeftFixedBreakpointCoordinate =
                selectedLeftBreakpointCoordinate;
            phaseChangeRegion.RightFixedBreakpointCoordinate =
                selectedRightBreakpointCoordinate;
            phaseChangeRegion.LeftFixedBreakpointPosition = Math.Round(
                selectedLeftBreakpointCoordinate - globalFrameLengthOrigin,
                1);
            phaseChangeRegion.RightFixedBreakpointPosition = Math.Round(
                selectedRightBreakpointCoordinate - globalFrameLengthOrigin,
                1);
            phaseChangeRegion.SuppressedBreakpointIntervalIndex =
                phaseChangeIntervalIndex;
            var selectedBreakpointSpan = selectedRightBreakpointCoordinate -
                selectedLeftBreakpointCoordinate;
            var selectedPhaseChangeIsBridge = IsBridgeRouteGroup(
                phaseChangeIntervalIndex);
            if (phaseChangeRegion.BusbarWidth > GeometryTolerance)
            {
                var preferredPhaseChangeClearance =
                    phaseChangeRegion.InsulationClearance +
                    PhaseChangePreferredClearanceAddition;
                phaseChangeRegion.MinimumRecommendedBreakpointSpan =
                    CalculatePhaseChangeMinimumRequiredBreakpointSpan(
                        preferredPhaseChangeClearance,
                        phaseChangeRegion.BusbarWidth,
                        phaseChangeRegion.BusbarThickness,
                        phaseChangeRegion.BendAllowance,
                        selectedPhaseChangeIsBridge);
                var selectedTransitionLength = selectedBreakpointSpan +
                    phaseChangeRegion.BusbarWidth;
                var requiredBridgeTerminalLength = 2d *
                    CalculatePhaseChangeBridgeTerminalLength(
                        phaseChangeRegion.BusbarWidth);
                if (selectedPhaseChangeIsBridge &&
                    selectedTransitionLength <=
                    requiredBridgeTerminalLength +
                    ModelAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        "错误：桥式换相段含搭接长度仅 " +
                        selectedTransitionLength.ToString("0.###") +
                        " mm，不足两端固定桥式单侧三点端头所需的 " +
                        requiredBridgeTerminalLength.ToString("0.###") +
                        " mm；请增大换相段断点间距。 ");
                }
            }
            if (selectedBreakpointSpan + ModelAlignmentTolerance <
                phaseChangeRegion.MinimumRecommendedBreakpointSpan)
            {
                FaceToWallPreviewTrace.Info(
                    "面对面换相段长度提示",
                    "客户调整后的换相断点间距=" +
                    selectedBreakpointSpan.ToString("0.###") +
                    " mm，小于推荐值=" +
                    phaseChangeRegion.MinimumRecommendedBreakpointSpan
                        .ToString("0.###") +
                    " mm；按客户断点继续规划。");
            }
        }

        /// <summary>
        /// 大偏移将原一个逻辑区间固定展开成 B 长度向、宽度向、A 长度向三组；
        /// 因而三组直接占用该区间原全局序号及其后两个序号。
        /// </summary>
        internal static void AssignLargeOffsetRouteGroupIndexes(
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            int sourceBreakpointIntervalIndex)
        {
            if (largeOffsetRegion == null)
            {
                throw new ArgumentNullException(nameof(largeOffsetRegion));
            }

            if (sourceBreakpointIntervalIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceBreakpointIntervalIndex));
            }

            largeOffsetRegion.BSideLengthGroupIndex =
                sourceBreakpointIntervalIndex;
            largeOffsetRegion.CrosswiseGroupIndexes.Clear();
            var crosswiseGroupCount =
                largeOffsetRegion.CrosswiseBreakpointFractions.Count + 1;
            for (var crosswiseGroupOffset = 0;
                crosswiseGroupOffset < crosswiseGroupCount;
                crosswiseGroupOffset++)
            {
                largeOffsetRegion.CrosswiseGroupIndexes.Add(
                    sourceBreakpointIntervalIndex + 1 +
                    crosswiseGroupOffset);
            }

            largeOffsetRegion.CrosswiseGroupIndex =
                largeOffsetRegion.CrosswiseGroupIndexes[0];
            largeOffsetRegion.ASideLengthGroupIndex =
                sourceBreakpointIntervalIndex + crosswiseGroupCount + 1;
        }

        /// <summary>
        /// 判断逻辑段是否为桥式段。面对面换相和设置界面共用同一奇偶规则。
        /// </summary>
        public static bool IsBridgeRouteGroup(int routeGroupIndex)
        {
            if (routeGroupIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(routeGroupIndex));
            }

            return routeGroupIndex % 2 == 1;
        }

        private static FaceToFaceSidePlanningContext CreateSidePlanningContext(
            string sideLabel,
            AssemblyPartCatalog partCatalog,
            BusbarBridgeAxisSetting sideAxisSetting,
            IReadOnlyList<ModeledBusbar> sideExtensionBusbars,
            IReadOnlyList<InsulatorConnectionInfo> availableConnectionInsulators,
            double cabinetBreakpointModelCoordinate,
            int singlePhaseStackCount,
            double bendingRadius,
            double bendAllowance,
            int terminalRouteLayerOffsetInThicknesses,
            Component2 offsetBaseComponent = null,
            Component2 preferredFirstFrameComponent = null,
            IReadOnlyList<InsulatorConnectionInfo>
                orderedBaseInsulatorOverride = null,
            IReadOnlyList<InsulatorConnectionInfo>
                orderedFrameInsulatorOverride = null,
            IReadOnlyList<int>
                logicalToPhysicalPhaseIndexes = null,
            IReadOnlyList<double>
                phaseCabinetBreakpointModelCoordinates = null,
            bool useSideToSideFixedVerticalFrameLap = false,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            bool useActualInsulatorPositions = false,
            Component2 baseInsulatorHostComponent = null,
            FaceToFaceCabinetEndpointGeometry endpointGeometry = null)
        {
            var physicalPhaseGroups =
                FaceToWallModelBusbarPlanner.GroupExtensionBusbarsByPhase(
                    sideExtensionBusbars,
                    sideAxisSetting.WidthVector);
            if (logicalToPhysicalPhaseIndexes != null &&
                (logicalToPhysicalPhaseIndexes.Count != PhaseCount ||
                 logicalToPhysicalPhaseIndexes.Any(physicalPhaseIndex =>
                     physicalPhaseIndex < 0 ||
                     physicalPhaseIndex >= PhaseCount) ||
                 logicalToPhysicalPhaseIndexes.Distinct().Count() != PhaseCount))
            {
                throw new InvalidOperationException(
                    sideLabel + "的 A/B 柜逻辑相位映射不完整。");
            }
            if (phaseCabinetBreakpointModelCoordinates != null &&
                (phaseCabinetBreakpointModelCoordinates.Count != PhaseCount ||
                 phaseCabinetBreakpointModelCoordinates.Any(
                     phaseCoordinate => double.IsNaN(phaseCoordinate) ||
                                        double.IsInfinity(phaseCoordinate))))
            {
                throw new InvalidOperationException(
                    sideLabel + "的三相中间排搭接坐标不完整。");
            }
            var phaseGroups = logicalToPhysicalPhaseIndexes == null
                ? physicalPhaseGroups
                : logicalToPhysicalPhaseIndexes
                    .Select(physicalPhaseIndex =>
                        physicalPhaseGroups[physicalPhaseIndex])
                    .ToList();
            var extensionReferencePoint = BusbarGeometryUtility.Average(
                phaseGroups.SelectMany(phaseGroup => phaseGroup)
                    .Select(busbar => busbar.TerminalSegment.P2));
            var baseComponent = offsetBaseComponent ??
                partCatalog.FindNearestComponent(
                    SystemPartType_e.Base,
                    extensionReferencePoint);
            if (baseComponent == null)
            {
                throw new InvalidOperationException(
                    sideLabel + "附近没有识别到基座。");
            }

            var baseCenter =
                BusbarGeometryUtility.GetComponentCenter(baseComponent);
            if (baseCenter == null)
            {
                throw new InvalidOperationException(
                    "无法读取" + sideLabel + "基座中心。");
            }

            var resolvedBaseInsulatorHostComponent =
                baseInsulatorHostComponent ?? baseComponent;
            var baseInsulatorHostTypeLabel =
                baseInsulatorHostComponent == null
                    ? sideLabel + "基座"
                    : sideLabel + "增高基座";
            if (baseInsulatorHostComponent != null)
            {
                FaceToWallPreviewTrace.Info(
                    "柜体侧基座职责分离",
                    sideLabel + "：绝缘子宿主=" +
                    BusbarGeometryUtility.SafeName(
                        resolvedBaseInsulatorHostComponent) +
                    "；首个基架距离起点（主体基座）=" +
                    BusbarGeometryUtility.SafeName(baseComponent));
            }
            var baseInsulators = orderedBaseInsulatorOverride?.ToList() ??
                FaceToWallModelBusbarPlanner.SelectHostInsulators(
                    partCatalog,
                    resolvedBaseInsulatorHostComponent,
                    availableConnectionInsulators,
                    sideAxisSetting.LengthVector,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    baseInsulatorHostTypeLabel,
                    PhaseCount,
                    useActualInsulatorPositions);
            if (baseInsulators.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    sideLabel + "基座没有识别到完整的三相绝缘子。");
            }

            var baseInsulatorNames = new HashSet<string>(
                baseInsulators.Select(baseInsulator =>
                    BusbarGeometryUtility.SafeName(baseInsulator.Component)),
                StringComparer.OrdinalIgnoreCase);
            Component2 firstFrameComponent = null;
            List<InsulatorConnectionInfo> frameInsulators = null;
            double breakpointOriginLengthCoordinate;
            double? plannedConnectionSupportHeightCoordinate = null;
            if (endpointGeometry != null)
            {
                if (!BusbarGeometryUtility.TryGetComponentProjectionRange(
                        baseComponent,
                        sideAxisSetting.LengthVector,
                        out var ignoredBaseMinimumLengthCoordinate,
                        out var baseBridgeEdgeLengthCoordinate) ||
                    !BusbarGeometryUtility.TryGetComponentProjectionRange(
                        baseComponent,
                        sideAxisSetting.HeightVector,
                        out var ignoredBaseMinimumHeightCoordinate,
                        out var baseTopHeightCoordinate))
                {
                    throw new InvalidOperationException(
                        "无法读取" + sideLabel +
                        "基座出桥边界或顶部高度，不能计算端部排。 ");
                }

                breakpointOriginLengthCoordinate =
                    baseBridgeEdgeLengthCoordinate;
                plannedConnectionSupportHeightCoordinate =
                    endpointGeometry.CalculateInsulatorConnectionHeight(
                        baseTopHeightCoordinate);
                FaceToWallPreviewTrace.Info(
                    "柜体端部排-基座几何定位",
                    sideLabel +
                    "；基座出桥边=" +
                    baseBridgeEdgeLengthCoordinate.ToString("0.###") +
                    "；基座顶=" + baseTopHeightCoordinate.ToString("0.###") +
                    "；开孔长度=" +
                    endpointGeometry.BaseOpeningHeight.ToString("0.###") +
                    "；基座支架=" +
                    endpointGeometry.BaseSupportBracketHeight.ToString("0.###") +
                    "；绝缘子+支架=" +
                    endpointGeometry.InsulatorWithBracketHeight.ToString("0.###") +
                    "；连接面高度=" +
                    plannedConnectionSupportHeightCoordinate.Value.ToString("0.###"));
            }
            else
            {
                // 侧对侧专用路径仍复用已确认的首基架及其绝缘子。
                var hasTrustedFrameInsulatorOverride =
                    preferredFirstFrameComponent != null &&
                    orderedFrameInsulatorOverride != null;
                firstFrameComponent = hasTrustedFrameInsulatorOverride
                    ? preferredFirstFrameComponent
                    : FaceToWallModelBusbarPlanner
                        .SelectNearestFrameHostingInsulators(
                            partCatalog,
                            baseCenter,
                            availableConnectionInsulators,
                            sideAxisSetting.HeightVector,
                            baseInsulatorNames,
                            PhaseCount,
                            useActualInsulatorPositions);
                if (firstFrameComponent == null)
                {
                    throw new InvalidOperationException(
                        sideLabel +
                        "附近没有识别到包含三相绝缘子的首个基架。");
                }
                if (preferredFirstFrameComponent != null &&
                    !string.Equals(
                        BusbarGeometryUtility.SafeName(
                            preferredFirstFrameComponent),
                        BusbarGeometryUtility.SafeName(firstFrameComponent),
                        StringComparison.OrdinalIgnoreCase))
                {
                    FaceToWallPreviewTrace.Warning(
                        "柜体侧首个基架-忽略非最近候选",
                        sideLabel + "传入的首基架候选“" +
                        BusbarGeometryUtility.SafeName(
                            preferredFirstFrameComponent) +
                        "”不是最近基架，实际使用“" +
                        BusbarGeometryUtility.SafeName(firstFrameComponent) +
                        "”。");
                }
                if (!BusbarGeometryUtility.TryGetComponentProjectionRange(
                        firstFrameComponent,
                        sideAxisSetting.LengthVector,
                        out breakpointOriginLengthCoordinate,
                        out var ignoredFirstFrameMaximumLengthCoordinate))
                {
                    throw new InvalidOperationException(
                        "无法读取" + sideLabel + "首个基架长度范围。");
                }

                frameInsulators = orderedFrameInsulatorOverride?.ToList() ??
                    FaceToWallModelBusbarPlanner.SelectHostInsulators(
                        partCatalog,
                        firstFrameComponent,
                        availableConnectionInsulators,
                        sideAxisSetting.HeightVector,
                        baseInsulatorNames,
                        sideLabel + "首个基架",
                        PhaseCount,
                        useActualInsulatorPositions);
                if (frameInsulators.Count != PhaseCount)
                {
                    throw new InvalidOperationException(
                        sideLabel + "首个基架没有识别到完整的三相绝缘子。");
                }
            }

            var phaseWidthCoordinates = phaseGroups
                .Select(phaseGroup => phaseGroup.Average(busbar =>
                    BusbarGeometryUtility.Project(
                        busbar.TerminalSegment.P2,
                        sideAxisSetting.WidthVector)))
                .ToList();
            var orderedBaseInsulators = orderedBaseInsulatorOverride != null
                ? baseInsulators
                : FaceToWallModelBusbarPlanner.MatchInsulatorsToPhases(
                    baseInsulators,
                    phaseWidthCoordinates,
                    sideAxisSetting.WidthVector);
            var orderedFrameInsulators = endpointGeometry != null
                ? null
                : orderedFrameInsulatorOverride != null
                    ? frameInsulators
                    : FaceToWallModelBusbarPlanner.MatchInsulatorsToPhases(
                        frameInsulators,
                        phaseWidthCoordinates,
                        sideAxisSetting.WidthVector);

            var result = new FaceToFaceSidePlanningContext
            {
                SideLabel = sideLabel,
                BaseComponent = baseComponent,
                FirstFrameComponent = firstFrameComponent,
                UseActualInsulatorPositions = useActualInsulatorPositions,
                ExtensionBusbars = sideExtensionBusbars.ToList()
            };
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var phaseCabinetBreakpointModelCoordinate =
                    phaseCabinetBreakpointModelCoordinates == null
                        ? cabinetBreakpointModelCoordinate
                        : phaseCabinetBreakpointModelCoordinates[phaseIndex];
                var relativeCabinetBreakpoint =
                    endpointGeometry == null
                        ? phaseCabinetBreakpointModelCoordinate -
                          breakpointOriginLengthCoordinate
                        // sideAxisSetting 在两柜侧都从基座指向桥体。界面定义
                        // 正值伸入基座、负值向桥体伸出，因此沿该局部轴取反。
                        : -endpointGeometry.BreakpointOffsetFromBaseBridgeEdge;
                var cabinetBreakpointPair = new[]
                {
                    relativeCabinetBreakpoint,
                    relativeCabinetBreakpoint + 1d
                };
                var phaseGroupsResult =
                    FaceToWallModelBusbarPlanner.BuildPhaseStack(
                        phaseGroups[phaseIndex],
                        orderedBaseInsulators[phaseIndex],
                        orderedFrameInsulators?[phaseIndex],
                        sideAxisSetting,
                        breakpointOriginLengthCoordinate,
                        cabinetBreakpointPair,
                        0d,
                        1d,
                        phaseIndex,
                        singlePhaseStackCount,
                        bendingRadius,
                        bendAllowance,
                        false,
                        terminalRouteLayerOffsetInThicknesses,
                        null,
                        null,
                        null,
                        useSideToSideFixedVerticalFrameLap,
                        generatedBusbarWidth,
                        generatedBusbarThickness,
                        out var phaseRouteContext,
                        useActualInsulatorPositions,
                        false,
                        plannedConnectionSupportHeightCoordinate);
                if (phaseGroupsResult.Count != 1 ||
                    phaseRouteContext == null)
                {
                    throw new InvalidOperationException(
                        sideLabel + FaceToWallModelBusbarPlanner.GetPhaseName(
                            phaseIndex) +
                        "相没有形成唯一柜顶五点搭接组。");
                }

                result.CabinetConnectionBusbarsByPhase.Add(
                    phaseGroupsResult[0].ToList());
                result.PhaseRouteContexts.Add(phaseRouteContext);
            }

            return result;
        }

        private static IReadOnlyList<FaceToFaceRouteGroupDefinition>
            BuildRouteDefinitions(
            IReadOnlyList<double> modelBreakpointCoordinates,
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            BusbarBridgeAxisSetting axisSetting,
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            FaceToFaceSmallOffsetRegionPlan smallOffsetRegion,
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion)
        {
            ValidateSpecialRouteRegionCompatibility(
                largeOffsetRegion,
                smallOffsetRegion,
                phaseChangeRegion);

            var result = new List<FaceToFaceRouteGroupDefinition>();
            var combinesPhaseChangeWithLargeOffsetBSide =
                largeOffsetRegion != null &&
                phaseChangeRegion != null &&
                phaseChangeRegion.UsesLargeOffsetBSide &&
                phaseChangeRegion.SuppressedBreakpointIntervalIndex ==
                    largeOffsetRegion.SourceBreakpointIntervalIndex;
            for (var intervalIndex = 0;
                intervalIndex < modelBreakpointCoordinates.Count - 1;
                intervalIndex++)
            {
                if (smallOffsetRegion != null &&
                    intervalIndex ==
                    smallOffsetRegion.SuppressedBreakpointIntervalIndex)
                {
                    continue;
                }
                if (phaseChangeRegion != null &&
                    intervalIndex ==
                    phaseChangeRegion.SuppressedBreakpointIntervalIndex &&
                    !combinesPhaseChangeWithLargeOffsetBSide)
                {
                    continue;
                }

                if (largeOffsetRegion != null &&
                    intervalIndex ==
                        largeOffsetRegion.SourceBreakpointIntervalIndex)
                {
                    var bSideLengthGroupIndex =
                        largeOffsetRegion.BSideLengthGroupIndex >= 0
                            ? largeOffsetRegion.BSideLengthGroupIndex
                            : intervalIndex;
                    if (!combinesPhaseChangeWithLargeOffsetBSide)
                    {
                        result.Add(CreateLargeOffsetLengthDefinition(
                            bSideLengthGroupIndex,
                            largeOffsetRegion,
                            bCabinetContext,
                            axisSetting,
                            true));
                    }
                    var crosswiseSegmentCount =
                        largeOffsetRegion.CrosswiseBreakpointFractions.Count +
                        1;
                    for (var crosswiseSegmentIndex = 0;
                        crosswiseSegmentIndex < crosswiseSegmentCount;
                        crosswiseSegmentIndex++)
                    {
                        var crosswiseGroupIndex =
                            crosswiseSegmentIndex <
                            largeOffsetRegion.CrosswiseGroupIndexes.Count
                                ? largeOffsetRegion.CrosswiseGroupIndexes[
                                    crosswiseSegmentIndex]
                                : bSideLengthGroupIndex + 1 +
                                  crosswiseSegmentIndex;
                        result.Add(CreateLargeOffsetCrosswiseDefinition(
                            crosswiseGroupIndex,
                            largeOffsetRegion,
                            bCabinetContext,
                            axisSetting,
                            crosswiseSegmentIndex));
                    }

                    var aSideLengthGroupIndex =
                        largeOffsetRegion.ASideLengthGroupIndex >= 0
                            ? largeOffsetRegion.ASideLengthGroupIndex
                            : bSideLengthGroupIndex +
                              crosswiseSegmentCount + 1;
                    result.Add(CreateLargeOffsetLengthDefinition(
                        aSideLengthGroupIndex,
                        largeOffsetRegion,
                        bCabinetContext,
                        axisSetting,
                        false));
                    continue;
                }

                if (largeOffsetRegion != null &&
                    intervalIndex >
                        largeOffsetRegion.SourceBreakpointIntervalIndex &&
                    intervalIndex <=
                        largeOffsetRegion.LastSourceBreakpointIntervalIndex)
                {
                    continue;
                }

                var useACabinetWidthCoordinates =
                    smallOffsetRegion != null
                        ? intervalIndex >
                          smallOffsetRegion
                              .SuppressedBreakpointIntervalIndex
                        : (largeOffsetRegion != null &&
                           intervalIndex >
                           largeOffsetRegion
                               .LastSourceBreakpointIntervalIndex) ||
                          (phaseChangeRegion != null &&
                           intervalIndex >
                           phaseChangeRegion
                               .SuppressedBreakpointIntervalIndex);
                var phaseOrderAlreadyChanged =
                    phaseChangeRegion != null &&
                    intervalIndex >
                    phaseChangeRegion.SuppressedBreakpointIntervalIndex;
                // 小偏移与换相是两个独立状态：前者决定当前路径位于 A/B
                // 哪一侧，后者决定逻辑相序是否已经 A/C 对调。只有两个状态
                // 不一致时，当前几何侧的物理相位索引才需要反序读取。
                var reverseSmallOffsetPhysicalPhaseOrder =
                    smallOffsetRegion != null &&
                    phaseChangeRegion != null &&
                    useACabinetWidthCoordinates != phaseOrderAlreadyChanged;
                var useACabinetRouteContext =
                    useACabinetWidthCoordinates &&
                    (smallOffsetRegion != null ||
                     phaseChangeRegion != null);
                var routeReferenceContext = useACabinetRouteContext
                        ? aCabinetContext
                        : bCabinetContext;
                var logicalRouteGroupIndex = ResolveLogicalRouteGroupIndex(
                    result.Count,
                    intervalIndex,
                    smallOffsetRegion,
                    phaseChangeRegion);
                if (largeOffsetRegion != null)
                {
                    logicalRouteGroupIndex =
                        ResolveLogicalRouteGroupIndex(
                            intervalIndex,
                            largeOffsetRegion,
                            logicalRouteGroupIndex);
                }
                result.Add(CreateStraightLengthDefinition(
                    logicalRouteGroupIndex,
                    modelBreakpointCoordinates[intervalIndex],
                    modelBreakpointCoordinates[intervalIndex + 1],
                    routeReferenceContext,
                    useACabinetWidthCoordinates
                        ? aCabinetContext
                        : bCabinetContext,
                    axisSetting,
                    reverseSmallOffsetPhysicalPhaseOrder));
            }

            return result.AsReadOnly();
        }

        /// <summary>
        /// Validate that special route regions do not overlap.  Large-offset
        /// regions remain exclusive; a small-offset region and a phase-change
        /// region may intentionally share one scanned window and are emitted
        /// by the combined small-offset phase-change template.
        /// </summary>
        private static void ValidateSpecialRouteRegionCompatibility(
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            FaceToFaceSmallOffsetRegionPlan smallOffsetRegion,
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion)
        {
            if (largeOffsetRegion != null &&
                smallOffsetRegion != null &&
                IsIntervalInsideInclusive(
                    smallOffsetRegion.SuppressedBreakpointIntervalIndex,
                    largeOffsetRegion.SourceBreakpointIntervalIndex,
                    largeOffsetRegion.LastSourceBreakpointIntervalIndex))
            {
                throw new InvalidOperationException(
                    "大偏移区间与小偏移过渡区间不能重叠。请调整基架或断点。 ");
            }

            if (largeOffsetRegion != null &&
                phaseChangeRegion != null &&
                IsIntervalInsideInclusive(
                    phaseChangeRegion.SuppressedBreakpointIntervalIndex,
                    largeOffsetRegion.SourceBreakpointIntervalIndex,
                    largeOffsetRegion.LastSourceBreakpointIntervalIndex) &&
                !phaseChangeRegion.UsesLargeOffsetBSide)
            {
                throw new InvalidOperationException(
                    "大偏移区间与换相区间不能重叠。当前换相路径缺少独立可用空间。 ");
            }

            // 小偏移与换相落在同一窗口时由组合模板一次生成；落在不同
            // 窗口时仍可由各自规划器独立生成，因此这里不再互相排斥。
        }

        private static bool IsCombinedSmallOffsetPhaseChange(
            FaceToFaceSmallOffsetRegionPlan smallOffsetRegion,
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion)
        {
            if (smallOffsetRegion?.SpecialFrame == null ||
                phaseChangeRegion?.MiddleFrame == null)
            {
                return false;
            }

            if (smallOffsetRegion.SuppressedBreakpointIntervalIndex >= 0 &&
                phaseChangeRegion.SuppressedBreakpointIntervalIndex >= 0)
            {
                return smallOffsetRegion.SuppressedBreakpointIntervalIndex ==
                       phaseChangeRegion.SuppressedBreakpointIntervalIndex;
            }

            return ReferenceEquals(
                       smallOffsetRegion.SpecialFrame,
                       phaseChangeRegion.MiddleFrame) ||
                   smallOffsetRegion.SpecialFrame.Component != null &&
                   ReferenceEquals(
                       smallOffsetRegion.SpecialFrame.Component,
                       phaseChangeRegion.MiddleFrame.Component) ||
                   Math.Abs(
                       smallOffsetRegion.SpecialFrame.LengthCenter -
                       phaseChangeRegion.MiddleFrame.LengthCenter) <=
                   ModelAlignmentTolerance &&
                   string.Equals(
                       smallOffsetRegion.SpecialFrame.ComponentName,
                       phaseChangeRegion.MiddleFrame.ComponentName,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIntervalInsideInclusive(
            int intervalIndex,
            int minimumIntervalIndex,
            int maximumIntervalIndex)
        {
            return intervalIndex >= 0 &&
                   minimumIntervalIndex >= 0 &&
                   maximumIntervalIndex >= minimumIntervalIndex &&
                   intervalIndex >= minimumIntervalIndex &&
                   intervalIndex <= maximumIntervalIndex;
        }

        private static int ReversePhaseIndex(int logicalPhaseIndex)
        {
            if (logicalPhaseIndex < 0 || logicalPhaseIndex >= PhaseCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalPhaseIndex));
            }

            return PhaseCount - 1 - logicalPhaseIndex;
        }

        private static int ResolveLogicalRouteGroupIndex(
            int sourceBreakpointIntervalIndex,
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            int fallbackRouteGroupIndex)
        {
            if (largeOffsetRegion == null)
            {
                return fallbackRouteGroupIndex;
            }

            if (sourceBreakpointIntervalIndex >=
                    largeOffsetRegion.SourceBreakpointIntervalIndex &&
                sourceBreakpointIntervalIndex <=
                    largeOffsetRegion.LastSourceBreakpointIntervalIndex)
            {
                throw new InvalidOperationException(
                    "大偏移区间内部不能再生成普通长度段。 ");
            }

            // A/B 两个长度段和宽度向中间段相对于原逻辑区间额外占用
            // 两个组号；后续普通段必须保留这一偏移，不能使用 result.Count。
            return sourceBreakpointIntervalIndex >
                       largeOffsetRegion.LastSourceBreakpointIntervalIndex
                ? sourceBreakpointIntervalIndex + 2
                : sourceBreakpointIntervalIndex;
        }

        internal static int ResolveLogicalRouteGroupIndex(
            int generatedRouteGroupCount,
            int sourceBreakpointIntervalIndex,
            FaceToFaceSmallOffsetRegionPlan smallOffsetRegion,
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion = null)
        {
            if (generatedRouteGroupCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(generatedRouteGroupCount));
            }
            if (sourceBreakpointIntervalIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceBreakpointIntervalIndex));
            }

            // 小偏移空窗不进入正式生成组，但必须保留其原始断点区间序号，
            // 这样空窗后的 A 侧仍按完整的“直排/桥式”逻辑序列交替。
            return smallOffsetRegion == null && phaseChangeRegion == null
                ? generatedRouteGroupCount
                : sourceBreakpointIntervalIndex;
        }

        internal static FaceToFaceSmallOffsetTransitionKind_e
            ResolveSmallOffsetTransitionKind(double absoluteWidthOffset)
        {
            if (double.IsNaN(absoluteWidthOffset) ||
                double.IsInfinity(absoluteWidthOffset) ||
                absoluteWidthOffset <= GeometryTolerance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(absoluteWidthOffset),
                    "小偏移过渡排的宽向偏移必须是有限正数。");
            }

            return ResolveSmallOffsetTransitionKindByBusbarWidth(
                absoluteWidthOffset);
        }

        private static FaceToFaceSmallOffsetTransitionKind_e
            ResolveSmallOffsetTransitionKindByBusbarWidth(
            double busbarWidth)
        {
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            return busbarWidth > SmallOffsetMilledBarWidthThreshold
                ? FaceToFaceSmallOffsetTransitionKind_e.MilledBar
                : FaceToFaceSmallOffsetTransitionKind_e.VerticalBend;
        }

        public static FaceToFaceSmallOffsetTransitionKind_e
            ResolveSmallOffsetTransitionKind(
            double absoluteWidthOffset,
            double commonBusbarWidth,
            bool useMilledBarForLargeSmallOffset)
        {
            // 保留旧调用签名；工艺只由偏移量与排宽决定，旧开关不再生效。
            return ResolveSmallOffsetTransitionKind(absoluteWidthOffset, commonBusbarWidth);
        }

        public static FaceToFaceSmallOffsetTransitionKind_e
            ResolveSmallOffsetTransitionKind(
            double absoluteWidthOffset,
            double commonBusbarWidth)
        {
            ValidatePositiveFinite(commonBusbarWidth, nameof(commonBusbarWidth));
            if (double.IsNaN(absoluteWidthOffset) ||
                double.IsInfinity(absoluteWidthOffset) ||
                absoluteWidthOffset <= GeometryTolerance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(absoluteWidthOffset),
                    "小偏移过渡排的宽向偏移必须是有限正数。");
            }

            // 等于一个排宽仍按排宽选择立弯/铣排；超过排宽才采用中间搭接排。
            return absoluteWidthOffset >
                    commonBusbarWidth + GeometryTolerance
                ? FaceToFaceSmallOffsetTransitionKind_e
                    .MiddleParallelConnection
                : ResolveSmallOffsetTransitionKindByBusbarWidth(commonBusbarWidth);
        }

        private static double ReadCommonPhaseSpacingFromEndpoints(
            IReadOnlyList<ModeledBusbar> bCabinetBusbars,
            IReadOnlyList<ModeledBusbar> aCabinetBusbars,
            Vector3d widthVector)
        {
            var bGaps = ReadPhaseGaps(bCabinetBusbars, widthVector, "B柜");
            var aGaps = ReadPhaseGaps(aCabinetBusbars, widthVector, "A柜");
            var allGaps = bGaps.Concat(aGaps).ToArray();
            var phaseSpacing = allGaps.Average();
            if (allGaps.Any(gap =>
                    Math.Abs(gap - phaseSpacing) > ModelAlignmentTolerance))
            {
                throw new InvalidOperationException(
                    "A/B柜固定端部的三相中心距不一致，不能生成统一路径。");
            }

            return phaseSpacing;
        }

        private static double[] ReadPhaseGaps(
            IReadOnlyList<ModeledBusbar> busbars,
            Vector3d widthVector,
            string sideName)
        {
            var phaseCoordinates = busbars
                .Where(busbar => busbar != null
                    && busbar.Phase != BusbarPhase_e.Unknow)
                .GroupBy(busbar => busbar.Phase)
                .Select(group => group.Average(busbar =>
                    BusbarGeometryUtility.Project(
                        busbar.CenterPoint,
                        widthVector)))
                .OrderBy(value => value)
                .ToArray();
            if (phaseCoordinates.Length != PhaseCount)
            {
                throw new InvalidOperationException(
                    sideName + "固定端部未能唯一识别A/B/C三相中心，不能计算相间距。");
            }

            return new[]
            {
                phaseCoordinates[1] - phaseCoordinates[0],
                phaseCoordinates[2] - phaseCoordinates[1]
            };
        }

        /// <summary>
        /// 固定接口入口已经给出偏移过渡段时，特殊区域直接由铜排路径生成，
        /// 不再从现有基架Box反推断点与过渡范围。
        /// </summary>
        private static FaceToFaceSmallOffsetRegionPlan
            CreateSmallOffsetRegionFromPath(MiddleBusbarPathPlan pathPlan)
        {
            var offsetSegments = pathPlan.Segments.Where(segment =>
                    (segment.Role & MiddleBusbarSegmentRole.Offset) != 0)
                .ToArray();
            if (offsetSegments.Length != 1)
            {
                throw new InvalidOperationException(
                    "小偏移统一路径必须包含唯一连续过渡段。");
            }

            var transition = offsetSegments[0];
            var frameStart = pathPlan.OffsetFrameStartX
                ?? transition.Start.X;
            var frameEnd = pathPlan.OffsetFrameEndX
                ?? transition.End.X;
            var widthMinimum = Math.Min(transition.Start.Y, transition.End.Y);
            var widthMaximum = Math.Max(transition.Start.Y, transition.End.Y);
            var referenceLength = Math.Max(1d,
                (frameEnd - frameStart) / 10d);
            return new FaceToFaceSmallOffsetRegionPlan
            {
                BCabinetReferenceFrame = CreateSyntheticFrameBox(
                    -3,
                    "路径规划B侧参考区",
                    frameStart - referenceLength,
                    frameStart,
                    transition.Start.Y - .5d,
                    transition.Start.Y + .5d),
                SpecialFrame = CreateSyntheticFrameBox(
                    -2,
                    "路径规划小偏移工艺区",
                    frameStart,
                    frameEnd,
                    widthMinimum - .5d,
                    widthMaximum + .5d),
                ACabinetReferenceFrame = CreateSyntheticFrameBox(
                    -1,
                    "路径规划A侧参考区",
                    frameEnd,
                    frameEnd + referenceLength,
                    transition.End.Y - .5d,
                    transition.End.Y + .5d)
            };
        }

        private static IReadOnlyList<FaceToFaceFrameBox>
            CreateLargeOffsetAnalysisFramesFromPath(
                MiddleBusbarPathPlan pathPlan,
                IReadOnlyList<double> bPhaseWidthCoordinates,
                IReadOnlyList<double> aPhaseWidthCoordinates,
                double busbarWidth)
        {
            var transition = pathPlan.Segments.Single(segment =>
                (segment.Role & MiddleBusbarSegmentRole.Offset) != 0);
            var frameStart = pathPlan.OffsetFrameStartX
                ?? transition.Start.X;
            var frameEnd = pathPlan.OffsetFrameEndX
                ?? transition.End.X;
            var bCenter = bPhaseWidthCoordinates.Average();
            var aCenter = aPhaseWidthCoordinates.Average();
            var halfEnvelope = (Math.Max(
                bPhaseWidthCoordinates.Max() - bPhaseWidthCoordinates.Min(),
                aPhaseWidthCoordinates.Max() - aPhaseWidthCoordinates.Min())
                + busbarWidth) / 2d;
            return new[]
            {
                CreateSyntheticFrameBox(0, "路径规划B侧拐角区",
                    frameStart, frameEnd,
                    bCenter - halfEnvelope, bCenter + halfEnvelope),
                CreateSyntheticFrameBox(1, "路径规划横向连接区",
                    frameStart, frameEnd,
                    (bCenter + aCenter) / 2d - halfEnvelope,
                    (bCenter + aCenter) / 2d + halfEnvelope),
                CreateSyntheticFrameBox(2, "路径规划A侧拐角区",
                    frameStart, frameEnd,
                    aCenter - halfEnvelope, aCenter + halfEnvelope)
            };
        }

        private static FaceToFaceFrameBox CreateSyntheticFrameBox(
            int physicalIndex,
            string name,
            double lengthMinimum,
            double lengthMaximum,
            double widthMinimum,
            double widthMaximum)
        {
            return new FaceToFaceFrameBox
            {
                PhysicalIndex = physicalIndex,
                ComponentName = name,
                LengthMinimum = lengthMinimum,
                LengthMaximum = lengthMaximum,
                WidthMinimum = widthMinimum,
                WidthMaximum = widthMaximum,
                HeightMinimum = 0d,
                HeightMaximum = 1d
            };
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateSmallOffsetTransitionPreviewBusbars(
            FaceToFaceSmallOffsetRegionPlan smallOffsetRegion,
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            BusbarBridgeAxisSetting axisSetting,
            double insulationClearance,
            bool useMilledBarForLargeSmallOffset,
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion,
            bool frontBridgeOnly = false, string scenarioName = "面对面")
        {
            if (smallOffsetRegion == null)
            {
                throw new ArgumentNullException(nameof(smallOffsetRegion));
            }
            if (bCabinetContext == null)
            {
                throw new ArgumentNullException(nameof(bCabinetContext));
            }
            if (aCabinetContext == null)
            {
                throw new ArgumentNullException(nameof(aCabinetContext));
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            if (smallOffsetRegion.SuppressedBreakpointIntervalIndex < 0)
            {
                throw new InvalidOperationException(
                    "小偏移中间过渡排缺少固定空窗逻辑段序号。");
            }
            if (smallOffsetRegion.TransitionRightBreakpointCoordinate <=
                smallOffsetRegion.TransitionLeftBreakpointCoordinate +
                GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移中间过渡排缺少有效的左右断点坐标。");
            }

            var phaseChangeOccursBeforeSmallOffset =
                phaseChangeRegion != null &&
                phaseChangeRegion.SuppressedBreakpointIntervalIndex <
                smallOffsetRegion.SuppressedBreakpointIntervalIndex;
            var bCabinetPhysicalPhaseIndexes =
                Enumerable.Range(0, PhaseCount)
                    .Select(logicalPhaseIndex =>
                        phaseChangeOccursBeforeSmallOffset
                            ? ReversePhaseIndex(logicalPhaseIndex)
                            : logicalPhaseIndex)
                    .ToList();
            var aCabinetPhysicalPhaseIndexes =
                Enumerable.Range(0, PhaseCount)
                    .Select(logicalPhaseIndex =>
                        phaseChangeRegion == null ||
                        phaseChangeOccursBeforeSmallOffset
                            ? logicalPhaseIndex
                            : ReversePhaseIndex(logicalPhaseIndex))
                    .ToList();
            var phaseSignedWidthOffsets = Enumerable.Range(0, PhaseCount)
                .Select(logicalPhaseIndex =>
                {
                    var bPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                        bCabinetContext.PhaseRouteContexts[
                                bCabinetPhysicalPhaseIndexes[logicalPhaseIndex]]
                            .OrdinaryRouteReferencePoint,
                        axisSetting.WidthVector);
                    var aPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                        aCabinetContext.PhaseRouteContexts[
                                aCabinetPhysicalPhaseIndexes[logicalPhaseIndex]]
                            .OrdinaryRouteReferencePoint,
                        axisSetting.WidthVector);
                    return aPhaseWidthCoordinate - bPhaseWidthCoordinate;
                })
                .ToList();
            var commonSignedWidthOffset = phaseSignedWidthOffsets.Average();
            if (phaseSignedWidthOffsets.Any(phaseSignedWidthOffset =>
                    Math.Abs(phaseSignedWidthOffset -
                        commonSignedWidthOffset) > ModelAlignmentTolerance))
            {
                throw new InvalidOperationException(
                    "面对面小偏移 A/B 柜对应相的宽向偏移不一致，无法生成统一的中间过渡排。");
            }

            var absoluteCommonWidthOffset = Math.Abs(
                commonSignedWidthOffset);
            var commonBusbarWidth = bCabinetContext
                .PhaseRouteContexts[0].BusbarWidth;
            var smallOffsetTransitionKind = ResolveSmallOffsetTransitionKind(
                absoluteCommonWidthOffset,
                commonBusbarWidth);
            smallOffsetRegion.SignedBusbarWidthOffset =
                commonSignedWidthOffset;
            smallOffsetRegion.InsulationClearance = insulationClearance;
            smallOffsetRegion.UsesMiddleParallelConnection =
                smallOffsetTransitionKind ==
                FaceToFaceSmallOffsetTransitionKind_e.MiddleParallelConnection;
            smallOffsetRegion.TransitionKind = smallOffsetTransitionKind;

            var transitionRouteGroupIndex =
                smallOffsetRegion.SuppressedBreakpointIntervalIndex;
            var transitionRouteIsBridge = !frontBridgeOnly &&
                smallOffsetTransitionKind != FaceToFaceSmallOffsetTransitionKind_e.MiddleParallelConnection &&
                IsBridgeRouteGroup(transitionRouteGroupIndex);
            var transitionStartLengthCoordinate =
                smallOffsetRegion.TransitionLeftBreakpointCoordinate -
                commonBusbarWidth / 2d;
            var transitionEndLengthCoordinate =
                smallOffsetRegion.TransitionRightBreakpointCoordinate +
                commonBusbarWidth / 2d;
            FaceToWallPreviewTrace.Info(
                "面对面小偏移过渡排-断点边界",
                "左断点=" +
                smallOffsetRegion.TransitionLeftBreakpointCoordinate
                    .ToString("0.###") +
                "，右断点=" +
                smallOffsetRegion.TransitionRightBreakpointCoordinate
                    .ToString("0.###") +
                "，实体起止=" +
                transitionStartLengthCoordinate.ToString("0.###") + "～" +
                transitionEndLengthCoordinate.ToString("0.###") +
                " mm，B相立弯固定中心=" +
                smallOffsetRegion.SpecialFrame.LengthCenter
                    .ToString("0.###") + " mm");
            if (transitionEndLengthCoordinate <=
                transitionStartLengthCoordinate + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "面对面小偏移中间过渡排的长度范围无效。");
            }
            if (transitionRouteIsBridge)
            {
                var minimumBridgeTransitionLength = 2d *
                    (commonBusbarWidth +
                     FaceToWallModelBusbarPlanner
                         .BridgeLowestEdgeStraightAdditionalLength +
                     FaceToWallModelBusbarPlanner
                         .BridgeInnermostTransitionProjectionLength);
                if (transitionEndLengthCoordinate -
                        transitionStartLengthCoordinate <=
                    minimumBridgeTransitionLength + GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "小偏移桥式中间过渡排长度不足以容纳两端“排宽+40 mm”直段和固定 50 mm 斜段。");
                }
            }

            var transitionPreviewBusbars =
                new List<PlanarBendBusbarSetting>();
            for (var phaseIndex = 0;
                phaseIndex < PhaseCount;
                phaseIndex++)
            {
                var bCabinetPhysicalPhaseIndex =
                    bCabinetPhysicalPhaseIndexes[phaseIndex];
                var aCabinetPhysicalPhaseIndex =
                    aCabinetPhysicalPhaseIndexes[phaseIndex];
                var transitionPhysicalPositionIndex =
                    phaseChangeOccursBeforeSmallOffset
                        ? ReversePhaseIndex(phaseIndex)
                        : phaseIndex;
                var bPhaseRouteContext =
                    bCabinetContext.PhaseRouteContexts[phaseIndex];
                var aPhaseRouteContext =
                    aCabinetContext.PhaseRouteContexts[phaseIndex];
                EnsureSmallOffsetStackOffsetsMatch(
                    bPhaseRouteContext,
                    aPhaseRouteContext,
                    phaseIndex);
                var bPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                    bCabinetContext.PhaseRouteContexts[
                            bCabinetPhysicalPhaseIndex]
                        .OrdinaryRouteReferencePoint,
                    axisSetting.WidthVector);
                var aPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                    aCabinetContext.PhaseRouteContexts[
                            aCabinetPhysicalPhaseIndex]
                        .OrdinaryRouteReferencePoint,
                    axisSetting.WidthVector);
                FaceToWallPreviewTrace.Info(
                    "面对面小偏移换相-相位映射",
                    "逻辑相=" +
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "，B柜物理相=" +
                    FaceToWallModelBusbarPlanner.GetPhaseName(
                        bCabinetPhysicalPhaseIndex) +
                    "，A柜物理相=" +
                    FaceToWallModelBusbarPlanner.GetPhaseName(
                        aCabinetPhysicalPhaseIndex) +
                    "，立弯位置索引=" + transitionPhysicalPositionIndex +
                    "，宽向偏移=" +
                    (aPhaseWidthCoordinate - bPhaseWidthCoordinate)
                        .ToString("0.###"));

                // 立弯/铣排必须先以叠排总厚度构造一条完整路径，再拆回单根铜排。
                // 中间平行连接仍保留逐成员的搭接规则，不走总厚度几何。
                var useStackEnvelopeTransitionGeometry =
                    smallOffsetTransitionKind ==
                        FaceToFaceSmallOffsetTransitionKind_e.MilledBar ||
                    smallOffsetTransitionKind ==
                        FaceToFaceSmallOffsetTransitionKind_e.VerticalBend;

                if (transitionRouteIsBridge)
                {
                    if (useStackEnvelopeTransitionGeometry)
                    {
                        var stackEnvelopeThickness =
                            CalculateStackEnvelopeThickness(
                                bPhaseRouteContext);
                        var usesVerticalBendBridgeGeometry =
                            smallOffsetTransitionKind ==
                            FaceToFaceSmallOffsetTransitionKind_e.VerticalBend;
                        var bridgeStackMemberSplitOffsets =
                            usesVerticalBendBridgeGeometry
                                ? CreateBridgeStackMemberRelativeOffsets(
                                    bPhaseRouteContext)
                                : bPhaseRouteContext.StackMemberOffsets;
                        var bBridgeCenterline =
                            CreateSmallOffsetBridgeReferenceCenterline(
                                bPhaseRouteContext,
                                axisSetting,
                                transitionStartLengthCoordinate,
                                transitionEndLengthCoordinate,
                                bPhaseWidthCoordinate,
                                phaseIndex,
                                stackEnvelopeThickness,
                                bPhaseRouteContext.BusbarThickness,
                                usesVerticalBendBridgeGeometry);
                        var aBridgeCenterline =
                            CreateSmallOffsetBridgeReferenceCenterline(
                                aPhaseRouteContext,
                                axisSetting,
                                transitionStartLengthCoordinate,
                                transitionEndLengthCoordinate,
                                aPhaseWidthCoordinate,
                                phaseIndex,
                                stackEnvelopeThickness,
                                aPhaseRouteContext.BusbarThickness,
                                usesVerticalBendBridgeGeometry);
                        IReadOnlyList<PlanarBendBusbarSetting>
                            stackEnvelopeTransitionSettings;
                        if (smallOffsetTransitionKind ==
                            FaceToFaceSmallOffsetTransitionKind_e.MilledBar)
                        {
                            stackEnvelopeTransitionSettings =
                                CreateBridgeMilledTransitionSettings(
                                    bBridgeCenterline,
                                    aBridgeCenterline,
                                    axisSetting,
                                    bPhaseWidthCoordinate,
                                    aPhaseWidthCoordinate,
                                    insulationClearance,
                                    transitionPhysicalPositionIndex);
                        }
                        else
                        {
                            stackEnvelopeTransitionSettings = new[]
                            {
                                CreateMergedBridgeVerticalTransitionSetting(
                                    bBridgeCenterline,
                                    aBridgeCenterline,
                                    axisSetting,
                                    bPhaseWidthCoordinate,
                                    aPhaseWidthCoordinate,
                                    commonSignedWidthOffset,
                                    bPhaseRouteContext.BusbarThickness,
                                    smallOffsetRegion.SpecialFrame.LengthCenter,
                                    transitionPhysicalPositionIndex)
                            };
                        }

                        transitionPreviewBusbars.AddRange(
                            SplitStackEnvelopeTransitionSettings(
                                stackEnvelopeTransitionSettings,
                                bridgeStackMemberSplitOffsets,
                                bPhaseRouteContext.BusbarThickness,
                                phaseIndex));
                        continue;
                    }

                    var bWidthBridgeMembers =
                        CreateSmallOffsetBridgeReferenceMembers(
                            bPhaseRouteContext,
                            axisSetting,
                            transitionStartLengthCoordinate,
                            transitionEndLengthCoordinate,
                            bPhaseWidthCoordinate,
                            phaseIndex);
                    var aWidthBridgeMembers =
                        CreateSmallOffsetBridgeReferenceMembers(
                            aPhaseRouteContext,
                            axisSetting,
                            transitionStartLengthCoordinate,
                            transitionEndLengthCoordinate,
                            aPhaseWidthCoordinate,
                            phaseIndex);
                    for (var memberIndex = 0;
                        memberIndex < bWidthBridgeMembers.Count;
                        memberIndex++)
                    {
                        if (smallOffsetTransitionKind ==
                            FaceToFaceSmallOffsetTransitionKind_e.MiddleParallelConnection)
                        {
                            transitionPreviewBusbars.AddRange(
                                CreateBridgeMiddleParallelTransitionSettings(
                                    bWidthBridgeMembers[memberIndex],
                                    aWidthBridgeMembers[memberIndex],
                                    axisSetting,
                                    bPhaseWidthCoordinate,
                                    aPhaseWidthCoordinate,
                                    commonSignedWidthOffset,
                                    insulationClearance,
                                    transitionPhysicalPositionIndex));
                        }
                        else if (smallOffsetTransitionKind ==
                            FaceToFaceSmallOffsetTransitionKind_e.MilledBar)
                        {
                            transitionPreviewBusbars.AddRange(
                                CreateBridgeMilledTransitionSettings(
                                    bWidthBridgeMembers[memberIndex],
                                    aWidthBridgeMembers[memberIndex],
                                    axisSetting,
                                    bPhaseWidthCoordinate,
                                    aPhaseWidthCoordinate,
                                    insulationClearance,
                                    transitionPhysicalPositionIndex));
                        }
                        else
                        {
                            transitionPreviewBusbars.Add(
                                CreateMergedBridgeVerticalTransitionSetting(
                                    bWidthBridgeMembers[memberIndex],
                                    aWidthBridgeMembers[memberIndex],
                                    axisSetting,
                                    bPhaseWidthCoordinate,
                                    aPhaseWidthCoordinate,
                                    commonSignedWidthOffset,
                                    bPhaseRouteContext.BusbarThickness,
                                    smallOffsetRegion.SpecialFrame.LengthCenter,
                                    transitionPhysicalPositionIndex));
                        }
                    }

                    continue;
                }

                if (useStackEnvelopeTransitionGeometry)
                {
                    var stackEnvelopeThickness =
                        CalculateStackEnvelopeThickness(
                            bPhaseRouteContext);
                    var transitionCenterlineStartPoint =
                        CreatePointAtLengthAndWidth(
                            bPhaseRouteContext.OrdinaryRouteReferencePoint,
                            axisSetting,
                            transitionStartLengthCoordinate,
                            bPhaseWidthCoordinate);
                    var transitionCenterlineEndPoint =
                        CreatePointAtLengthAndWidth(
                            aPhaseRouteContext.OrdinaryRouteReferencePoint,
                            axisSetting,
                            transitionEndLengthCoordinate,
                            aPhaseWidthCoordinate);
                    var stackEnvelopeRouteContext =
                        CreateStackEnvelopeRouteContext(
                            bPhaseRouteContext,
                            stackEnvelopeThickness);
                    IReadOnlyList<PlanarBendBusbarSetting>
                        stackEnvelopeTransitionSettings;
                    if (smallOffsetTransitionKind ==
                        FaceToFaceSmallOffsetTransitionKind_e.MilledBar)
                    {
                        stackEnvelopeTransitionSettings =
                            CreateOrdinaryMilledTransitionSettings(
                                transitionCenterlineStartPoint,
                                transitionCenterlineEndPoint,
                                axisSetting,
                                stackEnvelopeRouteContext,
                                commonSignedWidthOffset,
                                insulationClearance,
                                transitionPhysicalPositionIndex,
                                FaceToWallModelBusbarPlanner.GetPhaseName(
                                    phaseIndex));
                    }
                    else
                    {
                        stackEnvelopeTransitionSettings = new[]
                        {
                            CreateOrdinaryVerticalTransitionSetting(
                                transitionCenterlineStartPoint,
                                transitionCenterlineEndPoint,
                                axisSetting,
                                stackEnvelopeRouteContext,
                                commonSignedWidthOffset,
                                bPhaseRouteContext.BusbarThickness,
                                transitionPhysicalPositionIndex,
                                FaceToWallModelBusbarPlanner.GetPhaseName(
                                    phaseIndex),
                                smallOffsetRegion.SpecialFrame.LengthCenter)
                        };
                    }

                    transitionPreviewBusbars.AddRange(
                        SplitStackEnvelopeTransitionSettings(
                            stackEnvelopeTransitionSettings,
                            bPhaseRouteContext.StackMemberOffsets,
                            bPhaseRouteContext.BusbarThickness,
                            phaseIndex));
                    continue;
                }

                for (var memberIndex = 0;
                    memberIndex < bPhaseRouteContext.StackMemberOffsets.Count;
                    memberIndex++)
                {
                    var transitionMemberHeightOffset =
                        bPhaseRouteContext.StackMemberOffsets[memberIndex];
                    var transitionMemberStartPoint =
                        PlanarBendBusbarSetting.Translate(
                            CreatePointAtLengthAndWidth(
                                bPhaseRouteContext
                                    .OrdinaryRouteReferencePoint,
                                axisSetting,
                                transitionStartLengthCoordinate,
                                bPhaseWidthCoordinate),
                            bPhaseRouteContext.RouteThicknessDirection
                                .Normalized,
                            transitionMemberHeightOffset);
                    var transitionMemberEndPoint =
                        PlanarBendBusbarSetting.Translate(
                            CreatePointAtLengthAndWidth(
                                aPhaseRouteContext
                                    .OrdinaryRouteReferencePoint,
                                axisSetting,
                                transitionEndLengthCoordinate,
                                aPhaseWidthCoordinate),
                            aPhaseRouteContext.RouteThicknessDirection
                                .Normalized,
                            transitionMemberHeightOffset);
                    var transitionGroupKey =
                        FaceToWallModelBusbarPlanner.GetPhaseName(
                            phaseIndex) + "-" + (memberIndex + 1);
                    if (smallOffsetTransitionKind ==
                        FaceToFaceSmallOffsetTransitionKind_e.MiddleParallelConnection)
                    {
                        transitionPreviewBusbars.AddRange(
                            CreateOrdinaryMiddleParallelTransitionSettings(
                                transitionMemberStartPoint,
                                transitionMemberEndPoint,
                                axisSetting,
                                bPhaseRouteContext,
                                commonSignedWidthOffset,
                                insulationClearance,
                                transitionPhysicalPositionIndex,
                                transitionGroupKey));
                    }
                    else if (smallOffsetTransitionKind ==
                        FaceToFaceSmallOffsetTransitionKind_e.MilledBar)
                    {
                        transitionPreviewBusbars.AddRange(
                            CreateOrdinaryMilledTransitionSettings(
                                transitionMemberStartPoint,
                                transitionMemberEndPoint,
                                axisSetting,
                                bPhaseRouteContext,
                                commonSignedWidthOffset,
                                insulationClearance,
                                transitionPhysicalPositionIndex,
                                transitionGroupKey));
                    }
                    else
                    {
                        transitionPreviewBusbars.Add(
                            CreateOrdinaryVerticalTransitionSetting(
                                transitionMemberStartPoint,
                                transitionMemberEndPoint,
                                axisSetting,
                                bPhaseRouteContext,
                                commonSignedWidthOffset,
                                bPhaseRouteContext.BusbarThickness,
                                transitionPhysicalPositionIndex,
                                transitionGroupKey,
                                smallOffsetRegion.SpecialFrame.LengthCenter));
                    }
                }
            }

            if (frontBridgeOnly &&
                smallOffsetTransitionKind != FaceToFaceSmallOffsetTransitionKind_e.MiddleParallelConnection)
                transitionPreviewBusbars = CreateFixedSpecialFrontBridges(transitionPreviewBusbars).ToList();

            var transitionClearanceEvaluation =
                PlanarBusbarClearanceValidator.Evaluate(
                    transitionPreviewBusbars,
                    ResolveSmallOffsetClearancePhaseKey,
                    insulationClearance);
            var triedLongerVerticalBend = false;
            if (!transitionClearanceEvaluation.IsClear &&
                smallOffsetTransitionKind == FaceToFaceSmallOffsetTransitionKind_e.VerticalBend)
            {
                triedLongerVerticalBend = true;
                if (TryExtendSmallOffsetVerticalBends(
                    transitionPreviewBusbars, axisSetting, insulationClearance,
                    out var extendedBusbars, out var extendedEvaluation,
                    out var addedSlantProjection))
                {
                    transitionPreviewBusbars = extendedBusbars;
                    transitionClearanceEvaluation = extendedEvaluation;
                    FaceToWallPreviewTrace.Info(
                        "面对面小偏移立弯-净距优化",
                        "斜段桥向投影增加=" + addedSlantProjection.ToString("0.###") +
                        " mm，实体最小净距=" +
                        extendedEvaluation.MinimumDistance.ToString("0.###") + " mm");
                }
                else if (extendedEvaluation != null && extendedEvaluation.MinimumDistance > transitionClearanceEvaluation.MinimumDistance)
                    transitionClearanceEvaluation = extendedEvaluation;
            }
            if (!transitionClearanceEvaluation.IsClear)
            {
                throw new SmallOffsetClearanceException(
                    scenarioName + "小偏移铜排实体绝缘距离不足：" +
                    transitionClearanceEvaluation.FirstGroupKey +
                    " 与 " +
                    transitionClearanceEvaluation.SecondGroupKey +
                    " 的长方体表面最短距离为 " +
                    transitionClearanceEvaluation.MinimumDistance
                        .ToString("0.###") +
                    " mm，小于要求的 " +
                    insulationClearance.ToString("0.###") + " mm。" +
                    (triedLongerVerticalBend
                        ? "当前过渡区内延长斜段后的最佳净距仍不足；请扩大过渡区或增加相间距。"
                        : string.Empty), transitionClearanceEvaluation.MinimumDistance);
            }

            FaceToWallPreviewTrace.Info(
                "面对面小偏移中间过渡排-规划",
                "逻辑段=" + (transitionRouteGroupIndex + 1) +
                "，桥式=" + transitionRouteIsBridge +
                "，类型=" + smallOffsetTransitionKind +
                "，绝缘距离=" + insulationClearance.ToString("0.###") +
                "，错开并行=" + smallOffsetRegion.UsesMiddleParallelConnection +
                "，宽向偏移=" +
                commonSignedWidthOffset.ToString("0.###") +
                "，长方体实测最小净距=" +
                transitionClearanceEvaluation.MinimumDistance
                    .ToString("0.###") +
                "，最小净距组合=" +
                transitionClearanceEvaluation.FirstGroupKey + "/" +
                transitionClearanceEvaluation.SecondGroupKey +
                "，预览排数量=" + transitionPreviewBusbars.Count);
            return transitionPreviewBusbars.AsReadOnly();
        }

        /// <summary>
        /// 两个排宽是小偏移立弯的初始斜段长度。相距较近的相邻斜段在实体
        /// 测距时会比名义相间距更近；保持两端接口和桥头不动，只在现有直段
        /// 内扩展三相斜段，再用同一实体校验器选出满足净距的最短方案。
        /// </summary>
        private static bool TryExtendSmallOffsetVerticalBends(
            IReadOnlyList<PlanarBendBusbarSetting> source,
            BusbarBridgeAxisSetting axisSetting,
            double insulationClearance,
            out List<PlanarBendBusbarSetting> extended,
            out PlanarBusbarClearanceEvaluation evaluation,
            out double addedSlantProjection)
        {
            extended = null;
            evaluation = null;
            addedSlantProjection = 0d;
            var slantIndexes = new int[source.Count];
            var maximumExtension = double.PositiveInfinity;
            for (var settingIndex = 0; settingIndex < source.Count; settingIndex++)
            {
                var setting = source[settingIndex];
                var points = setting.PointList;
                var slantIndex = -1;
                for (var pointIndex = 1; pointIndex < points.Count - 2; pointIndex++)
                {
                    var widthDelta = Math.Abs(BusbarGeometryUtility.Project(
                        points[pointIndex + 1], axisSetting.WidthVector) -
                        BusbarGeometryUtility.Project(points[pointIndex], axisSetting.WidthVector));
                    if (widthDelta <= GeometryTolerance) continue;
                    if (slantIndex >= 0) return false;
                    slantIndex = pointIndex;
                }
                if (slantIndex < 0) return false;
                slantIndexes[settingIndex] = slantIndex;
                var reserve = Math.Max(setting.BarBendingRadius,
                    setting.VerticalBendingRadius);
                var lead = BusbarGeometryUtility.Project(points[slantIndex], axisSetting.LengthVector) -
                    BusbarGeometryUtility.Project(points[slantIndex - 1], axisSetting.LengthVector);
                var tail = BusbarGeometryUtility.Project(points[slantIndex + 2], axisSetting.LengthVector) -
                    BusbarGeometryUtility.Project(points[slantIndex + 1], axisSetting.LengthVector);
                maximumExtension = Math.Min(maximumExtension,
                    2d * Math.Min(lead - reserve, tail - reserve));
            }
            if (maximumExtension <= GeometryTolerance || double.IsInfinity(maximumExtension))
                return false;

            List<PlanarBendBusbarSetting> CreateCandidate(double extension)
            {
                var candidate = new List<PlanarBendBusbarSetting>(source.Count);
                for (var settingIndex = 0; settingIndex < source.Count; settingIndex++)
                {
                    var setting = source[settingIndex].Copy();
                    setting.PointList = setting.PointList.Select(point => point.Copy()).ToList();
                    var slantIndex = slantIndexes[settingIndex];
                    setting.PointList[slantIndex] = PlanarBendBusbarSetting.Translate(
                        setting.PointList[slantIndex], axisSetting.LengthVector, -extension / 2d);
                    setting.PointList[slantIndex + 1] = PlanarBendBusbarSetting.Translate(
                        setting.PointList[slantIndex + 1], axisSetting.LengthVector, extension / 2d);
                    candidate.Add(setting);
                }
                return candidate;
            }

            // 实体距离可能由不同相对主导，逐级试探后仅在首次合格区间内收敛。
            const int sampleCount = 16;
            var previousExtension = 0d;
            for (var sample = 1; sample <= sampleCount; sample++)
            {
                var extension = maximumExtension * sample / sampleCount;
                var candidate = CreateCandidate(extension);
                var candidateEvaluation = PlanarBusbarClearanceValidator.Evaluate(
                    candidate, ResolveSmallOffsetClearancePhaseKey, insulationClearance);
                if (!candidateEvaluation.IsClear)
                {
                    if (evaluation == null || candidateEvaluation.MinimumDistance > evaluation.MinimumDistance)
                        evaluation = candidateEvaluation;
                    previousExtension = extension;
                    continue;
                }
                var upperExtension = extension;
                for (var iteration = 0; iteration < 16; iteration++)
                {
                    var middleExtension = (previousExtension + upperExtension) / 2d;
                    var middleCandidate = CreateCandidate(middleExtension);
                    var middleEvaluation = PlanarBusbarClearanceValidator.Evaluate(
                        middleCandidate, ResolveSmallOffsetClearancePhaseKey, insulationClearance);
                    if (middleEvaluation.IsClear)
                    {
                        upperExtension = middleExtension;
                        candidate = middleCandidate;
                        candidateEvaluation = middleEvaluation;
                    }
                    else
                        previousExtension = middleExtension;
                }
                extended = candidate;
                evaluation = candidateEvaluation;
                addedSlantProjection = upperExtension;
                return true;
            }
            return false;
        }

        private static string ResolveSmallOffsetClearancePhaseKey(
            PlanarBendBusbarSetting transitionBusbar)
        {
            var transitionGroupKey = transitionBusbar?.GroupKey?.Trim();
            if (string.IsNullOrWhiteSpace(transitionGroupKey))
            {
                throw new InvalidOperationException(
                    "面对面小偏移铜排缺少相序分组，无法检查实体绝缘距离。");
            }

            var phaseKey = transitionGroupKey.Substring(0, 1)
                .ToUpperInvariant();
            if (phaseKey != "A" && phaseKey != "B" && phaseKey != "C")
            {
                throw new InvalidOperationException(
                    "面对面小偏移铜排分组“" + transitionGroupKey +
                    "”无法识别为 A/B/C 相，无法检查实体绝缘距离。");
            }

            return phaseKey;
        }

        private static void EnsureSmallOffsetStackOffsetsMatch(
            FaceToWallPhaseRouteContext bPhaseRouteContext,
            FaceToWallPhaseRouteContext aPhaseRouteContext,
            int phaseIndex)
        {
            if (bPhaseRouteContext.StackMemberOffsets == null ||
                aPhaseRouteContext.StackMemberOffsets == null ||
                bPhaseRouteContext.StackMemberOffsets.Count !=
                aPhaseRouteContext.StackMemberOffsets.Count ||
                bPhaseRouteContext.StackMemberOffsets
                    .Where((bStackMemberOffset, memberIndex) =>
                        Math.Abs(bStackMemberOffset -
                            aPhaseRouteContext.StackMemberOffsets[
                                memberIndex]) > ModelAlignmentTolerance)
                    .Any())
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相 A/B 柜叠排层位置不一致，无法生成小偏移中间过渡排。");
            }
        }

        /// <summary>
        /// 计算特殊过渡使用的叠排总包络厚度。立弯和铣排先以该厚度
        /// 构造完整中心线，随后再按单根排厚度拆分成员。
        /// </summary>
        private static double CalculateStackEnvelopeThickness(
            FaceToWallPhaseRouteContext phaseRouteContext)
        {
            if (phaseRouteContext == null)
            {
                throw new ArgumentNullException(nameof(phaseRouteContext));
            }

            var stackMemberCount = phaseRouteContext.StackMemberOffsets?.Count ?? 0;
            if (stackMemberCount <= 0)
            {
                throw new InvalidOperationException(
                    "特殊过渡排缺少有效的叠排成员数量。");
            }
            if (phaseRouteContext.BusbarThickness <= 0d ||
                double.IsNaN(phaseRouteContext.BusbarThickness) ||
                double.IsInfinity(phaseRouteContext.BusbarThickness))
            {
                throw new InvalidOperationException(
                    "特殊过渡排缺少有效的单根排厚度。");
            }

            return (2d * stackMemberCount - 1d) *
                phaseRouteContext.BusbarThickness;
        }

        /// <summary>
        /// 计算 RouteLinkBar 对末端垂直搭接边界线段要求的折弯预留。
        /// 该值表示折下后直段的最小保留长度：排宽 + 半叠排包络厚度
        /// + 折弯余量。动作中心之间使用半排宽和叠排折弯扫描半跨度，
        /// 不应把该完整长度重复叠加到每个中心间距。
        /// </summary>
        public static double
            CalculatePhaseChangePerpendicularBendReserve(
            double busbarWidth,
            double stackEnvelopeThickness,
            double bendAllowance)
        {
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            ValidatePositiveFinite(
                stackEnvelopeThickness,
                nameof(stackEnvelopeThickness));
            ValidateNonNegativeFinite(bendAllowance, nameof(bendAllowance));
            return busbarWidth + PlanarBusbarClearanceValidator.CalculateBendSweepHalfSpan(
                stackEnvelopeThickness, bendAllowance);
        }

        private static double CalculatePhaseChangeActionCenterSpacing(
            double insulationClearance, double busbarWidth,
            double stackEnvelopeThickness, double bendAllowance)
        {
            // 横排向交点外伸半排宽，另一相折弯占据扫描包络；只计半排厚
            // 会使终检比规划少一个折弯余量，不能通过降低净距要求补偿。
            return insulationClearance + busbarWidth / 2d +
                PlanarBusbarClearanceValidator.CalculateBendSweepHalfSpan(
                    stackEnvelopeThickness, bendAllowance);
        }

        /// <summary>
        /// 计算换相桥式单侧三点端头占用的长度轴投影。
        /// 端头由“排宽 + 40 mm”直段和固定 50 mm 斜段组成。
        /// </summary>
        private static double CalculatePhaseChangeBridgeTerminalLength(
            double busbarWidth)
        {
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            return busbarWidth +
                   FaceToWallModelBusbarPlanner
                       .BridgeLowestEdgeStraightAdditionalLength +
                   FaceToWallModelBusbarPlanner
                       .BridgeInnermostTransitionProjectionLength;
        }

        /// <summary>
        /// 计算垂直叠排搭接在线段交点两侧的实际投影长度。
        /// 两个排的搭接区域应为“排宽 × 排宽”，所以每个方向只允许
        /// 从投影交点向外保留半个排宽；折弯半径和叠排折弯余量由动作
        /// 中心之间的距离另行保证。
        /// </summary>
        private static double
            CalculatePhaseChangePerpendicularLapProjectionExtension(
            double busbarWidth)
        {
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            return busbarWidth / 2d;
        }

        private static FaceToWallPhaseRouteContext
            CreateStackEnvelopeRouteContext(
            FaceToWallPhaseRouteContext sourceRouteContext,
            double stackEnvelopeThickness,
            IReadOnlyList<double> stackMemberOffsetsOverride = null)
        {
            if (sourceRouteContext == null)
            {
                throw new ArgumentNullException(nameof(sourceRouteContext));
            }
            if (stackEnvelopeThickness <= 0d ||
                double.IsNaN(stackEnvelopeThickness) ||
                double.IsInfinity(stackEnvelopeThickness))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stackEnvelopeThickness));
            }

            return new FaceToWallPhaseRouteContext
            {
                PhaseIndex = sourceRouteContext.PhaseIndex,
                OrdinaryRouteReferencePoint =
                    sourceRouteContext.OrdinaryRouteReferencePoint?.Copy(),
                RouteWidthDirection =
                    sourceRouteContext.RouteWidthDirection?.Copy(),
                RouteThicknessDirection =
                    sourceRouteContext.RouteThicknessDirection?.Copy(),
                FrameOutwardNormal =
                    sourceRouteContext.FrameOutwardNormal?.Copy(),
                BusbarWidth = sourceRouteContext.BusbarWidth,
                BusbarThickness = stackEnvelopeThickness,
                BendingRadius = sourceRouteContext.BendingRadius,
                BendAllowance = sourceRouteContext.BendAllowance,
                StackMemberOffsets = stackMemberOffsetsOverride ??
                    sourceRouteContext.StackMemberOffsets
            };
        }

        private static PlanarBendBusbarSetting
            CreateSmallOffsetBridgeReferenceCenterline(
            FaceToWallPhaseRouteContext phaseRouteContext,
            BusbarBridgeAxisSetting axisSetting,
            double transitionStartLengthCoordinate,
            double transitionEndLengthCoordinate,
            double phaseWidthCoordinate,
            int phaseIndex,
            double stackEnvelopeThickness,
            double singleBusbarThickness,
            bool preserveInnermostBridgeMemberGeometry)
        {
            if (phaseRouteContext == null)
            {
                throw new ArgumentNullException(nameof(phaseRouteContext));
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            ValidatePositiveFinite(
                singleBusbarThickness,
                nameof(singleBusbarThickness));

            var bridgeStartPoint = CreatePointAtLengthAndWidth(
                phaseRouteContext.OrdinaryRouteReferencePoint,
                axisSetting,
                transitionStartLengthCoordinate,
                phaseWidthCoordinate);
            var bridgeEndPoint = CreatePointAtLengthAndWidth(
                phaseRouteContext.OrdinaryRouteReferencePoint,
                axisSetting,
                transitionEndLengthCoordinate,
                phaseWidthCoordinate);
            var bridgeReferenceStartPoint = bridgeStartPoint;
            var bridgeReferenceEndPoint = bridgeEndPoint;
            var bridgeOffsetDistance = stackEnvelopeThickness;
            if (preserveInnermostBridgeMemberGeometry)
            {
                var innermostBridgeMemberOffset =
                    ResolveInnermostBridgeMemberOffset(phaseRouteContext);
                bridgeReferenceStartPoint =
                    PlanarBendBusbarSetting.Translate(
                        bridgeStartPoint,
                        phaseRouteContext.RouteThicknessDirection.Normalized,
                        innermostBridgeMemberOffset);
                bridgeReferenceEndPoint =
                    PlanarBendBusbarSetting.Translate(
                        bridgeEndPoint,
                        phaseRouteContext.RouteThicknessDirection.Normalized,
                        innermostBridgeMemberOffset);
                bridgeOffsetDistance = singleBusbarThickness;
            }
            return new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = FaceToWallModelBusbarPlanner.GetPhaseName(
                    phaseIndex),
                StartBusbarVector =
                    phaseRouteContext.RouteThicknessDirection.Copy(),
                WidthVector = phaseRouteContext.RouteWidthDirection.Copy(),
                PointList = FaceToWallModelBusbarPlanner
                    .CreateBridgeReferenceCenterlinePoints(
                        bridgeReferenceStartPoint,
                        bridgeReferenceEndPoint,
                        axisSetting.LengthVector,
                        axisSetting.HeightVector,
                        phaseRouteContext.BusbarWidth,
                        stackEnvelopeThickness,
                        bridgeOffsetDistance,
                        bridgeOffsetDistance)
                    .Select(bridgeReferencePoint =>
                        bridgeReferencePoint.Copy())
                    .ToList(),
                BusBarWidth = phaseRouteContext.BusbarWidth,
                BusBarThickness = stackEnvelopeThickness,
                BarBendingRadius = phaseRouteContext.BendingRadius,
                BendAllowance = phaseRouteContext.BendAllowance,
                StartLapWidth = phaseRouteContext.BusbarWidth,
                EndLapWidth = phaseRouteContext.BusbarWidth
            };
        }

        /// <summary>
        /// 叠排桥式仍以最下侧成员作为“排宽+40 mm”直段和固定
        /// 50 mm 斜段的几何基准，其余成员只按相对排厚向外展开。
        /// </summary>
        private static IReadOnlyList<double>
            CreateBridgeStackMemberRelativeOffsets(
            FaceToWallPhaseRouteContext phaseRouteContext)
        {
            var innermostBridgeMemberOffset =
                ResolveInnermostBridgeMemberOffset(phaseRouteContext);
            return phaseRouteContext.StackMemberOffsets
                .Select(stackMemberOffset =>
                    stackMemberOffset - innermostBridgeMemberOffset)
                .ToList()
                .AsReadOnly();
        }

        private static double ResolveInnermostBridgeMemberOffset(
            FaceToWallPhaseRouteContext phaseRouteContext)
        {
            if (phaseRouteContext == null)
            {
                throw new ArgumentNullException(nameof(phaseRouteContext));
            }
            if (phaseRouteContext.StackMemberOffsets == null ||
                phaseRouteContext.StackMemberOffsets.Count == 0)
            {
                throw new InvalidOperationException(
                    "叠排桥式缺少有效的成员偏移。");
            }
            if (phaseRouteContext.RouteThicknessDirection == null ||
                phaseRouteContext.RouteThicknessDirection.Norm <=
                GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "叠排桥式缺少有效的排厚方向。");
            }
            if (phaseRouteContext.FrameOutwardNormal == null ||
                phaseRouteContext.FrameOutwardNormal.Norm <=
                GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "叠排桥式缺少有效的基架绝缘子外法向。");
            }
            if (phaseRouteContext.StackMemberOffsets.Any(stackMemberOffset =>
                    double.IsNaN(stackMemberOffset) ||
                    double.IsInfinity(stackMemberOffset)))
            {
                throw new InvalidOperationException(
                    "叠排桥式包含无效的成员偏移。");
            }

            var thicknessDirectionPointsOutward = phaseRouteContext
                .RouteThicknessDirection.Normalized.Dot(
                    phaseRouteContext.FrameOutwardNormal.Normalized) > 0d;
            return thicknessDirectionPointsOutward
                ? phaseRouteContext.StackMemberOffsets.Min()
                : phaseRouteContext.StackMemberOffsets.Max();
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            SplitStackEnvelopeTransitionSettings(
            IReadOnlyList<PlanarBendBusbarSetting>
                stackEnvelopeTransitionSettings,
            IReadOnlyList<double> stackMemberOffsets,
            double singleBusbarThickness,
            int phaseIndex)
        {
            if (stackEnvelopeTransitionSettings == null ||
                stackEnvelopeTransitionSettings.Count == 0)
            {
                throw new InvalidOperationException(
                    "特殊过渡排没有生成有效的叠排总厚度路径。");
            }
            if (stackMemberOffsets == null ||
                stackMemberOffsets.Count == 0)
            {
                throw new InvalidOperationException(
                    "特殊过渡排缺少叠排成员偏移。");
            }
            if (singleBusbarThickness <= 0d ||
                double.IsNaN(singleBusbarThickness) ||
                double.IsInfinity(singleBusbarThickness))
            {
                throw new InvalidOperationException(
                    "特殊过渡排缺少有效的单根排厚度。");
            }

            var splitTransitionSettings =
                new List<PlanarBendBusbarSetting>(
                    stackEnvelopeTransitionSettings.Count *
                    stackMemberOffsets.Count);
            for (var memberIndex = 0;
                memberIndex < stackMemberOffsets.Count;
                memberIndex++)
            {
                var memberGroupKey =
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "-" + (memberIndex + 1);
                foreach (var stackEnvelopeSetting in
                    stackEnvelopeTransitionSettings)
                {
                    if (stackEnvelopeSetting == null)
                    {
                        throw new InvalidOperationException(
                            "特殊过渡排包含空的叠排总厚度路径。");
                    }

                    var splitMemberSetting = stackEnvelopeSetting.Copy();
                    splitMemberSetting.GroupKey = memberGroupKey;
                    splitMemberSetting.BusBarThickness =
                        singleBusbarThickness;
                    splitMemberSetting.PointList =
                        GetOffsetCenterlinePreservingPointCount(
                            stackEnvelopeSetting,
                            stackMemberOffsets[memberIndex]);
                    splitTransitionSettings.Add(splitMemberSetting);
                }
            }

            return splitTransitionSettings.AsReadOnly();
        }

        private static List<Point3d>
            GetOffsetCenterlinePreservingPointCount(
            PlanarBendBusbarSetting groupCenterlineSetting,
            double memberOffset)
        {
            if (groupCenterlineSetting == null)
            {
                throw new ArgumentNullException(nameof(groupCenterlineSetting));
            }

            var compactCenterlinePoints = new List<Point3d>();
            var sourceToCompactPointIndexes = new List<int>();
            foreach (var sourcePoint in groupCenterlineSetting.PointList ??
                new List<Point3d>())
            {
                if (sourcePoint == null)
                {
                    throw new InvalidOperationException(
                        "特殊过渡排中心线包含空路径点。");
                }
                if (compactCenterlinePoints.Count == 0 ||
                    compactCenterlinePoints[compactCenterlinePoints.Count - 1]
                        .DistanceTo(sourcePoint) > 0.001d)
                {
                    compactCenterlinePoints.Add(sourcePoint.Copy());
                }

                sourceToCompactPointIndexes.Add(
                    compactCenterlinePoints.Count - 1);
            }

            var compactCenterlineSetting = groupCenterlineSetting.Copy();
            compactCenterlineSetting.PointList = compactCenterlinePoints;
            var offsetCompactPoints = compactCenterlineSetting
                .GetOffsetCenterlinePointList(memberOffset);
            if (offsetCompactPoints.Count != compactCenterlinePoints.Count)
            {
                throw new InvalidOperationException(
                    "特殊过渡排中心线偏移后无法保持原有路径点结构。");
            }

            return sourceToCompactPointIndexes
                .Select(compactPointIndex =>
                    offsetCompactPoints[compactPointIndex].Copy())
                .ToList();
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateSmallOffsetBridgeReferenceMembers(
            FaceToWallPhaseRouteContext phaseRouteContext,
            BusbarBridgeAxisSetting axisSetting,
            double transitionStartLengthCoordinate,
            double transitionEndLengthCoordinate,
            double phaseWidthCoordinate,
            int phaseIndex)
        {
            var bridgeStartPoint = CreatePointAtLengthAndWidth(
                phaseRouteContext.OrdinaryRouteReferencePoint,
                axisSetting,
                transitionStartLengthCoordinate,
                phaseWidthCoordinate);
            var bridgeEndPoint = CreatePointAtLengthAndWidth(
                phaseRouteContext.OrdinaryRouteReferencePoint,
                axisSetting,
                transitionEndLengthCoordinate,
                phaseWidthCoordinate);
            var bridgeReferenceCenterline = new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = FaceToWallModelBusbarPlanner.GetPhaseName(
                    phaseIndex),
                StartBusbarVector =
                    phaseRouteContext.RouteThicknessDirection.Copy(),
                WidthVector = phaseRouteContext.RouteWidthDirection.Copy(),
                PointList = FaceToWallModelBusbarPlanner
                    .CreateBridgeReferenceCenterlinePoints(
                        bridgeStartPoint,
                        bridgeEndPoint,
                        axisSetting.LengthVector,
                        axisSetting.HeightVector,
                        phaseRouteContext.BusbarWidth,
                        phaseRouteContext.BusbarThickness)
                    .Select(bridgeReferencePoint =>
                        bridgeReferencePoint.Copy())
                    .ToList(),
                BusBarWidth = phaseRouteContext.BusbarWidth,
                BusBarThickness = phaseRouteContext.BusbarThickness,
                BarBendingRadius = phaseRouteContext.BendingRadius,
                BendAllowance = phaseRouteContext.BendAllowance,
                StartLapWidth = phaseRouteContext.BusbarWidth,
                EndLapWidth = phaseRouteContext.BusbarWidth
            };
            return FaceToWallModelBusbarPlanner
                .CreateBridgeStackMemberSettings(
                    bridgeReferenceCenterline,
                    phaseRouteContext.StackMemberOffsets,
                    phaseRouteContext.FrameOutwardNormal,
                    phaseIndex);
        }

        private static PlanarBendBusbarSetting
            CreateMergedBridgeVerticalTransitionSetting(
            PlanarBendBusbarSetting bWidthBridgeMember,
            PlanarBendBusbarSetting aWidthBridgeMember,
            BusbarBridgeAxisSetting axisSetting,
            double bPhaseWidthCoordinate,
            double aPhaseWidthCoordinate,
            double signedWidthOffset,
            double singleBusbarThicknessForPhaseStagger,
            double fixedVerticalBendCenterLengthCoordinate,
            int phaseIndex)
        {
            ValidateBridgeTransitionMemberPair(
                bWidthBridgeMember,
                aWidthBridgeMember);
            var bBridgeInnerPoint = bWidthBridgeMember.PointList[2];
            var aBridgeInnerPoint = aWidthBridgeMember.PointList[3];
            var verticalBendPoints = CreateStaggeredVerticalBendPoints(
                bBridgeInnerPoint,
                aBridgeInnerPoint,
                axisSetting,
                bPhaseWidthCoordinate,
                aPhaseWidthCoordinate,
                signedWidthOffset,
                bWidthBridgeMember.BusBarWidth,
                singleBusbarThicknessForPhaseStagger,
                phaseIndex,
                fixedVerticalBendCenterLengthCoordinate);
            var mergedBridgeVerticalSetting = bWidthBridgeMember.Copy();
            mergedBridgeVerticalSetting.PointList = bWidthBridgeMember
                .PointList.Take(3)
                .Concat(verticalBendPoints)
                .Concat(aWidthBridgeMember.PointList.Skip(3))
                .Select(transitionPoint => transitionPoint.Copy())
                .ToList();
            mergedBridgeVerticalSetting.VerticalBendingRadius =
                bWidthBridgeMember.BusBarWidth / 4d;
            mergedBridgeVerticalSetting.IsVerticalBend = true;
            mergedBridgeVerticalSetting.PreviewBendPointKinds.Clear();
            return mergedBridgeVerticalSetting;
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateBridgeMilledTransitionSettings(
            PlanarBendBusbarSetting bWidthBridgeMember,
            PlanarBendBusbarSetting aWidthBridgeMember,
            BusbarBridgeAxisSetting axisSetting,
            double bPhaseWidthCoordinate,
            double aPhaseWidthCoordinate,
            double insulationClearance,
            int phaseIndex)
        {
            ValidateBridgeTransitionMemberPair(
                bWidthBridgeMember,
                aWidthBridgeMember);
            var bBridgeInnerPoint = bWidthBridgeMember.PointList[2];
            var aBridgeInnerPoint = aWidthBridgeMember.PointList[3];
            var bridgeInnerStartLengthCoordinate =
                BusbarGeometryUtility.Project(
                    bBridgeInnerPoint,
                    axisSetting.LengthVector);
            var bridgeInnerEndLengthCoordinate =
                BusbarGeometryUtility.Project(
                    aBridgeInnerPoint,
                    axisSetting.LengthVector);
            CalculateStaggeredMilledPlateRange(
                bridgeInnerStartLengthCoordinate,
                bridgeInnerEndLengthCoordinate,
                bWidthBridgeMember.BusBarWidth +
                MilledPlateAdditionalLength,
                aPhaseWidthCoordinate - bPhaseWidthCoordinate,
                insulationClearance,
                phaseIndex,
                out var milledPlateStartLengthCoordinate,
                out var milledPlateEndLengthCoordinate);

            var bBridgePlateConnectionPoint = CreatePointAtLengthAndWidth(
                bBridgeInnerPoint,
                axisSetting,
                milledPlateStartLengthCoordinate,
                bPhaseWidthCoordinate);
            var aBridgePlateConnectionPoint = CreatePointAtLengthAndWidth(
                aBridgeInnerPoint,
                axisSetting,
                milledPlateEndLengthCoordinate,
                aPhaseWidthCoordinate);
            var bBridgePiece = bWidthBridgeMember.Copy();
            bBridgePiece.PointList = bWidthBridgeMember.PointList.Take(3)
                .Concat(new[] { bBridgePlateConnectionPoint })
                .Select(transitionPoint => transitionPoint.Copy())
                .ToList();
            bBridgePiece.PreviewBendPointKinds.Clear();

            var aBridgePiece = aWidthBridgeMember.Copy();
            aBridgePiece.PointList = new[] { aBridgePlateConnectionPoint }
                .Concat(aWidthBridgeMember.PointList.Skip(3))
                .Select(transitionPoint => transitionPoint.Copy())
                .ToList();
            aBridgePiece.PreviewBendPointKinds.Clear();

            var milledBridgePiece = CreateMilledPlateSetting(
                bBridgePlateConnectionPoint,
                aBridgePlateConnectionPoint,
                axisSetting,
                bWidthBridgeMember,
                bPhaseWidthCoordinate,
                aPhaseWidthCoordinate,
                milledPlateStartLengthCoordinate,
                milledPlateEndLengthCoordinate);
            // 桥式铣排与直排铣排一样，最终是一根由三段几何相加得到的排。
            // 三段继承同一相/层 GroupKey，并显式标记后交给预览合并器处理。
            bBridgePiece.IsMilledBar = true;
            milledBridgePiece.IsMilledBar = true;
            aBridgePiece.IsMilledBar = true;

            // SL@草图1 是桥式铣排模板局部排长 X 方向上的切除位置。
            // 两端桥形虽包含斜段和折弯，但这里必须将总起点、连接点及总末点
            // 投影到统一排长轴后取坐标差，不能使用包含斜段长度和折弯补偿的展开长度。
            var bBridgeOverallStartLengthCoordinate =
                BusbarGeometryUtility.Project(
                    bWidthBridgeMember.PointList[0],
                    axisSetting.LengthVector);
            var aBridgeOverallEndLengthCoordinate =
                BusbarGeometryUtility.Project(
                    aWidthBridgeMember.PointList[
                        aWidthBridgeMember.PointList.Count - 1],
                    axisSetting.LengthVector);
            var bBridgeMilledCutLength =
                milledPlateStartLengthCoordinate -
                bBridgeOverallStartLengthCoordinate;
            var aBridgeMilledRemainingLength =
                aBridgeOverallEndLengthCoordinate -
                milledPlateEndLengthCoordinate;
            RecordMilledBarWidths(
                new[] { bBridgePiece, milledBridgePiece, aBridgePiece },
                bWidthBridgeMember.BusBarWidth,
                Math.Abs(aPhaseWidthCoordinate - bPhaseWidthCoordinate),
                bBridgeMilledCutLength,
                aBridgeMilledRemainingLength);
            FaceToWallPreviewTrace.Info(
                "面对面桥式铣排-模板切除位置",
                "相序索引=" + phaseIndex +
                "，左侧SL=" +
                bBridgeMilledCutLength.ToString("0.###") +
                "，右侧剩余长度=" +
                aBridgeMilledRemainingLength.ToString("0.###") +
                "，加宽段起点=" +
                milledPlateStartLengthCoordinate.ToString("0.###") +
                "，加宽段终点=" +
                milledPlateEndLengthCoordinate.ToString("0.###"));
            return new[]
            {
                bBridgePiece,
                milledBridgePiece,
                aBridgePiece
            };
        }

        private static void ValidateBridgeTransitionMemberPair(
            PlanarBendBusbarSetting bWidthBridgeMember,
            PlanarBendBusbarSetting aWidthBridgeMember)
        {
            if (bWidthBridgeMember?.PointList == null ||
                bWidthBridgeMember.PointList.Count != 6 ||
                aWidthBridgeMember?.PointList == null ||
                aWidthBridgeMember.PointList.Count != 6)
            {
                throw new InvalidOperationException(
                    "小偏移桥式中间过渡排两端必须使用完整六点桥式路径。");
            }
        }

        /// <summary>
        /// 按三相相序在可用的长度区间内错开中间连接点。相邻中间排中心距采用
        /// “排宽 + 界面绝缘距离”，保证实体边缘之间保留完整净绝缘距离。
        /// </summary>
        private static double ResolveStaggeredTransitionStepLength(
            double availableStartLengthCoordinate,
            double availableEndLengthCoordinate,
            double signedWidthOffset,
            double busbarWidth,
            double insulationClearance,
            int phaseIndex)
        {
            if (double.IsNaN(availableStartLengthCoordinate) ||
                double.IsInfinity(availableStartLengthCoordinate) ||
                double.IsNaN(availableEndLengthCoordinate) ||
                double.IsInfinity(availableEndLengthCoordinate) ||
                availableEndLengthCoordinate <=
                    availableStartLengthCoordinate + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移错开并行连接的可用长度区间无效。");
            }
            if (double.IsNaN(insulationClearance) ||
                double.IsInfinity(insulationClearance) ||
                insulationClearance < 0d)
            {
                throw new InvalidOperationException(
                    "小偏移错开并行连接的绝缘距离必须是有限非负数。");
            }
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            if (phaseIndex < 0 || phaseIndex >= PhaseCount)
            {
                throw new ArgumentOutOfRangeException(nameof(phaseIndex));
            }

            var availableCenterLengthCoordinate =
                (availableStartLengthCoordinate +
                 availableEndLengthCoordinate) / 2d;
            var phaseCenterOffset =
                phaseIndex - (PhaseCount - 1) / 2d;
            var orderingSign = signedWidthOffset < 0d ? -1d : 1d;
            var adjacentPhaseCenterSpacing =
                busbarWidth + insulationClearance;
            var staggeredStepLengthCoordinate =
                availableCenterLengthCoordinate +
                orderingSign * phaseCenterOffset *
                adjacentPhaseCenterSpacing;
            if (staggeredStepLengthCoordinate <=
                    availableStartLengthCoordinate + GeometryTolerance ||
                staggeredStepLengthCoordinate >=
                    availableEndLengthCoordinate - GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移错开并行连接的绝缘距离过大，特殊基架两侧没有足够的可用长度。");
            }

            return staggeredStepLengthCoordinate;
        }

        private static PlanarBendBusbarSetting
            CreateMiddleParallelStraightSetting(
            Point3d bConnectionPoint,
            Point3d aConnectionPoint,
            BusbarBridgeAxisSetting axisSetting,
            PlanarBendBusbarSetting settingPrototype,
            string transitionGroupKey)
        {
            if (bConnectionPoint == null || aConnectionPoint == null)
            {
                throw new ArgumentNullException(nameof(bConnectionPoint));
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            if (settingPrototype == null)
            {
                throw new ArgumentNullException(nameof(settingPrototype));
            }

            var middleParallelSetting = settingPrototype.Copy();
            middleParallelSetting.GroupKey = transitionGroupKey;
            var signedMiddleWidthOffset = BusbarGeometryUtility.Project(
                    aConnectionPoint,
                    axisSetting.WidthVector) -
                BusbarGeometryUtility.Project(
                    bConnectionPoint,
                    axisSetting.WidthVector);
            var middleWidthDirectionSign = Math.Sign(
                signedMiddleWidthOffset);
            if (middleWidthDirectionSign == 0d)
            {
                throw new InvalidOperationException(
                    "小偏移中间连接排两端没有形成有效的宽向偏移。");
            }

            var middleThicknessDirection = settingPrototype
                .StartBusbarVector.Normalized;
            // 三段垂直搭接按实体边界构造：中间排中心线在两侧排中心线之外
            // 各延长半个排宽，因此端头恰好到达两侧排的外侧宽边；中间排再沿
            // 排厚方向整体错开一个排厚，使相邻搭接面贴合而不发生实体重叠。
            var middleStartPoint = PlanarBendBusbarSetting.Translate(
                PlanarBendBusbarSetting.Translate(
                    bConnectionPoint,
                    axisSetting.WidthVector.Normalized,
                    -middleWidthDirectionSign *
                    settingPrototype.BusBarWidth / 2d),
                middleThicknessDirection,
                settingPrototype.BusBarThickness);
            var middleEndPoint = PlanarBendBusbarSetting.Translate(
                PlanarBendBusbarSetting.Translate(
                    aConnectionPoint,
                    axisSetting.WidthVector.Normalized,
                    middleWidthDirectionSign *
                    settingPrototype.BusBarWidth / 2d),
                middleThicknessDirection,
                settingPrototype.BusBarThickness);
            middleParallelSetting.PointList = new List<Point3d>
            {
                middleStartPoint,
                middleEndPoint
            };
            // 中间排沿长度方向的宽向量与两端排正交；路径方向仍由两点决定。
            middleParallelSetting.WidthVector = axisSetting.LengthVector.Copy();
            middleParallelSetting.PreviewBendPointKinds.Clear();
            FaceToWallPreviewTrace.Info(
                "面对面小偏移垂直搭接排-点集",
                "组=" + transitionGroupKey +
                "，规格=" + settingPrototype.BusBarWidth.ToString("0.###") +
                "x" + settingPrototype.BusBarThickness.ToString("0.###") +
                "，中间排两端各外延=" +
                (settingPrototype.BusBarWidth / 2d).ToString("0.###") +
                "，排厚错层=" +
                settingPrototype.BusBarThickness.ToString("0.###") +
                "，P1=" + FormatPoint(middleStartPoint) +
                "，P2=" + FormatPoint(middleEndPoint));
            return middleParallelSetting;
        }

        /// <summary>
        /// 桥式小偏移超过一个排宽时，保留两端桥式直段，另加一根沿宽向的
        /// 中间直排。三根排分别生成，避免把相邻相序合并到同一根斜排中。
        /// </summary>
        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateBridgeMiddleParallelTransitionSettings(
            PlanarBendBusbarSetting bWidthBridgeMember,
            PlanarBendBusbarSetting aWidthBridgeMember,
            BusbarBridgeAxisSetting axisSetting,
            double bPhaseWidthCoordinate,
            double aPhaseWidthCoordinate,
            double signedWidthOffset,
            double insulationClearance,
            int phaseIndex)
        {
            ValidateBridgeTransitionMemberPair(
                bWidthBridgeMember,
                aWidthBridgeMember);
            var bBridgeInnerPoint = bWidthBridgeMember.PointList[2];
            var aBridgeInnerPoint = aWidthBridgeMember.PointList[3];
            var bridgeInnerStartLengthCoordinate =
                BusbarGeometryUtility.Project(
                    bBridgeInnerPoint,
                    axisSetting.LengthVector);
            var bridgeInnerEndLengthCoordinate =
                BusbarGeometryUtility.Project(
                    aBridgeInnerPoint,
                    axisSetting.LengthVector);
            var staggeredStepLengthCoordinate =
                ResolveStaggeredTransitionStepLength(
                    bridgeInnerStartLengthCoordinate +
                        bWidthBridgeMember.BusBarWidth / 2d,
                    bridgeInnerEndLengthCoordinate -
                        bWidthBridgeMember.BusBarWidth / 2d,
                    signedWidthOffset,
                    bWidthBridgeMember.BusBarWidth,
                    insulationClearance,
                    phaseIndex);
            var bConnectionPoint = CreatePointAtLengthAndWidth(
                bBridgeInnerPoint,
                axisSetting,
                staggeredStepLengthCoordinate,
                bPhaseWidthCoordinate);
            var aConnectionPoint = CreatePointAtLengthAndWidth(
                aBridgeInnerPoint,
                axisSetting,
                staggeredStepLengthCoordinate,
                aPhaseWidthCoordinate);
            var bLongitudinalOverlapPoint = CreatePointAtLengthAndWidth(
                bBridgeInnerPoint,
                axisSetting,
                staggeredStepLengthCoordinate +
                    bWidthBridgeMember.BusBarWidth / 2d,
                bPhaseWidthCoordinate);
            var aLongitudinalOverlapPoint = CreatePointAtLengthAndWidth(
                aBridgeInnerPoint,
                axisSetting,
                staggeredStepLengthCoordinate -
                    aWidthBridgeMember.BusBarWidth / 2d,
                aPhaseWidthCoordinate);

            var bBridgePiece = bWidthBridgeMember.Copy();
            bBridgePiece.PointList = bWidthBridgeMember.PointList.Take(3)
                .Concat(new[] { bLongitudinalOverlapPoint })
                .Select(transitionPoint => transitionPoint.Copy())
                .ToList();
            bBridgePiece.PreviewBendPointKinds.Clear();

            var aBridgePiece = aWidthBridgeMember.Copy();
            aBridgePiece.PointList = new[] { aLongitudinalOverlapPoint }
                .Concat(aWidthBridgeMember.PointList.Skip(3))
                .Select(transitionPoint => transitionPoint.Copy())
                .ToList();
            aBridgePiece.PreviewBendPointKinds.Clear();

            var middleParallelPiece =
                CreateMiddleParallelStraightSetting(
                    bConnectionPoint,
                    aConnectionPoint,
                    axisSetting,
                    bWidthBridgeMember,
                    (bWidthBridgeMember.GroupKey ??
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex)) +
                    "-中间");
            return new[]
            {
                bBridgePiece,
                middleParallelPiece,
                aBridgePiece
            };
        }

        /// <summary>
        /// 普通直排小偏移超过一个排宽时的错开并行连接：B 侧直段、宽向
        /// 中间直排、A 侧直段各自独立生成，三相中间排按绝缘距离错开。
        /// </summary>
        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateOrdinaryMiddleParallelTransitionSettings(
            Point3d transitionMemberStartPoint,
            Point3d transitionMemberEndPoint,
            BusbarBridgeAxisSetting axisSetting,
            FaceToWallPhaseRouteContext phaseRouteContext,
            double signedWidthOffset,
            double insulationClearance,
            int phaseIndex,
            string transitionGroupKey)
        {
            if (transitionMemberStartPoint == null ||
                transitionMemberEndPoint == null)
            {
                throw new ArgumentNullException(
                    nameof(transitionMemberStartPoint));
            }
            if (phaseRouteContext == null)
            {
                throw new ArgumentNullException(nameof(phaseRouteContext));
            }

            var transitionStartLengthCoordinate =
                BusbarGeometryUtility.Project(
                    transitionMemberStartPoint,
                    axisSetting.LengthVector);
            var transitionEndLengthCoordinate =
                BusbarGeometryUtility.Project(
                    transitionMemberEndPoint,
                    axisSetting.LengthVector);
            var bPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                transitionMemberStartPoint,
                axisSetting.WidthVector);
            var aPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                transitionMemberEndPoint,
                axisSetting.WidthVector);
            if (Math.Abs(signedWidthOffset -
                    (aPhaseWidthCoordinate - bPhaseWidthCoordinate)) >
                ModelAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移错开并行排的实际两端宽向偏移与统一偏移不一致。");
            }

            var availableStartLengthCoordinate =
                transitionStartLengthCoordinate +
                phaseRouteContext.BusbarWidth / 2d;
            var availableEndLengthCoordinate =
                transitionEndLengthCoordinate -
                phaseRouteContext.BusbarWidth / 2d;
            var staggeredStepLengthCoordinate =
                ResolveStaggeredTransitionStepLength(
                    availableStartLengthCoordinate,
                    availableEndLengthCoordinate,
                    signedWidthOffset,
                    phaseRouteContext.BusbarWidth,
                    insulationClearance,
                    phaseIndex);
            var bConnectionPoint = CreatePointAtLengthAndWidth(
                transitionMemberStartPoint,
                axisSetting,
                staggeredStepLengthCoordinate,
                bPhaseWidthCoordinate);
            var aConnectionPoint = CreatePointAtLengthAndWidth(
                transitionMemberEndPoint,
                axisSetting,
                staggeredStepLengthCoordinate,
                aPhaseWidthCoordinate);
            var bLongitudinalOverlapPoint = CreatePointAtLengthAndWidth(
                transitionMemberStartPoint,
                axisSetting,
                staggeredStepLengthCoordinate +
                    phaseRouteContext.BusbarWidth / 2d,
                bPhaseWidthCoordinate);
            var aLongitudinalOverlapPoint = CreatePointAtLengthAndWidth(
                transitionMemberEndPoint,
                axisSetting,
                staggeredStepLengthCoordinate -
                    phaseRouteContext.BusbarWidth / 2d,
                aPhaseWidthCoordinate);
            var settingPrototype = CreateSmallOffsetFlatSetting(
                transitionMemberStartPoint,
                transitionMemberEndPoint,
                phaseRouteContext,
                transitionGroupKey);
            var bStraightPiece = settingPrototype.Copy();
            bStraightPiece.PointList = new List<Point3d>
            {
                transitionMemberStartPoint.Copy(),
                bLongitudinalOverlapPoint
            };
            var aStraightPiece = settingPrototype.Copy();
            aStraightPiece.PointList = new List<Point3d>
            {
                aLongitudinalOverlapPoint,
                transitionMemberEndPoint.Copy()
            };
            var middleParallelPiece =
                CreateMiddleParallelStraightSetting(
                    bConnectionPoint,
                    aConnectionPoint,
                    axisSetting,
                    settingPrototype,
                    transitionGroupKey + "-中间");
            // 首段仅入口做桥头，主体落回原层；中间横排垫高一排厚，末段直排回原层。
            // 三处搭接面贴合，横向短连接排和末段不承担桥头长度约束。
            bStraightPiece = CreateFixedSpecialFrontBridges(
                new[] { bStraightPiece }, axisSetting.HeightVector).Single();
            return new[]
            {
                bStraightPiece,
                middleParallelPiece,
                aStraightPiece
            };
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateOrdinaryMilledTransitionSettings(
            Point3d transitionMemberStartPoint,
            Point3d transitionMemberEndPoint,
            BusbarBridgeAxisSetting axisSetting,
            FaceToWallPhaseRouteContext phaseRouteContext,
            double signedWidthOffset,
            double insulationClearance,
            int phaseIndex,
            string transitionGroupKey)
        {
            var transitionStartLengthCoordinate =
                BusbarGeometryUtility.Project(
                    transitionMemberStartPoint,
                    axisSetting.LengthVector);
            var transitionEndLengthCoordinate =
                BusbarGeometryUtility.Project(
                    transitionMemberEndPoint,
                    axisSetting.LengthVector);
            CalculateStaggeredMilledPlateRange(
                transitionStartLengthCoordinate,
                transitionEndLengthCoordinate,
                phaseRouteContext.BusbarWidth +
                MilledPlateAdditionalLength,
                signedWidthOffset,
                insulationClearance,
                phaseIndex,
                out var milledPlateStartLengthCoordinate,
                out var milledPlateEndLengthCoordinate);
            var bPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                transitionMemberStartPoint,
                axisSetting.WidthVector);
            var aPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                transitionMemberEndPoint,
                axisSetting.WidthVector);
            var bPlateConnectionPoint = CreatePointAtLengthAndWidth(
                transitionMemberStartPoint,
                axisSetting,
                milledPlateStartLengthCoordinate,
                bPhaseWidthCoordinate);
            var aPlateConnectionPoint = CreatePointAtLengthAndWidth(
                transitionMemberEndPoint,
                axisSetting,
                milledPlateEndLengthCoordinate,
                aPhaseWidthCoordinate);
            var settingPrototype = CreateSmallOffsetFlatSetting(
                transitionMemberStartPoint,
                transitionMemberEndPoint,
                phaseRouteContext,
                transitionGroupKey);
            var bStraightPiece = settingPrototype.Copy();
            bStraightPiece.PointList = new List<Point3d>
            {
                transitionMemberStartPoint.Copy(),
                bPlateConnectionPoint.Copy()
            };
            var aStraightPiece = settingPrototype.Copy();
            aStraightPiece.PointList = new List<Point3d>
            {
                aPlateConnectionPoint.Copy(),
                transitionMemberEndPoint.Copy()
            };
            var milledPlatePiece = CreateMilledPlateSetting(
                bPlateConnectionPoint,
                aPlateConnectionPoint,
                axisSetting,
                settingPrototype,
                bPhaseWidthCoordinate,
                aPhaseWidthCoordinate,
                milledPlateStartLengthCoordinate,
                milledPlateEndLengthCoordinate);
            // 铣排预览固定由三段普通直排组成：两侧原规格直排与中间
            // 加宽直排。中间段沿排长方向固定为“排宽 + 20 mm”，三段生成
            // Body 后按同一 GroupKey 相加成一个可选中的铣排预览体。
            bStraightPiece.IsMilledBar = true;
            milledPlatePiece.IsMilledBar = true;
            aStraightPiece.IsMilledBar = true;
            RecordMilledBarWidths(
                new[] { bStraightPiece, milledPlatePiece, aStraightPiece },
                settingPrototype.BusBarWidth,
                Math.Abs(aPhaseWidthCoordinate - bPhaseWidthCoordinate),
                bStraightPiece.GetDevelopedLength(),
                aStraightPiece.GetDevelopedLength());
            if (Math.Abs(signedWidthOffset -
                    (aPhaseWidthCoordinate - bPhaseWidthCoordinate)) >
                ModelAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移铣排的实际两端宽向偏移与统一偏移不一致。");
            }

            return new[]
            {
                bStraightPiece,
                milledPlatePiece,
                aStraightPiece
            };
        }

        private static PlanarBendBusbarSetting
            CreateOrdinaryVerticalTransitionSetting(
            Point3d transitionMemberStartPoint,
            Point3d transitionMemberEndPoint,
            BusbarBridgeAxisSetting axisSetting,
            FaceToWallPhaseRouteContext phaseRouteContext,
            double signedWidthOffset,
            double singleBusbarThicknessForPhaseStagger,
            int phaseIndex,
            string transitionGroupKey,
            double? fixedVerticalBendCenterLengthCoordinate = null)
        {
            var bPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                transitionMemberStartPoint,
                axisSetting.WidthVector);
            var aPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                transitionMemberEndPoint,
                axisSetting.WidthVector);
            var verticalBendPoints = CreateStaggeredVerticalBendPoints(
                transitionMemberStartPoint,
                transitionMemberEndPoint,
                axisSetting,
                bPhaseWidthCoordinate,
                aPhaseWidthCoordinate,
                signedWidthOffset,
                phaseRouteContext.BusbarWidth,
                singleBusbarThicknessForPhaseStagger,
                phaseIndex,
                fixedVerticalBendCenterLengthCoordinate);
            var ordinaryVerticalTransitionSetting =
                new PlanarBendBusbarSetting
            {
                IsCenter = true,
                IsVerticalBend = true,
                GroupKey = transitionGroupKey,
                StartBusbarVector =
                    phaseRouteContext.RouteThicknessDirection.Copy(),
                WidthVector =
                    phaseRouteContext.RouteWidthDirection.Copy(),
                PointList = new List<Point3d>
                {
                    transitionMemberStartPoint.Copy(),
                    verticalBendPoints[0],
                    verticalBendPoints[1],
                    transitionMemberEndPoint.Copy()
                },
                BusBarWidth = phaseRouteContext.BusbarWidth,
                BusBarThickness = phaseRouteContext.BusbarThickness,
                BarBendingRadius = phaseRouteContext.BendingRadius,
                VerticalBendingRadius = phaseRouteContext.BusbarWidth / 4d,
                BendAllowance = phaseRouteContext.BendAllowance,
                StartLapWidth = phaseRouteContext.BusbarWidth,
                EndLapWidth = phaseRouteContext.BusbarWidth
            };
            return ordinaryVerticalTransitionSetting;
        }

        /// <summary>
        /// 在两侧既有路径之间插入一段真正的立弯斜段。两个折弯点的空间
        /// 距离固定为两个排宽；B 相居中，A/C 相按一个排厚沿排长方向
        /// 反向错开，使三相斜段保持平行且不在同一折弯截面上。
        /// </summary>
        private static IReadOnlyList<Point3d>
            CreateStaggeredVerticalBendPoints(
            Point3d bSideReferencePoint,
            Point3d aSideReferencePoint,
            BusbarBridgeAxisSetting axisSetting,
            double bSideWidthCoordinate,
            double aSideWidthCoordinate,
            double signedWidthOffset,
            double busbarWidth,
            double singleBusbarThicknessForPhaseStagger,
            int phaseIndex,
            double? fixedVerticalBendCenterLengthCoordinate)
        {
            if (bSideReferencePoint == null || aSideReferencePoint == null)
            {
                throw new ArgumentNullException(nameof(bSideReferencePoint));
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            ValidatePositiveFinite(
                singleBusbarThicknessForPhaseStagger,
                nameof(singleBusbarThicknessForPhaseStagger));
            if (phaseIndex < 0 || phaseIndex >= PhaseCount)
            {
                throw new ArgumentOutOfRangeException(nameof(phaseIndex));
            }

            var actualSignedWidthOffset =
                aSideWidthCoordinate - bSideWidthCoordinate;
            if (Math.Abs(actualSignedWidthOffset - signedWidthOffset) >
                ModelAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移立弯两端的实际排宽方向偏移与统一偏移不一致。");
            }

            var requestedVerticalBendLength = 2d * busbarWidth;
            var widthOffsetMagnitude = Math.Abs(actualSignedWidthOffset);
            if (widthOffsetMagnitude >=
                requestedVerticalBendLength - GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移立弯的排宽方向偏移不小于两个排宽，无法按两个排宽生成中间斜段。");
            }
            var verticalBendLengthProjection = Math.Sqrt(
                requestedVerticalBendLength * requestedVerticalBendLength -
                widthOffsetMagnitude * widthOffsetMagnitude);

            var bSideLengthCoordinate = BusbarGeometryUtility.Project(
                bSideReferencePoint,
                axisSetting.LengthVector);
            var aSideLengthCoordinate = BusbarGeometryUtility.Project(
                aSideReferencePoint,
                axisSetting.LengthVector);
            var availableVerticalBendLength =
                aSideLengthCoordinate - bSideLengthCoordinate;
            var phaseStaggerDirection = signedWidthOffset < 0d ? -1d : 1d;
            var phaseCenteredIndex = phaseIndex - (PhaseCount - 1) / 2d;
            var baseVerticalBendCenterLengthCoordinate =
                fixedVerticalBendCenterLengthCoordinate ??
                (bSideLengthCoordinate + aSideLengthCoordinate) / 2d;
            var verticalBendCenterLengthCoordinate =
                baseVerticalBendCenterLengthCoordinate +
                phaseStaggerDirection * phaseCenteredIndex *
                singleBusbarThicknessForPhaseStagger;
            var firstVerticalBendLengthCoordinate =
                verticalBendCenterLengthCoordinate -
                verticalBendLengthProjection / 2d;
            var secondVerticalBendLengthCoordinate =
                verticalBendCenterLengthCoordinate +
                verticalBendLengthProjection / 2d;
            if (availableVerticalBendLength <=
                    verticalBendLengthProjection + GeometryTolerance ||
                firstVerticalBendLengthCoordinate <=
                    bSideLengthCoordinate + GeometryTolerance ||
                secondVerticalBendLengthCoordinate >=
                    aSideLengthCoordinate - GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移立弯两侧没有足够长度容纳三相错开的两个排宽中间斜段。");
            }

            var firstVerticalBendPoint = CreatePointAtLengthAndWidth(
                bSideReferencePoint,
                axisSetting,
                firstVerticalBendLengthCoordinate,
                bSideWidthCoordinate);
            var secondVerticalBendPoint = CreatePointAtLengthAndWidth(
                aSideReferencePoint,
                axisSetting,
                secondVerticalBendLengthCoordinate,
                aSideWidthCoordinate);
            var actualVerticalBendLength = firstVerticalBendPoint.DistanceTo(
                secondVerticalBendPoint);
            if (Math.Abs(actualVerticalBendLength -
                    requestedVerticalBendLength) > 0.001d)
            {
                throw new InvalidOperationException(
                    "小偏移立弯两个折弯点的实际距离不是两个排宽。实际=" +
                    actualVerticalBendLength.ToString("0.###") +
                    " mm，目标=" +
                    requestedVerticalBendLength.ToString("0.###") + " mm。");
            }

            FaceToWallPreviewTrace.Info(
                "面对面小偏移立弯-中间折弯点",
                "相=" + FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                "，相位错开=" +
                (phaseStaggerDirection * phaseCenteredIndex *
                 singleBusbarThicknessForPhaseStagger).ToString("0.###") +
                " mm，B相固定中心=" +
                baseVerticalBendCenterLengthCoordinate.ToString("0.###") +
                " mm，目标斜段长度=" +
                requestedVerticalBendLength.ToString("0.###") +
                " mm，实际斜段长度=" +
                actualVerticalBendLength.ToString("0.###") +
                " mm，P1=" + FormatPoint(firstVerticalBendPoint) +
                "，P2=" + FormatPoint(secondVerticalBendPoint));

            return new[]
            {
                firstVerticalBendPoint,
                secondVerticalBendPoint
            };
        }

        private static PlanarBendBusbarSetting CreateSmallOffsetFlatSetting(
            Point3d startPoint,
            Point3d endPoint,
            FaceToWallPhaseRouteContext phaseRouteContext,
            string transitionGroupKey)
        {
            return new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = transitionGroupKey,
                StartBusbarVector =
                    phaseRouteContext.RouteThicknessDirection.Copy(),
                WidthVector = phaseRouteContext.RouteWidthDirection.Copy(),
                PointList = new List<Point3d>
                {
                    startPoint.Copy(),
                    endPoint.Copy()
                },
                BusBarWidth = phaseRouteContext.BusbarWidth,
                BusBarThickness = phaseRouteContext.BusbarThickness,
                BarBendingRadius = phaseRouteContext.BendingRadius,
                BendAllowance = phaseRouteContext.BendAllowance,
                StartLapWidth = phaseRouteContext.BusbarWidth,
                EndLapWidth = phaseRouteContext.BusbarWidth
            };
        }

        private static PlanarBendBusbarSetting CreateMilledPlateSetting(
            Point3d bPlateConnectionPoint,
            Point3d aPlateConnectionPoint,
            BusbarBridgeAxisSetting axisSetting,
            PlanarBendBusbarSetting settingPrototype,
            double bPhaseWidthCoordinate,
            double aPhaseWidthCoordinate,
            double milledPlateStartLengthCoordinate,
            double milledPlateEndLengthCoordinate)
        {
            var milledPlateCenterWidthCoordinate =
                (bPhaseWidthCoordinate + aPhaseWidthCoordinate) / 2d;
            var milledPlateStartPoint = CreatePointAtLengthAndWidth(
                bPlateConnectionPoint,
                axisSetting,
                milledPlateStartLengthCoordinate,
                milledPlateCenterWidthCoordinate);
            var milledPlateEndPoint = CreatePointAtLengthAndWidth(
                aPlateConnectionPoint,
                axisSetting,
                milledPlateEndLengthCoordinate,
                milledPlateCenterWidthCoordinate);
            var milledPlateSetting = settingPrototype.Copy();
            milledPlateSetting.PointList = new List<Point3d>
            {
                milledPlateStartPoint,
                milledPlateEndPoint
            };
            milledPlateSetting.BusBarWidth =
                settingPrototype.BusBarWidth +
                Math.Abs(aPhaseWidthCoordinate - bPhaseWidthCoordinate);
            milledPlateSetting.PreviewBendPointKinds.Clear();
            return milledPlateSetting;
        }

        private static void RecordMilledBarWidths(
            IEnumerable<PlanarBendBusbarSetting> milledBarSegments,
            double originalBusbarWidth,
            double additionalBusbarWidth,
            double milledBarStartLength,
            double milledBarEndLength)
        {
            foreach (var milledBarSegment in milledBarSegments)
            {
                milledBarSegment.MilledBarOriginalWidth = originalBusbarWidth;
                milledBarSegment.MilledBarAdditionalWidth =
                    additionalBusbarWidth;
                milledBarSegment.MilledBarStartLength =
                    milledBarStartLength;
                milledBarSegment.MilledBarEndLength = milledBarEndLength;
            }
        }

        private static void CalculateStaggeredMilledPlateRange(
            double availableStartLengthCoordinate,
            double availableEndLengthCoordinate,
            double milledPlateLength,
            double signedWidthOffset,
            double insulationClearance,
            int phaseIndex,
            out double milledPlateStartLengthCoordinate,
            out double milledPlateEndLengthCoordinate)
        {
            if (phaseIndex < 0 || phaseIndex >= PhaseCount)
            {
                throw new ArgumentOutOfRangeException(nameof(phaseIndex));
            }
            ValidatePositiveFinite(insulationClearance,
                nameof(insulationClearance));

            var availableTransitionLength =
                availableEndLengthCoordinate -
                availableStartLengthCoordinate;
            if (availableTransitionLength <=
                milledPlateLength + 2d * GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "小偏移铣排中间可用长度不足以容纳“排宽 + 20 mm”的加宽短排。");
            }

            var availableLengthCenter =
                (availableStartLengthCoordinate +
                 availableEndLengthCoordinate) / 2d;
            var phaseCenteredIndex =
                phaseIndex - (PhaseCount - 1) / 2d;
            // 先移动偏移方向最外侧的相，避免加宽区靠近尚未移动的邻相。
            var phaseStaggerDirection = signedWidthOffset < 0d ? 1d : -1d;
            // 相邻铣削区必须沿桥向留净距，不能只让区间首尾相接。
            var phaseStaggerSpacing = milledPlateLength + insulationClearance;
            var phaseMilledPlateCenter = availableLengthCenter +
                phaseStaggerDirection * phaseCenteredIndex *
                phaseStaggerSpacing;
            milledPlateStartLengthCoordinate =
                phaseMilledPlateCenter - milledPlateLength / 2d;
            milledPlateEndLengthCoordinate =
                phaseMilledPlateCenter + milledPlateLength / 2d;
            if (milledPlateStartLengthCoordinate <=
                    availableStartLengthCoordinate + GeometryTolerance ||
                milledPlateEndLengthCoordinate >=
                    availableEndLengthCoordinate - GeometryTolerance)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相小偏移铣排的可用长度不足以容纳错开后的铣削区。");
            }

            FaceToWallPreviewTrace.Info(
                "面对面小偏移铣排-相序错开",
                "相=" + FaceToWallModelBusbarPlanner.GetPhaseName(
                    phaseIndex) +
                "，B相基准中心=" +
                availableLengthCenter.ToString("0.###") +
                "，错开量=" +
                (phaseMilledPlateCenter - availableLengthCenter)
                    .ToString("0.###") +
                "，铣削区=" +
                milledPlateStartLengthCoordinate.ToString("0.###") +
                "~" + milledPlateEndLengthCoordinate.ToString("0.###"));
        }

        /// <summary>
        /// 第一种侧对侧以中间排为定位基准：三相分别使用已经按
        /// “最近外相/中间B相/最外相”分配后的首末基架绝缘子坐标。
        /// 先生成能搭住两侧柜顶首段的直排候选，只有候选叠排实体
        /// 搭不到绝缘子连接面时才改成桥式；两端仍只覆盖一个排宽。
        /// </summary>
        private static bool ShouldUseSideToSideFixedBridgeMiddleBusbar(
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            SideToSideFixedPhaseAssignments fixedPhaseAssignments,
            BusbarBridgeAxisSetting axisSetting)
        {
            var useBridgeByPhase = new List<bool>();
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var middleLapAlignment =
                    ResolveSideToSideFixedMiddleLapAlignment(
                        bCabinetContext,
                        aCabinetContext,
                        fixedPhaseAssignments.BCabinet
                            .BaseInsulatorsByPhase[phaseIndex],
                        fixedPhaseAssignments.ACabinet
                            .BaseInsulatorsByPhase[phaseIndex],
                        fixedPhaseAssignments.BCabinet
                            .FrameInsulatorsByPhase[phaseIndex],
                        fixedPhaseAssignments.ACabinet
                            .FrameInsulatorsByPhase[phaseIndex],
                        axisSetting,
                        phaseIndex);
                useBridgeByPhase.Add(middleLapAlignment.RequiresBridge);

                FaceToWallPreviewTrace.Info(
                    "侧对侧第一种-中间排类型逐相判断",
                    "相=" + FaceToWallModelBusbarPlanner.GetPhaseName(
                        phaseIndex) +
                    "，柜顶候选中心层=" +
                    middleLapAlignment.StraightRouteThicknessCoordinate
                        .ToString("0.###") +
                    "，柜顶末层(B/A)=" +
                    middleLapAlignment.BCabinetTerminalThicknessCoordinate
                        .ToString("0.###") + "/" +
                    middleLapAlignment.ACabinetTerminalThicknessCoordinate
                        .ToString("0.###") +
                    "，绝缘子搭接中心层=" +
                    middleLapAlignment.InsulatorRouteThicknessCoordinate
                        .ToString("0.###") +
                    "，B端实体可搭接=" +
                    middleLapAlignment
                        .BCabinetStraightRouteCanLapInsulator +
                    "，A端实体可搭接=" +
                    middleLapAlignment
                        .ACabinetStraightRouteCanLapInsulator +
                    "，基座/基架配合面法向平行=" +
                    middleLapAlignment.BaseAndFrameInsulatorNormalsAligned +
                    "，B端桥式偏移=" +
                    middleLapAlignment.BBridgeOffsetDistance
                        .ToString("0.###") +
                    "，A端桥式偏移=" +
                    middleLapAlignment.ABridgeOffsetDistance
                        .ToString("0.###") +
                    "，类型=" + (middleLapAlignment.RequiresBridge
                        ? "桥式直排"
                        : "直排"));
            }

            if (useBridgeByPhase.Distinct().Count() != 1)
            {
                throw new InvalidOperationException(
                    "侧对侧第一种三相柜顶首段与基架绝缘子平面的层位关系不一致，无法共用同一种中间排型式。");
            }

            return useBridgeByPhase[0];
        }

        /// <summary>
        /// 先以两侧柜顶首段末端确定直排候选层，再按叠排每个实体的
        /// 厚度范围检查是否实际触达基架绝缘子连接面。只有直排实体
        /// 搭接不上绝缘子时，才把中间长直段沿排厚方向移到绝缘子层。
        /// </summary>
        private static SideToSideFixedMiddleLapAlignment
            ResolveSideToSideFixedMiddleLapAlignment(
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            InsulatorConnectionInfo bBaseInsulator,
            InsulatorConnectionInfo aBaseInsulator,
            InsulatorConnectionInfo bFrameInsulator,
            InsulatorConnectionInfo aFrameInsulator,
            BusbarBridgeAxisSetting axisSetting,
            int phaseIndex)
        {
            var bTerminalCenter = BusbarGeometryUtility.Average(
                bCabinetContext.CabinetConnectionBusbarsByPhase[phaseIndex]
                    .Select(busbar => busbar.PointList.LastOrDefault())
                    .Where(point => point != null));
            var aTerminalCenter = BusbarGeometryUtility.Average(
                aCabinetContext.CabinetConnectionBusbarsByPhase[phaseIndex]
                    .Select(busbar => busbar.PointList.LastOrDefault())
                    .Where(point => point != null));
            if (bTerminalCenter == null || aTerminalCenter == null)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相缺少可用于判断直排/桥式直排的柜顶首段末点。");
            }
            if (bFrameInsulator?.ConnectionCircleCenter == null ||
                aFrameInsulator?.ConnectionCircleCenter == null)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相缺少基架绝缘子连接圆心，无法判断中间排实际搭接。");
            }
            if (bBaseInsulator?.ConnectionCircleCenter == null ||
                aBaseInsulator?.ConnectionCircleCenter == null)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相缺少基座绝缘子连接圆心，无法判断基座/基架是否同平面。 ");
            }

            var bRouteContext =
                bCabinetContext.PhaseRouteContexts[phaseIndex];
            var aRouteContext =
                aCabinetContext.PhaseRouteContexts[phaseIndex];
            if (bRouteContext.StackMemberOffsets == null ||
                bRouteContext.StackMemberOffsets.Count == 0)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相缺少中间叠排成员偏移，无法判断绝缘子搭接。");
            }
            var routeThicknessDirection = axisSetting.LengthVector
                .Cross(axisSetting.HeightVector).Normalized;
            if (routeThicknessDirection.Dot(
                    bRouteContext.FrameOutwardNormal) < 0d)
            {
                routeThicknessDirection = routeThicknessDirection.Negate();
            }
            if (!BusbarGeometryUtility.AreParallel(
                    routeThicknessDirection,
                    bRouteContext.FrameOutwardNormal) ||
                !BusbarGeometryUtility.AreParallel(
                    routeThicknessDirection,
                    aRouteContext.FrameOutwardNormal))
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相侧对侧第一种中间排厚度方向与基架绝缘子法向不平行。");
            }

            var bTerminalThicknessCoordinate =
                BusbarGeometryUtility.Project(
                    bTerminalCenter,
                    routeThicknessDirection);
            var aTerminalThicknessCoordinate =
                BusbarGeometryUtility.Project(
                    aTerminalCenter,
                    routeThicknessDirection);
            var straightRouteThicknessCoordinate =
                (bTerminalThicknessCoordinate +
                 aTerminalThicknessCoordinate) / 2d;
            var cabinetTerminalPlanesAligned = Math.Abs(
                bTerminalThicknessCoordinate -
                aTerminalThicknessCoordinate) <=
                SideToSidePhysicalAlignmentTolerance;

            var middleStackProjectedOffsets = bRouteContext
                .StackMemberOffsets
                .Select(stackMemberOffset => stackMemberOffset *
                    bRouteContext.RouteThicknessDirection.Normalized.Dot(
                        routeThicknessDirection))
                .ToList();

            var bInsulatorPlaneCoordinate = BusbarGeometryUtility.Project(
                bFrameInsulator.ConnectionCircleCenter,
                routeThicknessDirection);
            var aInsulatorPlaneCoordinate = BusbarGeometryUtility.Project(
                aFrameInsulator.ConnectionCircleCenter,
                routeThicknessDirection);
            var baseAndFrameInsulatorNormalsAligned =
                bBaseInsulator.ConnectionPlane?.Normal != null &&
                aBaseInsulator.ConnectionPlane?.Normal != null &&
                bFrameInsulator.ConnectionPlane?.Normal != null &&
                aFrameInsulator.ConnectionPlane?.Normal != null &&
                BusbarGeometryUtility.AreParallel(
                    bBaseInsulator.ConnectionPlane.Normal,
                    bFrameInsulator.ConnectionPlane.Normal) &&
                BusbarGeometryUtility.AreParallel(
                    aBaseInsulator.ConnectionPlane.Normal,
                    aFrameInsulator.ConnectionPlane.Normal);
            var bCanLapInsulator = cabinetTerminalPlanesAligned &&
                CanSideToSideStackLapConnectionPlane(
                    straightRouteThicknessCoordinate,
                    middleStackProjectedOffsets,
                    bRouteContext.BusbarThickness,
                    bInsulatorPlaneCoordinate);
            var aCanLapInsulator = cabinetTerminalPlanesAligned &&
                CanSideToSideStackLapConnectionPlane(
                    straightRouteThicknessCoordinate,
                    middleStackProjectedOffsets,
                    bRouteContext.BusbarThickness,
                    aInsulatorPlaneCoordinate);

            var bInsulatorStackCenterCoordinate =
                ResolveSideToSideInsulatorStackCenterCoordinate(
                    bFrameInsulator,
                    bRouteContext.FrameOutwardNormal,
                    routeThicknessDirection,
                    middleStackProjectedOffsets,
                    bRouteContext.BusbarThickness);
            var aInsulatorStackCenterCoordinate =
                ResolveSideToSideInsulatorStackCenterCoordinate(
                    aFrameInsulator,
                    aRouteContext.FrameOutwardNormal,
                    routeThicknessDirection,
                    middleStackProjectedOffsets,
                    bRouteContext.BusbarThickness);
            if (Math.Abs(
                    bInsulatorStackCenterCoordinate -
                    aInsulatorStackCenterCoordinate) >
                SideToSidePhysicalAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相两端基架绝缘子要求的中间叠排搭接层不一致，" +
                    "无法生成保持平行的中间排。");
            }

            var insulatorRouteThicknessCoordinate =
                (bInsulatorStackCenterCoordinate +
                 aInsulatorStackCenterCoordinate) / 2d;
            var bBridgeOffsetDistance = bTerminalThicknessCoordinate -
                insulatorRouteThicknessCoordinate;
            var aBridgeOffsetDistance = aTerminalThicknessCoordinate -
                insulatorRouteThicknessCoordinate;
            if (baseAndFrameInsulatorNormalsAligned &&
                Math.Abs(bBridgeOffsetDistance) <= GeometryTolerance &&
                Math.Abs(aBridgeOffsetDistance) <= GeometryTolerance)
            {
                // 基座与基架配合面法向一致时，即使柜顶候选层与绝缘子层
                // 数值上重合，业务仍要求保留两端桥式形状。零偏移会让
                // 六点桥式路径退化为直线，因此按实际单根排厚建立
                // 端部换层，不能误用半个单根排厚或叠排总厚度。
                var singleBusbarThickness =
                    ResolveSingleBusbarThickness(bRouteContext);
                bBridgeOffsetDistance = singleBusbarThickness;
                aBridgeOffsetDistance = singleBusbarThickness;
                FaceToWallPreviewTrace.Info(
                    "侧对侧第一种-同法向桥式端部补偿",
                    "相=" + FaceToWallModelBusbarPlanner.GetPhaseName(
                        phaseIndex) +
                    "，原始端部偏移=0/0，采用叠排总厚度=" +
                    bRouteContext.BusbarThickness.ToString("0.###") +
                    "，单根排厚度=" +
                    singleBusbarThickness.ToString("0.###"));
            }
            return new SideToSideFixedMiddleLapAlignment
            {
                BCabinetTerminalCenter = bTerminalCenter,
                ACabinetTerminalCenter = aTerminalCenter,
                RouteThicknessDirection = routeThicknessDirection,
                StraightRouteThicknessCoordinate =
                    straightRouteThicknessCoordinate,
                InsulatorRouteThicknessCoordinate =
                    insulatorRouteThicknessCoordinate,
                BCabinetTerminalThicknessCoordinate =
                    bTerminalThicknessCoordinate,
                ACabinetTerminalThicknessCoordinate =
                    aTerminalThicknessCoordinate,
                BBridgeOffsetDistance = bBridgeOffsetDistance,
                ABridgeOffsetDistance = aBridgeOffsetDistance,
                BCabinetStraightRouteCanLapInsulator = bCanLapInsulator,
                ACabinetStraightRouteCanLapInsulator = aCanLapInsulator,
                BaseAndFrameInsulatorNormalsAligned =
                    baseAndFrameInsulatorNormalsAligned,
                RequiresBridge = baseAndFrameInsulatorNormalsAligned ||
                    !bCanLapInsulator || !aCanLapInsulator
            };
        }

        private static bool CanSideToSideStackLapConnectionPlane(
            double stackCenterThicknessCoordinate,
            IReadOnlyList<double> projectedStackMemberOffsets,
            double busbarThickness,
            double connectionPlaneThicknessCoordinate)
        {
            var memberHalfThickness = busbarThickness / 2d;
            return projectedStackMemberOffsets.Any(
                projectedStackMemberOffset => Math.Abs(
                    stackCenterThicknessCoordinate +
                    projectedStackMemberOffset -
                    connectionPlaneThicknessCoordinate) <=
                    memberHalfThickness +
                    ModelAlignmentTolerance);
        }

        private static double ResolveSingleBusbarThickness(
            FaceToWallPhaseRouteContext phaseRouteContext)
        {
            if (phaseRouteContext == null ||
                phaseRouteContext.BusbarThickness <= GeometryTolerance ||
                phaseRouteContext.StackMemberOffsets == null ||
                phaseRouteContext.StackMemberOffsets.Count == 0)
            {
                throw new InvalidOperationException(
                    "侧对侧固定换相缺少有效的叠排厚度参数。 ");
            }

            var orderedOffsets = phaseRouteContext.StackMemberOffsets
                .OrderBy(offset => offset)
                .ToList();
            var minimumAdjacentCenterDistance = orderedOffsets
                .Zip(
                    orderedOffsets.Skip(1),
                    (lowerOffset, upperOffset) =>
                        upperOffset - lowerOffset)
                .Where(centerDistance => centerDistance > GeometryTolerance)
                .DefaultIfEmpty(0d)
                .Min();
            return minimumAdjacentCenterDistance > GeometryTolerance
                ? minimumAdjacentCenterDistance / 2d
                : phaseRouteContext.BusbarThickness;
        }

        private static double ResolveSideToSideInsulatorStackCenterCoordinate(
            InsulatorConnectionInfo frameInsulator,
            Vector3d frameOutwardNormal,
            Vector3d routeThicknessDirection,
            IReadOnlyList<double> projectedStackMemberOffsets,
            double busbarThickness)
        {
            var outwardDirectionSign = frameOutwardNormal.Normalized.Dot(
                routeThicknessDirection) >= 0d
                ? 1d
                : -1d;
            var innermostProjectedOffset = projectedStackMemberOffsets
                .OrderBy(projectedStackMemberOffset =>
                    outwardDirectionSign * projectedStackMemberOffset)
                .First();
            return BusbarGeometryUtility.Project(
                       frameInsulator.ConnectionCircleCenter,
                       routeThicknessDirection) +
                   outwardDirectionSign * busbarThickness / 2d -
                   innermostProjectedOffset;
        }

            private static FaceToFaceRouteGroupDefinition
            CreateSideToSideFixedMiddleRouteDefinition(
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            SideToSideFixedPhaseAssignments fixedPhaseAssignments,
            BusbarBridgeAxisSetting middleRouteAxisSetting,
            bool useBridgeMiddleBusbar)
        {
            var result = CreateRouteGroupDefinition(
                0,
                FaceToFaceBusbarGroupKind_e.LengthwiseOrdinary,
                useBridgeMiddleBusbar
                    ? "侧对侧第一种固定桥式中间排"
                    : "侧对侧第一种固定直排");
            result.IsBridge = useBridgeMiddleBusbar;

            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var bFrameInsulator = fixedPhaseAssignments.BCabinet
                    .FrameInsulatorsByPhase[phaseIndex];
                var aFrameInsulator = fixedPhaseAssignments.ACabinet
                    .FrameInsulatorsByPhase[phaseIndex];
                var bRouteContext = bCabinetContext
                    .PhaseRouteContexts[phaseIndex];
                var busbarWidth = bRouteContext.BusbarWidth;
                var middleLapAlignment =
                    ResolveSideToSideFixedMiddleLapAlignment(
                        bCabinetContext,
                        aCabinetContext,
                        fixedPhaseAssignments.BCabinet
                            .BaseInsulatorsByPhase[phaseIndex],
                        fixedPhaseAssignments.ACabinet
                            .BaseInsulatorsByPhase[phaseIndex],
                        bFrameInsulator,
                        aFrameInsulator,
                        middleRouteAxisSetting,
                        phaseIndex);
                if (middleLapAlignment.RequiresBridge !=
                    useBridgeMiddleBusbar)
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相中间排型式与三相统一判断结果不一致。");
                }

                // 中间排的两端必须搭到两柜柜顶首段，而不是只停在中间
                // 基架绝缘子中心。端点分别越过柜顶首段中心半个排宽，
                // 形成完整的排宽 x 排宽垂直搭接；中间排的高度仍由
                // 对应基架绝缘子确定。
                var bLapCenterLengthCoordinate = BusbarGeometryUtility.Project(
                    middleLapAlignment.BCabinetTerminalCenter,
                    middleRouteAxisSetting.LengthVector);
                var aLapCenterLengthCoordinate = BusbarGeometryUtility.Project(
                    middleLapAlignment.ACabinetTerminalCenter,
                    middleRouteAxisSetting.LengthVector);
                var bLapCenterHeightCoordinate = BusbarGeometryUtility.Project(
                    bFrameInsulator.ConnectionCircleCenter,
                    middleRouteAxisSetting.HeightVector);
                var aLapCenterHeightCoordinate = BusbarGeometryUtility.Project(
                    aFrameInsulator.ConnectionCircleCenter,
                    middleRouteAxisSetting.HeightVector);
                var middleRouteThicknessCoordinate = useBridgeMiddleBusbar
                    ? middleLapAlignment.InsulatorRouteThicknessCoordinate
                    : middleLapAlignment.StraightRouteThicknessCoordinate;
                var startPoint = BusbarGeometryUtility.WithProjection(
                    BusbarGeometryUtility.WithProjection(
                        BusbarGeometryUtility.WithProjection(
                            bRouteContext.OrdinaryRouteReferencePoint,
                            middleRouteAxisSetting.LengthVector,
                            bLapCenterLengthCoordinate - busbarWidth / 2d),
                        middleRouteAxisSetting.HeightVector,
                        bLapCenterHeightCoordinate),
                    middleLapAlignment.RouteThicknessDirection,
                    middleRouteThicknessCoordinate);
                var endPoint = BusbarGeometryUtility.WithProjection(
                    BusbarGeometryUtility.WithProjection(
                        BusbarGeometryUtility.WithProjection(
                            bRouteContext.OrdinaryRouteReferencePoint,
                            middleRouteAxisSetting.LengthVector,
                            aLapCenterLengthCoordinate + busbarWidth / 2d),
                        middleRouteAxisSetting.HeightVector,
                        aLapCenterHeightCoordinate),
                    middleLapAlignment.RouteThicknessDirection,
                    middleRouteThicknessCoordinate);

                result.PhaseSegments.Add(new FaceToFacePhaseRouteSegment
                {
                    PhaseIndex = phaseIndex,
                    StartPoint = startPoint,
                    EndPoint = endPoint,
                    RouteDirection = middleRouteAxisSetting.LengthVector.Copy(),
                    WidthDirection = middleRouteAxisSetting.HeightVector.Copy(),
                    ThicknessDirection =
                        middleLapAlignment.RouteThicknessDirection.Copy(),
                    BridgeStartOffsetDistance = useBridgeMiddleBusbar
                        ? (double?)middleLapAlignment.BBridgeOffsetDistance
                        : null,
                    BridgeEndOffsetDistance = useBridgeMiddleBusbar
                        ? (double?)middleLapAlignment.ABridgeOffsetDistance
                        : null
                });

                FaceToWallPreviewTrace.Info(
                    "侧对侧第一种-固定中间排逐相搭接",
                    "相=" + FaceToWallModelBusbarPlanner.GetPhaseName(
                        phaseIndex) +
                    "，B柜顶首段搭接中心=" + FormatPoint(
                        middleLapAlignment.BCabinetTerminalCenter) +
                    "，A柜顶首段搭接中心=" + FormatPoint(
                        middleLapAlignment.ACabinetTerminalCenter) +
                    "，B绝缘子中心=" + FormatPoint(
                        bFrameInsulator.ConnectionCircleCenter) +
                    "，A绝缘子中心=" + FormatPoint(
                        aFrameInsulator.ConnectionCircleCenter) +
                    "，B柜顶首段搭接边=" + FormatPoint(startPoint) +
                    "，A柜顶首段搭接边=" + FormatPoint(endPoint) +
                    "，中间排中心高度(B/A)=" +
                    bLapCenterHeightCoordinate.ToString("0.###") + "/" +
                    aLapCenterHeightCoordinate.ToString("0.###") +
                    "，直排实体可搭接(B/A)=" +
                    middleLapAlignment
                        .BCabinetStraightRouteCanLapInsulator + "/" +
                    middleLapAlignment
                        .ACabinetStraightRouteCanLapInsulator +
                    "，中间排类型=" +
                    (useBridgeMiddleBusbar ? "桥式直排" : "直排"));
            }

            return result;
        }

        private static FaceToFaceRouteGroupDefinition
            CreateStraightLengthDefinition(
            int routeGroupIndex,
            double lapCenterStartCoordinate,
            double lapCenterEndCoordinate,
            FaceToFaceSidePlanningContext routeReferenceContext,
            FaceToFaceSidePlanningContext widthSourceContext,
            BusbarBridgeAxisSetting axisSetting,
            bool reversePhysicalPhaseOrder = false)
        {
            var result = CreateRouteGroupDefinition(
                routeGroupIndex,
                FaceToFaceBusbarGroupKind_e.LengthwiseOrdinary,
                "面对面普通第" + (routeGroupIndex + 1) + "段");
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var physicalPhaseIndex = reversePhysicalPhaseOrder
                    ? ReversePhaseIndex(phaseIndex)
                    : phaseIndex;
                var routeContext =
                    routeReferenceContext.PhaseRouteContexts[phaseIndex];
                var widthCoordinate = BusbarGeometryUtility.Project(
                    widthSourceContext.PhaseRouteContexts[physicalPhaseIndex]
                        .OrdinaryRouteReferencePoint,
                    axisSetting.WidthVector);
                result.PhaseSegments.Add(CreateLengthPhaseSegment(
                    routeContext,
                    axisSetting,
                    lapCenterStartCoordinate - routeContext.BusbarWidth / 2d,
                    lapCenterEndCoordinate + routeContext.BusbarWidth / 2d,
                    widthCoordinate));
            }

            return result;
        }

        /// <summary>
        /// 侧对侧偏移段的两柜端点必须与常规侧对侧一致，分别接到本柜每一相
        /// 七点首段已经算出的实际边缘。断点只拆分中间路径，不能反过来把
        /// A/B/C 三相端头截成同一个公共坐标。
        /// </summary>
        internal static void ApplySideToSidePhaseEdgeExtensions(
            IReadOnlyList<FaceToFaceRouteGroupDefinition> routeDefinitions,
            IReadOnlyList<FaceToWallPhaseRouteContext>
                bCabinetPhaseRouteContexts,
            IReadOnlyList<FaceToWallPhaseRouteContext>
                aCabinetPhaseRouteContexts,
            BusbarBridgeAxisSetting offsetRouteAxisSetting)
        {
            if (routeDefinitions == null || routeDefinitions.Count == 0)
            {
                throw new InvalidOperationException(
                    "侧对侧偏移没有可延长到柜顶对应相边缘的中间排。");
            }
            if (bCabinetPhaseRouteContexts == null ||
                aCabinetPhaseRouteContexts == null ||
                bCabinetPhaseRouteContexts.Count != PhaseCount ||
                aCabinetPhaseRouteContexts.Count != PhaseCount ||
                bCabinetPhaseRouteContexts.Any(context => context == null ||
                    context.OrdinaryRouteReferencePoint == null ||
                    context.PhaseIndex < 0 || context.PhaseIndex >= PhaseCount) ||
                aCabinetPhaseRouteContexts.Any(context => context == null ||
                    context.OrdinaryRouteReferencePoint == null ||
                    context.PhaseIndex < 0 || context.PhaseIndex >= PhaseCount) ||
                bCabinetPhaseRouteContexts.Select(context => context.PhaseIndex)
                    .Distinct().Count() != PhaseCount ||
                aCabinetPhaseRouteContexts.Select(context => context.PhaseIndex)
                    .Distinct().Count() != PhaseCount)
            {
                throw new InvalidOperationException(
                    "侧对侧偏移的 A/B 柜必须各自提供完整三相端头边缘坐标。");
            }
            if (offsetRouteAxisSetting == null)
            {
                throw new ArgumentNullException(
                    nameof(offsetRouteAxisSetting));
            }
            var bCabinetContextsByPhase = bCabinetPhaseRouteContexts
                .ToDictionary(context => context.PhaseIndex);
            var aCabinetContextsByPhase = aCabinetPhaseRouteContexts
                .ToDictionary(context => context.PhaseIndex);

            var extensionLogItems = new List<string>();
            for (var phaseIndex = 0;
                phaseIndex < PhaseCount;
                phaseIndex++)
            {
                var phaseLengthSegments = routeDefinitions
                    .SelectMany(routeDefinition =>
                        routeDefinition.PhaseSegments)
                    .Where(phaseSegment =>
                        phaseSegment != null &&
                        phaseSegment.PhaseIndex == phaseIndex &&
                        phaseSegment.StartPoint != null &&
                        phaseSegment.EndPoint != null &&
                        BusbarGeometryUtility.AreParallel(
                            phaseSegment.RouteDirection,
                            offsetRouteAxisSetting.LengthVector))
                    .ToList();
                if (phaseLengthSegments.Count == 0)
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相侧对侧偏移没有可连接柜顶首段的长度向中间排。");
                }

                var bCabinetTerminalSegment = phaseLengthSegments.First();
                var aCabinetTerminalSegment = phaseLengthSegments.Last();
                if (bCabinetTerminalSegment.RouteDirection.Normalized.Dot(
                        offsetRouteAxisSetting.LengthVector.Normalized) <= 0d ||
                    aCabinetTerminalSegment.RouteDirection.Normalized.Dot(
                        offsetRouteAxisSetting.LengthVector.Normalized) <= 0d)
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相侧对侧偏移中间排方向未按 B 柜到 A 柜排列。");
                }

                var bCabinetPhaseEdgeCoordinate =
                    BusbarGeometryUtility.Project(
                        bCabinetContextsByPhase[phaseIndex]
                            .OrdinaryRouteReferencePoint,
                        offsetRouteAxisSetting.LengthVector);
                var aCabinetPhaseEdgeCoordinate =
                    BusbarGeometryUtility.Project(
                        aCabinetContextsByPhase[phaseIndex]
                            .OrdinaryRouteReferencePoint,
                        offsetRouteAxisSetting.LengthVector);
                var bTerminalInnerCoordinate = BusbarGeometryUtility.Project(
                    bCabinetTerminalSegment.EndPoint,
                    offsetRouteAxisSetting.LengthVector);
                var aTerminalInnerCoordinate = BusbarGeometryUtility.Project(
                    aCabinetTerminalSegment.StartPoint,
                    offsetRouteAxisSetting.LengthVector);
                if (bCabinetPhaseEdgeCoordinate >=
                        bTerminalInnerCoordinate - GeometryTolerance ||
                    aCabinetPhaseEdgeCoordinate <=
                        aTerminalInnerCoordinate + GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相侧对侧偏移端头边缘与中间排的 B→A 方向顺序异常。" +
                        " B端边缘=" +
                        bCabinetPhaseEdgeCoordinate.ToString("0.###") +
                        "，B端内侧=" +
                        bTerminalInnerCoordinate.ToString("0.###") +
                        "，A端内侧=" +
                        aTerminalInnerCoordinate.ToString("0.###") +
                        "，A端边缘=" +
                        aCabinetPhaseEdgeCoordinate.ToString("0.###") + "。");
                }

                var originalBCabinetStartCoordinate =
                    BusbarGeometryUtility.Project(
                        bCabinetTerminalSegment.StartPoint,
                        offsetRouteAxisSetting.LengthVector);
                var originalACabinetEndCoordinate =
                    BusbarGeometryUtility.Project(
                        aCabinetTerminalSegment.EndPoint,
                        offsetRouteAxisSetting.LengthVector);
                bCabinetTerminalSegment.StartPoint =
                    BusbarGeometryUtility.WithProjection(
                        bCabinetTerminalSegment.StartPoint,
                        offsetRouteAxisSetting.LengthVector,
                        bCabinetPhaseEdgeCoordinate);
                aCabinetTerminalSegment.EndPoint =
                    BusbarGeometryUtility.WithProjection(
                        aCabinetTerminalSegment.EndPoint,
                        offsetRouteAxisSetting.LengthVector,
                        aCabinetPhaseEdgeCoordinate);
                extensionLogItems.Add(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相:B端=" +
                    originalBCabinetStartCoordinate.ToString("0.###") +
                    "→" + bCabinetPhaseEdgeCoordinate.ToString("0.###") +
                    "，A端=" +
                    originalACabinetEndCoordinate.ToString("0.###") +
                    "→" + aCabinetPhaseEdgeCoordinate.ToString("0.###"));
            }

            FaceToWallPreviewTrace.Info(
                "侧对侧偏移-逐相延长到柜顶边缘",
                string.Join("；", extensionLogItems));
        }

        private static FaceToFaceRouteGroupDefinition
            CreateLargeOffsetLengthDefinition(
            int routeGroupIndex,
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            FaceToFaceSidePlanningContext bCabinetContext,
            BusbarBridgeAxisSetting axisSetting,
            bool useBSide)
        {
            var result = CreateRouteGroupDefinition(
                routeGroupIndex,
                FaceToFaceBusbarGroupKind_e.LengthwiseOrdinary,
                useBSide
                    ? "大偏移B侧长度向段"
                    : "大偏移A侧长度向段");
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var phaseRoute = largeOffsetRegion.PhaseRoutes[phaseIndex];
                var routeContext =
                    bCabinetContext.PhaseRouteContexts[phaseIndex];
                result.PhaseSegments.Add(CreateLengthPhaseSegment(
                    routeContext,
                    axisSetting,
                    useBSide
                        ? phaseRoute.BSideLengthStartCoordinate
                        : phaseRoute.ASideLengthStartCoordinate,
                    useBSide
                        ? phaseRoute.BSideLengthEndCoordinate
                        : phaseRoute.ASideLengthEndCoordinate,
                    useBSide
                        ? phaseRoute.BSideWidthCoordinate
                        : phaseRoute.ASideWidthCoordinate));
            }

            return result;
        }

        private static FaceToFaceRouteGroupDefinition
            CreateLargeOffsetCrosswiseDefinition(
            int routeGroupIndex,
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            FaceToFaceSidePlanningContext bCabinetContext,
            BusbarBridgeAxisSetting axisSetting,
            int crosswiseSegmentIndex)
        {
            var crosswiseSegmentCount =
                largeOffsetRegion.CrosswiseBreakpointFractions.Count + 1;
            if (crosswiseSegmentIndex < 0 ||
                crosswiseSegmentIndex >= crosswiseSegmentCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(crosswiseSegmentIndex));
            }

            var result = CreateRouteGroupDefinition(
                routeGroupIndex,
                FaceToFaceBusbarGroupKind_e.LargeOffsetCrosswise,
                "大偏移宽度向中间段" +
                (crosswiseSegmentCount == 1
                    ? string.Empty
                    : (crosswiseSegmentIndex + 1).ToString()));
            var offsetDirectionSign =
                largeOffsetRegion.SignedWidthOffset > 0d ? 1d : -1d;
            var crosswiseRouteDirection =
                offsetDirectionSign > 0d
                    ? axisSetting.WidthVector.Copy()
                    : axisSetting.WidthVector.Negate();
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var phaseRoute = largeOffsetRegion.PhaseRoutes[phaseIndex];
                var routeContext =
                    bCabinetContext.PhaseRouteContexts[phaseIndex];
                var crosswiseWidthDirection = routeContext
                    .RouteThicknessDirection
                    .Cross(crosswiseRouteDirection);
                if (crosswiseWidthDirection.Norm <= GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "大偏移宽度向中间排无法确定排宽方向。");
                }

                crosswiseWidthDirection = crosswiseWidthDirection.Normalized;
                var crosswiseSegmentRange =
                    CalculateLargeOffsetCrosswiseSegmentRanges(
                        largeOffsetRegion,
                        phaseRoute,
                        routeContext.BusbarWidth)[crosswiseSegmentIndex];
                var startPoint = CreatePointAtLengthAndWidth(
                    routeContext.OrdinaryRouteReferencePoint,
                    axisSetting,
                    phaseRoute.TurnLengthCoordinate,
                    crosswiseSegmentRange.StartCoordinate);
                var endPoint = CreatePointAtLengthAndWidth(
                    routeContext.OrdinaryRouteReferencePoint,
                    axisSetting,
                    phaseRoute.TurnLengthCoordinate,
                    crosswiseSegmentRange.EndCoordinate);
                phaseRoute.BSideTurnCenter = CreatePointAtLengthAndWidth(
                    routeContext.OrdinaryRouteReferencePoint,
                    axisSetting,
                    phaseRoute.TurnLengthCoordinate,
                    phaseRoute.BSideWidthCoordinate);
                phaseRoute.ASideTurnCenter = CreatePointAtLengthAndWidth(
                    routeContext.OrdinaryRouteReferencePoint,
                    axisSetting,
                    phaseRoute.TurnLengthCoordinate,
                    phaseRoute.ASideWidthCoordinate);
                result.PhaseSegments.Add(new FaceToFacePhaseRouteSegment
                {
                    PhaseIndex = phaseIndex,
                    StartPoint = startPoint,
                    EndPoint = endPoint,
                    RouteDirection = crosswiseRouteDirection.Copy(),
                    WidthDirection = crosswiseWidthDirection,
                    ThicknessDirection =
                        routeContext.RouteThicknessDirection.Copy()
                });
            }

            return result;
        }

        internal static IReadOnlyList<FaceToFaceLargeOffsetCrosswiseSegmentRange>
            CalculateLargeOffsetCrosswiseSegmentRanges(
            FaceToFaceLargeOffsetRegionPlan largeOffsetRegion,
            FaceToFaceLargeOffsetPhaseRoute phaseRoute,
            double busbarWidth)
        {
            if (largeOffsetRegion == null)
            {
                throw new ArgumentNullException(nameof(largeOffsetRegion));
            }

            if (phaseRoute == null)
            {
                throw new ArgumentNullException(nameof(phaseRoute));
            }

            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            var offsetDirectionSign =
                largeOffsetRegion.SignedWidthOffset > 0d ? 1d : -1d;
            var phaseCrosswiseBreakpointCoordinates =
                largeOffsetRegion.CrosswiseBreakpointFractions
                    .Select(crosswiseBreakpointFraction =>
                    {
                        if (double.IsNaN(crosswiseBreakpointFraction) ||
                            double.IsInfinity(crosswiseBreakpointFraction) ||
                            crosswiseBreakpointFraction <= 0d ||
                            crosswiseBreakpointFraction >= 1d)
                        {
                            throw new InvalidOperationException(
                                "大偏移宽度向搭接断点比例必须严格位于 0～1 之间。");
                        }

                        return phaseRoute.BSideWidthCoordinate +
                               (phaseRoute.ASideWidthCoordinate -
                                phaseRoute.BSideWidthCoordinate) *
                               crosswiseBreakpointFraction;
                    })
                    .ToList();
            var result = new List<
                FaceToFaceLargeOffsetCrosswiseSegmentRange>();
            for (var crosswiseSegmentIndex = 0;
                crosswiseSegmentIndex <=
                    phaseCrosswiseBreakpointCoordinates.Count;
                crosswiseSegmentIndex++)
            {
                result.Add(
                    new FaceToFaceLargeOffsetCrosswiseSegmentRange
                    {
                        StartCoordinate = crosswiseSegmentIndex == 0
                            ? phaseRoute.CrosswiseStartWidthCoordinate
                            : phaseCrosswiseBreakpointCoordinates[
                                  crosswiseSegmentIndex - 1] -
                              offsetDirectionSign * busbarWidth / 2d,
                        EndCoordinate = crosswiseSegmentIndex ==
                                        phaseCrosswiseBreakpointCoordinates.Count
                            ? phaseRoute.CrosswiseEndWidthCoordinate
                            : phaseCrosswiseBreakpointCoordinates[
                                  crosswiseSegmentIndex] +
                              offsetDirectionSign * busbarWidth / 2d
                    });
            }

            return result.AsReadOnly();
        }

        private static FaceToFaceRouteGroupDefinition CreateRouteGroupDefinition(
            int routeGroupIndex,
            FaceToFaceBusbarGroupKind_e kind,
            string displayName)
        {
            return new FaceToFaceRouteGroupDefinition
            {
                RouteGroupIndex = routeGroupIndex,
                Kind = kind,
                DisplayName = displayName,
                IsBridge = IsBridgeRouteGroup(routeGroupIndex)
            };
        }

        private static FaceToFacePhaseRouteSegment CreateLengthPhaseSegment(
            FaceToWallPhaseRouteContext routeContext,
            BusbarBridgeAxisSetting axisSetting,
            double startLengthCoordinate,
            double endLengthCoordinate,
            double widthCoordinate)
        {
            return new FaceToFacePhaseRouteSegment
            {
                PhaseIndex = routeContext.PhaseIndex,
                StartPoint = CreatePointAtLengthAndWidth(
                    routeContext.OrdinaryRouteReferencePoint,
                    axisSetting,
                    startLengthCoordinate,
                    widthCoordinate),
                EndPoint = CreatePointAtLengthAndWidth(
                    routeContext.OrdinaryRouteReferencePoint,
                    axisSetting,
                    endLengthCoordinate,
                    widthCoordinate),
                RouteDirection = axisSetting.LengthVector.Copy(),
                WidthDirection = routeContext.RouteWidthDirection.Copy(),
                ThicknessDirection =
                    routeContext.RouteThicknessDirection.Copy()
            };
        }

        private static FaceToFaceGeneratedBusbarGroup CreateGeneratedRouteGroup(
            FaceToFaceRouteGroupDefinition routeDefinition,
            FaceToFaceSidePlanningContext bCabinetContext,
            BusbarBridgeAxisSetting axisSetting)
        {
            var result = new FaceToFaceGeneratedBusbarGroup
            {
                DisplayName = routeDefinition.DisplayName,
                Kind = routeDefinition.Kind,
                RouteGroupIndex = routeDefinition.RouteGroupIndex,
                IsBridge = routeDefinition.IsBridge
            };
            foreach (var phaseSegment in routeDefinition.PhaseSegments)
            {
                var routeContext = bCabinetContext.PhaseRouteContexts[
                    phaseSegment.PhaseIndex];
                var centerlineSetting = CreateRouteCenterlineSetting(
                    routeDefinition,
                    phaseSegment,
                    routeContext,
                    axisSetting.HeightVector);
                var phaseBusbars = routeDefinition.IsBridge
                    ? FaceToWallModelBusbarPlanner.CreateBridgeStackMemberSettings(
                        centerlineSetting,
                        routeContext.StackMemberOffsets,
                        routeContext.FrameOutwardNormal,
                        phaseSegment.PhaseIndex)
                    : FaceToWallModelBusbarPlanner.CreateStackMemberSettings(
                        centerlineSetting,
                        routeContext.StackMemberOffsets,
                        phaseSegment.PhaseIndex);
                result.Busbars.AddRange(phaseBusbars);
            }

            return result;
        }

        private static PlanarBendBusbarSetting CreateRouteCenterlineSetting(
            FaceToFaceRouteGroupDefinition routeDefinition,
            FaceToFacePhaseRouteSegment phaseSegment,
            FaceToWallPhaseRouteContext routeContext,
            Vector3d heightDirection)
        {
            var routeVector = new Vector3d(
                phaseSegment.StartPoint,
                phaseSegment.EndPoint);
            var routeLengthProjection = routeVector.Dot(
                phaseSegment.RouteDirection.Normalized);
            if (routeLengthProjection <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(
                        phaseSegment.PhaseIndex) +
                    "相“" + routeDefinition.DisplayName +
                    "”的路径方向或长度无效。");
            }

            if (routeDefinition.IsBridge)
            {
                var minimumBridgeLength = 2d *
                    (routeContext.BusbarWidth +
                     FaceToWallModelBusbarPlanner
                         .BridgeLowestEdgeStraightAdditionalLength +
                     FaceToWallModelBusbarPlanner
                         .BridgeInnermostTransitionProjectionLength);
                if (routeLengthProjection <=
                    minimumBridgeLength + GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "{0}相“{1}”含搭接长度仅 {2:0.###} mm，不足桥式两端“排宽 + 40 mm”直段和固定 50 mm 斜段所需的 {3:0.###} mm。",
                            FaceToWallModelBusbarPlanner.GetPhaseName(
                                phaseSegment.PhaseIndex),
                            routeDefinition.DisplayName,
                            routeLengthProjection,
                            minimumBridgeLength));
                }
            }

            List<Point3d> centerlinePoints;
            if (!routeDefinition.IsBridge)
            {
                centerlinePoints = new List<Point3d>
                {
                    phaseSegment.StartPoint.Copy(),
                    phaseSegment.EndPoint.Copy()
                };
            }
            else if (phaseSegment.BridgeStartOffsetDistance.HasValue &&
                     phaseSegment.BridgeEndOffsetDistance.HasValue)
            {
                // 侧对侧第一种的桥式中间排只在排厚轴上换层：两端回到
                // 各自柜顶首段的搭接层，中间长直段保持在绝缘子搭接层，
                // 高度全程仍由绝缘子连接圆心确定。
                centerlinePoints = FaceToWallModelBusbarPlanner
                    .CreateBridgeReferenceCenterlinePoints(
                        phaseSegment.StartPoint,
                        phaseSegment.EndPoint,
                        phaseSegment.RouteDirection,
                        phaseSegment.ThicknessDirection,
                        routeContext.BusbarWidth,
                        routeContext.BusbarThickness,
                        phaseSegment.BridgeStartOffsetDistance.Value,
                        phaseSegment.BridgeEndOffsetDistance.Value)
                    .Select(centerlinePoint => centerlinePoint.Copy())
                    .ToList();
            }
            else if (phaseSegment.BridgeStartOffsetDistance.HasValue ||
                     phaseSegment.BridgeEndOffsetDistance.HasValue)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(
                        phaseSegment.PhaseIndex) +
                    "相桥式中间排只设置了一端的排厚偏移。");
            }
            else
            {
                centerlinePoints = FaceToWallModelBusbarPlanner
                    .CreateBridgeReferenceCenterlinePoints(
                        phaseSegment.StartPoint,
                        phaseSegment.EndPoint,
                        phaseSegment.RouteDirection,
                        heightDirection,
                        routeContext.BusbarWidth,
                        routeContext.BusbarThickness)
                    .Select(centerlinePoint => centerlinePoint.Copy())
                    .ToList();
            }
            return new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = FaceToWallModelBusbarPlanner.GetPhaseName(
                    phaseSegment.PhaseIndex),
                StartBusbarVector =
                    phaseSegment.ThicknessDirection.Copy(),
                WidthVector = phaseSegment.WidthDirection.Copy(),
                PointList = centerlinePoints,
                BusBarWidth = routeContext.BusbarWidth,
                BusBarThickness = routeContext.BusbarThickness,
                BarBendingRadius = routeContext.BendingRadius,
                BendAllowance = routeContext.BendAllowance,
                StartLapWidth = routeContext.BusbarWidth,
                EndLapWidth = routeContext.BusbarWidth
            };
        }

        /// <summary>
        /// 换相结构固定 A 相走底层“纵向直排 + 横向短排 + 纵向直排”，
        /// B 相为一件纵向起伏排，C 相在与 B 相共享的高层错位换相。
        /// 换相核心固定按输入绝缘距离额外增加 5 mm 生成；前后端点仍取
        /// 相邻普通段、大偏移段或桥式段的实际端点，禁止降低净距回退。
        /// </summary>
        private static FaceToFaceGeneratedBusbarGroup
            CreatePhaseChangeBusbarGroup(
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion,
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            BusbarBridgeAxisSetting axisSetting,
            bool allowInsufficientPhaseChangeClearancePreview,
            FaceToFaceSmallOffsetRegionPlan combinedSmallOffsetRegion = null,
            bool useMilledBarForLargeSmallOffset = false,
            IReadOnlyList<double> phaseChangeEndWidthCoordinates = null,
            IReadOnlyList<double> phaseChangeEndLengthCoordinates = null,
            IReadOnlyList<double> phaseChangeStartWidthCoordinates = null,
            FaceToFaceSidePlanningContext
                phaseChangeGeometricSideContext = null)
        {
            if (phaseChangeRegion?.MiddleFrame == null)
            {
                throw new ArgumentNullException(nameof(phaseChangeRegion));
            }
            if (phaseChangeRegion.SuppressedBreakpointIntervalIndex < 0)
            {
                throw new InvalidOperationException(
                    "面对面换相区域缺少固定断点逻辑段序号。");
            }

            var fixedPhaseChangeClearance =
                phaseChangeRegion.InsulationClearance +
                PhaseChangePreferredClearanceAddition;
            return CreatePhaseChangeBusbarGroupWithClearance(
                phaseChangeRegion,
                bCabinetContext,
                aCabinetContext,
                axisSetting,
                fixedPhaseChangeClearance,
                allowInsufficientPhaseChangeClearancePreview,
                combinedSmallOffsetRegion,
                useMilledBarForLargeSmallOffset,
                phaseChangeEndWidthCoordinates,
                phaseChangeEndLengthCoordinates,
                phaseChangeStartWidthCoordinates,
                phaseChangeGeometricSideContext);
        }

        private static FaceToFaceGeneratedBusbarGroup
            CreatePhaseChangeBusbarGroupWithClearance(
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion,
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            BusbarBridgeAxisSetting axisSetting,
            double phaseChangeRoutingClearance,
            bool allowInsufficientPhaseChangeClearancePreview,
            FaceToFaceSmallOffsetRegionPlan combinedSmallOffsetRegion = null,
            bool useMilledBarForLargeSmallOffset = false,
            IReadOnlyList<double> phaseChangeEndWidthCoordinates = null,
            IReadOnlyList<double> phaseChangeEndLengthCoordinates = null,
            IReadOnlyList<double> phaseChangeStartWidthCoordinates = null,
            FaceToFaceSidePlanningContext
                phaseChangeGeometricSideContext = null,
            bool frontBridgeOnly = false)
        {
            ValidateNonNegativeFinite(
                phaseChangeRoutingClearance,
                nameof(phaseChangeRoutingClearance));
            if (phaseChangeStartWidthCoordinates != null &&
                (phaseChangeStartWidthCoordinates.Count != PhaseCount ||
                 phaseChangeStartWidthCoordinates.Any(widthCoordinate =>
                     double.IsNaN(widthCoordinate) ||
                     double.IsInfinity(widthCoordinate))))
            {
                throw new InvalidOperationException(
                    "小偏移换相前的三相入口坐标不完整。");
            }
            if (phaseChangeEndWidthCoordinates != null &&
                (phaseChangeEndWidthCoordinates.Count != PhaseCount ||
                 phaseChangeEndWidthCoordinates.Any(widthCoordinate =>
                     double.IsNaN(widthCoordinate) ||
                     double.IsInfinity(widthCoordinate))))
            {
                throw new InvalidOperationException(
                    "换相后的三相出口坐标不完整。");
            }
            if (phaseChangeEndLengthCoordinates != null &&
                (phaseChangeEndLengthCoordinates.Count != PhaseCount ||
                 phaseChangeEndLengthCoordinates.Any(lengthCoordinate =>
                     double.IsNaN(lengthCoordinate) ||
                     double.IsInfinity(lengthCoordinate))))
            {
                throw new InvalidOperationException(
                    "大偏移换相后的三相转角长度坐标不完整。");
            }

            var referenceRouteContext =
                bCabinetContext.PhaseRouteContexts[0];
            var busbarWidth = referenceRouteContext.BusbarWidth;
            var busbarThickness = referenceRouteContext.BusbarThickness;
            var phaseChangeGroupIsBridge = !frontBridgeOnly && IsBridgeRouteGroup(
                phaseChangeRegion.SuppressedBreakpointIntervalIndex);
            // 换相中心线按叠排总包络建模。实体表面净距是客户输入的绝缘距离，
            // 叠排包络只用于把中心线间距换算成实体间距，不能把同一段厚度
            // 再次叠加到绝缘距离上。
            var phaseChangeStackEnvelopeThickness =
                bCabinetContext.PhaseRouteContexts
                    .Concat(aCabinetContext.PhaseRouteContexts)
                    .Select(CalculateStackEnvelopeThickness)
                    .Max();
            // 路径中心线需要为扫描叠排实体预留包络修正；最终实体表面净距
            // 仍使用 phaseChangeRoutingClearance（用户输入值）进行终检。
            var phaseChangeGeometryClearance =
                phaseChangeRoutingClearance +
                phaseChangeStackEnvelopeThickness;
            var phaseChangeMinimumRecommendedBreakpointSpan =
                CalculatePhaseChangeMinimumRequiredBreakpointSpan(
                    phaseChangeRoutingClearance,
                    busbarWidth,
                    phaseChangeStackEnvelopeThickness,
                    bCabinetContext.PhaseRouteContexts
                        .Concat(aCabinetContext.PhaseRouteContexts)
                    .Select(phaseRouteContext =>
                            phaseRouteContext.BendAllowance)
                        .DefaultIfEmpty(0d)
                        .Max(),
                    phaseChangeGroupIsBridge);
            phaseChangeRegion.MinimumRecommendedBreakpointSpan = Math.Max(
                phaseChangeRegion.MinimumRecommendedBreakpointSpan,
                phaseChangeMinimumRecommendedBreakpointSpan);
            FaceToFaceSmallOffsetTransitionKind_e?
                combinedPhaseChangeTemplateKind = null;
            var combinedSignedWidthOffset = 0d;
            if (combinedSmallOffsetRegion != null)
            {
                if (!IsCombinedSmallOffsetPhaseChange(
                        combinedSmallOffsetRegion,
                        phaseChangeRegion))
                {
                    throw new InvalidOperationException(
                        "小偏移换相模板必须与小偏移窗口使用同一断点区间。");
                }

                var bPhaseRouteContext =
                    bCabinetContext.PhaseRouteContexts[1];
                var aPhaseRouteContext =
                    aCabinetContext.PhaseRouteContexts[1];
                combinedSignedWidthOffset =
                    BusbarGeometryUtility.Project(
                        aPhaseRouteContext.OrdinaryRouteReferencePoint,
                        axisSetting.WidthVector) -
                    BusbarGeometryUtility.Project(
                        bPhaseRouteContext.OrdinaryRouteReferencePoint,
                        axisSetting.WidthVector);
                combinedPhaseChangeTemplateKind =
                    ResolveSmallOffsetTransitionKind(
                        Math.Abs(combinedSignedWidthOffset),
                        busbarWidth);
                if (combinedPhaseChangeTemplateKind ==
                    FaceToFaceSmallOffsetTransitionKind_e
                        .MiddleParallelConnection)
                {
                    throw new InvalidOperationException(
                        "小偏移换相固定模板只支持 B 相先立弯或先铣排；当前偏移已超过一排宽，不属于该模板范围。");
                }

                combinedSmallOffsetRegion.SignedBusbarWidthOffset =
                    combinedSignedWidthOffset;
                combinedSmallOffsetRegion.InsulationClearance =
                    phaseChangeRoutingClearance;
                combinedSmallOffsetRegion.UsesMiddleParallelConnection =
                    false;
                combinedSmallOffsetRegion.TransitionKind =
                    combinedPhaseChangeTemplateKind.Value;
            }
            phaseChangeRegion.RoutingClearance =
                phaseChangeRoutingClearance;
            // 绝缘距离用于换相排之间的净距，不是排到基架 Box 六个面的退让量。
            // 以净距缩小 Box 会把上下空间各重复扣除一次，最终迫使预览回退
            // 到 0 mm。换相核心只对实际排间净距负责，边界校验使用原始 Box。
            phaseChangeRegion.AvailableWidthMinimum =
                phaseChangeRegion.MiddleFrame.WidthMinimum;
            phaseChangeRegion.AvailableWidthMaximum =
                phaseChangeRegion.MiddleFrame.WidthMaximum;
            phaseChangeRegion.AvailableHeightMinimum =
                phaseChangeRegion.MiddleFrame.HeightMinimum;
            phaseChangeRegion.AvailableHeightMaximum =
                phaseChangeRegion.MiddleFrame.HeightMaximum;
            if (phaseChangeRegion.AvailableWidthMaximum <=
                    phaseChangeRegion.AvailableWidthMinimum +
                    GeometryTolerance ||
                phaseChangeRegion.AvailableHeightMaximum <=
                    phaseChangeRegion.AvailableHeightMinimum +
                    GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "中间基架 Box 没有有效换相空间。");
            }
            ValidatePhaseChangeLongitudinalClearance(
                bCabinetContext,
                aCabinetContext,
                axisSetting,
                busbarWidth,
                // 这里校验的是柜内各相在宽度方向的实际实体表面净距。
                // phaseChangeGeometryClearance 只用于换相中心线的包络抬高，
                // 额外包含叠排包络厚度，不能拿它作为相间净距要求，否则会把
                // 例如 130 mm 的实际相间净距错误判成小于 186 mm，导致预览退化到
                // 0 mm 路径净距，进而出现图中换相段过近。相间校验应严格使用客户
                // 输入的实际绝缘距离。
                phaseChangeRoutingClearance);
            var transitionStartLengthCoordinate =
                phaseChangeRegion.LeftFixedBreakpointCoordinate -
                busbarWidth / 2d;
            var transitionEndLengthCoordinate =
                phaseChangeRegion.RightFixedBreakpointCoordinate +
                busbarWidth / 2d;
            var transitionLength = transitionEndLengthCoordinate -
                transitionStartLengthCoordinate;
            if (transitionLength <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "面对面换相段长度必须大于零。");
            }
            var phaseChangeLongitudinalLayout =
                CreateMinimumPhaseChangeLongitudinalLayout(
                    transitionStartLengthCoordinate,
                    transitionEndLengthCoordinate,
                    phaseChangeRoutingClearance,
                    busbarWidth,
                    phaseChangeStackEnvelopeThickness,
                    0d,
                    bCabinetContext.PhaseRouteContexts
                        .Concat(aCabinetContext.PhaseRouteContexts)
                    .Select(phaseRouteContext =>
                            phaseRouteContext.BendAllowance)
                        .DefaultIfEmpty(0d)
                        .Max(),
                    false,
                    phaseChangeGroupIsBridge);
            var phaseChangePerpendicularBendReserve =
                CalculatePhaseChangePerpendicularBendReserve(
                    busbarWidth,
                    phaseChangeStackEnvelopeThickness,
                    bCabinetContext.PhaseRouteContexts
                        .Concat(aCabinetContext.PhaseRouteContexts)
                        .Select(phaseRouteContext =>
                            phaseRouteContext.BendAllowance)
                        .DefaultIfEmpty(0d)
                        .Max());
            var phaseChangePerpendicularLapProjection =
                CalculatePhaseChangePerpendicularLapProjectionExtension(
                    busbarWidth);
            FaceToWallPreviewTrace.Info(
                "FaceToFacePhaseChangeLayoutParameters",
                "用户绝缘距离=" + phaseChangeRoutingClearance
                    .ToString("0.###") +
                "；路径中心线布局距离=" + phaseChangeGeometryClearance
                    .ToString("0.###") +
                "；动作中心间距=" +
                (phaseChangeLongitudinalLayout.BPhaseRiseLengthCoordinate -
                 phaseChangeLongitudinalLayout.CPhaseCrossLengthCoordinate)
                    .ToString("0.###") +
                "；末端折下直段预留=" + phaseChangePerpendicularBendReserve
                    .ToString("0.###") +
                "；搭接投影=" + phaseChangePerpendicularLapProjection
                    .ToString("0.###") +
                "；实际点坐标=" +
                "C升=" + phaseChangeLongitudinalLayout
                    .CPhaseRiseLengthCoordinate.ToString("0.###") +
                "，C横=" + phaseChangeLongitudinalLayout
                    .CPhaseCrossLengthCoordinate.ToString("0.###") +
                "，B升=" + phaseChangeLongitudinalLayout
                    .BPhaseRiseLengthCoordinate.ToString("0.###") +
                "，A横=" + phaseChangeLongitudinalLayout
                    .APhaseCrossLengthCoordinate.ToString("0.###") +
                "，共降=" + phaseChangeLongitudinalLayout
                    .SharedDropLengthCoordinate.ToString("0.###"));

            var normalizedHeightDirection =
                axisSetting.HeightVector.Normalized;
            var thicknessHeightDirectionSign = referenceRouteContext
                .RouteThicknessDirection.Normalized.Dot(
                    normalizedHeightDirection);
            if (Math.Abs(thicknessHeightDirectionSign) <= 0.99d)
            {
                throw new InvalidOperationException(
                    "面对面换相排厚方向与基架高度方向不一致。");
            }

            var projectedStackOffsets = referenceRouteContext
                .StackMemberOffsets
                .Select(stackMemberOffset =>
                    stackMemberOffset * thicknessHeightDirectionSign)
                .ToList();
            var minimumStackOffset = projectedStackOffsets.Min();
            var maximumStackOffset = projectedStackOffsets.Max();
            var routeLayerHeightOffset = phaseChangeGroupIsBridge
                ? busbarThickness
                : 0d;
            var aPhaseBaseHeightCoordinate = BusbarGeometryUtility.Project(
                    referenceRouteContext.OrdinaryRouteReferencePoint,
                    axisSetting.HeightVector);
            // StackMemberOffsets 表示每根实际铜排中心线相对于叠排中心线的
            // 厚向偏移。中心线已经是实体中心，包络边界只能在最外层
            // 中心线两侧各加半个单根排厚；此前再加一次完整排厚会把
            // 4×8 叠排错误放大成 64 mm（实际为 56 mm）。
            var layerRelativeMinimum = minimumStackOffset -
                busbarThickness / 2d;
            var layerRelativeMaximum = maximumStackOffset +
                busbarThickness / 2d;
            var aPhaseLayerMinimum = aPhaseBaseHeightCoordinate +
                layerRelativeMinimum;
            var aPhaseLayerMaximum = aPhaseBaseHeightCoordinate +
                layerRelativeMaximum;
            if (aPhaseLayerMinimum <
                    phaseChangeRegion.AvailableHeightMinimum -
                    ModelAlignmentTolerance ||
                aPhaseLayerMaximum >
                    phaseChangeRegion.AvailableHeightMaximum +
                    ModelAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    "固定 A 相底层路径超出中间基架按当前净距计算的有效高度范围。");
            }
            // 高层基准直接相对于 A 相底层中心线抬高“叠排包络厚度 +
            // 实际绝缘距离”。不能从两侧包络边界反推，否则会重复加算
            // 叠排厚度，造成高度不足后退化到错误的零净距路径。A 相横向
            // 短排还要在底层中心线上抬高一个单根排厚，因此共享高层同样
            // 增加一个单根排厚，保证横向短排的扫描包络与 B/C 高层实体
            // 之间仍保留客户要求的实际净距。
            var sharedRaisedLayerHeightCoordinate =
                aPhaseBaseHeightCoordinate + phaseChangeGeometryClearance +
                busbarThickness;
            if (sharedRaisedLayerHeightCoordinate + layerRelativeMaximum >
                phaseChangeRegion.AvailableHeightMaximum +
                ModelAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    "最中间基架的有效高度不足以布置 A 相底层和 B/C 相共享高层；层间需要保留 " +
                    phaseChangeRoutingClearance.ToString("0.###") +
                    " mm 实体净距（叠排包络中心线间距为 " +
                    phaseChangeGeometryClearance.ToString("0.###") +
                    " mm）。");
            }

            var result = new FaceToFaceGeneratedBusbarGroup
            {
                DisplayName = combinedPhaseChangeTemplateKind.HasValue
                    ? "面对面中间小偏移换相段-" +
                      combinedPhaseChangeTemplateKind.Value
                    : "面对面中间换相段",
                Kind = FaceToFaceBusbarGroupKind_e.PhaseChange,
                RouteGroupIndex =
                    phaseChangeRegion.SuppressedBreakpointIntervalIndex,
                IsBridge = phaseChangeGroupIsBridge
            };
            for (var phaseIndex = 0;
                phaseIndex < PhaseCount;
                phaseIndex++)
            {
                var bPhaseRouteContext =
                    bCabinetContext.PhaseRouteContexts[phaseIndex];
                var aPhaseRouteContext =
                    aCabinetContext.PhaseRouteContexts[phaseIndex];
                EnsureSmallOffsetStackOffsetsMatch(
                    bPhaseRouteContext,
                    aPhaseRouteContext,
                    phaseIndex);
                var bPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                    bPhaseRouteContext.OrdinaryRouteReferencePoint,
                    axisSetting.WidthVector);
                if (phaseChangeStartWidthCoordinates != null)
                {
                    bPhaseWidthCoordinate =
                        phaseChangeStartWidthCoordinates[phaseIndex];
                }
                var aPhaseWidthCoordinate = BusbarGeometryUtility.Project(
                    aPhaseRouteContext.OrdinaryRouteReferencePoint,
                    axisSetting.WidthVector);
                if (phaseChangeEndWidthCoordinates != null)
                {
                    aPhaseWidthCoordinate =
                        phaseChangeEndWidthCoordinates[phaseIndex];
                }
                var phaseLevelHeightCoordinate = phaseIndex == 0
                    ? aPhaseBaseHeightCoordinate
                    : sharedRaisedLayerHeightCoordinate;
                var phaseChangeStartReferenceContext =
                    phaseChangeGeometricSideContext == null
                        ? bPhaseRouteContext
                        : phaseChangeGeometricSideContext.PhaseRouteContexts[
                            phaseChangeStartWidthCoordinates == null
                                ? phaseIndex
                                : ReversePhaseIndex(phaseIndex)];
                var phaseChangeEndReferenceContext =
                    phaseChangeGeometricSideContext == null
                        ? aPhaseRouteContext
                        : phaseChangeGeometricSideContext.PhaseRouteContexts[
                            phaseChangeEndWidthCoordinates == null
                                ? phaseIndex
                                : ReversePhaseIndex(phaseIndex)];

                for (var memberIndex = 0;
                    memberIndex < 1;
                    memberIndex++)
                {
                    var startPoint = CreatePointAtLengthAndWidth(
                        phaseChangeStartReferenceContext
                            .OrdinaryRouteReferencePoint,
                        axisSetting,
                        transitionStartLengthCoordinate,
                        bPhaseWidthCoordinate);
                    startPoint = BusbarGeometryUtility.WithProjection(
                        startPoint,
                        axisSetting.HeightVector,
                        BusbarGeometryUtility.Project(
                            startPoint,
                            axisSetting.HeightVector) +
                        routeLayerHeightOffset);
                    var endPoint = CreatePointAtLengthAndWidth(
                        phaseChangeEndReferenceContext
                            .OrdinaryRouteReferencePoint,
                        axisSetting,
                        phaseChangeEndLengthCoordinates == null
                            ? transitionEndLengthCoordinate
                            : phaseChangeEndLengthCoordinates[phaseIndex],
                        aPhaseWidthCoordinate);
                    endPoint = BusbarGeometryUtility.WithProjection(
                        endPoint,
                        axisSetting.HeightVector,
                        BusbarGeometryUtility.Project(
                            endPoint,
                            axisSetting.HeightVector) +
                        routeLayerHeightOffset);
                    FaceToWallPreviewTrace.Info(
                        "面对面换相叠排偏移",
                        "相=" + FaceToWallModelBusbarPlanner.GetPhaseName(
                            phaseIndex) +
                        "，根=" + (memberIndex + 1) +
                        "，原始叠排偏移=" +
                        bPhaseRouteContext.StackMemberOffsets[memberIndex]
                            .ToString("0.###") +
                        "，纵向错开=" +
                        ((bPhaseRouteContext.StackMemberOffsets.Max() -
                          bPhaseRouteContext.StackMemberOffsets[memberIndex]) /
                         2d).ToString("0.###") +
                        "，排厚高度符号=" +
                        bPhaseRouteContext.RouteThicknessDirection.Normalized
                            .Dot(normalizedHeightDirection)
                            .ToString("0.###"));
                    // 先按扫描出的叠排总包络构造唯一中心线，再拆分成实际成员。
                    // 这里故意只生成一次，禁止在成员循环中重复拼装换相几何。
                    var stackEnvelopeThickness =
                        CalculateStackEnvelopeThickness(
                            bPhaseRouteContext);
                    var stackEnvelopePhaseRouteContext =
                        CreateStackEnvelopeRouteContext(
                            bPhaseRouteContext,
                            stackEnvelopeThickness,
                            new[] { 0d });
                    var stackEnvelopeTemplateSettings =
                        new List<PlanarBendBusbarSetting>();
                    var phasePathStartPoint = startPoint;
                    var phasePathStartWidthCoordinate =
                        bPhaseWidthCoordinate;
                    if (phaseIndex == 1 &&
                        combinedPhaseChangeTemplateKind.HasValue)
                    {
                        // B 相先在换相首段完成小偏移。立弯或铣排都先以总包络
                        // 建模；完成偏移后，余下路径继续走原固定换相模板。
                        var bPhasePreludeEndLengthCoordinate =
                            phaseChangeLongitudinalLayout
                                .BPhaseRiseLengthCoordinate -
                            busbarWidth / 2d;
                        if (bPhasePreludeEndLengthCoordinate <=
                            transitionStartLengthCoordinate +
                            GeometryTolerance)
                        {
                            throw new InvalidOperationException(
                                "B相小偏移首段没有足够长度衔接后续换相。");
                        }

                        var bPhasePreludeEndPoint =
                            CreatePointAtLengthAndWidth(
                                startPoint,
                                axisSetting,
                                bPhasePreludeEndLengthCoordinate,
                                aPhaseWidthCoordinate);
                        if (combinedPhaseChangeTemplateKind.Value ==
                            FaceToFaceSmallOffsetTransitionKind_e.MilledBar)
                        {
                            stackEnvelopeTemplateSettings.AddRange(
                                CreateOrdinaryMilledTransitionSettings(
                                    startPoint,
                                    bPhasePreludeEndPoint,
                                    axisSetting,
                                    stackEnvelopePhaseRouteContext,
                                    combinedSignedWidthOffset,
                                    phaseChangeRoutingClearance,
                                    phaseIndex,
                                    FaceToWallModelBusbarPlanner.GetPhaseName(
                                        phaseIndex)));
                        }
                        else
                        {
                            stackEnvelopeTemplateSettings.Add(
                                CreateOrdinaryVerticalTransitionSetting(
                                    startPoint,
                                    bPhasePreludeEndPoint,
                                    axisSetting,
                                    stackEnvelopePhaseRouteContext,
                                    combinedSignedWidthOffset,
                                    bPhaseRouteContext.BusbarThickness,
                                    phaseIndex,
                                    FaceToWallModelBusbarPlanner.GetPhaseName(
                                        phaseIndex)));
                        }

                        phasePathStartPoint = bPhasePreludeEndPoint;
                        phasePathStartWidthCoordinate =
                            aPhaseWidthCoordinate;
                    }

                    var stackEnvelopePhaseSettings =
                        CreatePhaseChangeMemberSettings(
                            phaseIndex,
                            0,
                            phasePathStartPoint,
                            endPoint,
                            phasePathStartWidthCoordinate,
                            aPhaseWidthCoordinate,
                            phaseLevelHeightCoordinate,
                            phaseChangeLongitudinalLayout,
                            stackEnvelopePhaseRouteContext,
                            axisSetting,
                            0d,
                            // 横向短排仍需与本相底层/高层的纵向段
                            // 保持实体搭接，因此其高度偏移使用单根排厚；
                            // 叠排总包络只用于中心线层间距和纵向净距。
                            bPhaseRouteContext.BusbarThickness,
                            phaseChangeGroupIsBridge);
                    stackEnvelopeTemplateSettings.AddRange(
                        stackEnvelopePhaseSettings);
                    result.Busbars.AddRange(
                        SplitStackEnvelopeTransitionSettings(
                            stackEnvelopeTemplateSettings,
                            bPhaseRouteContext.StackMemberOffsets,
                            bPhaseRouteContext.BusbarThickness,
                            phaseIndex));
                    FaceToWallPreviewTrace.Info(
                        "面对面换相-扫描叠排中心线",
                        "相=" + FaceToWallModelBusbarPlanner.GetPhaseName(
                            phaseIndex) +
                        "，总包络厚度=" +
                        stackEnvelopeThickness.ToString("0.###") +
                        "，成员数=" +
                        bPhaseRouteContext.StackMemberOffsets.Count);
                }
            }

            if (frontBridgeOnly)
            {
                var frontBridges = CreateFixedSpecialFrontBridges(
                    result.Busbars, firstPiecePerGroupOnly: true).ToArray();
                result.Busbars.Clear();
                result.Busbars.AddRange(frontBridges);
                result.IsBridge = true;
            }

            var phaseChangeTransitionCenter =
                (transitionStartLengthCoordinate +
                 transitionEndLengthCoordinate) / 2d;
            var phaseChangeLayoutCenter =
                (phaseChangeLongitudinalLayout.CPhaseRiseLengthCoordinate +
                 phaseChangeLongitudinalLayout.SharedDropLengthCoordinate) /
                2d;
            FaceToWallPreviewTrace.Info(
                "面对面换相几何中心",
                "段边界中心=" + phaseChangeTransitionCenter.ToString("0.###") +
                "，路径动作中心=" + phaseChangeLayoutCenter.ToString("0.###") +
                "，中心偏差=" +
                (phaseChangeLayoutCenter - phaseChangeTransitionCenter)
                    .ToString("0.###") +
                "，段起止=" + transitionStartLengthCoordinate.ToString("0.###") +
                "/" + transitionEndLengthCoordinate.ToString("0.###") +
                "，推荐最小段长=" +
                phaseChangeRegion.MinimumRecommendedBreakpointSpan
                    .ToString("0.###"));

            foreach (var phaseChangePreviewBusbar in result.Busbars)
            {
                FaceToWallPreviewTrace.Info(
                    "面对面换相预览线段",
                    "组=" + (phaseChangePreviewBusbar.GroupKey ?? "未命名") +
                    "；点数=" +
                    (phaseChangePreviewBusbar.PointList?.Count ?? 0) +
                    "；排宽=" +
                    phaseChangePreviewBusbar.BusBarWidth.ToString("0.###") +
                    "；排厚=" +
                    phaseChangePreviewBusbar.BusBarThickness.ToString("0.###") +
                    "；排厚方向=" +
                    FormatVector(phaseChangePreviewBusbar.StartBusbarVector) +
                    "；排宽方向=" +
                    FormatVector(phaseChangePreviewBusbar.WidthVector) +
                    "；点集合=" +
                    string.Join(
                        " -> ",
                        (phaseChangePreviewBusbar.PointList ??
                            new List<Point3d>()).Select(FormatPoint)));
            }

            foreach (var phaseChangeBusbar in result.Busbars)
            {
                ValidatePhaseChangeBusbarContainment(
                    phaseChangeBusbar,
                    phaseChangeRegion,
                    axisSetting);
            }

            var phaseClearanceEvaluation =
                PlanarBusbarClearanceValidator.Evaluate(
                    result.Busbars,
                    GetPhaseChangeClearanceGroupKey,
                    phaseChangeRoutingClearance);
            if (!phaseClearanceEvaluation.IsClear)
            {
                var clearanceWarning =
                    "换相排实际最小净距仅 " +
                    phaseClearanceEvaluation.MinimumDistance
                        .ToString("0.###") +
                    " mm，小于要求的 " +
                    phaseChangeRoutingClearance.ToString("0.###") +
                    " mm；干涉位置=" +
                    phaseClearanceEvaluation.FirstGroupKey + "/" +
                    phaseClearanceEvaluation.SecondGroupKey + "。";
                FaceToWallPreviewTrace.Info(
                    "面对面换相排-净距不足预览",
                    clearanceWarning);
                if (allowInsufficientPhaseChangeClearancePreview)
                {
                    result.ClearanceWarning = clearanceWarning;
                    return result;
                }
                throw new InvalidOperationException(clearanceWarning);
            }

            FaceToWallPreviewTrace.Info(
                "面对面中间换相排-规划",
                "中间基架=" +
                phaseChangeRegion.MiddleFrame.ComponentName +
                "，逻辑段=" +
                (phaseChangeRegion.SuppressedBreakpointIntervalIndex + 1) +
                "，A相底层/B-C共享高层=" +
                aPhaseBaseHeightCoordinate.ToString("0.###") + "/" +
                sharedRaisedLayerHeightCoordinate.ToString("0.###") +
                "，规划实体净距=" +
                phaseChangeRoutingClearance.ToString("0.###") +
                "，叠排包络修正=" +
                phaseChangeStackEnvelopeThickness.ToString("0.###") +
                "，实测最小净距=" +
                phaseClearanceEvaluation.MinimumDistance
                    .ToString("0.###") +
                "，铜排件数=" + result.Busbars.Count);
            return result;
        }

        internal static PhaseChangeLongitudinalLayout
            CreateMinimumPhaseChangeLongitudinalLayout(
            double transitionStartLengthCoordinate,
            double transitionEndLengthCoordinate,
            double phaseChangeRoutingClearance,
            double busbarWidth,
            double busbarThickness,
            double maximumMemberLongitudinalStagger,
            double phaseChangeBendAllowance,
            bool allowShortLayout,
            bool isBridge = false)
        {
            var transitionLength = transitionEndLengthCoordinate -
                transitionStartLengthCoordinate;
            var minimumPostBendStraightLength =
                CalculatePhaseChangePerpendicularBendReserve(
                    busbarWidth,
                    busbarThickness,
                    phaseChangeBendAllowance);
            var nonNegativeMaximumMemberLongitudinalStagger = Math.Max(
                0d,
                maximumMemberLongitudinalStagger);
            double crossbarToVerticalCenterDistance =
                CalculatePhaseChangeActionCenterSpacing(
                    phaseChangeRoutingClearance, busbarWidth,
                    busbarThickness, phaseChangeBendAllowance) +
                nonNegativeMaximumMemberLongitudinalStagger;
            var cPhasePostBendStraightAdditionalLength =
                PlanarBusbarClearanceValidator.CalculateBendSweepHalfSpan(
                    busbarThickness, phaseChangeBendAllowance);
            var endSegmentMinimumLength =
                minimumPostBendStraightLength +
                nonNegativeMaximumMemberLongitudinalStagger;
            // C 相动作组前后使用同一端部最小长度，入口、出口各保留
            // “排宽 + 半叠排厚 + 折弯余量”（再叠加逐根错位），确保坐标对称。
            double minimumTransitionLength =
                3d * crossbarToVerticalCenterDistance +
                busbarWidth / 2d +
                cPhasePostBendStraightAdditionalLength +
                2d * endSegmentMinimumLength;
            var bridgeTerminalLength = isBridge
                ? CalculatePhaseChangeBridgeTerminalLength(busbarWidth)
                : 0d;
            minimumTransitionLength += 2d * bridgeTerminalLength;
            var transitionLengthIsInsufficient =
                transitionLength + ModelAlignmentTolerance <
                minimumTransitionLength;
            if (transitionLengthIsInsufficient && !allowShortLayout)
            {
                throw new InvalidOperationException(
                    "当前换相断点间距仅 " +
                    (transitionLength - busbarWidth).ToString("0.###") +
                    " mm，小于按当前绝缘距离计算的最小换相长度 " +
                    (minimumTransitionLength - busbarWidth)
                        .ToString("0.###") +
                        " mm。");
            }

            if (transitionLengthIsInsufficient)
            {
                // 设置界面要求净距不足时仍能显示预览。保留动作顺序，
                // 按现有断点把三个中心间距均匀压缩；最终由实体校验返回
                // ClearanceWarning，不能把压缩结果当作可生成方案。
                crossbarToVerticalCenterDistance = Math.Max(
                    GeometryTolerance,
                    (transitionLength -
                     2d * bridgeTerminalLength -
                     busbarWidth / 2d -
                     cPhasePostBendStraightAdditionalLength -
                     2d * endSegmentMinimumLength) / 3d);
                minimumTransitionLength = transitionLength;
            }

            // 三个动作中心间距保持最小值，多余长度在换相段两端均分，
            // 使动作组整体居中；桥式段的两端固定端头已在最小长度中
            // 对称让位，末端折下后的直段仍至少保留上述最小搭接长度。
            var extraLength = Math.Max(
                0d,
                transitionLength - minimumTransitionLength);
            var endSegmentLength = bridgeTerminalLength +
                endSegmentMinimumLength + extraLength / 2d;
            var cPhaseRiseLengthCoordinate =
                transitionStartLengthCoordinate + endSegmentLength;
            var cPhaseCrossLengthCoordinate =
                cPhaseRiseLengthCoordinate + busbarWidth / 2d +
                cPhasePostBendStraightAdditionalLength;
            var bPhaseRiseLengthCoordinate =
                cPhaseCrossLengthCoordinate +
                crossbarToVerticalCenterDistance;
            var aPhaseCrossLengthCoordinate =
                bPhaseRiseLengthCoordinate +
                crossbarToVerticalCenterDistance;
            var sharedDropLengthCoordinate =
                aPhaseCrossLengthCoordinate +
                crossbarToVerticalCenterDistance;

            return new PhaseChangeLongitudinalLayout
            {
                CPhaseRiseLengthCoordinate = cPhaseRiseLengthCoordinate,
                CPhaseCrossLengthCoordinate = cPhaseCrossLengthCoordinate,
                BPhaseRiseLengthCoordinate = bPhaseRiseLengthCoordinate,
                APhaseCrossLengthCoordinate = aPhaseCrossLengthCoordinate,
                SharedDropLengthCoordinate = sharedDropLengthCoordinate
            };
        }

        private static string GetPhaseChangeClearanceGroupKey(
            PlanarBendBusbarSetting busbarSetting)
        {
            var groupKey = busbarSetting?.GroupKey ?? string.Empty;
            var separatorIndex = groupKey.IndexOf('-');
            return separatorIndex > 0
                ? groupKey.Substring(0, separatorIndex)
                : groupKey;
        }

        private static void ValidatePhaseChangeLongitudinalClearance(
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            BusbarBridgeAxisSetting axisSetting,
            double busbarWidth,
            double insulationClearance)
        {
            foreach (var cabinetSideContext in new[]
                {
                    bCabinetContext,
                    aCabinetContext
                })
            {
                var orderedWidthCoordinates = cabinetSideContext
                    .PhaseRouteContexts
                    .Select(phaseRouteContext =>
                        BusbarGeometryUtility.Project(
                            phaseRouteContext.OrdinaryRouteReferencePoint,
                            axisSetting.WidthVector))
                    .OrderBy(widthCoordinate => widthCoordinate)
                    .ToList();
                for (var widthCoordinateIndex = 0;
                    widthCoordinateIndex <
                        orderedWidthCoordinates.Count - 1;
                    widthCoordinateIndex++)
                {
                    var surfaceClearance =
                        orderedWidthCoordinates[widthCoordinateIndex + 1] -
                        orderedWidthCoordinates[widthCoordinateIndex] -
                        busbarWidth;
                    if (surfaceClearance <
                        insulationClearance - ModelAlignmentTolerance)
                    {
                        throw new InvalidOperationException(
                            cabinetSideContext.SideLabel +
                            "换相段相邻两相纵向铜排的净距仅 " +
                            surfaceClearance.ToString("0.###") +
                            " mm，小于换相绝缘距离 " +
                            insulationClearance.ToString("0.###") +
                            " mm。");
                    }
                }
            }
        }

        internal static IReadOnlyList<PlanarBendBusbarSetting>
            CreatePhaseChangeMemberSettings(
            int phaseIndex,
            int memberIndex,
            Point3d startPoint,
            Point3d endPoint,
            double startWidthCoordinate,
            double endWidthCoordinate,
            double phaseLevelHeightCoordinate,
            PhaseChangeLongitudinalLayout longitudinalLayout,
            FaceToWallPhaseRouteContext phaseRouteContext,
            BusbarBridgeAxisSetting axisSetting,
            double stackMemberOffset,
            double? phaseGeometryBusbarThickness = null,
            bool isBridge = false)
        {
            if (longitudinalLayout == null)
            {
                throw new ArgumentNullException(nameof(longitudinalLayout));
            }
            var groupKey = FaceToWallModelBusbarPlanner.GetPhaseName(
                    phaseIndex) + "-" + (memberIndex + 1);
            var effectivePhaseGeometryBusbarThickness =
                phaseGeometryBusbarThickness ??
                phaseRouteContext.BusbarThickness;
            if (effectivePhaseGeometryBusbarThickness <= 0d ||
                double.IsNaN(effectivePhaseGeometryBusbarThickness) ||
                double.IsInfinity(effectivePhaseGeometryBusbarThickness))
            {
                throw new InvalidOperationException(
                    "换相几何排厚必须为有效正数。");
            }
            var longitudinalPrototype = new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = groupKey,
                StartBusbarVector =
                    phaseRouteContext.RouteThicknessDirection.Copy(),
                WidthVector = phaseRouteContext.RouteWidthDirection.Copy(),
                BusBarWidth = phaseRouteContext.BusbarWidth,
                BusBarThickness = phaseRouteContext.BusbarThickness,
                BarBendingRadius = phaseRouteContext.BendingRadius,
                BendAllowance = phaseRouteContext.BendAllowance,
                StartLapWidth = phaseRouteContext.BusbarWidth,
                EndLapWidth = phaseRouteContext.BusbarWidth
            };

            // 旧版预览对换相组的每根叠排都按成员顺序错开折弯点，
            // 相邻铜排的纵向错开量为 2 个排厚。StackMemberOffsets
            // 是以中心为零点的有符号坐标（例如 -48,-16,16,48），
            // 因此最大值减当前值还要除以 2，才能还原旧版的
            // 48,32,16,0，而不能直接使用完整的坐标差。
            // 同一根叠排的 C 横向段、B 起伏段、A 横向段和 B/C 下降段
            // 必须使用同一个纵向错开量整体平移；如果一侧加、另一侧减，
            // 会把本应保留的相间净距直接扣掉两倍错开量。
            var maximumStackMemberOffset = phaseRouteContext
                .StackMemberOffsets == null ||
                phaseRouteContext.StackMemberOffsets.Count == 0
                ? 0d
                : phaseRouteContext.StackMemberOffsets.Max();
            var memberLongitudinalStagger = phaseRouteContext
                .StackMemberOffsets == null ||
                memberIndex < 0 ||
                memberIndex >= phaseRouteContext.StackMemberOffsets.Count
                ? 0d
                : (maximumStackMemberOffset -
                   phaseRouteContext.StackMemberOffsets[memberIndex]) / 2d;
            if (phaseIndex == 0)
            {
                return OffsetPhaseChangeStackMemberSettings(
                    CreateBottomAPhaseChangeMemberSettings(
                    startPoint,
                    endPoint,
                    startWidthCoordinate,
                    endWidthCoordinate,
                    phaseLevelHeightCoordinate,
                    longitudinalLayout.APhaseCrossLengthCoordinate +
                    memberLongitudinalStagger,
                    longitudinalPrototype,
                    phaseRouteContext,
                    axisSetting,
                    effectivePhaseGeometryBusbarThickness,
                    isBridge),
                    stackMemberOffset);
            }

            // 桥式斜段固定只升降一个实际排厚；叠排总包络仍由
            // longitudinalPrototype.BusBarThickness 表示并在后续拆分。
            var phaseChangeBridgeReferencePoints = isBridge
                ? CreatePhaseChangeBridgeReferenceCenterlinePoints(
                    startPoint,
                    endPoint,
                    axisSetting,
                    phaseRouteContext.BusbarWidth,
                    effectivePhaseGeometryBusbarThickness)
                : null;
            var innerPhaseStartPoint = isBridge
                ? phaseChangeBridgeReferencePoints[2]
                : startPoint;
            var innerPhaseEndPoint = isBridge
                ? phaseChangeBridgeReferencePoints[3]
                : endPoint;

            if (phaseIndex == 1)
            {
                var staggeredRiseLengthCoordinate =
                    longitudinalLayout.BPhaseRiseLengthCoordinate +
                    memberLongitudinalStagger;
                var staggeredDropLengthCoordinate =
                    longitudinalLayout.SharedDropLengthCoordinate +
                    memberLongitudinalStagger;
                if (staggeredRiseLengthCoordinate >=
                    staggeredDropLengthCoordinate - GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "B相换相段长度不足以容纳叠排逐根错开的垂直段。请增大中间段长度或减小换相绝缘距离。");
                }
                var middleSetting = longitudinalPrototype.Copy();
                middleSetting.PointList = new List<Point3d>
                {
                    innerPhaseStartPoint.Copy(),
                    CreatePointWithLengthAndHeight(
                        innerPhaseStartPoint,
                        axisSetting,
                        staggeredRiseLengthCoordinate,
                        BusbarGeometryUtility.Project(
                            innerPhaseStartPoint,
                            axisSetting.HeightVector)),
                    CreatePointWithLengthAndHeight(
                        innerPhaseStartPoint,
                        axisSetting,
                        staggeredRiseLengthCoordinate,
                        phaseLevelHeightCoordinate),
                    CreatePointWithLengthAndHeight(
                        innerPhaseStartPoint,
                        axisSetting,
                        staggeredDropLengthCoordinate,
                        phaseLevelHeightCoordinate),
                    CreatePointWithLengthAndHeight(
                        innerPhaseEndPoint,
                        axisSetting,
                        staggeredDropLengthCoordinate,
                        BusbarGeometryUtility.Project(
                            innerPhaseEndPoint,
                            axisSetting.HeightVector)),
                    innerPhaseEndPoint.Copy()
                };
                if (isBridge)
                {
                    middleSetting.PointList =
                        AddPhaseChangeBridgeTerminalPoints(
                            middleSetting.PointList,
                            phaseChangeBridgeReferencePoints);
                }
                middleSetting.RemoveInvalidPoints();
                return OffsetPhaseChangeStackMemberSettings(
                    new[] { middleSetting },
                    stackMemberOffset);
            }

            var crossLengthCoordinate =
                longitudinalLayout.CPhaseCrossLengthCoordinate +
                memberLongitudinalStagger;
            var levelStartLengthCoordinate =
                longitudinalLayout.CPhaseRiseLengthCoordinate +
                memberLongitudinalStagger;
            var levelEndLengthCoordinate =
                longitudinalLayout.SharedDropLengthCoordinate +
                memberLongitudinalStagger;
            if (levelStartLengthCoordinate >=
                    crossLengthCoordinate - GeometryTolerance ||
                levelEndLengthCoordinate <=
                    crossLengthCoordinate + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "C相换相段长度不足以容纳叠排逐根错开的横向搭接段。请增大中间段长度或减小换相绝缘距离。");
            }
            var widthDirectionSign = Math.Sign(
                endWidthCoordinate - startWidthCoordinate);
            if (widthDirectionSign == 0d || Math.Abs(
                    endWidthCoordinate - startWidthCoordinate) <=
                phaseRouteContext.BusbarWidth + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相换相前后宽度位置不足以形成横向搭接短排。");
            }

            var normalizedPhaseChangeHeightDirection =
                axisSetting.HeightVector.Normalized;
            var routeThicknessHeightDirectionSign =
                phaseRouteContext.RouteThicknessDirection.Normalized.Dot(
                    normalizedPhaseChangeHeightDirection);
            if (Math.Abs(routeThicknessHeightDirectionSign) <= 0.99d)
            {
                throw new InvalidOperationException(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相换相排厚方向与基架高度方向不一致。");
            }

            var perpendicularLapProjectionExtension =
                CalculatePhaseChangePerpendicularLapProjectionExtension(
                    phaseRouteContext.BusbarWidth);
            // 横向换相短排必须位于纵向母排的统一“上侧”。StartBusbarVector
            // 只表示零件局部排厚方向，柜体两端的路径方向可能相反，不能把
            // 它的符号直接当成高度偏移符号，否则反向路径会把短排压到
            // 下侧，造成相序重叠。高度坐标已经由 axisSetting.HeightVector
            // 统一定向，因此这里固定沿高度正方向偏移一个排厚。
            var crossbarHeightCoordinate =
                phaseLevelHeightCoordinate +
                effectivePhaseGeometryBusbarThickness;

            var incomingSetting = longitudinalPrototype.Copy();
            incomingSetting.PointList = new List<Point3d>
            {
                innerPhaseStartPoint.Copy(),
                CreatePointWithLengthAndHeight(
                    innerPhaseStartPoint,
                    axisSetting,
                    levelStartLengthCoordinate,
                    BusbarGeometryUtility.Project(
                        innerPhaseStartPoint,
                        axisSetting.HeightVector)),
                CreatePointWithLengthAndHeight(
                    innerPhaseStartPoint,
                    axisSetting,
                    levelStartLengthCoordinate,
                    phaseLevelHeightCoordinate),
                // 内侧入线段只延伸到横向搭接排的半排宽边界；桥式段
                // 的固定三点端头在该内侧路径外单独拼接。
                CreatePointWithLengthAndHeight(
                    innerPhaseStartPoint,
                    axisSetting,
                    crossLengthCoordinate +
                    perpendicularLapProjectionExtension,
                    phaseLevelHeightCoordinate)
            };
            if (isBridge)
            {
                incomingSetting.PointList =
                    AddPhaseChangeBridgeStartTerminalPoints(
                        incomingSetting.PointList,
                        phaseChangeBridgeReferencePoints);
            }
            incomingSetting.RemoveInvalidPoints();

            var crossbarSetting = longitudinalPrototype.Copy();
            // 横向短排本身沿业务宽度方向铺开，排厚方向必须绑定统一高度轴，
            // 不能继承柜体端可能反向的 StartBusbarVector。
            crossbarSetting.StartBusbarVector =
                axisSetting.HeightVector.Copy();
            crossbarSetting.WidthVector =
                axisSetting.LengthVector.Copy();
            crossbarSetting.PointList = new List<Point3d>
            {
                // 旧版换相预览的横向搭接段不是把相序距离收窄到
                // 两个相中心之间，而是从两端各向外延半个排宽。
                // 这样横向段在两端分别与前、后纵向段形成排宽×排宽搭接。
                CreatePointAtLengthWidthAndHeight(
                    startPoint,
                    axisSetting,
                    crossLengthCoordinate,
                    startWidthCoordinate - widthDirectionSign *
                        phaseRouteContext.BusbarWidth / 2d,
                    crossbarHeightCoordinate),
                CreatePointAtLengthWidthAndHeight(
                    endPoint,
                    axisSetting,
                    crossLengthCoordinate,
                    endWidthCoordinate + widthDirectionSign *
                        phaseRouteContext.BusbarWidth / 2d,
                    crossbarHeightCoordinate)
            };

            var outgoingSetting = longitudinalPrototype.Copy();
            outgoingSetting.PointList = new List<Point3d>
            {
                CreatePointWithLengthAndHeight(
                    innerPhaseEndPoint,
                    axisSetting,
                    crossLengthCoordinate -
                    perpendicularLapProjectionExtension,
                    phaseLevelHeightCoordinate),
                CreatePointWithLengthAndHeight(
                    innerPhaseEndPoint,
                    axisSetting,
                    levelEndLengthCoordinate,
                    phaseLevelHeightCoordinate),
                CreatePointWithLengthAndHeight(
                    innerPhaseEndPoint,
                    axisSetting,
                    levelEndLengthCoordinate,
                    BusbarGeometryUtility.Project(
                        innerPhaseEndPoint,
                        axisSetting.HeightVector)),
                innerPhaseEndPoint.Copy()
            };
            if (isBridge)
            {
                outgoingSetting.PointList =
                    AddPhaseChangeBridgeEndTerminalPoints(
                        outgoingSetting.PointList,
                        phaseChangeBridgeReferencePoints);
            }
            outgoingSetting.RemoveInvalidPoints();
            return OffsetPhaseChangeStackMemberSettings(
                new[]
                {
                    incomingSetting,
                    crossbarSetting,
                    outgoingSetting
                },
                stackMemberOffset);
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            OffsetPhaseChangeStackMemberSettings(
            IReadOnlyList<PlanarBendBusbarSetting> centerlineSettings,
            double stackMemberOffset)
        {
            if (centerlineSettings == null)
            {
                throw new ArgumentNullException(nameof(centerlineSettings));
            }

            var offsetSettings = new List<PlanarBendBusbarSetting>(
                centerlineSettings.Count);
            foreach (var centerlineSetting in centerlineSettings)
            {
                if (centerlineSetting == null)
                {
                    throw new InvalidOperationException(
                        "换相叠排中心线不能包含空项。");
                }

                var offsetSetting = centerlineSetting.Copy();
                offsetSetting.PointList =
                    offsetSetting.GetOffsetCenterlinePointList(
                        stackMemberOffset);
                offsetSettings.Add(offsetSetting);
            }

            return offsetSettings.AsReadOnly();
        }

        /// <summary>
        /// 按桥式普通段的同一套六点算法生成换相段两端的参考端头。
        /// startPoint/endPoint 是桥式端头的抬高搭接点；返回点 2、3 为
        /// 两端斜段落回换相层后的内侧连接点。
        /// </summary>
        private static IReadOnlyList<Point3d>
            CreatePhaseChangeBridgeReferenceCenterlinePoints(
            Point3d startPoint,
            Point3d endPoint,
            BusbarBridgeAxisSetting axisSetting,
            double busbarWidth,
            double busbarThickness)
        {
            if (startPoint == null || endPoint == null)
            {
                throw new ArgumentNullException(
                    startPoint == null ? nameof(startPoint) : nameof(endPoint));
            }
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }
            ValidatePositiveFinite(busbarThickness, nameof(busbarThickness));
            var normalizedHeightDirection = axisSetting.HeightVector.Normalized;
            var ordinaryStartPoint = PlanarBendBusbarSetting.Translate(
                startPoint,
                normalizedHeightDirection,
                -busbarThickness);
            var ordinaryEndPoint = PlanarBendBusbarSetting.Translate(
                endPoint,
                normalizedHeightDirection,
                -busbarThickness);
            return FaceToWallModelBusbarPlanner
                .CreateBridgeReferenceCenterlinePoints(
                    ordinaryStartPoint,
                    ordinaryEndPoint,
                    axisSetting.LengthVector,
                    axisSetting.HeightVector,
                    busbarWidth,
                    busbarThickness)
                .Select(bridgeReferencePoint =>
                    bridgeReferencePoint.Copy())
                .ToList()
                .AsReadOnly();
        }

        private static List<Point3d>
            AddPhaseChangeBridgeStartTerminalPoints(
            IReadOnlyList<Point3d> innerCenterlinePoints,
            IReadOnlyList<Point3d> bridgeReferencePoints)
        {
            ValidatePhaseChangeBridgeReferencePointLists(
                innerCenterlinePoints,
                bridgeReferencePoints);
            var result = bridgeReferencePoints
                .Take(3)
                .Select(bridgeReferencePoint => bridgeReferencePoint.Copy())
                .ToList();
            result.AddRange(innerCenterlinePoints
                .Skip(1)
                .Select(innerCenterlinePoint => innerCenterlinePoint.Copy()));
            return result;
        }

        private static List<Point3d>
            AddPhaseChangeBridgeEndTerminalPoints(
            IReadOnlyList<Point3d> innerCenterlinePoints,
            IReadOnlyList<Point3d> bridgeReferencePoints)
        {
            ValidatePhaseChangeBridgeReferencePointLists(
                innerCenterlinePoints,
                bridgeReferencePoints);
            var result = innerCenterlinePoints
                .Take(innerCenterlinePoints.Count - 1)
                .Select(innerCenterlinePoint => innerCenterlinePoint.Copy())
                .ToList();
            result.AddRange(bridgeReferencePoints
                .Skip(3)
                .Select(bridgeReferencePoint => bridgeReferencePoint.Copy()));
            return result;
        }

        private static List<Point3d>
            AddPhaseChangeBridgeTerminalPoints(
            IReadOnlyList<Point3d> innerCenterlinePoints,
            IReadOnlyList<Point3d> bridgeReferencePoints)
        {
            ValidatePhaseChangeBridgeReferencePointLists(
                innerCenterlinePoints,
                bridgeReferencePoints);
            var result = bridgeReferencePoints
                .Take(3)
                .Select(bridgeReferencePoint => bridgeReferencePoint.Copy())
                .ToList();
            result.AddRange(innerCenterlinePoints
                .Skip(1)
                .Take(innerCenterlinePoints.Count - 2)
                .Select(innerCenterlinePoint => innerCenterlinePoint.Copy()));
            result.AddRange(bridgeReferencePoints
                .Skip(3)
                .Select(bridgeReferencePoint => bridgeReferencePoint.Copy()));
            return result;
        }

        private static void ValidatePhaseChangeBridgeReferencePointLists(
            IReadOnlyList<Point3d> innerCenterlinePoints,
            IReadOnlyList<Point3d> bridgeReferencePoints)
        {
            if (innerCenterlinePoints == null ||
                innerCenterlinePoints.Count < 2 ||
                innerCenterlinePoints.Any(innerCenterlinePoint =>
                    innerCenterlinePoint == null))
            {
                throw new InvalidOperationException(
                    "换相桥式端头缺少有效的内部中心线路径。 ");
            }
            if (bridgeReferencePoints == null ||
                bridgeReferencePoints.Count != 6 ||
                bridgeReferencePoints.Any(bridgeReferencePoint =>
                    bridgeReferencePoint == null))
            {
                throw new InvalidOperationException(
                    "换相桥式端头必须使用完整的六点参考路径。 ");
            }
        }

        internal static IReadOnlyList<PlanarBendBusbarSetting>
            CreateBottomAPhaseChangeMemberSettings(
            Point3d startPoint,
            Point3d endPoint,
            double startWidthCoordinate,
            double endWidthCoordinate,
            double phaseLevelHeightCoordinate,
            double crossLengthCoordinate,
            PlanarBendBusbarSetting longitudinalPrototype,
            FaceToWallPhaseRouteContext phaseRouteContext,
            BusbarBridgeAxisSetting axisSetting,
            double phaseGeometryBusbarThickness,
            bool isBridge = false)
        {
            var widthDirectionSign = Math.Sign(
                endWidthCoordinate - startWidthCoordinate);
            if (widthDirectionSign == 0d || Math.Abs(
                    endWidthCoordinate - startWidthCoordinate) <=
                phaseRouteContext.BusbarWidth + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "A相换相前后宽度位置不足以形成底层横向搭接短排。");
            }

            var thicknessHeightDirectionSign =
                phaseRouteContext.RouteThicknessDirection.Normalized.Dot(
                    axisSetting.HeightVector.Normalized);
            if (Math.Abs(thicknessHeightDirectionSign) <= 0.99d)
            {
                throw new InvalidOperationException(
                    "A相底层换相排厚方向与基架高度方向不一致。");
            }

            var perpendicularLapProjectionExtension =
                CalculatePhaseChangePerpendicularLapProjectionExtension(
                    phaseRouteContext.BusbarWidth);
            var phaseChangeBridgeReferencePoints = isBridge
                ? CreatePhaseChangeBridgeReferenceCenterlinePoints(
                    startPoint,
                    endPoint,
                    axisSetting,
                    phaseRouteContext.BusbarWidth,
                    phaseGeometryBusbarThickness)
                : null;
            var innerPhaseStartPoint = isBridge
                ? phaseChangeBridgeReferencePoints[2]
                : startPoint;
            var innerPhaseEndPoint = isBridge
                ? phaseChangeBridgeReferencePoints[3]
                : endPoint;
            var incomingSetting = longitudinalPrototype.Copy();
            incomingSetting.PointList = new List<Point3d>
            {
                innerPhaseStartPoint.Copy(),
                CreatePointWithLengthAndHeight(
                    innerPhaseStartPoint,
                    axisSetting,
                    crossLengthCoordinate +
                    perpendicularLapProjectionExtension,
                    BusbarGeometryUtility.Project(
                        innerPhaseStartPoint,
                        axisSetting.HeightVector))
            };
            if (isBridge)
            {
                incomingSetting.PointList =
                    AddPhaseChangeBridgeStartTerminalPoints(
                        incomingSetting.PointList,
                        phaseChangeBridgeReferencePoints);
            }

            var crossbarSetting = longitudinalPrototype.Copy();
            crossbarSetting.StartBusbarVector =
                axisSetting.HeightVector.Copy();
            crossbarSetting.WidthVector = axisSetting.LengthVector.Copy();
            crossbarSetting.PointList = new List<Point3d>
            {
                CreatePointAtLengthWidthAndHeight(
                    innerPhaseStartPoint,
                    axisSetting,
                    crossLengthCoordinate,
                    startWidthCoordinate - widthDirectionSign *
                        phaseRouteContext.BusbarWidth / 2d,
                    phaseLevelHeightCoordinate +
                    phaseGeometryBusbarThickness),
                CreatePointAtLengthWidthAndHeight(
                    innerPhaseEndPoint,
                    axisSetting,
                    crossLengthCoordinate,
                    endWidthCoordinate + widthDirectionSign *
                        phaseRouteContext.BusbarWidth / 2d,
                    phaseLevelHeightCoordinate +
                    phaseGeometryBusbarThickness)
            };

            var outgoingSetting = longitudinalPrototype.Copy();
            outgoingSetting.PointList = new List<Point3d>
            {
                CreatePointWithLengthAndHeight(
                    innerPhaseEndPoint,
                    axisSetting,
                    crossLengthCoordinate -
                    perpendicularLapProjectionExtension,
                    BusbarGeometryUtility.Project(
                        innerPhaseEndPoint,
                        axisSetting.HeightVector)),
                innerPhaseEndPoint.Copy()
            };
            if (isBridge)
            {
                outgoingSetting.PointList =
                    AddPhaseChangeBridgeEndTerminalPoints(
                        outgoingSetting.PointList,
                        phaseChangeBridgeReferencePoints);
            }
            return new[]
            {
                incomingSetting,
                crossbarSetting,
                outgoingSetting
            };
        }

        private static Point3d CreatePointWithLengthAndHeight(
            Point3d referencePoint,
            BusbarBridgeAxisSetting axisSetting,
            double lengthCoordinate,
            double heightCoordinate)
        {
            return BusbarGeometryUtility.WithProjection(
                BusbarGeometryUtility.WithProjection(
                    referencePoint,
                    axisSetting.LengthVector,
                    lengthCoordinate),
                axisSetting.HeightVector,
                heightCoordinate);
        }

        private static Point3d CreatePointAtLengthWidthAndHeight(
            Point3d referencePoint,
            BusbarBridgeAxisSetting axisSetting,
            double lengthCoordinate,
            double widthCoordinate,
            double heightCoordinate)
        {
            return BusbarGeometryUtility.WithProjection(
                CreatePointAtLengthAndWidth(
                    referencePoint,
                    axisSetting,
                    lengthCoordinate,
                    widthCoordinate),
                axisSetting.HeightVector,
                heightCoordinate);
        }

        private static void ValidatePhaseChangeBusbarContainment(
            PlanarBendBusbarSetting phaseChangeBusbar,
            FaceToFacePhaseChangeRegionPlan phaseChangeRegion,
            BusbarBridgeAxisSetting axisSetting)
        {
            if (phaseChangeBusbar?.PointList == null ||
                phaseChangeBusbar.PointList.Count < 2)
            {
                throw new InvalidOperationException(
                    "面对面换相排没有形成有效路径。");
            }

            var widthVectorIsFrameWidth =
                BusbarGeometryUtility.AreParallel(
                    phaseChangeBusbar.WidthVector,
                    axisSetting.WidthVector);
            var widthEnvelopeHalfSize = widthVectorIsFrameWidth
                ? phaseChangeBusbar.BusBarWidth / 2d
                : phaseChangeBusbar.BusBarThickness / 2d;
            var heightEnvelopeHalfSize =
                phaseChangeBusbar.BusBarThickness / 2d;
            foreach (var point in phaseChangeBusbar.PointList)
            {
                var widthCoordinate = BusbarGeometryUtility.Project(
                    point,
                    axisSetting.WidthVector);
                var heightCoordinate = BusbarGeometryUtility.Project(
                    point,
                    axisSetting.HeightVector);
                if (widthCoordinate - widthEnvelopeHalfSize <
                        phaseChangeRegion.AvailableWidthMinimum -
                        ModelAlignmentTolerance ||
                    widthCoordinate + widthEnvelopeHalfSize >
                        phaseChangeRegion.AvailableWidthMaximum +
                        ModelAlignmentTolerance ||
                    heightCoordinate - heightEnvelopeHalfSize <
                        phaseChangeRegion.AvailableHeightMinimum -
                        ModelAlignmentTolerance ||
                    heightCoordinate + heightEnvelopeHalfSize >
                        phaseChangeRegion.AvailableHeightMaximum +
                        ModelAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        "面对面换相排超出最中间基架 Box 在宽度/高度方向缩小绝缘距离后的范围；" +
                        "请检查基架尺寸或减小换相绝缘距离。");
                }
            }
        }

        private static FaceToFaceGeneratedBusbarGroup CreateCabinetConnectionGroup(
            string displayName,
            FaceToFaceBusbarGroupKind_e kind,
            FaceToFaceSidePlanningContext sideContext)
        {
            var result = new FaceToFaceGeneratedBusbarGroup
            {
                DisplayName = displayName,
                Kind = kind,
                RouteGroupIndex = -1,
                IsBridge = false
            };
            result.Busbars.AddRange(
                sideContext.CabinetConnectionBusbarsByPhase.SelectMany(
                    phaseBusbars => phaseBusbars));
            return result;
        }

        private static SideToSideFixedPhaseAssignments
            CreateSideToSideFixedPhaseAssignments(
            AssemblyPartCatalog partCatalog,
            BusbarBridgeAxisSetting routeAxisSetting,
            FaceToFaceCabinetExtensionSides cabinetExtensionSides,
            FaceToFaceStandaloneBaseAssignment standaloneBaseAssignment,
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxes,
            IReadOnlyList<InsulatorConnectionInfo>
                availableConnectionInsulators)
        {
            var bPhaseGroups =
                FaceToWallModelBusbarPlanner.GroupExtensionBusbarsByPhase(
                    cabinetExtensionSides.BCabinetBusbars,
                    routeAxisSetting.WidthVector);
            var aPhaseGroups =
                FaceToWallModelBusbarPlanner.GroupExtensionBusbarsByPhase(
                    cabinetExtensionSides.ACabinetBusbars,
                    routeAxisSetting.WidthVector);
            var bPhaseReferencePoints = GetPhaseTerminalReferencePoints(
                bPhaseGroups,
                "B柜");
            var aPhaseReferencePoints = GetPhaseTerminalReferencePoints(
                aPhaseGroups,
                "A柜");
            var aPhysicalPhaseIndexesByLogicalPhase =
                ResolveSideToSidePhasePairing(
                    bPhaseGroups,
                    aPhaseGroups);
            var bBaseReferencePoint =
                standaloneBaseAssignment.BCabinetBaseCenter;
            var aBaseReferencePoint =
                standaloneBaseAssignment.ACabinetBaseCenter;
            var cabinetSpanDirection = ResolveSideToSideCabinetSpanDirection(
                routeAxisSetting,
                bBaseReferencePoint,
                aBaseReferencePoint);
            var nearestOuterPhaseIndex =
                SideToSideFixedPhaseChangeRules
                    .ResolveNearestBCabinetOuterPhaseIndex(
                        bPhaseReferencePoints,
                        aBaseReferencePoint,
                        cabinetSpanDirection,
                        SideToSidePhysicalAlignmentTolerance,
                        out var phaseADistanceToACabinet,
                        out var phaseCDistanceToACabinet);
            var nearestOuterPhase = nearestOuterPhaseIndex == 0
                ? BusbarPhase_e.A
                : BusbarPhase_e.C;
            var nearestPhasePairHeightReference =
                (BusbarGeometryUtility.Project(
                     bPhaseReferencePoints[nearestOuterPhaseIndex],
                     routeAxisSetting.HeightVector) +
                 BusbarGeometryUtility.Project(
                     aPhaseReferencePoints[
                         aPhysicalPhaseIndexesByLogicalPhase[
                             nearestOuterPhaseIndex]],
                     routeAxisSetting.HeightVector)) / 2d;

            FaceToWallPreviewTrace.Info(
                "侧对侧固定换相-靠近A柜的B柜外侧相",
                "柜间方向=" + cabinetSpanDirection +
                "，A相距离=" +
                phaseADistanceToACabinet.ToString("0.###") +
                "，C相距离=" +
                phaseCDistanceToACabinet.ToString("0.###") +
                "，选择=" + nearestOuterPhase);
            var bBaseInsulatorsByPhase =
                SelectNearestSideToSideBaseInsulatorsByPhase(
                partCatalog,
                standaloneBaseAssignment.BCabinetBaseComponent,
                availableConnectionInsulators,
                routeAxisSetting.LengthVector,
                bPhaseReferencePoints,
                "B柜基座");
            var aBaseInsulatorsByPhase =
                SelectNearestSideToSideBaseInsulatorsByPhase(
                partCatalog,
                standaloneBaseAssignment.ACabinetBaseComponent,
                availableConnectionInsulators,
                routeAxisSetting.LengthVector,
                aPhaseReferencePoints,
                "A柜基座");
            var aBaseInsulatorsByLogicalPhase =
                aPhysicalPhaseIndexesByLogicalPhase
                    .Select(physicalPhaseIndex =>
                        aBaseInsulatorsByPhase[physicalPhaseIndex])
                    .ToList();

            var bFirstFrame = orderedFrameBoxes[0].Component;
            var aFirstFrame =
                orderedFrameBoxes[orderedFrameBoxes.Count - 1].Component;
            var alignedFrameHeightLaneGroups =
                new List<List<InsulatorConnectionInfo>>();
            for (var frameIndex = 0;
                frameIndex < orderedFrameBoxes.Count;
                frameIndex++)
            {
                var frameBox = orderedFrameBoxes[frameIndex];
                var frameLabel = "B→A第" + (frameIndex + 1) + "个垂直基架";
                var frameReferencePoint = frameIndex == 0
                    ? bBaseReferencePoint
                    : frameIndex == orderedFrameBoxes.Count - 1
                        ? aBaseReferencePoint
                        : BusbarGeometryUtility.GetComponentCenter(
                            frameBox.Component);
                if (frameReferencePoint == null)
                {
                    throw new InvalidOperationException(
                        frameLabel + "没有可用的 Box 中心，无法读取高度绝缘子层。");
                }

                var currentFrameInsulators =
                    SelectStrictSideToSideSupportInsulators(
                        partCatalog,
                        frameBox.Component,
                        availableConnectionInsulators,
                        routeAxisSetting.LengthVector,
                        routeAxisSetting.LengthVector,
                        frameReferencePoint,
                        frameLabel);
                var currentFrameHeightLanes = frameIndex == 0
                    ? OrderHeightLaneInsulators(
                        currentFrameInsulators,
                        routeAxisSetting.HeightVector,
                        nearestPhasePairHeightReference,
                        frameLabel)
                    : MatchInsulatorsToHeightLanes(
                        currentFrameInsulators,
                        alignedFrameHeightLaneGroups[0],
                        routeAxisSetting.HeightVector,
                        frameLabel);
                alignedFrameHeightLaneGroups.Add(currentFrameHeightLanes);
            }

            var bHeightLaneInsulators = alignedFrameHeightLaneGroups[0];
            var aHeightLaneInsulators = alignedFrameHeightLaneGroups[
                alignedFrameHeightLaneGroups.Count - 1];
            var bFrameInsulatorsByPhase = MapHeightLanesToPhases(
                bHeightLaneInsulators,
                nearestOuterPhaseIndex);
            var aFrameInsulatorsByLogicalPhase = MapHeightLanesToPhases(
                aHeightLaneInsulators,
                nearestOuterPhaseIndex);

            FaceToWallPreviewTrace.Info(
                "侧对侧固定换相-绝缘子分配",
                "高度最近层=" + nearestOuterPhase +
                "相，中间层=B相，最外层=" +
                (nearestOuterPhase == BusbarPhase_e.A ? "C" : "A") +
                "相；B柜基座=" + FormatInsulatorPhaseAssignment(
                    bBaseInsulatorsByPhase) +
                "；A柜基座=" + FormatInsulatorPhaseAssignment(
                    aBaseInsulatorsByLogicalPhase) +
                "；B柜首基架=" + FormatInsulatorPhaseAssignment(
                    bFrameInsulatorsByPhase) +
                "；A柜首基架=" + FormatInsulatorPhaseAssignment(
                    aFrameInsulatorsByLogicalPhase) +
                "；A柜实际相位映射=" + string.Join(
                    "-",
                    aPhysicalPhaseIndexesByLogicalPhase.Select(
                        physicalPhaseIndex =>
                            FaceToWallModelBusbarPlanner.GetPhaseName(
                                physicalPhaseIndex))));
            return new SideToSideFixedPhaseAssignments
            {
                BCabinet = new SideToSideFixedPhaseSideAssignment
                {
                    FirstFrameComponent = bFirstFrame,
                    BaseInsulatorsByPhase =
                        bBaseInsulatorsByPhase.AsReadOnly(),
                    FrameInsulatorsByPhase =
                        bFrameInsulatorsByPhase.AsReadOnly()
                },
                ACabinet = new SideToSideFixedPhaseSideAssignment
                {
                    FirstFrameComponent = aFirstFrame,
                    BaseInsulatorsByPhase =
                        aBaseInsulatorsByLogicalPhase.AsReadOnly(),
                    FrameInsulatorsByPhase =
                        aFrameInsulatorsByLogicalPhase.AsReadOnly()
                },
                ACabinetPhysicalPhaseIndexesByLogicalPhase =
                    aPhysicalPhaseIndexesByLogicalPhase,
                NearestOuterPhase = nearestOuterPhase,
                PhaseAPairDistance = phaseADistanceToACabinet,
                PhaseCPairDistance = phaseCDistanceToACabinet,
#if DEBUG
                FrameHeightLaneGroups = alignedFrameHeightLaneGroups
                    .Select(frameHeightLaneGroup =>
                        (IReadOnlyList<InsulatorConnectionInfo>)
                            frameHeightLaneGroup.AsReadOnly())
                    .ToList()
                    .AsReadOnly()
#endif
            };
        }

        private static BusbarBridgeAxisSetting ResolveSideToSideAxisSetting(
            IReadOnlyList<ModeledBusbar> bCabinetBusbars,
            Point3d bCabinetBaseCenter)
        {
            if (bCabinetBusbars == null || bCabinetBusbars.Count < PhaseCount)
            {
                throw new InvalidOperationException(
                    "侧对侧业务轴解析需要 B 柜完整的三相柜顶伸出排。 ");
            }
            if (bCabinetBaseCenter == null)
            {
                throw new InvalidOperationException(
                    "侧对侧业务轴解析缺少 B 柜基座 Box 中心。 ");
            }

            var bHeightCandidateDirections = bCabinetBusbars
                .Select(GetLongestCenterlineSegmentDirection)
                .ToList();
            if (bHeightCandidateDirections.Any(direction => direction == null))
            {
                throw new InvalidOperationException(
                    "B 柜柜顶伸出排存在无法读取排长方向的相，不能解析侧对侧高度轴。 ");
            }
            var heightDirection = AverageAlignedSideToSideDirection(
                bHeightCandidateDirections);
            if (heightDirection == null)
            {
                throw new InvalidOperationException(
                    "无法从 B 柜柜顶伸出排的排长方向确定侧对侧高度轴。 ");
            }
            if (bHeightCandidateDirections.Any(direction =>
                    !BusbarGeometryUtility.AreParallel(
                        direction,
                        heightDirection)))
            {
                throw new InvalidOperationException(
                    "B 柜三相柜顶伸出排的排长方向不一致，无法确定统一的侧对侧高度轴。 ");
            }

            var bExtensionCenter = BusbarGeometryUtility.Average(
                bCabinetBusbars.Select(busbar => busbar.CenterPoint));
            if (bExtensionCenter == null)
            {
                throw new InvalidOperationException(
                    "无法从 B 柜柜顶伸出排确定高度轴正向。 ");
            }

            var extensionToBaseVector = new Vector3d(
                bExtensionCenter,
                bCabinetBaseCenter);
            if (extensionToBaseVector.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "B 柜柜顶伸出排与基座 Box 中心重合，无法确定高度轴正向。 ");
            }
            if (heightDirection.Dot(extensionToBaseVector) < 0d)
            {
                heightDirection = heightDirection.Negate();
            }

            var bPhaseCenters = GetSideToSidePhaseCenterPoints(
                bCabinetBusbars,
                "B柜");
            var cToAVector = new Vector3d(
                bPhaseCenters[2],
                bPhaseCenters[0]);
            var widthDirection = RemoveHeightComponent(
                cToAVector,
                heightDirection);
            if (widthDirection == null)
            {
                throw new InvalidOperationException(
                    "B 柜 C/A 柜顶伸出排在高度轴垂直平面内没有有效的 C→A 相序方向。 ");
            }

            var lengthDirection = heightDirection.Cross(widthDirection);
            if (lengthDirection == null || lengthDirection.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "侧对侧长度轴无法由高度轴 Cross 宽度轴确定。 ");
            }
            lengthDirection = lengthDirection.Normalized;

            var resolvedAxisSetting = BusbarBridgeAxisSetting.CreateFromVectors(
                lengthDirection,
                heightDirection,
                widthDirection);
            FaceToWallPreviewTrace.Info(
                "侧对侧固定换相-业务轴解析详情",
                "B柜伸出排中心=" + FormatPoint(bExtensionCenter) +
                "；B柜基座中心=" + FormatPoint(bCabinetBaseCenter) +
                "；C→A宽度=" + FormatPoint(bPhaseCenters[2]) +
                "→" + FormatPoint(bPhaseCenters[0]) +
                "；高=" + resolvedAxisSetting.HeightVector +
                "；宽=" + resolvedAxisSetting.WidthVector +
                "；长=" + resolvedAxisSetting.LengthVector);
            return resolvedAxisSetting;
        }

        /// <summary>
        /// 根据两柜实际位置解析 B→A 柜间方向。业务长宽高只用于说明该连线由
        /// 哪些业务轴分量组成；柜间方向允许落在长度、宽度或二者组合方向，不能
        /// 预设为固定的业务宽度轴。中间排保持在业务水平面内，因此剔除高度差。
        /// </summary>
        private static Vector3d ResolveSideToSideCabinetSpanDirection(
            BusbarBridgeAxisSetting sideToSideBusinessAxisSetting,
            Point3d bCabinetBaseCenter,
            Point3d aCabinetBaseCenter)
        {
            if (sideToSideBusinessAxisSetting == null)
            {
                throw new ArgumentNullException(
                    nameof(sideToSideBusinessAxisSetting));
            }
            if (bCabinetBaseCenter == null || aCabinetBaseCenter == null)
            {
                throw new ArgumentNullException(
                    bCabinetBaseCenter == null
                        ? nameof(bCabinetBaseCenter)
                        : nameof(aCabinetBaseCenter));
            }

            var bToACabinetVector = new Vector3d(
                bCabinetBaseCenter,
                aCabinetBaseCenter);
            if (bToACabinetVector.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "B柜/A柜基座 Box 中点重合，无法确定柜间 B→A 方向。");
            }

            var normalizedHeightDirection = sideToSideBusinessAxisSetting
                .HeightVector.Normalized;
            var signedHeightComponent = bToACabinetVector.Dot(
                normalizedHeightDirection);
            var cabinetHorizontalVector = new Vector3d(
                bToACabinetVector.X -
                    normalizedHeightDirection.X * signedHeightComponent,
                bToACabinetVector.Y -
                    normalizedHeightDirection.Y * signedHeightComponent,
                bToACabinetVector.Z -
                    normalizedHeightDirection.Z * signedHeightComponent);
            if (cabinetHorizontalVector.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "侧对侧 B/A 柜基座中心连线仅包含业务高度分量，无法形成中间排连接方向。");
            }

            var cabinetSpanDirection = cabinetHorizontalVector.Normalized;
            var signedLengthComponent = bToACabinetVector.Dot(
                sideToSideBusinessAxisSetting.LengthVector.Normalized);
            var signedWidthComponent = bToACabinetVector.Dot(
                sideToSideBusinessAxisSetting.WidthVector.Normalized);
            var activeBusinessComponents = new List<string>();
            if (Math.Abs(signedLengthComponent) >
                SideToSidePhysicalAlignmentTolerance)
            {
                activeBusinessComponents.Add("长度");
            }
            if (Math.Abs(signedWidthComponent) >
                SideToSidePhysicalAlignmentTolerance)
            {
                activeBusinessComponents.Add("宽度");
            }
            if (Math.Abs(signedHeightComponent) >
                SideToSidePhysicalAlignmentTolerance)
            {
                activeBusinessComponents.Add("高度");
            }

            FaceToWallPreviewTrace.Info(
                "侧对侧柜间轴-B到A",
                "B柜基座中心=" + FormatPoint(bCabinetBaseCenter) +
                "；A柜基座中心=" + FormatPoint(aCabinetBaseCenter) +
                "；业务分量[长/宽/高]=" +
                signedLengthComponent.ToString("0.###") + "/" +
                signedWidthComponent.ToString("0.###") + "/" +
                signedHeightComponent.ToString("0.###") +
                "，匹配=" + (activeBusinessComponents.Count == 0
                    ? "未识别"
                    : string.Join("+", activeBusinessComponents)) +
                "；柜间B→A轴=" + cabinetSpanDirection +
                "；水平柜间距=" +
                cabinetHorizontalVector.Norm.ToString("0.###") +
                "，高度差=" +
                Math.Abs(signedHeightComponent).ToString("0.###"));
            return cabinetSpanDirection;
        }

        private static List<Point3d> GetSideToSidePhaseCenterPoints(
            IReadOnlyList<ModeledBusbar> cabinetBusbars,
            string cabinetLabel)
        {
            var phaseCenters = new List<Point3d>();
            foreach (var phase in new[]
            {
                BusbarPhase_e.A,
                BusbarPhase_e.B,
                BusbarPhase_e.C
            })
            {
                var phaseCenter = BusbarGeometryUtility.Average(
                    cabinetBusbars
                        .Where(busbar => busbar?.Phase == phase)
                        .Select(busbar => busbar.CenterPoint));
                if (phaseCenter == null)
                {
                    throw new InvalidOperationException(
                        cabinetLabel + "缺少有效的" + phase +
                        "相柜顶伸出排中心点。 ");
                }

                phaseCenters.Add(phaseCenter);
            }

            return phaseCenters;
        }

        private static Vector3d GetLongestCenterlineSegmentDirection(
            ModeledBusbar sourceBusbar)
        {
            var pointList = sourceBusbar?.CenterlineSetting?.PointList;
            if (pointList == null || pointList.Count < 2)
            {
                return null;
            }

            var longestSegment = Enumerable.Range(0, pointList.Count - 1)
                .Where(pointIndex => pointList[pointIndex] != null &&
                                     pointList[pointIndex + 1] != null)
                .Select(pointIndex => new Vector3d(
                    pointList[pointIndex],
                    pointList[pointIndex + 1]))
                .Where(segment => segment != null &&
                                  segment.Norm > GeometryTolerance)
                .OrderByDescending(segment => segment.Norm)
                .FirstOrDefault();
            return longestSegment?.Normalized;
        }

        private static Vector3d AverageAlignedSideToSideDirection(
            IEnumerable<Vector3d> sourceDirections)
        {
            var usableDirections = sourceDirections
                .Where(direction => direction != null &&
                                    direction.Norm > GeometryTolerance)
                .Select(direction => direction.Normalized)
                .ToList();
            if (usableDirections.Count == 0)
            {
                return null;
            }

            var referenceDirection = usableDirections[0];
            var alignedDirections = usableDirections
                .Select(direction => BusbarGeometryUtility.Align(
                    direction,
                    referenceDirection))
                .ToList();
            var averageDirection = new Vector3d(
                alignedDirections.Average(direction => direction.X),
                alignedDirections.Average(direction => direction.Y),
                alignedDirections.Average(direction => direction.Z));
            return averageDirection.Norm <= GeometryTolerance
                ? null
                : averageDirection.Normalized;
        }

        private static Vector3d RemoveHeightComponent(
            Vector3d sourceVector,
            Vector3d heightDirection)
        {
            if (sourceVector == null || heightDirection == null)
            {
                return null;
            }

            var normalizedHeightDirection = heightDirection.Normalized;
            var heightComponent = sourceVector.Dot(normalizedHeightDirection);
            var projectedVector = new Vector3d(
                sourceVector.X - normalizedHeightDirection.X * heightComponent,
                sourceVector.Y - normalizedHeightDirection.Y * heightComponent,
                sourceVector.Z - normalizedHeightDirection.Z * heightComponent);
            return projectedVector.Norm <= GeometryTolerance
                ? null
                : projectedVector.Normalized;
        }

        private static IReadOnlyList<int> ResolveSideToSidePhasePairing(
            IReadOnlyList<List<ModeledBusbar>> bPhaseGroups,
            IReadOnlyList<List<ModeledBusbar>> aPhaseGroups)
        {
            if (bPhaseGroups == null || aPhaseGroups == null ||
                bPhaseGroups.Count != PhaseCount ||
                aPhaseGroups.Count != PhaseCount ||
                bPhaseGroups.Any(phaseGroup =>
                    phaseGroup == null || phaseGroup.Count == 0) ||
                aPhaseGroups.Any(phaseGroup =>
                    phaseGroup == null || phaseGroup.Count == 0))
            {
                throw new InvalidOperationException(
                    "侧对侧第一种必须从 A/B 柜组件名中分别识别出完整的 A、B、C 三相。 ");
            }

            // GroupExtensionBusbarsByPhase 已按组件名中间字段完成分组：
            // 例如“A柜内伸出排-C-A柜前”的 C 就是相序。空间位置只用于
            // 选择最近绝缘子，不能再把 A 柜的实际相序重排成 C-B-A。
            var physicalPhaseIndexesByLogicalPhase = new[] { 0, 1, 2 }
                .ToList()
                .AsReadOnly();
            FaceToWallPreviewTrace.Info(
                "侧对侧第一种-组件名相序配对",
                "B柜 A/B/C → A柜 A/B/C；相序直接采用组件名中间字段，" +
                "宽度方向位置不再改写相序");
            return physicalPhaseIndexesByLogicalPhase;
        }

        internal static List<Point3d> GetPhaseTerminalReferencePoints(
            IReadOnlyList<List<ModeledBusbar>> phaseGroups,
            string cabinetLabel)
        {
            if (phaseGroups == null || phaseGroups.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    cabinetLabel + "没有形成完整的 A/B/C 三相伸出排分组。");
            }

            var phaseReferencePoints = phaseGroups
                .Select(phaseGroup => BusbarGeometryUtility.Average(
                    phaseGroup.Select(extensionBusbar =>
                        extensionBusbar.TerminalSegment.P2)))
                .ToList();
            if (phaseReferencePoints.Any(phaseReferencePoint =>
                phaseReferencePoint == null))
            {
                throw new InvalidOperationException(
                    cabinetLabel + "存在没有有效柜顶搭接端点的相。");
            }

            return phaseReferencePoints;
        }

        private static List<InsulatorConnectionInfo>
            SelectNearestSideToSideBaseInsulatorsByPhase(
            AssemblyPartCatalog partCatalog,
            Component2 baseComponent,
            IReadOnlyList<InsulatorConnectionInfo> availableInsulators,
            Vector3d expectedConnectionNormal,
            IReadOnlyList<Point3d> phaseReferencePoints,
            string baseLabel)
        {
            var descendantComponentNames = new HashSet<string>(
                partCatalog.GetAllDescendants(baseComponent)
                    .Select(BusbarGeometryUtility.SafeName),
                StringComparer.OrdinalIgnoreCase);
            var strictBaseCandidates = availableInsulators
                .Where(insulator =>
                    insulator?.ConnectionPlane?.Normal != null &&
                    insulator.ConnectionCircleCenter != null &&
                    descendantComponentNames.Contains(
                        BusbarGeometryUtility.SafeName(insulator.Component)) &&
                    BusbarGeometryUtility.AreParallel(
                        insulator.ConnectionPlane.Normal,
                        expectedConnectionNormal))
                .ToList();
            if (strictBaseCandidates.Count < PhaseCount)
            {
                throw new InvalidOperationException(
                    baseLabel + "自身组件树内只识别到" +
                    strictBaseCandidates.Count +
                    "个方向正确的定位绝缘子，不能借用另一基座或基架绝缘子。");
            }

            var phaseReferenceLengthCoordinates = phaseReferencePoints
                .Select(phaseReferencePoint => BusbarGeometryUtility.Project(
                    phaseReferencePoint,
                    expectedConnectionNormal))
                .ToList();
            var selectedBaseInsulatorsByPhase =
                MatchInsulatorsToPhaseReferencePoints(
                    strictBaseCandidates,
                    phaseReferencePoints,
                    baseLabel);
            var selectedLengthAxisDeviations = Enumerable.Range(0, PhaseCount)
                .Select(phaseIndex => Math.Abs(
                    BusbarGeometryUtility.Project(
                        selectedBaseInsulatorsByPhase[phaseIndex]
                            .ConnectionCircleCenter,
                        expectedConnectionNormal) -
                    phaseReferenceLengthCoordinates[phaseIndex]))
                .ToList();
            FaceToWallPreviewTrace.Info(
                "侧对侧第一种固定换相-基座定位绝缘子",
                "宿主=" + baseLabel + "，组件=" +
                BusbarGeometryUtility.SafeName(baseComponent) +
                "，方向正确候选=" + strictBaseCandidates.Count +
                "，相对柜顶端点长度轴偏差[A/B/C]=" +
                string.Join(
                    "/",
                    selectedLengthAxisDeviations.Select(deviation =>
                        deviation.ToString("0.###"))) +
                "，分配=" + FormatInsulatorPhaseAssignment(
                    selectedBaseInsulatorsByPhase));
            return selectedBaseInsulatorsByPhase;
        }

        private static List<InsulatorConnectionInfo>
            SelectStrictSideToSideSupportInsulators(
            AssemblyPartCatalog partCatalog,
            Component2 hostComponent,
            IReadOnlyList<InsulatorConnectionInfo> availableInsulators,
            Vector3d expectedConnectionNormal,
            Vector3d supportPlaneAxis,
            Point3d referencePoint,
            string hostLabel)
        {
            var descendantComponentNames = new HashSet<string>(
                partCatalog.GetAllDescendants(hostComponent)
                    .Select(BusbarGeometryUtility.SafeName),
                StringComparer.OrdinalIgnoreCase);
            var hostInsulatorCandidates = availableInsulators
                .Where(insulator =>
                    insulator?.ConnectionPlane?.Normal != null &&
                    insulator.ConnectionCircleCenter != null &&
                    descendantComponentNames.Contains(
                        BusbarGeometryUtility.SafeName(insulator.Component)))
                .ToList();
            FaceToWallPreviewTrace.Info(
                "侧对侧第一种固定换相-基架绝缘子法向筛选",
                "宿主=" + hostLabel + "/" +
                BusbarGeometryUtility.SafeName(hostComponent) +
                "，期望法向=" + expectedConnectionNormal.Normalized +
                "，宿主内有效绝缘子=" + hostInsulatorCandidates.Count +
                "，候选=" + string.Join(
                    "；",
                    hostInsulatorCandidates.Select(insulator =>
                        BusbarGeometryUtility.SafeName(insulator.Component) +
                        "@N" + insulator.ConnectionPlane.Normal.Normalized +
                        "，平行=" + BusbarGeometryUtility.AreParallel(
                            insulator.ConnectionPlane.Normal,
                            expectedConnectionNormal))));
            var strictCandidates = hostInsulatorCandidates
                .Where(insulator => BusbarGeometryUtility.AreParallel(
                    insulator.ConnectionPlane.Normal,
                    expectedConnectionNormal))
                .OrderBy(insulator => BusbarGeometryUtility.Project(
                    insulator.ConnectionCircleCenter,
                    supportPlaneAxis))
                .ToList();
            if (strictCandidates.Count < PhaseCount)
            {
                throw new InvalidOperationException(
                    hostLabel + "自身组件树内只识别到" +
                    strictCandidates.Count +
                    "个方向正确的绝缘子，不能借用其他基座或基架绝缘子。");
            }

            var supportPlaneGroups = new List<List<InsulatorConnectionInfo>>();
            foreach (var strictCandidate in strictCandidates)
            {
                var candidateCoordinate = BusbarGeometryUtility.Project(
                    strictCandidate.ConnectionCircleCenter,
                    supportPlaneAxis);
                var matchingGroup = supportPlaneGroups.FirstOrDefault(group =>
                    Math.Abs(BusbarGeometryUtility.Project(
                        group[0].ConnectionCircleCenter,
                        supportPlaneAxis) - candidateCoordinate) <=
                    SideToSidePhysicalAlignmentTolerance);
                if (matchingGroup == null)
                {
                    matchingGroup = new List<InsulatorConnectionInfo>();
                    supportPlaneGroups.Add(matchingGroup);
                }
                matchingGroup.Add(strictCandidate);
            }

            var completeSupportPlaneGroups = supportPlaneGroups
                .Where(group => group.Count >= PhaseCount)
                .Select(group => new
                {
                    Group = group,
                    Center = BusbarGeometryUtility.Average(group.Select(
                        insulator => insulator.ConnectionCircleCenter))
                })
                .Where(group => group.Center != null)
                .OrderBy(group => group.Center.DistanceTo(referencePoint))
                .ToList();
            if (completeSupportPlaneGroups.Count == 0)
            {
                throw new InvalidOperationException(
                    hostLabel + "没有在 2 mm 同一定位平面内形成完整的三相绝缘子组。");
            }

            var selectedInsulators = completeSupportPlaneGroups[0].Group
                .OrderBy(insulator =>
                    insulator.ConnectionCircleCenter.DistanceTo(referencePoint))
                .Take(PhaseCount)
                .ToList();
            FaceToWallPreviewTrace.Info(
                "侧对侧固定换相-严格绝缘子组",
                "宿主=" + hostLabel + "，组件=" +
                BusbarGeometryUtility.SafeName(hostComponent) +
                "，候选=" + strictCandidates.Count +
                "，定位平面组=" + completeSupportPlaneGroups.Count +
                "，选中=" + string.Join(
                    ",",
                    selectedInsulators.Select(insulator =>
                        BusbarGeometryUtility.SafeName(insulator.Component))));
            return selectedInsulators;
        }

        private static List<InsulatorConnectionInfo>
            MatchInsulatorsToPhaseReferencePoints(
            IReadOnlyList<InsulatorConnectionInfo> insulators,
            IReadOnlyList<Point3d> phaseReferencePoints,
            string hostLabel)
        {
            var bestPermutation = SelectUniqueMinimumCostPermutation(
                insulators,
                (phaseIndex, insulator) =>
                    phaseReferencePoints[phaseIndex].DistanceTo(
                        insulator.ConnectionCircleCenter),
                hostLabel + "与三相柜顶端头");
            return bestPermutation
                .Select(insulatorIndex => insulators[insulatorIndex])
                .ToList();
        }

        private static List<InsulatorConnectionInfo> OrderHeightLaneInsulators(
            IReadOnlyList<InsulatorConnectionInfo> insulators,
            Vector3d heightDirection,
            double nearestPhaseHeightReference,
            string hostLabel)
        {
            var orderedHeightLanes = insulators
                .OrderBy(insulator =>
                    BusbarGeometryUtility.Project(
                        insulator.ConnectionCircleCenter,
                        heightDirection))
                .ToList();
            var heightLaneCoordinates = orderedHeightLanes
                .Select(insulator =>
                    BusbarGeometryUtility.Project(
                        insulator.ConnectionCircleCenter,
                        heightDirection))
                .ToList();
            if (heightLaneCoordinates.Count != PhaseCount ||
                heightLaneCoordinates[1] - heightLaneCoordinates[0] <=
                    SideToSidePhysicalAlignmentTolerance ||
                heightLaneCoordinates[2] - heightLaneCoordinates[1] <=
                    SideToSidePhysicalAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    hostLabel + "的三个高度绝缘子无法按高度方向以 2 mm 容差唯一排序。");
            }

            var nearestLaneIndex = heightLaneCoordinates
                .Select((heightCoordinate, laneIndex) => new
                {
                    LaneIndex = laneIndex,
                    Distance = Math.Abs(
                        heightCoordinate - nearestPhaseHeightReference)
                })
                .OrderBy(lane => lane.Distance)
                .First()
                .LaneIndex;
            if (nearestLaneIndex != 0)
            {
                FaceToWallPreviewTrace.Warning(
                    "侧对侧固定换相-高度层顺序",
                    hostLabel + "按高度从低到高排序后，优先相端头最近层为第" +
                    (nearestLaneIndex + 1) + "层；仍按‘最低/中间/最高’分别映射最近外相/B相/远外相。");
            }

            return orderedHeightLanes;
        }

        private static List<InsulatorConnectionInfo> MatchInsulatorsToHeightLanes(
            IReadOnlyList<InsulatorConnectionInfo> insulators,
            IReadOnlyList<InsulatorConnectionInfo> referenceHeightLanes,
            Vector3d heightDirection,
            string hostLabel)
        {
            var referenceHeightCoordinates = referenceHeightLanes
                .Select(insulator => BusbarGeometryUtility.Project(
                    insulator.ConnectionCircleCenter,
                    heightDirection))
                .ToList();
            var bestPermutation = SelectUniqueMinimumCostPermutation(
                insulators,
                (laneIndex, insulator) => Math.Abs(
                    referenceHeightCoordinates[laneIndex] -
                    BusbarGeometryUtility.Project(
                        insulator.ConnectionCircleCenter,
                        heightDirection)),
                hostLabel + "与 B 柜侧高度层");
            var matchedHeightLanes = bestPermutation
                .Select(insulatorIndex => insulators[insulatorIndex])
                .ToList();
            for (var laneIndex = 0;
                laneIndex < matchedHeightLanes.Count;
                laneIndex++)
            {
                var matchedHeightCoordinate = BusbarGeometryUtility.Project(
                    matchedHeightLanes[laneIndex].ConnectionCircleCenter,
                    heightDirection);
                if (Math.Abs(matchedHeightCoordinate -
                        referenceHeightCoordinates[laneIndex]) >
                    SideToSidePhysicalAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        hostLabel + "的高度层" + (laneIndex + 1) +
                        "与 B 柜侧对应基架绝缘子高度不一致，偏差=" +
                        Math.Abs(matchedHeightCoordinate -
                            referenceHeightCoordinates[laneIndex])
                            .ToString("0.###") + " mm。");
                }
            }

            return matchedHeightLanes;
        }

        private static int[] SelectUniqueMinimumCostPermutation(
            IReadOnlyList<InsulatorConnectionInfo> insulators,
            Func<int, InsulatorConnectionInfo, double> costSelector,
            string assignmentLabel)
        {
            if (insulators == null || insulators.Count < PhaseCount)
            {
                throw new InvalidOperationException(
                    assignmentLabel + "至少需要三个绝缘子候选。");
            }

            var distinctPhaseAssignments = Enumerable
                .Range(0, insulators.Count)
                .SelectMany(firstInsulatorIndex => Enumerable
                    .Range(0, insulators.Count)
                    .Where(secondInsulatorIndex =>
                        secondInsulatorIndex != firstInsulatorIndex)
                    .SelectMany(secondInsulatorIndex => Enumerable
                        .Range(0, insulators.Count)
                        .Where(thirdInsulatorIndex =>
                            thirdInsulatorIndex != firstInsulatorIndex &&
                            thirdInsulatorIndex != secondInsulatorIndex)
                        .Select(thirdInsulatorIndex => new[]
                        {
                            firstInsulatorIndex,
                            secondInsulatorIndex,
                            thirdInsulatorIndex
                        })))
                .ToList();
            var rankedAssignments = distinctPhaseAssignments
                .Select(permutation => new
                {
                    Permutation = permutation,
                    Cost = Enumerable.Range(0, PhaseCount).Sum(index =>
                        costSelector(index, insulators[permutation[index]]))
                })
                .OrderBy(assignment => assignment.Cost)
                .ToList();
            if (rankedAssignments.Count < 2 ||
                rankedAssignments[1].Cost - rankedAssignments[0].Cost <=
                    SideToSidePhysicalAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    assignmentLabel + "在 2 mm 容差内存在多个等价的一一对应结果。");
            }

            return rankedAssignments[0].Permutation;
        }

        private static List<InsulatorConnectionInfo> MapHeightLanesToPhases(
            IReadOnlyList<InsulatorConnectionInfo> heightLaneInsulators,
            int nearestOuterPhaseIndex)
        {
            var phaseInsulators = new InsulatorConnectionInfo[PhaseCount];
            phaseInsulators[nearestOuterPhaseIndex] = heightLaneInsulators[0];
            phaseInsulators[1] = heightLaneInsulators[1];
            phaseInsulators[nearestOuterPhaseIndex == 0 ? 2 : 0] =
                heightLaneInsulators[2];
            return phaseInsulators.ToList();
        }

#if DEBUG
        private static SideToSideFixedPhaseChangePlanningSnapshot
            CreateSideToSideFixedPhaseChangePlanningSnapshot(
            Component2 bCabinetHostComponent,
            Component2 aCabinetHostComponent,
            FaceToFaceStandaloneBaseAssignment standaloneBaseAssignment,
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxes,
            SideToSideFixedPhaseAssignments fixedPhaseAssignments,
            BusbarBridgeAxisSetting axisSetting)
        {
            if (fixedPhaseAssignments?.FrameHeightLaneGroups == null ||
                fixedPhaseAssignments.FrameHeightLaneGroups.Count !=
                    orderedFrameBoxes.Count)
            {
                throw new InvalidOperationException(
                    "侧对侧固定换相的基架高度层快照与物理基架数量不一致。");
            }

            var planningSnapshot =
                new SideToSideFixedPhaseChangePlanningSnapshot
                {
                    PhysicalAlignmentTolerance =
                        SideToSidePhysicalAlignmentTolerance,
                    LengthVector =
                        SideToSideFixedPhaseChangeDebugCaptureSession
                            .CreateCoordinate(axisSetting?.LengthVector),
                    HeightVector =
                        SideToSideFixedPhaseChangeDebugCaptureSession
                            .CreateCoordinate(axisSetting?.HeightVector),
                    WidthVector =
                        SideToSideFixedPhaseChangeDebugCaptureSession
                            .CreateCoordinate(axisSetting?.WidthVector),
                    BCabinetComponentName = BusbarGeometryUtility.SafeName(
                        bCabinetHostComponent),
                    ACabinetComponentName = BusbarGeometryUtility.SafeName(
                        aCabinetHostComponent),
                    BCabinetBaseComponentName = BusbarGeometryUtility.SafeName(
                        standaloneBaseAssignment.BCabinetBaseComponent),
                    ACabinetBaseComponentName = BusbarGeometryUtility.SafeName(
                        standaloneBaseAssignment.ACabinetBaseComponent),
                    BCabinetBaseCenter =
                        SideToSideFixedPhaseChangeDebugCaptureSession
                            .CreateCoordinate(
                                standaloneBaseAssignment.BCabinetBaseCenter),
                    ACabinetBaseCenter =
                        SideToSideFixedPhaseChangeDebugCaptureSession
                            .CreateCoordinate(
                                standaloneBaseAssignment.ACabinetBaseCenter),
                    PhaseAPairDistance = fixedPhaseAssignments.PhaseAPairDistance,
                    PhaseCPairDistance = fixedPhaseAssignments.PhaseCPairDistance,
                    NearestOuterPhase =
                        fixedPhaseAssignments.NearestOuterPhase.ToString(),
                    ACabinetPhysicalPhaseByLogicalPhase =
                        fixedPhaseAssignments
                            .ACabinetPhysicalPhaseIndexesByLogicalPhase
                            .Select(physicalPhaseIndex =>
                                FaceToWallModelBusbarPlanner.GetPhaseName(
                                    physicalPhaseIndex))
                            .ToList(),
                    BCabinetPhaseAssignments =
                        CreateSideToSidePhaseAssignmentSnapshots(
                            fixedPhaseAssignments.BCabinet),
                    ACabinetPhaseAssignments =
                        CreateSideToSidePhaseAssignmentSnapshots(
                            fixedPhaseAssignments.ACabinet)
                };
            for (var frameIndex = 0;
                frameIndex < orderedFrameBoxes.Count;
                frameIndex++)
            {
                var frameHeightLaneSnapshot =
                    new SideToSideFixedPhaseChangeFrameLaneSnapshot
                    {
                        PhysicalIndex =
                            orderedFrameBoxes[frameIndex].PhysicalIndex,
                        FrameComponentName = BusbarGeometryUtility.SafeName(
                            orderedFrameBoxes[frameIndex].Component)
                    };
                var frameHeightLaneInsulators =
                    fixedPhaseAssignments.FrameHeightLaneGroups[frameIndex];
                for (var heightLaneIndex = 0;
                    heightLaneIndex < frameHeightLaneInsulators.Count;
                    heightLaneIndex++)
                {
                    var heightLaneInsulator =
                        frameHeightLaneInsulators[heightLaneIndex];
                    frameHeightLaneSnapshot.HeightLaneInsulators.Add(
                        new SideToSideFixedPhaseChangeFrameLaneInsulatorSnapshot
                        {
                            HeightLaneIndex = heightLaneIndex,
                            InsulatorComponentName =
                                BusbarGeometryUtility.SafeName(
                                    heightLaneInsulator?.Component),
                            ConnectionCircleCenter =
                                SideToSideFixedPhaseChangeDebugCaptureSession
                                    .CreateCoordinate(
                                        heightLaneInsulator?
                                            .ConnectionCircleCenter),
                                        HeightCoordinate = heightLaneInsulator?
                                .ConnectionCircleCenter == null
                                    ? (double?)null
                                        : BusbarGeometryUtility.Project(
                                        heightLaneInsulator
                                            .ConnectionCircleCenter,
                                        axisSetting.HeightVector)
                        });
                }
                planningSnapshot.FrameHeightLanes.Add(
                    frameHeightLaneSnapshot);
            }

            return planningSnapshot;
        }

        private static List<SideToSideFixedPhaseChangePhaseAssignmentSnapshot>
            CreateSideToSidePhaseAssignmentSnapshots(
            SideToSideFixedPhaseSideAssignment sideAssignment)
        {
            if (sideAssignment?.BaseInsulatorsByPhase == null ||
                sideAssignment.FrameInsulatorsByPhase == null ||
                sideAssignment.BaseInsulatorsByPhase.Count != PhaseCount ||
                sideAssignment.FrameInsulatorsByPhase.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    "侧对侧固定换相的三相绝缘子分配快照不完整。");
            }

            return Enumerable.Range(0, PhaseCount)
                .Select(phaseIndex =>
                {
                    var baseInsulator =
                        sideAssignment.BaseInsulatorsByPhase[phaseIndex];
                    var frameInsulator =
                        sideAssignment.FrameInsulatorsByPhase[phaseIndex];
                    return new SideToSideFixedPhaseChangePhaseAssignmentSnapshot
                    {
                        Phase = FaceToWallModelBusbarPlanner.GetPhaseName(
                            phaseIndex),
                        BaseInsulatorComponentName =
                            BusbarGeometryUtility.SafeName(
                                baseInsulator?.Component),
                        BaseInsulatorCircleCenter =
                            SideToSideFixedPhaseChangeDebugCaptureSession
                                .CreateCoordinate(
                                    baseInsulator?.ConnectionCircleCenter),
                        FrameInsulatorComponentName =
                            BusbarGeometryUtility.SafeName(
                                frameInsulator?.Component),
                        FrameInsulatorCircleCenter =
                            SideToSideFixedPhaseChangeDebugCaptureSession
                                .CreateCoordinate(
                                    frameInsulator?.ConnectionCircleCenter)
                    };
                })
                .ToList();
        }
#endif

        private static string FormatInsulatorPhaseAssignment(
            IReadOnlyList<InsulatorConnectionInfo> phaseInsulators)
        {
            return string.Join(
                ",",
                phaseInsulators.Select((insulator, phaseIndex) =>
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "=" + BusbarGeometryUtility.SafeName(
                        insulator?.Component)));
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

        private static string FormatVector(Vector3d vector)
        {
            return vector == null
                ? "null"
                : string.Format(
                    "({0:0.###},{1:0.###},{2:0.###})",
                    vector.X,
                    vector.Y,
                    vector.Z);
        }

        private static void EnsureDistinctCabinetHostComponents(
            Component2 bCabinetHostComponent,
            Component2 aCabinetHostComponent)
        {
            var bCabinetHostName =
                BusbarGeometryUtility.SafeName(bCabinetHostComponent);
            var aCabinetHostName =
                BusbarGeometryUtility.SafeName(aCabinetHostComponent);
            if (bCabinetHostComponent == null ||
                aCabinetHostComponent == null ||
                string.Equals(
                    bCabinetHostName,
                    aCabinetHostName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "面对面顶层设计树中的 B柜 与 A柜 必须是两个不同装配体。");
            }

            FaceToWallPreviewTrace.Info(
                "面对面柜体-顶层设计树定位",
                "B柜=" + bCabinetHostName +
                "；A柜=" + aCabinetHostName);
        }

        private static FaceToFaceStandaloneBaseAssignment
            MatchStackedOrdinaryBasesToCabinets(
            AssemblyPartCatalog partCatalog,
            Component2 bCabinetHostComponent,
            Component2 aCabinetHostComponent,
            BusbarBridgeAxisSetting axisSetting)
        {
            return ResolveStandaloneBaseAssignmentByBoxDistance(
                partCatalog,
                bCabinetHostComponent,
                aCabinetHostComponent);

#pragma warning disable CS0162
            if (axisSetting == null)
            {
                throw new ArgumentNullException(nameof(axisSetting));
            }

            var recognizedBaseComponents = partCatalog
                .GetTopLevelComponentsByConfiguredRdType(SystemPartType_e.Base)
                .ToList();
            if (recognizedBaseComponents.Count != 4)
            {
                throw new InvalidOperationException(
                    "侧对侧前后开孔必须通过 RD类型 唯一识别两套普通基座及其下方的" +
                    "两套增高基座，当前顶层 RD类型=基座 的组件数量=" +
                    recognizedBaseComponents.Count + "，组件=" + string.Join(
                        ",",
                        recognizedBaseComponents.Select(
                            BusbarGeometryUtility.SafeName)) + "。");
            }

            var unresolvedAssemblyComponents = recognizedBaseComponents
                .Where(component => !IsResolvedAssemblyComponent(component))
                .ToList();
            if (unresolvedAssemblyComponents.Count > 0)
            {
                throw new InvalidOperationException(
                    "侧对侧基座2结构包含未解析为装配体的组件：" +
                    string.Join(
                        ",",
                        unresolvedAssemblyComponents.Select(
                            BusbarGeometryUtility.SafeName)) + "。");
            }

            var baseCenters = recognizedBaseComponents
                .Select((component, componentIndex) =>
                    ReadRequiredComponentBoxCenter(
                        component,
                        "第" + (componentIndex + 1) + "个 RD基座"))
                .ToList();
            var normalizedHeightDirection = axisSetting.HeightVector.Normalized;
            var heightCoordinates = baseCenters
                .Select(center => BusbarGeometryUtility.Project(
                    center,
                    normalizedHeightDirection))
                .ToList();
            var pairingCandidates = new[]
            {
                new[] { Tuple.Create(0, 1), Tuple.Create(2, 3) },
                new[] { Tuple.Create(0, 2), Tuple.Create(1, 3) },
                new[] { Tuple.Create(0, 3), Tuple.Create(1, 2) }
            }
                .Select(pairs => new
                {
                    Pairs = pairs,
                    TotalDistance = pairs.Sum(pair =>
                        baseCenters[pair.Item1].DistanceTo(
                            baseCenters[pair.Item2]))
                })
                .OrderBy(candidate => candidate.TotalDistance)
                .ToList();
            if (pairingCandidates[1].TotalDistance -
                    pairingCandidates[0].TotalDistance <=
                SideToSidePhysicalAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    "四套 RD基座 无法按 Box 中心总距离唯一分成左右两组，" +
                    "最优/次优总距离=" +
                    pairingCandidates[0].TotalDistance.ToString("0.###") +
                    "/" +
                    pairingCandidates[1].TotalDistance.ToString("0.###") +
                    " mm。");
            }
            var pairedBaseIndexes = pairingCandidates[0].Pairs;

            var ordinaryBaseComponents = new List<Component2>();
            var ordinaryBaseCenters = new List<Point3d>();
            var elevatedBaseComponents = new List<Component2>();
            var basePairDescriptions = new List<string>();
            foreach (var pairedBaseIndex in pairedBaseIndexes)
            {
                var firstIndex = pairedBaseIndex.Item1;
                var secondIndex = pairedBaseIndex.Item2;
                var ordinaryBaseIndex =
                    heightCoordinates[firstIndex] > heightCoordinates[secondIndex]
                        ? firstIndex
                        : secondIndex;
                var elevatedBaseIndex = ordinaryBaseIndex == firstIndex
                    ? secondIndex
                    : firstIndex;
                ordinaryBaseComponents.Add(
                    recognizedBaseComponents[ordinaryBaseIndex]);
                ordinaryBaseCenters.Add(baseCenters[ordinaryBaseIndex]);
                elevatedBaseComponents.Add(
                    recognizedBaseComponents[elevatedBaseIndex]);
                basePairDescriptions.Add(
                    "普通=" + BusbarGeometryUtility.SafeName(
                        recognizedBaseComponents[ordinaryBaseIndex]) +
                    "@H" + heightCoordinates[ordinaryBaseIndex].ToString("0.###") +
                    "，下方增高=" + BusbarGeometryUtility.SafeName(
                        recognizedBaseComponents[elevatedBaseIndex]) +
                    "@H" + heightCoordinates[elevatedBaseIndex].ToString("0.###"));
            }

            FaceToWallPreviewTrace.Info(
                "侧对侧第二种-RD基座上下关系",
                string.Join("；", basePairDescriptions));

            var bCabinetCenter = ReadRequiredComponentBoxCenter(
                bCabinetHostComponent,
                "B柜");
            var aCabinetCenter = ReadRequiredComponentBoxCenter(
                aCabinetHostComponent,
                "A柜");
            var firstOrdinaryBaseComponent = ordinaryBaseComponents[0];
            var secondOrdinaryBaseComponent = ordinaryBaseComponents[1];
            var firstOrdinaryBaseCenter = ordinaryBaseCenters[0];
            var secondOrdinaryBaseCenter = ordinaryBaseCenters[1];
            var firstToBCabinetDistance =
                firstOrdinaryBaseCenter.DistanceTo(bCabinetCenter);
            var firstToACabinetDistance =
                firstOrdinaryBaseCenter.DistanceTo(aCabinetCenter);
            var secondToBCabinetDistance =
                secondOrdinaryBaseCenter.DistanceTo(bCabinetCenter);
            var secondToACabinetDistance =
                secondOrdinaryBaseCenter.DistanceTo(aCabinetCenter);
            var firstBelongsToBCabinet =
                firstToBCabinetDistance + GeometryTolerance <
                firstToACabinetDistance;
            var firstBelongsToACabinet =
                firstToACabinetDistance + GeometryTolerance <
                firstToBCabinetDistance;
            var secondBelongsToBCabinet =
                secondToBCabinetDistance + GeometryTolerance <
                secondToACabinetDistance;
            var secondBelongsToACabinet =
                secondToACabinetDistance + GeometryTolerance <
                secondToBCabinetDistance;
            if ((!firstBelongsToBCabinet && !firstBelongsToACabinet) ||
                (!secondBelongsToBCabinet && !secondBelongsToACabinet) ||
                firstBelongsToBCabinet == secondBelongsToBCabinet)
            {
                throw new InvalidOperationException(
                    "两套普通基座无法按实际 Box 中心唯一归属到 B/A 柜。" +
                    "第一普通基座到B/A=" +
                    firstToBCabinetDistance.ToString("0.###") + "/" +
                    firstToACabinetDistance.ToString("0.###") +
                    "，第二普通基座到B/A=" +
                    secondToBCabinetDistance.ToString("0.###") + "/" +
                    secondToACabinetDistance.ToString("0.###") + "。");
            }

            var bCabinetInsulatorHost = firstBelongsToBCabinet
                ? firstOrdinaryBaseComponent
                : secondOrdinaryBaseComponent;
            var aCabinetInsulatorHost = firstBelongsToBCabinet
                ? secondOrdinaryBaseComponent
                : firstOrdinaryBaseComponent;
            var bCabinetInsulatorHostCenter = firstBelongsToBCabinet
                ? firstOrdinaryBaseCenter
                : secondOrdinaryBaseCenter;
            var aCabinetInsulatorHostCenter = firstBelongsToBCabinet
                ? secondOrdinaryBaseCenter
                : firstOrdinaryBaseCenter;
            var bCabinetElevatedBaseComponent = firstBelongsToBCabinet
                ? elevatedBaseComponents[0]
                : elevatedBaseComponents[1];
            var aCabinetElevatedBaseComponent = firstBelongsToBCabinet
                ? elevatedBaseComponents[1]
                : elevatedBaseComponents[0];
            FaceToWallPreviewTrace.Info(
                "侧对侧第二种-基座2绝缘子宿主归属",
                "B柜宿主=" +
                BusbarGeometryUtility.SafeName(bCabinetInsulatorHost) +
                "，中心=" + bCabinetInsulatorHostCenter +
                "；A柜宿主=" +
                BusbarGeometryUtility.SafeName(aCabinetInsulatorHost) +
                "，中心=" + aCabinetInsulatorHostCenter +
                "；结构校验=RD类型识别基座，长度×宽度中心容差2mm，" +
                "增高基座位于普通基座下方");
            return new FaceToFaceStandaloneBaseAssignment
            {
                BCabinetBaseComponent = bCabinetInsulatorHost,
                ACabinetBaseComponent = aCabinetInsulatorHost,
                BCabinetElevatedBaseComponent = bCabinetElevatedBaseComponent,
                ACabinetElevatedBaseComponent = aCabinetElevatedBaseComponent,
                BCabinetBaseCenter = bCabinetInsulatorHostCenter,
                ACabinetBaseCenter = aCabinetInsulatorHostCenter
            };
#pragma warning restore CS0162
        }

        private static FaceToFaceStandaloneBaseAssignment
            MatchStandaloneBasesToCabinets(
            AssemblyPartCatalog partCatalog,
            Component2 bCabinetHostComponent,
            Component2 aCabinetHostComponent,
            BusbarBridgeAxisSetting axisSetting)
        {
            var standaloneBaseComponents = partCatalog
                .GetTopLevelComponentsByConfiguredRdType(SystemPartType_e.Base)
                .ToList();
            if (standaloneBaseComponents.Count >= 2 &&
                standaloneBaseComponents.Count <= 4)
            {
                return ResolveStandaloneBaseAssignmentByBoxDistance(
                    partCatalog,
                    bCabinetHostComponent,
                    aCabinetHostComponent);
            }
            if (standaloneBaseComponents.Count == 4)
            {
                return MatchStackedOrdinaryBasesToCabinets(
                    partCatalog,
                    bCabinetHostComponent,
                    aCabinetHostComponent,
                    axisSetting);
            }

            var nonAssemblyBaseComponents = standaloneBaseComponents
                .Where(standaloneBaseComponent =>
                    !IsResolvedAssemblyComponent(
                        standaloneBaseComponent))
                .ToList();
            if (nonAssemblyBaseComponents.Count > 0)
            {
                throw new InvalidOperationException(
                    "总装配体顶层 RD基座 必须全部是已解析装配体，当前基座数量=" +
                    standaloneBaseComponents.Count +
                    "，组件=" + string.Join(
                        ",",
                        standaloneBaseComponents.Select(
                            BusbarGeometryUtility.SafeName)) +
                    "，非装配体基座=" + string.Join(
                        ",",
                        nonAssemblyBaseComponents.Select(
                            BusbarGeometryUtility.SafeName)) + "。");
            }

            var elevatedBaseByOrdinaryBase =
                new Dictionary<Component2, Component2>();

            if (standaloneBaseComponents.Count == 3)
            {
                const double baseHorizontalCenterTolerance = 2d;
                var threeBaseCenters = standaloneBaseComponents
                    .Select((component, componentIndex) =>
                        ReadRequiredComponentBoxCenter(
                            component,
                            "第" + (componentIndex + 1) + "个 RD基座"))
                    .ToList();
                var normalizedLengthDirection = axisSetting.LengthVector.Normalized;
                var normalizedWidthDirection = axisSetting.WidthVector.Normalized;
                var normalizedHeightDirection = axisSetting.HeightVector.Normalized;
                var alignedBasePairs = new List<Tuple<int, int>>();
                for (var firstBaseIndex = 0;
                    firstBaseIndex < standaloneBaseComponents.Count - 1;
                    firstBaseIndex++)
                {
                    for (var secondBaseIndex = firstBaseIndex + 1;
                        secondBaseIndex < standaloneBaseComponents.Count;
                        secondBaseIndex++)
                    {
                        var firstCenter = threeBaseCenters[firstBaseIndex];
                        var secondCenter = threeBaseCenters[secondBaseIndex];
                        var centerDifference = new Vector3d(firstCenter, secondCenter);
                        var lengthDifference = centerDifference.Dot(
                            normalizedLengthDirection);
                        var widthDifference = centerDifference.Dot(
                            normalizedWidthDirection);
                        var heightDifference = centerDifference.Dot(
                            normalizedHeightDirection);
                        if (Math.Sqrt(
                                lengthDifference * lengthDifference +
                                widthDifference * widthDifference) <=
                                baseHorizontalCenterTolerance &&
                            Math.Abs(heightDifference) > GeometryTolerance)
                        {
                            alignedBasePairs.Add(Tuple.Create(
                                firstBaseIndex,
                                secondBaseIndex));
                        }
                    }
                }

                if (alignedBasePairs.Count != 1)
                {
                    throw new InvalidOperationException(
                        "三个 RD基座 中必须且只能有一组在长度×宽度平面中心" +
                        "容差 2 mm 内上下叠装，当前匹配组数=" +
                        alignedBasePairs.Count + "。");
                }

                var alignedBasePair = alignedBasePairs[0];
                var firstAlignedHeight = BusbarGeometryUtility.Project(
                    threeBaseCenters[alignedBasePair.Item1],
                    normalizedHeightDirection);
                var secondAlignedHeight = BusbarGeometryUtility.Project(
                    threeBaseCenters[alignedBasePair.Item2],
                    normalizedHeightDirection);
                var ordinaryBaseIndex = firstAlignedHeight > secondAlignedHeight
                    ? alignedBasePair.Item1
                    : alignedBasePair.Item2;
                var elevatedBaseIndex = ordinaryBaseIndex == alignedBasePair.Item1
                    ? alignedBasePair.Item2
                    : alignedBasePair.Item1;
                var unstackedOrdinaryBaseIndex = Enumerable.Range(
                        0,
                        standaloneBaseComponents.Count)
                    .Single(componentIndex =>
                        componentIndex != alignedBasePair.Item1 &&
                        componentIndex != alignedBasePair.Item2);
                FaceToWallPreviewTrace.Info(
                    "面对面柜体-RD基座上下关系",
                    "普通=" + BusbarGeometryUtility.SafeName(
                        standaloneBaseComponents[ordinaryBaseIndex]) +
                    "；下方增高=" + BusbarGeometryUtility.SafeName(
                        standaloneBaseComponents[elevatedBaseIndex]) +
                    "；另一侧普通=" + BusbarGeometryUtility.SafeName(
                        standaloneBaseComponents[unstackedOrdinaryBaseIndex]));
                var selectedOrdinaryBaseComponent =
                    standaloneBaseComponents[ordinaryBaseIndex];
                elevatedBaseByOrdinaryBase.Add(
                    selectedOrdinaryBaseComponent,
                    standaloneBaseComponents[elevatedBaseIndex]);
                standaloneBaseComponents = new List<Component2>
                {
                    selectedOrdinaryBaseComponent,
                    standaloneBaseComponents[unstackedOrdinaryBaseIndex]
                };
            }

            if (standaloneBaseComponents.Count != 2)
            {
                throw new InvalidOperationException(
                    "总装配体顶层必须通过 RD类型 识别两套普通基座，" +
                    "每套普通基座下方最多允许一套增高基座；当前 RD基座 数量=" +
                    standaloneBaseComponents.Count + "，组件=" + string.Join(
                        ",",
                        standaloneBaseComponents.Select(
                            BusbarGeometryUtility.SafeName)) + "。");
            }

            var bCabinetBoxCenter = ReadRequiredComponentBoxCenter(
                bCabinetHostComponent,
                "B柜");
            var aCabinetBoxCenter = ReadRequiredComponentBoxCenter(
                aCabinetHostComponent,
                "A柜");
            var firstStandaloneBaseComponent = standaloneBaseComponents[0];
            var secondStandaloneBaseComponent = standaloneBaseComponents[1];
            var firstStandaloneBaseCenter = ReadRequiredComponentBoxCenter(
                firstStandaloneBaseComponent,
                "第一独立基座");
            var secondStandaloneBaseCenter = ReadRequiredComponentBoxCenter(
                secondStandaloneBaseComponent,
                "第二独立基座");

            var firstBaseToBCabinetDistance =
                firstStandaloneBaseCenter.DistanceTo(bCabinetBoxCenter);
            var firstBaseToACabinetDistance =
                firstStandaloneBaseCenter.DistanceTo(aCabinetBoxCenter);
            var secondBaseToBCabinetDistance =
                secondStandaloneBaseCenter.DistanceTo(bCabinetBoxCenter);
            var secondBaseToACabinetDistance =
                secondStandaloneBaseCenter.DistanceTo(aCabinetBoxCenter);
            var firstBaseCabinetDistanceDifference =
                firstBaseToBCabinetDistance - firstBaseToACabinetDistance;
            var secondBaseCabinetDistanceDifference =
                secondBaseToBCabinetDistance - secondBaseToACabinetDistance;
            var directAssignmentTotalDistance =
                firstBaseToBCabinetDistance +
                secondBaseToACabinetDistance;
            var swappedAssignmentTotalDistance =
                secondBaseToBCabinetDistance +
                firstBaseToACabinetDistance;
            if (Math.Abs(firstBaseCabinetDistanceDifference) <=
                    GeometryTolerance ||
                Math.Abs(secondBaseCabinetDistanceDifference) <=
                    GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "独立基座到 A柜/B柜 的 Box 中心距离相同，无法唯一判断归属。" +
                    "第一基座=" +
                    BusbarGeometryUtility.SafeName(
                        firstStandaloneBaseComponent) +
                    "，到B柜=" + firstBaseToBCabinetDistance +
                    "，到A柜=" + firstBaseToACabinetDistance +
                    "；第二基座=" +
                    BusbarGeometryUtility.SafeName(
                        secondStandaloneBaseComponent) +
                    "，到B柜=" + secondBaseToBCabinetDistance +
                    "，到A柜=" + secondBaseToACabinetDistance + "。");
            }

            var firstBaseBelongsToBCabinet =
                firstBaseCabinetDistanceDifference < 0d;
            var secondBaseBelongsToBCabinet =
                secondBaseCabinetDistanceDifference < 0d;
            if (firstBaseBelongsToBCabinet == secondBaseBelongsToBCabinet)
            {
                throw new InvalidOperationException(
                    "两个独立基座按 Box 中心距离都更靠近同一个柜体，" +
                    "无法完成 A柜/B柜 一对一归属。" +
                    "第一基座=" +
                    BusbarGeometryUtility.SafeName(
                        firstStandaloneBaseComponent) +
                    "，到B柜=" + firstBaseToBCabinetDistance +
                    "，到A柜=" + firstBaseToACabinetDistance +
                    "；第二基座=" +
                    BusbarGeometryUtility.SafeName(
                        secondStandaloneBaseComponent) +
                    "，到B柜=" + secondBaseToBCabinetDistance +
                    "，到A柜=" + secondBaseToACabinetDistance + "。");
            }

            var bCabinetBaseComponent = firstBaseBelongsToBCabinet
                ? firstStandaloneBaseComponent
                : secondStandaloneBaseComponent;
            var aCabinetBaseComponent = firstBaseBelongsToBCabinet
                ? secondStandaloneBaseComponent
                : firstStandaloneBaseComponent;
            var bCabinetBaseCenter = firstBaseBelongsToBCabinet
                ? firstStandaloneBaseCenter
                : secondStandaloneBaseCenter;
            var aCabinetBaseCenter = firstBaseBelongsToBCabinet
                ? secondStandaloneBaseCenter
                : firstStandaloneBaseCenter;
            elevatedBaseByOrdinaryBase.TryGetValue(
                bCabinetBaseComponent,
                out var bCabinetElevatedBaseComponent);
            elevatedBaseByOrdinaryBase.TryGetValue(
                aCabinetBaseComponent,
                out var aCabinetElevatedBaseComponent);
            FaceToWallPreviewTrace.Info(
                "面对面柜体-独立基座距离归属",
                "B柜=" +
                BusbarGeometryUtility.SafeName(bCabinetHostComponent) +
                "，中心=" + bCabinetBoxCenter +
                "，匹配基座=" +
                BusbarGeometryUtility.SafeName(bCabinetBaseComponent) +
                "，基座中心=" + bCabinetBaseCenter +
                "；A柜=" +
                BusbarGeometryUtility.SafeName(aCabinetHostComponent) +
                "，中心=" + aCabinetBoxCenter +
                "，匹配基座=" +
                BusbarGeometryUtility.SafeName(aCabinetBaseComponent) +
                "，基座中心=" + aCabinetBaseCenter +
                "；第一基座到B/A距离=" +
                firstBaseToBCabinetDistance + "/" +
                firstBaseToACabinetDistance +
                "；第二基座到B/A距离=" +
                secondBaseToBCabinetDistance + "/" +
                secondBaseToACabinetDistance +
                "；第一基座配B/第二基座配A总距离=" +
                directAssignmentTotalDistance +
                "；第二基座配B/第一基座配A总距离=" +
                swappedAssignmentTotalDistance);
            return new FaceToFaceStandaloneBaseAssignment
            {
                BCabinetBaseComponent = bCabinetBaseComponent,
                ACabinetBaseComponent = aCabinetBaseComponent,
                BCabinetElevatedBaseComponent = bCabinetElevatedBaseComponent,
                ACabinetElevatedBaseComponent = aCabinetElevatedBaseComponent,
                BCabinetBaseCenter = bCabinetBaseCenter,
                ACabinetBaseCenter = aCabinetBaseCenter
            };
        }

        private static FaceToFaceStandaloneBaseAssignment
            ResolveStandaloneBaseAssignmentByBoxDistance(
            AssemblyPartCatalog partCatalog,
            Component2 bCabinetHostComponent,
            Component2 aCabinetHostComponent)
        {
            var topLevelBaseComponents = partCatalog
                .GetTopLevelComponentsByConfiguredRdType(SystemPartType_e.Base)
                .ToList();
            if (topLevelBaseComponents.Count < 2 ||
                topLevelBaseComponents.Count > 4)
            {
                throw new InvalidOperationException(
                    "总装配体顶层 RD类型=基座 的数量必须为 2 至 4 个，" +
                    "当前数量=" + topLevelBaseComponents.Count + "，组件=" +
                    string.Join(
                        "、",
                        topLevelBaseComponents.Select(
                            BusbarGeometryUtility.SafeName)) + "。 ");
            }

            var unresolvedBaseComponents = topLevelBaseComponents
                .Where(baseComponent => !IsResolvedAssemblyComponent(
                    baseComponent))
                .ToList();
            if (unresolvedBaseComponents.Count > 0)
            {
                throw new InvalidOperationException(
                    "总装配体顶层 RD类型=基座 的组件必须是已解析装配体，" +
                    "当前未解析组件=" + string.Join(
                        "、",
                        unresolvedBaseComponents.Select(
                            BusbarGeometryUtility.SafeName)) + "。 ");
            }

            var bCabinetBoxCenter = ReadRequiredComponentBoxCenter(
                bCabinetHostComponent,
                "B柜");
            var aCabinetBoxCenter = ReadRequiredComponentBoxCenter(
                aCabinetHostComponent,
                "A柜");
            var baseDistanceCandidates = topLevelBaseComponents
                .Select(baseComponent =>
                {
                    var baseBoxCenter = ReadRequiredComponentBoxCenter(
                        baseComponent,
                        "基座“" + BusbarGeometryUtility.SafeName(
                            baseComponent) + "”");
                    return new
                    {
                        BaseComponent = baseComponent,
                        BaseCenter = baseBoxCenter,
                        DistanceToBCabinet = baseBoxCenter.DistanceTo(
                            bCabinetBoxCenter),
                        DistanceToACabinet = baseBoxCenter.DistanceTo(
                            aCabinetBoxCenter)
                    };
                })
                .ToList();

            foreach (var baseDistanceCandidate in baseDistanceCandidates)
            {
                if (Math.Abs(
                        baseDistanceCandidate.DistanceToBCabinet -
                        baseDistanceCandidate.DistanceToACabinet) <=
                    GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "基座无法按总装配体 Box 中心唯一归属 A柜/B柜：" +
                        "基座=" + BusbarGeometryUtility.SafeName(
                            baseDistanceCandidate.BaseComponent) +
                        "，到B柜=" + baseDistanceCandidate.DistanceToBCabinet
                            .ToString("0.###") +
                        "，到A柜=" + baseDistanceCandidate.DistanceToACabinet
                            .ToString("0.###") + "。 ");
                }
            }

            var bCabinetBaseCandidates = baseDistanceCandidates
                .Where(baseDistanceCandidate =>
                    baseDistanceCandidate.DistanceToBCabinet <
                    baseDistanceCandidate.DistanceToACabinet)
                .OrderByDescending(baseDistanceCandidate =>
                    baseDistanceCandidate.DistanceToBCabinet)
                .ToList();
            var aCabinetBaseCandidates = baseDistanceCandidates
                .Where(baseDistanceCandidate =>
                    baseDistanceCandidate.DistanceToACabinet <
                    baseDistanceCandidate.DistanceToBCabinet)
                .OrderByDescending(baseDistanceCandidate =>
                    baseDistanceCandidate.DistanceToACabinet)
                .ToList();
            if (bCabinetBaseCandidates.Count == 0 ||
                aCabinetBaseCandidates.Count == 0 ||
                bCabinetBaseCandidates.Count > 2 ||
                aCabinetBaseCandidates.Count > 2)
            {
                throw new InvalidOperationException(
                    "基座按 Box 中心距离归属后，A柜/B柜必须各有 1 至 2 个基座；" +
                    "当前 B柜=" + bCabinetBaseCandidates.Count +
                    "，A柜=" + aCabinetBaseCandidates.Count +
                    "；基座总数=" + baseDistanceCandidates.Count + "。 ");
            }

            var bCabinetOrdinaryBase = bCabinetBaseCandidates[0];
            var aCabinetOrdinaryBase = aCabinetBaseCandidates[0];
            var bCabinetElevatedBase = bCabinetBaseCandidates.Count == 2
                ? bCabinetBaseCandidates[1]
                : null;
            var aCabinetElevatedBase = aCabinetBaseCandidates.Count == 2
                ? aCabinetBaseCandidates[1]
                : null;

            FaceToWallPreviewTrace.Info(
                "A/B柜基座-统一Box中心距离归属",
                "B柜中心=" + FormatPoint(bCabinetBoxCenter) +
                "，主体基座=" + BusbarGeometryUtility.SafeName(
                    bCabinetOrdinaryBase.BaseComponent) +
                "，主体基座距B柜=" + bCabinetOrdinaryBase.DistanceToBCabinet
                    .ToString("0.###") +
                "，增高基座=" + BusbarGeometryUtility.SafeName(
                    bCabinetElevatedBase?.BaseComponent) +
                "，A柜中心=" + FormatPoint(aCabinetBoxCenter) +
                "，主体基座=" + BusbarGeometryUtility.SafeName(
                    aCabinetOrdinaryBase.BaseComponent) +
                "，主体基座距A柜=" + aCabinetOrdinaryBase.DistanceToACabinet
                    .ToString("0.###") +
                "，增高基座=" + BusbarGeometryUtility.SafeName(
                    aCabinetElevatedBase?.BaseComponent) +
                "；规则=先按基座与对应柜体Box中心最近归属，再在同一柜体内按距离远者为主体、近者为增高");

            return new FaceToFaceStandaloneBaseAssignment
            {
                BCabinetBaseComponent = bCabinetOrdinaryBase.BaseComponent,
                ACabinetBaseComponent = aCabinetOrdinaryBase.BaseComponent,
                BCabinetElevatedBaseComponent = bCabinetElevatedBase?.BaseComponent,
                ACabinetElevatedBaseComponent = aCabinetElevatedBase?.BaseComponent,
                BCabinetBaseCenter = bCabinetOrdinaryBase.BaseCenter,
                ACabinetBaseCenter = aCabinetOrdinaryBase.BaseCenter
            };
        }

        private static bool IsResolvedAssemblyComponent(
            Component2 component)
        {
            try
            {
                return component?.IGetModelDoc() is AssemblyDoc;
            }
            catch (Exception assemblyTypeException)
            {
                FaceToWallPreviewTrace.Error(
                    "面对面柜体-检查独立基座文档类型",
                    assemblyTypeException,
                    "组件=" + BusbarGeometryUtility.SafeName(component));
                return false;
            }
        }

        private static Point3d ReadRequiredComponentBoxCenter(
            Component2 component,
            string componentLabel)
        {
            if (!BusbarGeometryUtility.TryGetComponentBoundingBox(
                    component,
                    out var componentBoxMinimum,
                    out var componentBoxMaximum))
            {
                throw new InvalidOperationException(
                    "无法读取" + componentLabel + "的实际 Box，不能判断基座归属。");
            }

            return new Point3d(
                (componentBoxMinimum.X + componentBoxMaximum.X) / 2d,
                (componentBoxMinimum.Y + componentBoxMaximum.Y) / 2d,
                (componentBoxMinimum.Z + componentBoxMaximum.Z) / 2d);
        }

        /// <summary>
        /// The side-to-side second-type planner passes its resolved axis to
        /// the generic planner with <c>axisAlreadyOriented=true</c>.  In that
        /// mode the generic planner intentionally skips its normal B→A
        /// orientation step, so correct the sign here before either planner
        /// consumes the axis.  Only the length vector is reversed; height and
        /// width retain the physical directions resolved from the B cabinet.
        /// </summary>
        private static BusbarBridgeAxisSetting
            OrientSideToSideAxisFromBCabinetToACabinet(
            BusbarBridgeAxisSetting sourceAxisSetting,
            Point3d bCabinetBaseCenter,
            Point3d aCabinetBaseCenter)
        {
            if (sourceAxisSetting == null)
            {
                throw new ArgumentNullException(nameof(sourceAxisSetting));
            }
            if (bCabinetBaseCenter == null)
            {
                throw new ArgumentNullException(nameof(bCabinetBaseCenter));
            }
            if (aCabinetBaseCenter == null)
            {
                throw new ArgumentNullException(nameof(aCabinetBaseCenter));
            }

            var bToACabinetVector = new Vector3d(
                bCabinetBaseCenter,
                aCabinetBaseCenter);
            var signedLengthComponent = bToACabinetVector.Dot(
                sourceAxisSetting.LengthVector);
            if (Math.Abs(signedLengthComponent) <= GeometryTolerance)
            {
                // A cabinet span can contain a width component.  When its
                // projection on the resolved length axis is effectively zero
                // there is no reliable sign to infer; retain the resolved
                // axis and let the subsequent route validation report any
                // genuinely unusable geometry.
                FaceToWallPreviewTrace.Warning(
                    "side-to-side second type axis orientation",
                    "B→A cabinet vector has no usable length-axis projection; " +
                    "keep the resolved length direction.");
                return sourceAxisSetting;
            }

            var orientedAxisSetting = signedLengthComponent > 0d
                ? sourceAxisSetting
                : CreateLengthReversedAxisSetting(sourceAxisSetting);
            FaceToWallPreviewTrace.Info(
                "side-to-side second type axis orientation",
                "B→A length projection=" +
                signedLengthComponent.ToString("0.###") +
                "; resolved length=" + orientedAxisSetting.LengthVector);
            return orientedAxisSetting;
        }

        private static BusbarBridgeAxisSetting
            OrientLengthAxisFromBCabinetToACabinet(
            BusbarBridgeAxisSetting sourceAxisSetting,
            Point3d bCabinetBaseCenter,
            Point3d aCabinetBaseCenter)
        {
            var sourceBCabinetLengthCoordinate = BusbarGeometryUtility.Project(
                bCabinetBaseCenter,
                sourceAxisSetting.LengthVector);
            var sourceACabinetLengthCoordinate = BusbarGeometryUtility.Project(
                aCabinetBaseCenter,
                sourceAxisSetting.LengthVector);
            var sourceSignedCabinetDistance =
                sourceACabinetLengthCoordinate -
                sourceBCabinetLengthCoordinate;
            if (Math.Abs(sourceSignedCabinetDistance) <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "B柜/A柜 基座 Box 中点在长度轴上重合，无法确定面对面路径方向。");
            }

            var routeAxisSetting = sourceSignedCabinetDistance > 0d
                ? sourceAxisSetting
                : CreateLengthReversedAxisSetting(sourceAxisSetting);
            FaceToWallPreviewTrace.Info(
                "面对面柜体-B到A长度方向",
                "B柜基座中心=" + bCabinetBaseCenter +
                "；A柜基座中心=" + aCabinetBaseCenter +
                "；原长度方向=" + sourceAxisSetting.LengthVector +
                "；采用长度方向=" + routeAxisSetting.LengthVector);
            return routeAxisSetting;
        }

        private static FaceToFaceCabinetExtensionSides SplitCabinetExtensionBusbars(
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            Vector3d lengthDirection)
        {
            var usableBusbars = extensionBusbars
                .Where(busbar => busbar?.CenterlineSetting != null &&
                                 busbar.TerminalSegment != null)
                .Select(busbar => new
                {
                    Busbar = busbar,
                    LengthCoordinate = BusbarGeometryUtility.Project(
                        busbar.TerminalSegment.P2,
                        lengthDirection)
                })
                .OrderBy(busbarItem => busbarItem.LengthCoordinate)
                .ToList();
            if (usableBusbars.Count < PhaseCount * 2)
            {
                throw new InvalidOperationException(
                    "面对面柜顶伸出排不足 B/A 两柜各三相所需的 6 根。");
            }

            var splitCandidates = Enumerable.Range(0, usableBusbars.Count - 1)
                .Select(splitIndex => new
                {
                    SplitIndex = splitIndex,
                    Gap = usableBusbars[splitIndex + 1].LengthCoordinate -
                          usableBusbars[splitIndex].LengthCoordinate
                })
                .OrderByDescending(splitCandidate => splitCandidate.Gap)
                .ToList();
            var selectedSplit = splitCandidates[0];
            if (selectedSplit.Gap <= ModelAlignmentTolerance ||
                selectedSplit.SplitIndex + 1 < PhaseCount ||
                usableBusbars.Count - selectedSplit.SplitIndex - 1 < PhaseCount)
            {
                throw new InvalidOperationException(
                    "无法沿长度轴把柜顶伸出排可靠分为 B 柜和 A 柜两组。");
            }

            var bCabinetBusbars = usableBusbars
                .Take(selectedSplit.SplitIndex + 1)
                .Select(busbarItem => busbarItem.Busbar)
                .ToList();
            var aCabinetBusbars = usableBusbars
                .Skip(selectedSplit.SplitIndex + 1)
                .Select(busbarItem => busbarItem.Busbar)
                .ToList();
            var separationLengthCoordinate =
                (usableBusbars[selectedSplit.SplitIndex].LengthCoordinate +
                 usableBusbars[selectedSplit.SplitIndex + 1].LengthCoordinate) /
                2d;
            return new FaceToFaceCabinetExtensionSides
            {
                BCabinetBusbars = bCabinetBusbars,
                ACabinetBusbars = aCabinetBusbars,
                SeparationLengthCoordinate = separationLengthCoordinate
            };
        }

        private static FaceToFaceCabinetExtensionSides
            SplitLargeOffsetCabinetExtensionBusbars(
            AssemblyPartCatalog partCatalog,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            Component2 bCabinetHostComponent,
            Component2 aCabinetHostComponent)
        {
            var usableBusbars = extensionBusbars
                .Where(busbar => busbar?.Component != null &&
                                 busbar.CenterlineSetting != null &&
                                 busbar.TerminalSegment != null)
                .ToList();
            var bCabinetBusbars = usableBusbars
                .Where(busbar => partCatalog.IsComponentInSubtree(
                    busbar.Component,
                    bCabinetHostComponent))
                .ToList();
            var aCabinetBusbars = usableBusbars
                .Where(busbar => partCatalog.IsComponentInSubtree(
                    busbar.Component,
                    aCabinetHostComponent))
                .ToList();
            var unassignedBusbars = usableBusbars
                .Where(busbar =>
                    !bCabinetBusbars.Contains(busbar) &&
                    !aCabinetBusbars.Contains(busbar))
                .ToList();
            var multiplyAssignedBusbars = bCabinetBusbars
                .Where(aCabinetBusbars.Contains)
                .ToList();
            if (unassignedBusbars.Count > 0 ||
                multiplyAssignedBusbars.Count > 0)
            {
                throw new InvalidOperationException(
                    "面对面柜顶伸出排必须明确属于顶层 B柜 或 A柜 子树；未归属=" +
                    string.Join(
                        ",",
                        unassignedBusbars.Select(busbar =>
                            BusbarGeometryUtility.SafeName(busbar.Component))) +
                    "；重复归属=" + string.Join(
                        ",",
                        multiplyAssignedBusbars.Select(busbar =>
                            BusbarGeometryUtility.SafeName(busbar.Component))) +
                    "。");
            }

            if (bCabinetBusbars.Count < PhaseCount ||
                aCabinetBusbars.Count < PhaseCount)
            {
                throw new InvalidOperationException(
                    "按顶层设计树归属后柜顶伸出排不足：B柜=" +
                    bCabinetBusbars.Count + "根，A柜=" +
                    aCabinetBusbars.Count + "根；两柜都至少需要完整三相。");
            }

            FaceToWallPreviewTrace.Info(
                "面对面柜顶伸出排-按顶层柜体归属",
                "B柜=" + string.Join(
                    ",",
                    bCabinetBusbars.Select(busbar =>
                        BusbarGeometryUtility.SafeName(busbar.Component))) +
                "；A柜=" + string.Join(
                    ",",
                    aCabinetBusbars.Select(busbar =>
                        BusbarGeometryUtility.SafeName(busbar.Component))));
            return new FaceToFaceCabinetExtensionSides
            {
                BCabinetBusbars = bCabinetBusbars,
                ACabinetBusbars = aCabinetBusbars
            };
        }

        private static double ReadConnectedBaseWidth(Component2 component)
        {
            var model = component?.GetModelDoc2() as IModelDoc2;
            if (model == null || string.IsNullOrWhiteSpace(component.ReferencedConfiguration)
                || !string.Equals(model.ConfigurationManager?.ActiveConfiguration?.Name,
                    component.ReferencedConfiguration, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("无法读取连接基座引用配置的基座宽，不能判定偏移场景：" + component?.Name2);
            var equations = model.GetEquationMgr() as IEquationMgr;
            if (equations == null) throw new InvalidOperationException("连接基座缺少驱动方程：" + component.Name2);
            for (var index = 0; index < equations.GetCount(); index++)
            {
                var text = equations.Equation[index]; var equals = text.IndexOf('=');
                if (equals < 0 || text.Substring(0, equals).Trim() != "\"基座宽\"") continue;
                if (equations.Suppression[index]) break;
                var width = equations.Value[index];
                if (equations.Status == index && width > 0d && !double.IsInfinity(width)) return width;
                break;
            }
            throw new InvalidOperationException("连接基座的基座宽驱动变量无效：" + component.Name2);
        }

        private static double CalculateCabinetSeparationLengthCoordinate(
            FaceToFaceCabinetExtensionSides cabinetExtensionSides,
            Vector3d separationDirection)
        {
            var bCabinetLengthCoordinate = cabinetExtensionSides
                .BCabinetBusbars
                .Average(busbar => BusbarGeometryUtility.Project(
                    busbar.TerminalSegment.P2,
                    separationDirection));
            var aCabinetLengthCoordinate = cabinetExtensionSides
                .ACabinetBusbars
                .Average(busbar => BusbarGeometryUtility.Project(
                    busbar.TerminalSegment.P2,
                    separationDirection));
            if (aCabinetLengthCoordinate <=
                bCabinetLengthCoordinate + ModelAlignmentTolerance)
            {
                throw new InvalidOperationException(
                    "按顶层名称识别出的 B柜/A柜柜顶伸出排在柜间分离方向上的位置顺序异常。");
            }

            return (bCabinetLengthCoordinate + aCabinetLengthCoordinate) / 2d;
        }

        /// <summary>
        /// 按两柜柜顶伸出排之间的长度轴分界面约束绝缘子候选。
        /// 后续宿主后代不足时即使进入距离回退，也只能在同一柜侧候选中选择。
        /// </summary>
        internal static IReadOnlyList<InsulatorConnectionInfo>
            SelectConnectionInsulatorsForCabinetSide(
            IReadOnlyList<InsulatorConnectionInfo> availableConnectionInsulators,
            Vector3d lengthDirection,
            double separationLengthCoordinate,
            bool selectLowerLengthSide,
            string cabinetSideLabel,
            int minimumCandidateCount)
        {
            if (availableConnectionInsulators == null)
            {
                throw new ArgumentNullException(
                    nameof(availableConnectionInsulators));
            }

            if (lengthDirection == null ||
                lengthDirection.Norm <= GeometryTolerance)
            {
                throw new ArgumentException(
                    "面对面柜侧绝缘子筛选缺少有效长度方向。",
                    nameof(lengthDirection));
            }

            if (double.IsNaN(separationLengthCoordinate) ||
                double.IsInfinity(separationLengthCoordinate))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(separationLengthCoordinate));
            }

            if (minimumCandidateCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minimumCandidateCount));
            }

            var normalizedCabinetSideLabel =
                string.IsNullOrWhiteSpace(cabinetSideLabel)
                    ? (selectLowerLengthSide ? "长度轴较小侧柜体" : "长度轴较大侧柜体")
                    : cabinetSideLabel.Trim();
            var selectedCabinetSideInsulators =
                availableConnectionInsulators
                    .Where(connectionInsulator =>
                        connectionInsulator?.ConnectionCircleCenter != null)
                    .Where(connectionInsulator =>
                    {
                        var connectionInsulatorLengthCoordinate =
                            BusbarGeometryUtility.Project(
                                connectionInsulator.ConnectionCircleCenter,
                                lengthDirection);
                        return selectLowerLengthSide
                            ? connectionInsulatorLengthCoordinate <
                              separationLengthCoordinate - GeometryTolerance
                            : connectionInsulatorLengthCoordinate >
                              separationLengthCoordinate + GeometryTolerance;
                    })
                    .ToList();
            if (selectedCabinetSideInsulators.Count < minimumCandidateCount)
            {
                throw new InvalidOperationException(
                    normalizedCabinetSideLabel +
                    "在柜顶两端长度分界内只识别到 " +
                    selectedCabinetSideInsulators.Count +
                    " 个绝缘子候选，少于当前端部规划所需的 " +
                    minimumCandidateCount +
                    " 个；不允许从另一柜回退补足。");
            }

            FaceToWallPreviewTrace.Info(
                "面对面绝缘子-按柜侧过滤",
                "柜侧=" + normalizedCabinetSideLabel +
                "，长度分界=" + separationLengthCoordinate.ToString("0.###") +
                "，候选数=" + selectedCabinetSideInsulators.Count +
                "，候选=" + string.Join(
                    ",",
                    selectedCabinetSideInsulators.Select(
                        connectionInsulator =>
                            BusbarGeometryUtility.SafeName(
                                connectionInsulator.Component))));
            return selectedCabinetSideInsulators.AsReadOnly();
        }

        private static void ValidateSideRouteCompatibility(
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            BusbarBridgeAxisSetting axisSetting,
            bool allowsNonAlignedPhaseCoordinates,
            bool usesReversedPhaseOrder)
        {
            var commonBusbarWidth =
                bCabinetContext.PhaseRouteContexts[0].BusbarWidth;
            var commonBusbarThickness =
                bCabinetContext.PhaseRouteContexts[0].BusbarThickness;
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var bRouteContext =
                    bCabinetContext.PhaseRouteContexts[phaseIndex];
                var aRouteContext =
                    aCabinetContext.PhaseRouteContexts[phaseIndex];
                if (Math.Abs(bRouteContext.BusbarWidth -
                             aRouteContext.BusbarWidth) >
                        ModelAlignmentTolerance ||
                    Math.Abs(bRouteContext.BusbarThickness -
                             aRouteContext.BusbarThickness) >
                        ModelAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相 A/B 柜柜顶伸出排规格不一致。");
                }

                if (Math.Abs(bRouteContext.BusbarWidth -
                             commonBusbarWidth) >
                        ModelAlignmentTolerance ||
                    Math.Abs(aRouteContext.BusbarWidth -
                             commonBusbarWidth) >
                        ModelAlignmentTolerance ||
                    Math.Abs(bRouteContext.BusbarThickness -
                             commonBusbarThickness) >
                        ModelAlignmentTolerance ||
                    Math.Abs(aRouteContext.BusbarThickness -
                             commonBusbarThickness) >
                        ModelAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        "面对面 A/B/C 三相必须使用相同排宽和排厚，才能形成一致的搭接区域及叠排层。");
                }

                var bRouteHeight = BusbarGeometryUtility.Project(
                    bRouteContext.OrdinaryRouteReferencePoint,
                    axisSetting.HeightVector);
                var aRouteHeight = BusbarGeometryUtility.Project(
                    aRouteContext.OrdinaryRouteReferencePoint,
                    axisSetting.HeightVector);
                if (Math.Abs(bRouteHeight - aRouteHeight) >
                    ModelAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相 A/B 柜首基架绝缘子确定的普通段高度不一致。");
                }

                if (!BusbarGeometryUtility.AreParallel(
                        bRouteContext.RouteThicknessDirection,
                        aRouteContext.RouteThicknessDirection))
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相 A/B 柜普通段排厚方向不一致。");
                }
            }

            var bWidthCoordinates = GetPhaseWidthCoordinates(
                bCabinetContext,
                axisSetting.WidthVector);
            var aWidthCoordinates = GetPhaseWidthCoordinates(
                aCabinetContext,
                axisSetting.WidthVector);
            ValidatePhaseCoordinateLists(
                bWidthCoordinates,
                aWidthCoordinates,
                usesReversedPhaseOrder);
            if (!allowsNonAlignedPhaseCoordinates &&
                Enumerable.Range(0, PhaseCount).Any(
                    phaseIndex => Math.Abs(
                        aWidthCoordinates[phaseIndex] -
                        bWidthCoordinates[phaseIndex]) >
                        ModelAlignmentTolerance))
            {
                throw new InvalidOperationException(
                    "未标记“大偏移”的面对面模型中，A/B 柜三相中心不在同一宽度位置；当前仅支持同轴普通面对面，若为大偏移请检查总装配体“RD类型”。");
            }
        }

        /// <summary>
        /// 侧对侧固定换相的三相共用同一宽度走向，以三个不同高度层区分相别，
        /// 因而不能复用面对面按宽度相序校验。这里只校验同相两端的高度、宽度
        /// 搭接层以及所有铜排规格；模型反读使用统一的 2 mm 物理容差。
        /// </summary>
        private static void ValidateSideToSideFixedPhaseRouteCompatibility(
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext,
            BusbarBridgeAxisSetting axisSetting)
        {
            var commonBusbarWidth =
                bCabinetContext.PhaseRouteContexts[0].BusbarWidth;
            var commonBusbarThickness =
                bCabinetContext.PhaseRouteContexts[0].BusbarThickness;
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var bRouteContext =
                    bCabinetContext.PhaseRouteContexts[phaseIndex];
                var aRouteContext =
                    aCabinetContext.PhaseRouteContexts[phaseIndex];
                if (Math.Abs(bRouteContext.BusbarWidth -
                             aRouteContext.BusbarWidth) >
                        ModelAlignmentTolerance ||
                    Math.Abs(bRouteContext.BusbarThickness -
                             aRouteContext.BusbarThickness) >
                        ModelAlignmentTolerance ||
                    Math.Abs(bRouteContext.BusbarWidth - commonBusbarWidth) >
                        ModelAlignmentTolerance ||
                    Math.Abs(aRouteContext.BusbarWidth - commonBusbarWidth) >
                        ModelAlignmentTolerance ||
                    Math.Abs(bRouteContext.BusbarThickness -
                             commonBusbarThickness) >
                        ModelAlignmentTolerance ||
                    Math.Abs(aRouteContext.BusbarThickness -
                             commonBusbarThickness) >
                        ModelAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        "侧对侧固定换相的 A/B/C 三相及两柜伸出排必须使用相同排宽和排厚。");
                }

                var bRouteReferencePoint =
                    bRouteContext.OrdinaryRouteReferencePoint;
                var aRouteReferencePoint =
                    aRouteContext.OrdinaryRouteReferencePoint;
                var heightCoordinateDifference = Math.Abs(
                    BusbarGeometryUtility.Project(
                        bRouteReferencePoint,
                        axisSetting.HeightVector) -
                    BusbarGeometryUtility.Project(
                        aRouteReferencePoint,
                        axisSetting.HeightVector));
                var widthCoordinateDifference = Math.Abs(
                    BusbarGeometryUtility.Project(
                        bRouteReferencePoint,
                        axisSetting.WidthVector) -
                    BusbarGeometryUtility.Project(
                        aRouteReferencePoint,
                        axisSetting.WidthVector));
                if (heightCoordinateDifference >
                        SideToSidePhysicalAlignmentTolerance ||
                    widthCoordinateDifference >
                        SideToSidePhysicalAlignmentTolerance)
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相 A/B 柜垂直基架绝缘子未形成同一搭接层：高度偏差=" +
                        heightCoordinateDifference.ToString("0.###") +
                        " mm，宽度偏差=" +
                        widthCoordinateDifference.ToString("0.###") + " mm。");
                }

                if (!BusbarGeometryUtility.AreParallel(
                        bRouteContext.RouteThicknessDirection,
                        aRouteContext.RouteThicknessDirection))
                {
                    throw new InvalidOperationException(
                        FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                        "相 A/B 柜垂直搭接排厚方向不一致。");
                }
            }
        }

        /// <summary>
        /// 奇数断点的大偏移会让末段按逻辑奇偶落到桥式层，但 A 柜基座排的
        /// 搭接端必须始终保持直排。末段保留原普通段中心层，与 A 柜基座排
        /// 中心相差一个排厚形成面搭接；仅 B 侧保留桥式端头。
        /// </summary>
        private static void ConfigureHalfBridgeAtACabinetTerminal(
            FaceToFaceRouteGroupDefinition lastRouteDefinition,
            FaceToFaceSidePlanningContext aCabinetContext,
            Vector3d heightDirection)
        {
            if (lastRouteDefinition == null)
            {
                throw new ArgumentNullException(nameof(lastRouteDefinition));
            }
            if (aCabinetContext == null ||
                aCabinetContext.PhaseRouteContexts.Count != PhaseCount ||
                aCabinetContext.CabinetConnectionBusbarsByPhase.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    "A柜缺少完整的三相末端搭接数据。");
            }
            if (heightDirection == null ||
                heightDirection.Norm <= GeometryTolerance)
            {
                throw new ArgumentException(
                    "A柜末端半桥式排高度方向无效。",
                    nameof(heightDirection));
            }

            var terminalLapLogItems = new List<string>();
            foreach (var phaseSegment in lastRouteDefinition.PhaseSegments)
            {
                var phaseIndex = phaseSegment.PhaseIndex;
                var aTerminalPoints = aCabinetContext
                    .CabinetConnectionBusbarsByPhase[phaseIndex]
                    .Select(busbar => busbar.PointList.LastOrDefault())
                    .Where(terminalPoint => terminalPoint != null)
                    .ToList();
                var aTerminalCenter = BusbarGeometryUtility.Average(
                    aTerminalPoints);
                var aTerminalHeightCoordinate = BusbarGeometryUtility.Project(
                    aTerminalCenter,
                    heightDirection);

                var routeContext = aCabinetContext
                    .PhaseRouteContexts[phaseIndex];
                var routeEndHeightCoordinate = BusbarGeometryUtility.Project(
                    phaseSegment.EndPoint,
                    heightDirection);
                var terminalCenterDistance = Math.Abs(
                    aTerminalHeightCoordinate - routeEndHeightCoordinate);
                var thicknessToHeightSign = phaseSegment.ThicknessDirection
                    .Normalized.Dot(heightDirection.Normalized) >= 0d
                    ? 1d
                    : -1d;
                phaseSegment.BridgeStartOffsetDistance =
                    thicknessToHeightSign * routeContext.BusbarThickness;
                phaseSegment.BridgeEndOffsetDistance = 0d;
                terminalLapLogItems.Add(
                    FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex) +
                    "相中心距=" + terminalCenterDistance.ToString("0.###") +
                    " mm/目标=" +
                    routeContext.BusbarThickness.ToString("0.###") + " mm");
            }

            FaceToWallPreviewTrace.Info(
                "大偏移A柜末端半桥式",
                "逻辑段=" + (lastRouteDefinition.RouteGroupIndex + 1) +
                "；B侧保留桥式端头；A侧保持原普通段层直排收尾；" +
                string.Join("；", terminalLapLogItems));
        }

        private static void ValidateACabinetTerminalLayer(
            FaceToFaceSidePlanningContext aCabinetContext,
            FaceToFaceSidePlanningContext bCabinetContext,
            Vector3d heightDirection,
            bool lastRouteIsBridge,
            double alignmentTolerance = ModelAlignmentTolerance)
        {
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var aTerminalPoints = aCabinetContext
                    .CabinetConnectionBusbarsByPhase[phaseIndex]
                    .Select(busbar => busbar.PointList.LastOrDefault())
                    .Where(terminalPoint => terminalPoint != null)
                    .ToList();
                var aTerminalCenter = BusbarGeometryUtility.Average(
                    aTerminalPoints);
                var bRouteContext =
                    bCabinetContext.PhaseRouteContexts[phaseIndex];
                var expectedHeightCoordinate = BusbarGeometryUtility.Project(
                    bRouteContext.OrdinaryRouteReferencePoint,
                    heightDirection) +
                    (lastRouteIsBridge ? 0d : bRouteContext.BusbarThickness);
                var actualHeightCoordinate = BusbarGeometryUtility.Project(
                    aTerminalCenter,
                    heightDirection);
                if (Math.Abs(actualHeightCoordinate - expectedHeightCoordinate) >
                    alignmentTolerance)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "{0}相 A 柜收尾搭接层 {1:0.###} 与末段{2}所需的另一搭接层 {3:0.###} 不一致。",
                            FaceToWallModelBusbarPlanner.GetPhaseName(phaseIndex),
                            actualHeightCoordinate,
                            lastRouteIsBridge ? "桥式" : "I排",
                            expectedHeightCoordinate));
                }
            }
        }

        private static void EnsureDistinctCabinetHosts(
            FaceToFaceSidePlanningContext bCabinetContext,
            FaceToFaceSidePlanningContext aCabinetContext)
        {
            if (string.Equals(
                    BusbarGeometryUtility.SafeName(
                        bCabinetContext.BaseComponent),
                    BusbarGeometryUtility.SafeName(
                        aCabinetContext.BaseComponent),
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    BusbarGeometryUtility.SafeName(
                        bCabinetContext.FirstFrameComponent),
                    BusbarGeometryUtility.SafeName(
                        aCabinetContext.FirstFrameComponent),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "面对面 B/A 两柜必须分别匹配不同的基座和首个基架。");
            }
        }

        private static void EnsureStraightFrameChain(
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxes)
        {
            if (GetChangedWidthCenterEdgeIndexes(orderedFrameBoxes).Count > 0)
            {
                throw new InvalidOperationException(
                    "未标记“大偏移”的面对面模型中，基架 Box 存在宽度偏移；当前仅支持同轴普通面对面，若为大偏移请检查总装配体“RD类型”。");
            }
        }

        private static List<int> GetChangedWidthCenterEdgeIndexes(
            IReadOnlyList<FaceToFaceFrameBox> orderedFrameBoxes)
        {
            return Enumerable.Range(0, orderedFrameBoxes.Count - 1)
                .Where(frameIndex => Math.Abs(
                    orderedFrameBoxes[frameIndex + 1].WidthCenter -
                    orderedFrameBoxes[frameIndex].WidthCenter) >
                    FrameWidthCenterChangeTolerance)
                .ToList();
        }

        private static IReadOnlyList<double> GetPhaseWidthCoordinates(
            FaceToFaceSidePlanningContext sideContext,
            Vector3d widthDirection)
        {
            return sideContext.PhaseRouteContexts
                .Select(routeContext => BusbarGeometryUtility.Project(
                    routeContext.OrdinaryRouteReferencePoint,
                    widthDirection))
                .ToList()
                .AsReadOnly();
        }

        private static void ValidatePhaseCoordinateLists(
            IReadOnlyList<double> bPhaseWidthCoordinates,
            IReadOnlyList<double> aPhaseWidthCoordinates,
            bool allowReversedPhaseOrder = false)
        {
            if (bPhaseWidthCoordinates == null ||
                aPhaseWidthCoordinates == null ||
                bPhaseWidthCoordinates.Count != PhaseCount ||
                aPhaseWidthCoordinates.Count != PhaseCount)
            {
                throw new InvalidOperationException(
                    "面对面规划需要 B/A 两柜各三个明确 A/B/C 相中心坐标。");
            }

            if (bPhaseWidthCoordinates.Concat(aPhaseWidthCoordinates).Any(
                    phaseCoordinate => double.IsNaN(phaseCoordinate) ||
                                       double.IsInfinity(phaseCoordinate)))
            {
                throw new InvalidOperationException(
                    "面对面 A/B 柜三相中心包含无效数值。");
            }

            if (bPhaseWidthCoordinates.SelectMany(
                    (firstCoordinate, firstIndex) =>
                        bPhaseWidthCoordinates.Skip(firstIndex + 1)
                            .Select(secondCoordinate => Math.Abs(
                                secondCoordinate - firstCoordinate)))
                    .Any(phaseSpacing =>
                        phaseSpacing <= ModelAlignmentTolerance) ||
                aPhaseWidthCoordinates.SelectMany(
                    (firstCoordinate, firstIndex) =>
                        aPhaseWidthCoordinates.Skip(firstIndex + 1)
                            .Select(secondCoordinate => Math.Abs(
                                secondCoordinate - firstCoordinate)))
                    .Any(phaseSpacing =>
                        phaseSpacing <= ModelAlignmentTolerance))
            {
                throw new InvalidOperationException(
                    "面对面同柜三相中心不能重合。");
            }

            for (var firstPhaseIndex = 0;
                firstPhaseIndex < PhaseCount;
                firstPhaseIndex++)
            {
                for (var secondPhaseIndex = firstPhaseIndex + 1;
                    secondPhaseIndex < PhaseCount;
                    secondPhaseIndex++)
                {
                    var bPhaseSpacing = bPhaseWidthCoordinates[secondPhaseIndex] -
                                        bPhaseWidthCoordinates[firstPhaseIndex];
                    var aPhaseSpacing = aPhaseWidthCoordinates[secondPhaseIndex] -
                                        aPhaseWidthCoordinates[firstPhaseIndex];
                    var phaseSpacingDifference = allowReversedPhaseOrder
                        ? bPhaseSpacing + aPhaseSpacing
                        : bPhaseSpacing - aPhaseSpacing;
                    if (Math.Abs(phaseSpacingDifference) >
                        ModelAlignmentTolerance)
                    {
                        throw new InvalidOperationException(
                            allowReversedPhaseOrder
                                ? "A/B 柜实际相间中心距不一致，无法按 A-C、C-A 反序换相。"
                                : "A/B 柜实际相间中心距或相序不一致，无法保持相间距连接。");
                    }
                }
            }
        }

        private static Point3d CreatePointAtLengthAndWidth(
            Point3d referencePoint,
            BusbarBridgeAxisSetting axisSetting,
            double lengthCoordinate,
            double widthCoordinate)
        {
            return BusbarGeometryUtility.WithProjection(
                BusbarGeometryUtility.WithProjection(
                    referencePoint,
                    axisSetting.LengthVector,
                    lengthCoordinate),
                axisSetting.WidthVector,
                widthCoordinate);
        }

        private static BusbarBridgeAxisSetting CreateLengthReversedAxisSetting(
            BusbarBridgeAxisSetting sourceAxisSetting)
        {
            return BusbarBridgeAxisSetting.CreateFromVectors(
                sourceAxisSetting.LengthVector.Negate(),
                sourceAxisSetting.HeightVector,
                sourceAxisSetting.WidthVector);
        }

        private static IReadOnlyList<double> NormalizeBreakpointPositions(
            IReadOnlyList<double> breakpointPositions)
        {
            if (breakpointPositions == null || breakpointPositions.Count < 2)
            {
                throw new InvalidOperationException(
                    "面对面至少需要 B 柜起始断点和 A 柜末端断点。");
            }

            var normalizedPositions = breakpointPositions.ToList();
            for (var breakpointIndex = 0;
                breakpointIndex < normalizedPositions.Count;
                breakpointIndex++)
            {
                var breakpointPosition = normalizedPositions[breakpointIndex];
                if (double.IsNaN(breakpointPosition) ||
                    double.IsInfinity(breakpointPosition) ||
                    breakpointIndex > 0 &&
                    breakpointPosition <=
                    normalizedPositions[breakpointIndex - 1] +
                    GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "面对面断点必须是沿 B 柜到 A 柜严格递增的有效数值。");
                }
            }

            return normalizedPositions.AsReadOnly();
        }

        private static void ValidatePositiveFinite(
            double value,
            string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "参数必须是有限正数。");
            }
        }

        private static void ValidateOptionalGeneratedBusbarSpecification(
            double? generatedBusbarWidth,
            double? generatedBusbarThickness)
        {
            if (generatedBusbarWidth.HasValue !=
                generatedBusbarThickness.HasValue)
            {
                throw new ArgumentException(
                    "中间排排宽和排厚必须同时提供。");
            }

            if (!generatedBusbarWidth.HasValue)
            {
                return;
            }

            ValidatePositiveFinite(
                generatedBusbarWidth.Value,
                nameof(generatedBusbarWidth));
            ValidatePositiveFinite(
                generatedBusbarThickness.Value,
                nameof(generatedBusbarThickness));
            if (generatedBusbarWidth.Value <=
                generatedBusbarThickness.Value)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(generatedBusbarWidth),
                    "中间排排宽必须大于排厚。");
            }
        }

        private static void ValidateNonNegativeFinite(
            double value,
            string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "参数必须是有限非负数。");
            }
        }

        private static BusbarPhase_e GetPhase(int phaseIndex)
        {
            return phaseIndex == 0
                ? BusbarPhase_e.A
                : phaseIndex == 1
                    ? BusbarPhase_e.B
                    : BusbarPhase_e.C;
        }

        private sealed class FaceToFaceCabinetExtensionSides
        {
            internal List<ModeledBusbar> BCabinetBusbars { get; set; }
            internal List<ModeledBusbar> ACabinetBusbars { get; set; }
            internal double SeparationLengthCoordinate { get; set; }
        }

        private sealed class FaceToFaceSidePlanningContext
        {
            internal string SideLabel { get; set; }
            internal Component2 BaseComponent { get; set; }
            internal Component2 FirstFrameComponent { get; set; }
            internal bool UseActualInsulatorPositions { get; set; }
            internal List<ModeledBusbar> ExtensionBusbars { get; set; }
            internal List<FaceToWallPhaseRouteContext> PhaseRouteContexts { get; } =
                new List<FaceToWallPhaseRouteContext>();
            internal List<List<PlanarBendBusbarSetting>>
                CabinetConnectionBusbarsByPhase { get; } =
                    new List<List<PlanarBendBusbarSetting>>();
        }

        private sealed class FrameBoxGeometryCandidate
        {
            internal FaceToFaceFrameBox FrameBox { get; set; }
            internal int InputIndex { get; set; }
            internal double PathProgress { get; set; }
            internal double PathDistance { get; set; }
        }

        internal sealed class FaceToFaceRouteGroupDefinition
        {
            internal string DisplayName { get; set; }
            internal FaceToFaceBusbarGroupKind_e Kind { get; set; }
            internal int RouteGroupIndex { get; set; }
            internal bool IsBridge { get; set; }
            internal List<FaceToFacePhaseRouteSegment> PhaseSegments { get; } =
                new List<FaceToFacePhaseRouteSegment>();
        }

        internal sealed class FaceToFacePhaseRouteSegment
        {
            internal int PhaseIndex { get; set; }
            internal Point3d StartPoint { get; set; }
            internal Point3d EndPoint { get; set; }
            internal Vector3d RouteDirection { get; set; }
            internal Vector3d WidthDirection { get; set; }
            internal Vector3d ThicknessDirection { get; set; }
            internal double? BridgeStartOffsetDistance { get; set; }
            internal double? BridgeEndOffsetDistance { get; set; }
        }
    }
}
