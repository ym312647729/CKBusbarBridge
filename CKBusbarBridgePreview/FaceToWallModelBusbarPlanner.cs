using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>面对墙柜侧固定普通段及算法端点规划结果。</summary>
    public sealed class FaceToWallModelBusbarPlan
    {
        /// <summary>按物理段分组的全部柜侧固定铜排。</summary>
        public List<FaceToWallGeneratedBusbarGroup> GeneratedOrdinaryBusbarGroups { get; } =
            new List<FaceToWallGeneratedBusbarGroup>();

        /// <summary>全部柜侧固定铜排的扁平集合，仅用于预览。</summary>
        public List<PlanarBendBusbarSetting> GeneratedBusbars { get; } =
            new List<PlanarBendBusbarSetting>();

        /// <summary>紧邻算法生成区域的最后一组固定排，仅用于 Worker 始端。</summary>
        public List<PlanarBendBusbarSetting> AlgorithmStartBusbars { get; } =
            new List<PlanarBendBusbarSetting>();

        public IReadOnlyList<ModeledBusbar> CabinetTopExtensionBusbars { get; set; }
        public IReadOnlyList<ModeledBusbar> WallBushingBusbars { get; set; }
        public Point3d AlgorithmContainmentMinimum { get; set; }
        public Point3d AlgorithmContainmentMaximum { get; set; }
        public string LastFrameComponentName { get; set; }
    }

    /// <summary>实际生成时需要共同组成一个子装配体的单个物理普通段。</summary>
    public sealed class FaceToWallGeneratedBusbarGroup
    {
        public string DisplayName { get; set; }
        public bool IsBridge { get; set; }

        public List<PlanarBendBusbarSetting> Busbars { get; } =
            new List<PlanarBendBusbarSetting>();
    }

    public sealed class FaceToWallAlgorithmContainmentBox
    {
        public Point3d Minimum { get; set; }
        public Point3d Maximum { get; set; }
        public string SourceFrameComponentName { get; set; }
        public double InsetDistance { get; set; }
    }

    /// <summary>
    /// 柜顶五点搭接完成后，长度向普通段继续规划所需的单相几何基准。
    /// 面对面规划复用同一套柜顶识别和搭接算法，避免两端各维护一份公式。
    /// </summary>
    internal sealed class FaceToWallPhaseRouteContext
    {
        internal int PhaseIndex { get; set; }
        internal Point3d OrdinaryRouteReferencePoint { get; set; }
        internal Vector3d RouteWidthDirection { get; set; }
        internal Vector3d RouteThicknessDirection { get; set; }
        internal Vector3d FrameOutwardNormal { get; set; }
        internal double BusbarWidth { get; set; }
        internal double BusbarThickness { get; set; }
        internal double BendingRadius { get; set; }
        internal double BendAllowance { get; set; }
        internal IReadOnlyList<double> StackMemberOffsets { get; set; }
    }

    public static class FaceToWallModelBusbarPlanner
    {
        private const int PhaseCount = 3;
        private const double GeometryTolerance = 0.001d;
        private const float CabinetTopLengthAxisMergeTolerance = 1.0f;
        private const double FrameStructureInsetDistance = 40d;
        /// <summary>
        /// 桥式排越过一个排宽搭接区后，斜段在长度轴上的固定投影。
        /// 该值不能随排宽、叠排数量或搭接参数变化。
        /// </summary>
        internal const double BridgeInnermostTransitionProjectionLength = 50d;
        /// <summary>
        /// 最下层桥式排两端直段在实际排宽之外固定增加的长度。
        /// </summary>
        internal const double BridgeLowestEdgeStraightAdditionalLength = 40d;
        private const int MaximumSinglePhaseStackCount = 4;
        private const double BusbarSpecificationTolerance = 0.2d;

        private sealed class FaceToWallFrameSupportProfile
        {
            internal Component2 FrameComponent { get; set; }
            internal double MinimumLengthCoordinate { get; set; }
            internal double SupportLengthCoordinate { get; set; }
            internal IReadOnlyList<InsulatorConnectionInfo>
                OrderedPhaseInsulators { get; set; }
            internal IReadOnlyList<double> PhaseRouteHeightCoordinates { get; set; }
            internal IReadOnlyList<double> PhaseBsideEdgeLengthCoordinates { get; set; }
        }

        private sealed class FaceToWallVerticalRisePlan
        {
            internal IReadOnlyList<InsulatorConnectionInfo>
                FirstFramePhaseInsulators { get; set; }
            internal int? TransitionOrdinarySegmentIndex { get; set; }
            internal IReadOnlyList<double> ElevatedPhaseRouteHeightCoordinates
                { get; set; }
            internal IReadOnlyList<double> TransitionLengthCoordinates { get; set; }
        }

        private sealed class FaceToWallBaseInsulatorCandidate
        {
            internal Component2 BaseComponent { get; set; }
            internal Point3d BaseCenter { get; set; }
            internal double CabinetDistance { get; set; }
            internal double HeightCoordinate { get; set; }
            internal IReadOnlyList<InsulatorConnectionInfo> Insulators { get; set; }
        }

        private sealed class FaceToWallBaseInsulatorSelection
        {
            internal Component2 SelectedBaseComponent { get; set; }
            internal Point3d SelectedBaseCenter { get; set; }
            internal IReadOnlyList<InsulatorConnectionInfo> SelectedInsulators { get; set; }
            internal ISet<string> AllBaseInsulatorComponentNames { get; set; }
        }

        public static FaceToWallModelBusbarPlan Create(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            IReadOnlyList<ModeledBusbar> wallBushingBusbars,
            IReadOnlyList<double> breakpointPositions,
            int singlePhaseStackCount,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            double insulationClearance = 125d)
        {
            if (partCatalog == null) throw new ArgumentNullException(nameof(partCatalog));
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            if (axisSetting == null) throw new ArgumentNullException(nameof(axisSetting));
            if (extensionBusbars == null) throw new ArgumentNullException(nameof(extensionBusbars));
            if (wallBushingBusbars == null) throw new ArgumentNullException(nameof(wallBushingBusbars));
            if (breakpointPositions == null) throw new ArgumentNullException(nameof(breakpointPositions));

            var algorithmContainmentBox = CreateAlgorithmContainmentBox(
                partCatalog,
                insulationClearance);
            var generatedOrdinaryBusbarGroups =
                CreateCabinetTopConnectionGroupsCore(
                partCatalog,
                swApp,
                axisSetting,
                extensionBusbars,
                breakpointPositions,
                singlePhaseStackCount,
                bendingRadius,
                bendAllowance,
                algorithmContainmentBox);
            var result = new FaceToWallModelBusbarPlan
            {
                CabinetTopExtensionBusbars = extensionBusbars,
                WallBushingBusbars = wallBushingBusbars,
                AlgorithmContainmentMinimum = algorithmContainmentBox.Minimum.Copy(),
                AlgorithmContainmentMaximum = algorithmContainmentBox.Maximum.Copy(),
                LastFrameComponentName =
                    algorithmContainmentBox.SourceFrameComponentName
            };
            result.GeneratedOrdinaryBusbarGroups.AddRange(
                generatedOrdinaryBusbarGroups);
            result.GeneratedBusbars.AddRange(
                generatedOrdinaryBusbarGroups.SelectMany(
                    generatedBusbarGroup => generatedBusbarGroup.Busbars));
            var algorithmStartBusbarGroup =
                generatedOrdinaryBusbarGroups.LastOrDefault();
            if (algorithmStartBusbarGroup == null ||
                algorithmStartBusbarGroup.Busbars.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对墙没有形成可供算法使用的始端普通段。");
            }

            result.AlgorithmStartBusbars.AddRange(
                algorithmStartBusbarGroup.Busbars);
            return result;
        }

        /// <summary>
        /// 根据实际柜顶伸出排、绝缘子和可编辑断点，生成算法前的全部固定普通段。
        /// 柜顶伸出排只作为固定搭接端和坐标基准，不加入生成结果。
        /// </summary>
        public static IReadOnlyList<PlanarBendBusbarSetting> CreateCabinetTopConnectionGroups(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            IReadOnlyList<double> breakpointPositions,
            int singlePhaseStackCount,
            double bendingRadius,
            double bendAllowance,
            double insulationClearance = 125d)
        {
            var algorithmContainmentBox = CreateAlgorithmContainmentBox(
                partCatalog,
                insulationClearance);
            return CreateCabinetTopConnectionGroupsCore(
                partCatalog,
                swApp,
                axisSetting,
                extensionBusbars,
                breakpointPositions,
                singlePhaseStackCount,
                bendingRadius,
                bendAllowance,
                algorithmContainmentBox)
                .SelectMany(generatedBusbarGroup => generatedBusbarGroup.Busbars)
                .ToList()
                .AsReadOnly();
        }

        private static IReadOnlyList<FaceToWallGeneratedBusbarGroup>
            CreateCabinetTopConnectionGroupsCore(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            IReadOnlyList<double> breakpointPositions,
            int singlePhaseStackCount,
            double bendingRadius,
            double bendAllowance,
            FaceToWallAlgorithmContainmentBox algorithmContainmentBox)
        {
            if (partCatalog == null) throw new ArgumentNullException(nameof(partCatalog));
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            if (axisSetting == null) throw new ArgumentNullException(nameof(axisSetting));
            if (extensionBusbars == null) throw new ArgumentNullException(nameof(extensionBusbars));
            if (breakpointPositions == null) throw new ArgumentNullException(nameof(breakpointPositions));
            ValidateSinglePhaseStackCount(singlePhaseStackCount);
            if (algorithmContainmentBox?.Minimum == null ||
                algorithmContainmentBox.Maximum == null)
            {
                throw new ArgumentNullException(nameof(algorithmContainmentBox));
            }
            if (double.IsNaN(bendingRadius) || double.IsInfinity(bendingRadius) ||
                bendingRadius <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(bendingRadius), "折弯半径必须大于 0。");
            }

            if (double.IsNaN(bendAllowance) || double.IsInfinity(bendAllowance) ||
                bendAllowance < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(bendAllowance), "折弯余量不能小于 0。");
            }

            GetBoundingBoxProjectionRange(
                algorithmContainmentBox.Minimum,
                algorithmContainmentBox.Maximum,
                axisSetting.LengthVector,
                out var algorithmContainmentNearLengthCoordinate,
                out var algorithmContainmentFarLengthCoordinate);
            var normalizedBreakpointPositions = NormalizeBreakpointPositions(
                breakpointPositions);

            var phaseGroups = GroupExtensionBusbarsByPhase(extensionBusbars, axisSetting.WidthVector);
            var cabinetAssemblyComponent = ResolveFaceToWallCabinetAssembly(
                partCatalog,
                extensionBusbars);
            var cabinetAssemblyCenter = BusbarGeometryUtility.GetComponentCenter(
                cabinetAssemblyComponent);
            if (cabinetAssemblyCenter == null)
            {
                throw new InvalidOperationException(
                    "面对墙对应柜体装配体没有可用的 Box 中心，无法判断两个基座的远近。 ");
            }
            FaceToWallPreviewTrace.Info(
                "面对墙基座归属-对应柜体Box中心",
                "柜体=" + BusbarGeometryUtility.SafeName(cabinetAssemblyComponent) +
                "，Box中心=" + FormatPoint(cabinetAssemblyCenter));
            var physicallyOrderedFaceToWallFrames = partCatalog
                .GetGeneratedFrameComponentsInOrder()
                .ToList();
            if (physicallyOrderedFaceToWallFrames.Count < 2)
            {
                throw new InvalidOperationException(
                    "面对墙至少需要一个柜侧固定基架和一个仅供算法使用的末基架。");
            }

            var algorithmRangeLastFrameComponent =
                physicallyOrderedFaceToWallFrames[
                    physicallyOrderedFaceToWallFrames.Count - 1];
            var algorithmRangeLastFrameDescendantNames =
                new HashSet<string>(
                    partCatalog.GetAllDescendants(
                            algorithmRangeLastFrameComponent)
                        .Select(BusbarGeometryUtility.SafeName),
                    StringComparer.OrdinalIgnoreCase);
            FaceToWallPreviewTrace.Info(
                "面对墙末基架-排除绝缘子读取",
                "末基架=" + BusbarGeometryUtility.SafeName(
                    algorithmRangeLastFrameComponent) +
                "，排除组件数=" +
                algorithmRangeLastFrameDescendantNames.Count);
            var availableConnectionInsulators = ReadAvailableConnectionInsulators(
                partCatalog,
                swApp,
                algorithmRangeLastFrameDescendantNames);
            var baseInsulatorSelection = ResolveFaceToWallBaseInsulators(
                partCatalog,
                availableConnectionInsulators,
                axisSetting.LengthVector,
                axisSetting.HeightVector,
                cabinetAssemblyCenter);
            var baseInsulators = baseInsulatorSelection.SelectedInsulators;
            var baseInsulatorComponentNames = new HashSet<string>(
                baseInsulatorSelection.AllBaseInsulatorComponentNames,
                StringComparer.OrdinalIgnoreCase);

            var selectedFirstFrameComponent = SelectNearestFrameHostingInsulators(
                partCatalog,
                baseInsulatorSelection.SelectedBaseCenter,
                availableConnectionInsulators,
                axisSetting.HeightVector,
                baseInsulatorComponentNames,
                PhaseCount);
            if (selectedFirstFrameComponent == null)
            {
                throw new InvalidOperationException(
                    "面对墙主体基座附近没有识别到包含三相绝缘子的首个基架。 ");
            }

            physicallyOrderedFaceToWallFrames = OrderFaceToWallFramesFromBase(
                physicallyOrderedFaceToWallFrames,
                selectedFirstFrameComponent,
                baseInsulatorSelection.SelectedBaseCenter);
            FaceToWallPreviewTrace.Info(
                "面对墙首个基架-主体基座最近规则",
                "主体基座=" + BusbarGeometryUtility.SafeName(
                    baseInsulatorSelection.SelectedBaseComponent) +
                "，首个基架=" + BusbarGeometryUtility.SafeName(
                    selectedFirstFrameComponent) +
                "，基架顺序=" + string.Join(
                    "、",
                    physicallyOrderedFaceToWallFrames.Select(
                        BusbarGeometryUtility.SafeName)));

            var firstFrameComponent = physicallyOrderedFaceToWallFrames[0];
            if (firstFrameComponent == null)
            {
                throw new InvalidOperationException(
                    "按主体基座距离确定的首个基架无效。 ");
            }

            double firstFrameMinimumLengthCoordinate;
            double firstFrameMaximumLengthCoordinate;
            if (!BusbarGeometryUtility.TryGetComponentProjectionRange(
                    firstFrameComponent,
                    axisSetting.LengthVector,
                    out firstFrameMinimumLengthCoordinate,
                    out firstFrameMaximumLengthCoordinate))
            {
                throw new InvalidOperationException(
                    "无法获取第一个基架“" +
                    BusbarGeometryUtility.SafeName(firstFrameComponent) +
                    "”在长度轴上的模型范围。");
            }
            FaceToWallPreviewTrace.Info(
                "第一个基架-长度范围",
                "组件=" + BusbarGeometryUtility.SafeName(firstFrameComponent) +
                "，长度轴最小值=" + firstFrameMinimumLengthCoordinate.ToString("0.###") +
                "，长度轴右侧值=" + firstFrameMaximumLengthCoordinate.ToString("0.###"));

            var phaseWidthCoordinates = phaseGroups
                .Select(group => group.Average(busbar =>
                    BusbarGeometryUtility.Project(
                        busbar.TerminalSegment.P2,
                        axisSetting.WidthVector)))
                .ToList();
            var orderedBaseInsulators = MatchInsulatorsToPhases(
                baseInsulators,
                phaseWidthCoordinates,
                axisSetting.WidthVector);
            ResolveCommonFaceToWallBusbarSpecification(
                phaseGroups,
                out var commonFaceToWallBusbarWidth,
                out var commonFaceToWallBusbarThickness);
            var phaseFaceToWallBusbarThicknesses = phaseGroups
                .Select(phaseBusbars => phaseBusbars.Average(
                    phaseBusbar =>
                        phaseBusbar.CenterlineSetting.BusBarThickness))
                .ToList();
            FaceToWallPreviewTrace.Info(
                "面对墙统一排规格",
                "排宽=" + commonFaceToWallBusbarWidth.ToString("0.###") +
                "，排厚=" +
                commonFaceToWallBusbarThickness.ToString("0.###"));
            var faceToWallAlgorithmStartLengthCoordinate =
                algorithmContainmentNearLengthCoordinate +
                commonFaceToWallBusbarWidth;
            var verticalRisePlan = CreateVerticalRisePlan(
                partCatalog,
                physicallyOrderedFaceToWallFrames,
                availableConnectionInsulators,
                baseInsulatorComponentNames,
                phaseWidthCoordinates,
                axisSetting,
                phaseFaceToWallBusbarThicknesses,
                singlePhaseStackCount,
                bendAllowance,
                firstFrameMinimumLengthCoordinate,
                normalizedBreakpointPositions,
                faceToWallAlgorithmStartLengthCoordinate);
            var orderedFrameInsulators =
                verticalRisePlan.FirstFramePhaseInsulators;

            var generatedOrdinaryBusbarGroups =
                new List<FaceToWallGeneratedBusbarGroup>();
            for (var phaseIndex = 0; phaseIndex < PhaseCount; phaseIndex++)
            {
                var phaseOrdinaryBusbarGroups = BuildPhaseStack(
                    phaseGroups[phaseIndex],
                    orderedBaseInsulators[phaseIndex],
                    orderedFrameInsulators[phaseIndex],
                    axisSetting,
                    firstFrameMinimumLengthCoordinate,
                    normalizedBreakpointPositions,
                    algorithmContainmentNearLengthCoordinate,
                    algorithmContainmentFarLengthCoordinate,
                    phaseIndex,
                    singlePhaseStackCount,
                    bendingRadius,
                    bendAllowance,
                    true,
                    0,
                    verticalRisePlan.TransitionOrdinarySegmentIndex,
                    verticalRisePlan.TransitionOrdinarySegmentIndex.HasValue
                        ? (double?)verticalRisePlan
                            .ElevatedPhaseRouteHeightCoordinates[phaseIndex]
                        : null,
                    verticalRisePlan.TransitionOrdinarySegmentIndex.HasValue
                        ? (double?)verticalRisePlan
                            .TransitionLengthCoordinates[phaseIndex]
                        : null,
                    false,
                    null,
                    null,
                    out var ignoredFaceToWallRouteContext,
                    useActualInsulatorPositions: false,
                    useInsulatorFaceForLengthPosition: true);
                for (var ordinaryGroupIndex = 0;
                    ordinaryGroupIndex < phaseOrdinaryBusbarGroups.Count;
                    ordinaryGroupIndex++)
                {
                    while (generatedOrdinaryBusbarGroups.Count <=
                        ordinaryGroupIndex)
                    {
                        generatedOrdinaryBusbarGroups.Add(
                            new FaceToWallGeneratedBusbarGroup
                            {
                                DisplayName = ordinaryGroupIndex == 0
                                    ? "柜顶连接段"
                                    : "柜侧普通第" + ordinaryGroupIndex + "段",
                                IsBridge = ordinaryGroupIndex % 2 == 1
                            });
                    }

                    generatedOrdinaryBusbarGroups[ordinaryGroupIndex]
                        .Busbars.AddRange(
                            phaseOrdinaryBusbarGroups[ordinaryGroupIndex]);
                }
            }

            FaceToWallPreviewTrace.Info(
                "面对墙固定普通段规划-完成",
                "单相参数叠排数=" + singlePhaseStackCount +
                "，物理段数=" + generatedOrdinaryBusbarGroups.Count +
                "，三相生成路径数=" + generatedOrdinaryBusbarGroups.Sum(
                    generatedBusbarGroup => generatedBusbarGroup.Busbars.Count));
            return generatedOrdinaryBusbarGroups.AsReadOnly();
        }

        public static FaceToWallAlgorithmContainmentBox CreateAlgorithmContainmentBox(
            AssemblyPartCatalog partCatalog,
            double insulationClearance = 125d)
        {
            if (partCatalog == null)
            {
                throw new ArgumentNullException(nameof(partCatalog));
            }

            var lastFrameComponent = partCatalog.FindLastGeneratedFrameComponent();
            if (!BusbarGeometryUtility.TryGetComponentBoundingBox(
                    lastFrameComponent,
                    out var lastFrameMinimum,
                    out var lastFrameMaximum))
            {
                throw new InvalidOperationException(
                    "无法获取最后一个基架“" +
                    BusbarGeometryUtility.SafeName(lastFrameComponent) +
                    "”的模型包围盒。");
            }

            var containmentInsetDistance =
                CalculateAlgorithmContainmentInsetDistance(insulationClearance);
            var containmentMinimum = new Point3d(
                lastFrameMinimum.X + containmentInsetDistance,
                lastFrameMinimum.Y + containmentInsetDistance,
                lastFrameMinimum.Z + containmentInsetDistance);
            var containmentMaximum = new Point3d(
                lastFrameMaximum.X - containmentInsetDistance,
                lastFrameMaximum.Y - containmentInsetDistance,
                lastFrameMaximum.Z - containmentInsetDistance);
            if (containmentMaximum.X <= containmentMinimum.X ||
                containmentMaximum.Y <= containmentMinimum.Y ||
                containmentMaximum.Z <= containmentMinimum.Z)
            {
                throw new InvalidOperationException(
                    "最后一个基架“" +
                    BusbarGeometryUtility.SafeName(lastFrameComponent) +
                    "”的包围盒不足以在六个方向各内缩 " +
                    containmentInsetDistance.ToString("0.###") +
                    " mm。");
            }

            FaceToWallPreviewTrace.Info(
                "算法允许范围-最后基架包围盒",
                "组件=" + BusbarGeometryUtility.SafeName(lastFrameComponent) +
                "，原始最小=" + FormatPoint(lastFrameMinimum) +
                "，原始最大=" + FormatPoint(lastFrameMaximum) +
                "，等距内缩=" + containmentInsetDistance.ToString("0.###") +
                "，允许最小=" + FormatPoint(containmentMinimum) +
                "，允许最大=" + FormatPoint(containmentMaximum));
            return new FaceToWallAlgorithmContainmentBox
            {
                Minimum = containmentMinimum,
                Maximum = containmentMaximum,
                SourceFrameComponentName =
                    BusbarGeometryUtility.SafeName(lastFrameComponent),
                InsetDistance = containmentInsetDistance
            };
        }

        /// <summary>
        /// 面对墙算法允许范围在最后基架包围盒六个方向上的统一内缩量。
        /// 40 mm 为基架结构保护距离，绝缘距离在此基础上继续等距内缩。
        /// </summary>
        public static double CalculateAlgorithmContainmentInsetDistance(
            double insulationClearance)
        {
            if (double.IsNaN(insulationClearance) ||
                double.IsInfinity(insulationClearance) ||
                insulationClearance < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(insulationClearance),
                    "绝缘距离不能小于 0。");
            }

            return FrameStructureInsetDistance + insulationClearance;
        }

        /// <summary>
        /// 计算固定墙端断点相对最后基架左侧的位置。
        /// 断点位于搭接中心；普通段在该中心基础上再越过半个排宽，
        /// 因而实体端边最终位于 40 + 整排宽 + 绝缘距离处。
        /// </summary>
        public static double CalculateFixedEndBreakpointOffsetFromLastFrameLeft(
            double busbarWidth,
            double insulationClearance)
        {
            if (double.IsNaN(busbarWidth) ||
                double.IsInfinity(busbarWidth) ||
                busbarWidth <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(busbarWidth),
                    "排宽必须大于 0。");
            }

            return CalculateAlgorithmContainmentInsetDistance(
                       insulationClearance) +
                   busbarWidth / 2d;
        }

        internal static List<List<ModeledBusbar>> GroupExtensionBusbarsByPhase(
            IReadOnlyList<ModeledBusbar> extensionBusbars,
            Vector3d widthDirection)
        {
            var usableExtensionBusbars = extensionBusbars
                .Where(busbar => busbar?.CenterlineSetting != null &&
                                 busbar.TerminalSegment != null)
                .ToList();
            if (usableExtensionBusbars.Count < PhaseCount)
            {
                throw new InvalidOperationException("柜顶伸出排少于三相所需的 3 根。");
            }

            var unknownPhaseBusbars = usableExtensionBusbars
                .Where(extensionBusbar =>
                    extensionBusbar.Phase != BusbarPhase_e.A &&
                    extensionBusbar.Phase != BusbarPhase_e.B &&
                    extensionBusbar.Phase != BusbarPhase_e.C)
                .ToList();
            if (unknownPhaseBusbars.Count > 0)
            {
                throw new InvalidOperationException(
                    "存在未识别 A/B/C 相序的柜顶伸出排：" +
                    string.Join(
                        "、",
                        unknownPhaseBusbars.Select(unknownPhaseBusbar =>
                            BusbarGeometryUtility.SafeName(
                                unknownPhaseBusbar.Component))) +
                    "。");
            }

            var orderedPhases = new[]
            {
                BusbarPhase_e.A,
                BusbarPhase_e.B,
                BusbarPhase_e.C
            };
            var result = orderedPhases
                .Select(busbarPhase => usableExtensionBusbars
                    .Where(extensionBusbar =>
                        extensionBusbar.Phase == busbarPhase)
                    .OrderBy(extensionBusbar => BusbarGeometryUtility.Project(
                        extensionBusbar.TerminalSegment.P2,
                        widthDirection))
                    .ToList())
                .ToList();
            if (result.Any(phaseBusbars => phaseBusbars.Count == 0))
            {
                var missingPhaseNames = orderedPhases
                    .Where((ignoredBusbarPhase, phaseIndex) =>
                        result[phaseIndex].Count == 0)
                    .Select(missingBusbarPhase => missingBusbarPhase.ToString());
                throw new InvalidOperationException(
                    "柜顶伸出排缺少以下相序：" +
                    string.Join("、", missingPhaseNames) + "。");
            }

            FaceToWallPreviewTrace.Info(
                "柜顶伸出排-按实际相序分组",
                string.Join(
                    "；",
                    orderedPhases.Select((busbarPhase, phaseIndex) =>
                        busbarPhase + "相=" + result[phaseIndex].Count +
                        "根[" +
                        string.Join(
                            ",",
                            result[phaseIndex].Select(extensionBusbar =>
                                BusbarGeometryUtility.SafeName(
                                    extensionBusbar.Component))) +
                        "]")));

            return result;
        }

        internal static List<InsulatorConnectionInfo> ReadAvailableConnectionInsulators(
            AssemblyPartCatalog partCatalog,
            ISldWorks swApp,
            ISet<string> excludedInsulatorComponentNames = null)
        {
            var normalizedExcludedInsulatorComponentNames =
                excludedInsulatorComponentNames ??
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return ReadConnectionInsulators(
                partCatalog
                .GetComponents(SystemPartType_e.Insulator)
                .Where(insulatorComponent =>
                    !normalizedExcludedInsulatorComponentNames.Contains(
                        BusbarGeometryUtility.SafeName(
                            insulatorComponent))),
                swApp,
                "绝缘子配合圆-有效候选");
        }

        internal static List<InsulatorConnectionInfo> ReadConnectionInsulators(
            IEnumerable<Component2> insulatorComponents,
            ISldWorks swApp,
            string traceTitle)
        {
            if (insulatorComponents == null)
            {
                throw new ArgumentNullException(nameof(insulatorComponents));
            }

            var connectionInsulators = insulatorComponents
                .Where(insulatorComponent => insulatorComponent != null)
                .Select(insulatorComponent =>
                    InsulatorConnectionPlaneReader.Read(insulatorComponent, swApp))
                .Where(IsUsableConnectionInsulator)
                .ToList();
            FaceToWallPreviewTrace.Info(
                string.IsNullOrWhiteSpace(traceTitle)
                    ? "绝缘子配合圆-有效候选"
                    : traceTitle,
                "数量=" + connectionInsulators.Count +
                "，组件=" + string.Join(
                    ",",
                    connectionInsulators.Select(connectionInsulator =>
                        BusbarGeometryUtility.SafeName(connectionInsulator.Component))));
            return connectionInsulators;
        }

        private static FaceToWallBaseInsulatorSelection
            ResolveFaceToWallBaseInsulators(
            AssemblyPartCatalog partCatalog,
            IReadOnlyList<InsulatorConnectionInfo> availableConnectionInsulators,
            Vector3d expectedConnectionNormal,
            Vector3d heightDirection,
            Point3d cabinetAssemblyCenter)
        {
            var topLevelBaseComponents = partCatalog
                .GetTopLevelComponentsByConfiguredRdType(SystemPartType_e.Base)
                .ToList();
            if (topLevelBaseComponents.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对墙总装配体中没有识别到顶层 RD类型=基座的组件。");
            }

            if (topLevelBaseComponents.Count > 2)
            {
                throw new InvalidOperationException(
                    "面对墙通用规则最多允许两个顶层基座（上方普通基座、下方增高基座），" +
                    "当前识别到 " + topLevelBaseComponents.Count + " 个。组件=" +
                    string.Join(
                        "、",
                        topLevelBaseComponents.Select(
                            BusbarGeometryUtility.SafeName)) +
                    "。 ");
            }

            var baseInsulatorCandidates = topLevelBaseComponents
                .Select(baseComponent =>
                {
                    var baseCenter = BusbarGeometryUtility.GetComponentCenter(
                        baseComponent);
                    if (baseCenter == null)
                    {
                        throw new InvalidOperationException(
                            "面对墙基座“" +
                            BusbarGeometryUtility.SafeName(baseComponent) +
                            "”没有可用的装配体中心位置。");
                    }

                    var heightCoordinate = BusbarGeometryUtility.Project(
                        baseCenter,
                        heightDirection);
                    var strictBaseInsulators = SelectStrictBaseInsulators(
                        partCatalog,
                        baseComponent,
                        availableConnectionInsulators,
                        expectedConnectionNormal,
                        baseCenter);
                    return new FaceToWallBaseInsulatorCandidate
                    {
                        BaseComponent = baseComponent,
                        BaseCenter = baseCenter,
                        CabinetDistance = baseCenter.DistanceTo(cabinetAssemblyCenter),
                        HeightCoordinate = heightCoordinate,
                        Insulators = strictBaseInsulators.AsReadOnly()
                    };
                })
                .OrderBy(candidate => candidate.CabinetDistance)
                .ThenBy(candidate => candidate.HeightCoordinate)
                .ThenBy(candidate =>
                    BusbarGeometryUtility.SafeName(candidate.BaseComponent),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            FaceToWallPreviewTrace.Info(
                "面对墙基座-通用两基座识别",
                string.Join(
                    "；",
                    baseInsulatorCandidates.Select((candidate, candidateIndex) =>
                        "位置=" + (candidateIndex == 0 ? "柜体近侧增高基座" : "柜体远侧主体基座") +
                        "，组件=" + BusbarGeometryUtility.SafeName(
                            candidate.BaseComponent) +
                        "，距对应柜体Box中心距离=" + candidate.CabinetDistance.ToString("0.###") +
                        "，基座Box中心=" + FormatPoint(candidate.BaseCenter) +
                        "，高度轴坐标=" + candidate.HeightCoordinate.ToString("0.###") +
                        "，严格后代绝缘子数=" + candidate.Insulators.Count)));

            foreach (var baseInsulatorCandidate in baseInsulatorCandidates)
            {
                if (baseInsulatorCandidate.Insulators.Count != 0 &&
                    baseInsulatorCandidate.Insulators.Count != PhaseCount)
                {
                    throw new InvalidOperationException(
                        "面对墙基座“" + BusbarGeometryUtility.SafeName(
                            baseInsulatorCandidate.BaseComponent) +
                        "”内的绝缘子必须为 0 个或三相 3 个，当前识别到 " +
                        baseInsulatorCandidate.Insulators.Count + " 个。");
                }
            }

            var baseCandidatesWithInsulators = baseInsulatorCandidates
                .Where(candidate => candidate.Insulators.Count == PhaseCount)
                .ToList();
            if (baseCandidatesWithInsulators.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对墙两个基座内都没有识别到完整的三相绝缘子。");
            }

            var selectedBaseCandidate = baseCandidatesWithInsulators
                .OrderByDescending(candidate => candidate.CabinetDistance)
                .ThenBy(candidate =>
                    BusbarGeometryUtility.SafeName(candidate.BaseComponent),
                    StringComparer.OrdinalIgnoreCase)
                .First();
            FaceToWallPreviewTrace.Info(
                "面对墙基座-34点宿主选择",
                "宿主=" + BusbarGeometryUtility.SafeName(
                    selectedBaseCandidate.BaseComponent) +
                "，绝缘子数量=" + selectedBaseCandidate.Insulators.Count +
                "，候选宿主数=" + baseCandidatesWithInsulators.Count +
                "，规则=距柜体更远者为主体基座，距柜体更近者为增高基座；" +
                "P3/P4按该基座绝缘子实际配合圆计算");

            var allBaseInsulatorComponentNames = new HashSet<string>(
                baseInsulatorCandidates
                    .SelectMany(candidate => candidate.Insulators)
                    .Select(insulator => BusbarGeometryUtility.SafeName(
                        insulator.Component)),
                StringComparer.OrdinalIgnoreCase);
            return new FaceToWallBaseInsulatorSelection
            {
                SelectedBaseComponent = selectedBaseCandidate.BaseComponent,
                SelectedBaseCenter = selectedBaseCandidate.BaseCenter,
                SelectedInsulators = selectedBaseCandidate.Insulators,
                AllBaseInsulatorComponentNames = allBaseInsulatorComponentNames
            };
        }

        private static Component2 ResolveFaceToWallCabinetAssembly(
            AssemblyPartCatalog partCatalog,
            IReadOnlyList<ModeledBusbar> extensionBusbars)
        {
            var cabinetAssemblies = extensionBusbars
                .Select(extensionBusbar => partCatalog.GetTopLevelComponent(
                    extensionBusbar?.Component))
                .Where(component => component != null)
                .GroupBy(
                    component => BusbarGeometryUtility.SafeName(component),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (cabinetAssemblies.Count != 1)
            {
                throw new InvalidOperationException(
                    "面对墙柜顶伸出排必须全部属于同一个总装配体顶层柜体装配体；" +
                    "当前识别到=" +
                    (cabinetAssemblies.Count == 0
                        ? "无"
                        : string.Join(
                            "、",
                            cabinetAssemblies.Select(
                                BusbarGeometryUtility.SafeName))) + "。 ");
            }

            return cabinetAssemblies[0];
        }

        private static List<InsulatorConnectionInfo> SelectStrictBaseInsulators(
            AssemblyPartCatalog partCatalog,
            Component2 baseComponent,
            IReadOnlyList<InsulatorConnectionInfo> availableConnectionInsulators,
            Vector3d expectedConnectionNormal,
            Point3d baseCenter)
        {
            var baseDescendantComponentNames = new HashSet<string>(
                partCatalog.GetAllDescendants(baseComponent)
                    .Select(BusbarGeometryUtility.SafeName),
                StringComparer.OrdinalIgnoreCase);
            return availableConnectionInsulators
                .Where(connectionInsulator =>
                    baseDescendantComponentNames.Contains(
                        BusbarGeometryUtility.SafeName(
                            connectionInsulator.Component)) &&
                    BusbarGeometryUtility.AreParallel(
                        connectionInsulator.ConnectionPlane.Normal,
                        expectedConnectionNormal))
                .OrderBy(connectionInsulator => BusbarGeometryUtility.Distance(
                    connectionInsulator.ConnectionCircleCenter,
                    baseCenter))
                .ToList();
        }

        internal static Component2 SelectNearestFrameHostingInsulators(
            AssemblyPartCatalog partCatalog,
            Point3d baseReferencePoint,
            IReadOnlyList<InsulatorConnectionInfo> availableConnectionInsulators,
            Vector3d expectedFrameConnectionNormal,
            ISet<string> excludedInsulatorComponentNames,
            int requiredInsulatorCount,
            bool useActualInsulatorPositions = false)
        {
            var frameHostCandidates = partCatalog
                .GetTopLevelComponentsByConfiguredRdType(SystemPartType_e.Frame)
                .Select(frameCandidateComponent =>
                {
                    var frameDescendantComponentNames = new HashSet<string>(
                        partCatalog.GetAllDescendants(frameCandidateComponent)
                            .Select(BusbarGeometryUtility.SafeName),
                        StringComparer.OrdinalIgnoreCase);
                    var matchingFrameInsulatorCount = availableConnectionInsulators.Count(
                        connectionInsulatorCandidate =>
                        {
                            var connectionInsulatorCandidateName =
                                BusbarGeometryUtility.SafeName(
                                    connectionInsulatorCandidate.Component);
                            return !excludedInsulatorComponentNames.Contains(
                                       connectionInsulatorCandidateName) &&
                                   frameDescendantComponentNames.Contains(
                                       connectionInsulatorCandidateName) &&
                                   (useActualInsulatorPositions ||
                                    BusbarGeometryUtility.AreParallel(
                                        connectionInsulatorCandidate.ConnectionPlane.Normal,
                                        expectedFrameConnectionNormal));
                        });
                    return new
                    {
                        FrameComponent = frameCandidateComponent,
                        FrameCenterPoint = BusbarGeometryUtility.GetComponentCenter(
                            frameCandidateComponent),
                        MatchingInsulatorCount = matchingFrameInsulatorCount
                    };
                })
                .ToList();
            FaceToWallPreviewTrace.Info(
                "第一个基架-宿主候选",
                string.Join(
                    ";",
                    frameHostCandidates.Select(frameHostCandidate =>
                        BusbarGeometryUtility.SafeName(frameHostCandidate.FrameComponent) +
                        "@" + FormatPoint(frameHostCandidate.FrameCenterPoint) +
                        "，三相绝缘子数=" + frameHostCandidate.MatchingInsulatorCount)));

            var selectedFirstFrameComponent = frameHostCandidates
                .Where(frameHostCandidate =>
                    frameHostCandidate.FrameCenterPoint != null &&
                    frameHostCandidate.MatchingInsulatorCount >= requiredInsulatorCount)
                .OrderBy(frameHostCandidate => BusbarGeometryUtility.Distance(
                    frameHostCandidate.FrameCenterPoint,
                    baseReferencePoint))
                .ThenBy(frameHostCandidate => BusbarGeometryUtility.SafeName(
                    frameHostCandidate.FrameComponent),
                    StringComparer.OrdinalIgnoreCase)
                .Select(frameHostCandidate => frameHostCandidate.FrameComponent)
                .FirstOrDefault();
            FaceToWallPreviewTrace.Info(
                "第一个基架-宿主选择",
                "选中=" + BusbarGeometryUtility.SafeName(selectedFirstFrameComponent) +
                "，要求三相绝缘子数=" + requiredInsulatorCount);
            return selectedFirstFrameComponent;
        }

        private static List<Component2> OrderFaceToWallFramesFromBase(
            IReadOnlyList<Component2> configuredFrameOrder,
            Component2 selectedFirstFrameComponent,
            Point3d ordinaryBaseCenter)
        {
            if (configuredFrameOrder == null || configuredFrameOrder.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对墙没有可用于按主体基座距离排序的基架。 ");
            }

            if (selectedFirstFrameComponent == null || ordinaryBaseCenter == null)
            {
                throw new ArgumentNullException(nameof(selectedFirstFrameComponent));
            }

            var selectedFirstFrameName = BusbarGeometryUtility.SafeName(
                selectedFirstFrameComponent);
            var algorithmRangeLastFrameComponent = configuredFrameOrder[
                configuredFrameOrder.Count - 1];
            var algorithmRangeLastFrameName = BusbarGeometryUtility.SafeName(
                algorithmRangeLastFrameComponent);
            if (string.Equals(
                    selectedFirstFrameName,
                    algorithmRangeLastFrameName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "主体基座最近的首个基架不能同时作为面对墙算法范围末基架。 ");
            }

            var frameDistanceCandidates = configuredFrameOrder
                .Select(frameComponent => new
                {
                    FrameComponent = frameComponent,
                    FrameCenter = BusbarGeometryUtility.GetComponentCenter(
                        frameComponent),
                    IsSelectedFirst = string.Equals(
                        BusbarGeometryUtility.SafeName(frameComponent),
                        selectedFirstFrameName,
                        StringComparison.OrdinalIgnoreCase)
                })
                .ToList();
            if (frameDistanceCandidates.Any(frameCandidate =>
                    frameCandidate.FrameCenter == null))
            {
                throw new InvalidOperationException(
                    "面对墙存在无法读取包围盒中心的基架，无法按主体基座距离确定基架顺序。 ");
            }

            var selectedFrameCandidate = frameDistanceCandidates.First(
                frameCandidate => frameCandidate.IsSelectedFirst);
            var orderedFrameComponents = frameDistanceCandidates
                .Where(frameCandidate => !frameCandidate.IsSelectedFirst &&
                    !string.Equals(
                        BusbarGeometryUtility.SafeName(frameCandidate.FrameComponent),
                        algorithmRangeLastFrameName,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(frameCandidate => BusbarGeometryUtility.Distance(
                    frameCandidate.FrameCenter,
                    ordinaryBaseCenter))
                .ThenBy(frameCandidate => BusbarGeometryUtility.SafeName(
                    frameCandidate.FrameComponent),
                    StringComparer.OrdinalIgnoreCase)
                .Select(frameCandidate => frameCandidate.FrameComponent)
                .ToList();
            orderedFrameComponents.Insert(0, selectedFrameCandidate.FrameComponent);
            orderedFrameComponents.Add(algorithmRangeLastFrameComponent);
            if (!string.Equals(
                    BusbarGeometryUtility.SafeName(orderedFrameComponents[0]),
                    selectedFirstFrameName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "按主体基座距离排序后，首个基架不是已选中的最近基架。 ");
            }

            return orderedFrameComponents;
        }

        internal static List<InsulatorConnectionInfo> SelectHostInsulators(
            AssemblyPartCatalog partCatalog,
            Component2 hostComponent,
            IReadOnlyList<InsulatorConnectionInfo> availableConnectionInsulators,
            Vector3d expectedConnectionNormal,
            ISet<string> excludedComponentNames,
            string hostTypeLabel,
            int requiredCount,
            bool useActualInsulatorPositions = false)
        {
            var hostCenterPoint = BusbarGeometryUtility.GetComponentCenter(hostComponent);
            if (hostCenterPoint == null)
            {
                throw new InvalidOperationException(
                    hostTypeLabel + "“" + BusbarGeometryUtility.SafeName(hostComponent) +
                    "”没有可用的装配体中心位置。");
            }

            var descendantComponentNames = new HashSet<string>(
                partCatalog.GetAllDescendants(hostComponent)
                    .Select(BusbarGeometryUtility.SafeName),
                StringComparer.OrdinalIgnoreCase);
            var orientedConnectionInsulators = availableConnectionInsulators
                .Where(connectionInsulator =>
                {
                    var connectionInsulatorName = BusbarGeometryUtility.SafeName(
                        connectionInsulator.Component);
                    return !excludedComponentNames.Contains(connectionInsulatorName) &&
                           (useActualInsulatorPositions ||
                            BusbarGeometryUtility.AreParallel(
                                connectionInsulator.ConnectionPlane.Normal,
                                expectedConnectionNormal));
                })
                .ToList();
            var descendantConnectionInsulators = orientedConnectionInsulators
                .Where(connectionInsulator => descendantComponentNames.Contains(
                    BusbarGeometryUtility.SafeName(connectionInsulator.Component)))
                .ToList();
            var selectionPool = useActualInsulatorPositions
                ? descendantConnectionInsulators
                : descendantConnectionInsulators.Count >= requiredCount
                    ? descendantConnectionInsulators
                    : orientedConnectionInsulators;
            if (selectionPool.Count < requiredCount)
            {
                throw new InvalidOperationException(
                    hostTypeLabel + "未能从实际宿主组件中识别到完整的绝缘子数量，" +
                    "要求=" + requiredCount + "，实际=" + selectionPool.Count + "。");
            }
            var selectedConnectionInsulators = selectionPool
                .OrderBy(connectionInsulator => BusbarGeometryUtility.Distance(
                    connectionInsulator.ConnectionCircleCenter,
                    hostCenterPoint))
                .Take(requiredCount)
                .ToList();
            FaceToWallPreviewTrace.Info(
                "绝缘子宿主-选择",
                "宿主类型=" + hostTypeLabel +
                "，宿主=" + BusbarGeometryUtility.SafeName(hostComponent) +
                "，期望配合面法向=" + expectedConnectionNormal.Normalized +
                "，有效方向候选=" + orientedConnectionInsulators.Count +
                "，宿主后代候选=" + descendantConnectionInsulators.Count +
                "，选中=" + string.Join(
                    ",",
                    selectedConnectionInsulators.Select(connectionInsulator =>
                        BusbarGeometryUtility.SafeName(connectionInsulator.Component) +
                        "@" + FormatPoint(connectionInsulator.ConnectionCircleCenter))));
            return selectedConnectionInsulators;
        }

        private static void ResolveCommonFaceToWallBusbarSpecification(
            IReadOnlyList<List<ModeledBusbar>> phaseBusbarGroups,
            out double commonBusbarWidth,
            out double commonBusbarThickness)
        {
            var allCabinetTopBusbars = (phaseBusbarGroups ??
                    Array.Empty<List<ModeledBusbar>>())
                .SelectMany(phaseBusbarGroup => phaseBusbarGroup ??
                    new List<ModeledBusbar>())
                .Where(cabinetTopBusbar =>
                    cabinetTopBusbar?.CenterlineSetting != null)
                .ToList();
            if (allCabinetTopBusbars.Count == 0)
            {
                throw new InvalidOperationException(
                    "没有可用于识别面对墙统一排规格的柜顶伸出排。");
            }

            var resolvedFaceToWallCommonBusbarWidth =
                allCabinetTopBusbars.Average(
                    cabinetTopBusbar =>
                        cabinetTopBusbar.CenterlineSetting.BusBarWidth);
            var resolvedFaceToWallCommonBusbarThickness =
                allCabinetTopBusbars.Average(
                    cabinetTopBusbar =>
                        cabinetTopBusbar.CenterlineSetting.BusBarThickness);
            if (resolvedFaceToWallCommonBusbarWidth <= GeometryTolerance ||
                resolvedFaceToWallCommonBusbarThickness <= GeometryTolerance ||
                resolvedFaceToWallCommonBusbarWidth <=
                    resolvedFaceToWallCommonBusbarThickness)
            {
                throw new InvalidOperationException(
                    "面对墙柜顶伸出排的统一排宽或排厚无效。");
            }

            var mismatchingBusbarSpecification = allCabinetTopBusbars
                .FirstOrDefault(cabinetTopBusbar =>
                    Math.Abs(cabinetTopBusbar.CenterlineSetting.BusBarWidth -
                        resolvedFaceToWallCommonBusbarWidth) >
                            BusbarSpecificationTolerance ||
                    Math.Abs(cabinetTopBusbar.CenterlineSetting.BusBarThickness -
                        resolvedFaceToWallCommonBusbarThickness) >
                            BusbarSpecificationTolerance);
            if (mismatchingBusbarSpecification != null)
            {
                throw new InvalidOperationException(
                    "面对墙三相柜顶伸出排规格不一致，无法用一个排厚作为基架高度变化容差。");
            }

            commonBusbarWidth = resolvedFaceToWallCommonBusbarWidth;
            commonBusbarThickness = resolvedFaceToWallCommonBusbarThickness;
        }

        private static FaceToWallVerticalRisePlan CreateVerticalRisePlan(
            AssemblyPartCatalog partCatalog,
            IReadOnlyList<Component2> physicallyOrderedFrameComponents,
            IReadOnlyList<InsulatorConnectionInfo>
                availableConnectionInsulators,
            ISet<string> excludedBaseInsulatorComponentNames,
            IReadOnlyList<double> phaseWidthCoordinates,
            BusbarBridgeAxisSetting axisSetting,
            IReadOnlyList<double> phaseBusbarThicknesses,
            int singlePhaseStackCount,
            double bendAllowance,
            double firstFrameMinimumLengthCoordinate,
            IReadOnlyList<double> breakpointPositions,
            double algorithmStartLengthCoordinate)
        {
            if (phaseBusbarThicknesses == null ||
                phaseBusbarThicknesses.Count != PhaseCount ||
                phaseBusbarThicknesses.Any(phaseBusbarThickness =>
                    phaseBusbarThickness <= GeometryTolerance))
            {
                throw new InvalidOperationException(
                    "面对墙抬升识别缺少三相有效排厚。");
            }

            if (physicallyOrderedFrameComponents == null)
            {
                throw new ArgumentNullException(nameof(physicallyOrderedFrameComponents));
            }

            var orderedFrameComponents = physicallyOrderedFrameComponents.ToList();
            var frameSupportProfileCount =
                orderedFrameComponents.Count - 1;
            if (frameSupportProfileCount < 1)
            {
                throw new InvalidOperationException(
                    "面对墙没有可读取的柜侧固定基架；最后一个基架仅用于算法范围，不能参与绝缘子高度识别。");
            }
            var frameSupportProfiles =
                new List<FaceToWallFrameSupportProfile>();
            for (var physicalFrameIndex = 0;
                physicalFrameIndex < frameSupportProfileCount;
                physicalFrameIndex++)
            {
                var physicalFrameComponent =
                    orderedFrameComponents[physicalFrameIndex];
                var strictFrameInsulators = SelectStrictFrameInsulators(
                    partCatalog,
                    physicalFrameComponent,
                    availableConnectionInsulators,
                    axisSetting.HeightVector,
                    excludedBaseInsulatorComponentNames,
                    physicalFrameIndex,
                    PhaseCount);
                var orderedPhaseFrameInsulators = MatchInsulatorsToPhases(
                    strictFrameInsulators,
                    phaseWidthCoordinates,
                    axisSetting.WidthVector);
                var phaseRouteHeightCoordinates = new List<double>();
                var phaseBsideEdgeLengthCoordinates = new List<double>();
                for (var phaseIndex = 0;
                    phaseIndex < orderedPhaseFrameInsulators.Count;
                    phaseIndex++)
                {
                    var phaseFrameInsulator =
                        orderedPhaseFrameInsulators[phaseIndex];
                    ValidateInsulatorConnectionGeometry(
                        phaseFrameInsulator,
                        "基架" + (physicalFrameIndex + 1),
                        phaseIndex);
                    var phaseFrameOutwardNormal =
                        ResolveConnectionOutwardNormal(phaseFrameInsulator);
                    if (!BusbarGeometryUtility.AreParallel(
                            phaseFrameOutwardNormal,
                            axisSetting.HeightVector))
                    {
                        throw new InvalidOperationException(
                            GetPhaseName(phaseIndex) + "相基架" +
                            (physicalFrameIndex + 1) +
                            "绝缘子外法向与高度轴不平行，无法判断抬升高度。");
                    }

                    var phaseFrameStackReferencePoint =
                        PlanarBendBusbarSetting.Translate(
                            phaseFrameInsulator.ConnectionCircleCenter,
                            phaseFrameOutwardNormal,
                            (2d * singlePhaseStackCount - 1d) *
                            phaseBusbarThicknesses[phaseIndex] / 2d);
                    phaseRouteHeightCoordinates.Add(
                        BusbarGeometryUtility.Project(
                            phaseFrameStackReferencePoint,
                            axisSetting.HeightVector));
                    var frameInsulatorLengthAxisHalfSpan =
                        GetConnectionCircleLengthAxisHalfSpan(
                            phaseFrameInsulator,
                            axisSetting.LengthVector,
                            "基架" + (physicalFrameIndex + 1),
                            phaseIndex);
                    var frameInsulatorBsideEdgeLengthCoordinate =
                        BusbarGeometryUtility.Project(
                            phaseFrameInsulator.ConnectionCircleCenter,
                            axisSetting.LengthVector) -
                        frameInsulatorLengthAxisHalfSpan;
                    // 基架按 B 柜→A/墙方向递增；抬升直角要在倒数第二个
                    // 绝缘子靠 B 柜一侧开始，因此从 B 侧圆边界再反向
                    // 留出内侧排折弯余量，得到上下两条路径共用的长度轴。
                    var innerPhaseBendAllowance =
                        (singlePhaseStackCount - 1d) *
                        phaseBusbarThicknesses[phaseIndex] +
                        bendAllowance;
                    phaseBsideEdgeLengthCoordinates.Add(
                        frameInsulatorBsideEdgeLengthCoordinate -
                        innerPhaseBendAllowance);
                }

                if (!BusbarGeometryUtility.TryGetComponentProjectionRange(
                        physicalFrameComponent,
                        axisSetting.LengthVector,
                        out var frameMinimumLengthCoordinate,
                        out _))
                {
                    throw new InvalidOperationException(
                        "无法获取基架" + (physicalFrameIndex + 1) + "“" +
                        BusbarGeometryUtility.SafeName(
                            physicalFrameComponent) +
                        "”在长度轴上的模型范围。");
                }

                var frameSupportLengthCoordinate =
                    orderedPhaseFrameInsulators.Average(
                        frameInsulator => BusbarGeometryUtility.Project(
                            frameInsulator.ConnectionCircleCenter,
                            axisSetting.LengthVector));
                if (frameSupportProfiles.Count > 0 &&
                    frameSupportLengthCoordinate <=
                    frameSupportProfiles[frameSupportProfiles.Count - 1]
                        .SupportLengthCoordinate + GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "模型对照映射中的位置" + physicalFrameIndex + "基架" +
                        "到位置" + (physicalFrameIndex + 1) + "基架" +
                        "没有沿面对墙长度正方向排列，无法判断抬升位置。");
                }

                frameSupportProfiles.Add(
                    new FaceToWallFrameSupportProfile
                    {
                        FrameComponent = physicalFrameComponent,
                        MinimumLengthCoordinate =
                            frameMinimumLengthCoordinate,
                        SupportLengthCoordinate =
                            frameSupportLengthCoordinate,
                        OrderedPhaseInsulators =
                            orderedPhaseFrameInsulators.AsReadOnly(),
                        PhaseRouteHeightCoordinates =
                            phaseRouteHeightCoordinates.AsReadOnly(),
                        PhaseBsideEdgeLengthCoordinates =
                            phaseBsideEdgeLengthCoordinates.AsReadOnly()
                    });
                FaceToWallPreviewTrace.Info(
                    "面对墙抬升识别-基架支撑高度",
                    "位置=" + (physicalFrameIndex + 1) +
                    "，基架=" + BusbarGeometryUtility.SafeName(
                        physicalFrameComponent) +
                    "，近端坐标=" +
                    frameMinimumLengthCoordinate.ToString("0.###") +
                    "，绝缘子中心长度坐标=" +
                    frameSupportLengthCoordinate.ToString("0.###") +
                    "，三相排组中心高度=" + string.Join(
                        "/",
                        phaseRouteHeightCoordinates.Select(
                            phaseRouteHeightCoordinate =>
                                phaseRouteHeightCoordinate.ToString(
                                    "0.###"))));
            }

            if (orderedFrameComponents.Count > 1)
            {
                FaceToWallPreviewTrace.Info(
                    "面对墙抬升识别-跳过末基架",
                    "末基架=" + BusbarGeometryUtility.SafeName(
                        orderedFrameComponents[orderedFrameComponents.Count - 1]) +
                    "，用途=算法允许范围，不读取其内部绝缘子高度");
            }

            if (frameSupportProfiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对墙没有形成可用于判断抬升的基架绝缘子高度序列。");
            }

            if (Math.Abs(frameSupportProfiles[0].MinimumLengthCoordinate -
                    firstFrameMinimumLengthCoordinate) > GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "基架1长度范围在抬升识别前后不一致，无法继续规划。");
            }

            var elevatedFrameProfileIndex = -1;
            for (var frameProfileIndex = 1;
                frameProfileIndex < frameSupportProfiles.Count;
                frameProfileIndex++)
            {
                var precedingFrameProfile =
                    frameSupportProfiles[frameProfileIndex - 1];
                var currentFrameProfile =
                    frameSupportProfiles[frameProfileIndex];
                var phaseHeightDeltas = Enumerable.Range(0, PhaseCount)
                    .Select(phaseIndex =>
                        currentFrameProfile.PhaseRouteHeightCoordinates[
                            phaseIndex] -
                        precedingFrameProfile.PhaseRouteHeightCoordinates[
                            phaseIndex])
                    .ToList();
                if (Enumerable.Range(0, PhaseCount).Any(
                        phaseIndex => phaseHeightDeltas[phaseIndex] <
                            -phaseBusbarThicknesses[phaseIndex] -
                            GeometryTolerance))
                {
                    throw new InvalidOperationException(
                        "面对墙基架" + frameProfileIndex + "到基架" +
                        (frameProfileIndex + 1) +
                        "出现超过一个排厚的下降；当前只允许一次低到高抬升。");
                }

                var risingPhaseCount = Enumerable.Range(0, PhaseCount)
                    .Count(phaseIndex => phaseHeightDeltas[phaseIndex] >
                        phaseBusbarThicknesses[phaseIndex] +
                        GeometryTolerance);
                if (risingPhaseCount > 0 && risingPhaseCount < PhaseCount)
                {
                    throw new InvalidOperationException(
                        "面对墙基架" + frameProfileIndex + "到基架" +
                        (frameProfileIndex + 1) +
                        "的三相绝缘子没有在同一位置共同抬升。");
                }

                if (risingPhaseCount == PhaseCount)
                {
                    if (elevatedFrameProfileIndex >= 0)
                    {
                        throw new InvalidOperationException(
                            "面对墙绝缘子高度序列存在多次抬升；当前只允许一次低到高抬升。");
                    }

                    elevatedFrameProfileIndex = frameProfileIndex;
                }
            }

            var penultimateFrameProfileIndex = frameSupportProfiles.Count - 1;
            var terminalPhaseHeightDeltas = Enumerable.Range(0, PhaseCount)
                .Select(phaseIndex =>
                    frameSupportProfiles[penultimateFrameProfileIndex]
                        .PhaseRouteHeightCoordinates[phaseIndex] -
                    frameSupportProfiles[0]
                        .PhaseRouteHeightCoordinates[phaseIndex])
                .ToList();
            var terminalRisingPhaseCount = Enumerable.Range(0, PhaseCount)
                .Count(phaseIndex => terminalPhaseHeightDeltas[phaseIndex] >
                    phaseBusbarThicknesses[phaseIndex] + GeometryTolerance);
            if (terminalRisingPhaseCount > 0 &&
                terminalRisingPhaseCount < PhaseCount)
            {
                throw new InvalidOperationException(
                    "面对墙倒数第二个基架与 B 柜最近基架的三相绝缘子没有在同一位置共同抬升。");
            }

            if (terminalRisingPhaseCount == 0)
            {
                if (elevatedFrameProfileIndex >= 0)
                {
                    throw new InvalidOperationException(
                        "面对墙基架高度变化没有落在倒数第二个基架；无法按倒数第二个绝缘子位置生成抬升段。");
                }

                FaceToWallPreviewTrace.Info(
                    "面对墙基架绝缘子高度-无抬升",
                    "基架数=" + frameSupportProfiles.Count +
                    "，三相高度变化容差=" + string.Join(
                        "/",
                        phaseBusbarThicknesses.Select(
                            phaseBusbarThickness =>
                                phaseBusbarThickness.ToString("0.###"))) +
                    " mm");
                return new FaceToWallVerticalRisePlan
                {
                    FirstFramePhaseInsulators =
                        frameSupportProfiles[0].OrderedPhaseInsulators,
                    TransitionOrdinarySegmentIndex = null,
                    ElevatedPhaseRouteHeightCoordinates = Array.Empty<double>(),
                    TransitionLengthCoordinates = Array.Empty<double>()
                };
            }

            ValidateFaceToWallHeightPlateau(
                frameSupportProfiles,
                0,
                penultimateFrameProfileIndex,
                phaseBusbarThicknesses,
                "抬升前");
            elevatedFrameProfileIndex = penultimateFrameProfileIndex;

            var editableBreakpointLengthCoordinates = breakpointPositions
                .Take(breakpointPositions.Count - 1)
                .Select(breakpointPosition =>
                    ConvertRelativeBreakpointToModelLengthCoordinate(
                        firstFrameMinimumLengthCoordinate,
                        breakpointPosition))
                .ToList();
            var elevatedFrameNearLengthCoordinate =
                frameSupportProfiles[elevatedFrameProfileIndex]
                    .MinimumLengthCoordinate;
            var verticalRiseOrdinarySegmentIndex = -1;
            for (var ordinarySegmentIndex = 0;
                ordinarySegmentIndex <
                    editableBreakpointLengthCoordinates.Count;
                ordinarySegmentIndex++)
            {
                var ordinarySegmentStartCenterCoordinate =
                    editableBreakpointLengthCoordinates[
                        ordinarySegmentIndex];
                var ordinarySegmentEndCenterCoordinate =
                    ordinarySegmentIndex <
                    editableBreakpointLengthCoordinates.Count - 1
                        ? editableBreakpointLengthCoordinates[
                            ordinarySegmentIndex + 1]
                        : algorithmStartLengthCoordinate;
                if (elevatedFrameNearLengthCoordinate + GeometryTolerance <
                        ordinarySegmentStartCenterCoordinate ||
                    elevatedFrameNearLengthCoordinate - GeometryTolerance >
                        ordinarySegmentEndCenterCoordinate)
                {
                    continue;
                }

                verticalRiseOrdinarySegmentIndex = ordinarySegmentIndex;
                break;
            }

            if (verticalRiseOrdinarySegmentIndex < 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "面对墙首个抬升基架近端坐标 {0:0.###} 不在固定普通段中心范围 {1:0.###}～{2:0.###} 内，无法把抬升放到其前一段。",
                        elevatedFrameNearLengthCoordinate,
                        editableBreakpointLengthCoordinates.First(),
                        algorithmStartLengthCoordinate));
            }

            var lowerFrameProfile =
                frameSupportProfiles[elevatedFrameProfileIndex - 1];
            var elevatedFrameProfile =
                frameSupportProfiles[elevatedFrameProfileIndex];
            FaceToWallPreviewTrace.Info(
                "面对墙基架绝缘子高度-识别抬升",
                "低侧基架=" + BusbarGeometryUtility.SafeName(
                    lowerFrameProfile.FrameComponent) +
                "，高侧基架=" + BusbarGeometryUtility.SafeName(
                    elevatedFrameProfile.FrameComponent) +
                "，抬升定位=倒数第二个基架 B 柜侧绝缘子边缘外" +
                "，三相抬升点长度坐标=" + string.Join(
                    "/",
                    elevatedFrameProfile.PhaseBsideEdgeLengthCoordinates.Select(
                        transitionLengthCoordinate =>
                            transitionLengthCoordinate.ToString("0.###"))) +
                "，高侧近端坐标=" +
                elevatedFrameNearLengthCoordinate.ToString("0.###") +
                "，抬升普通段=" +
                (verticalRiseOrdinarySegmentIndex + 1) +
                "，高度变化容差=" +
                string.Join(
                    "/",
                    phaseBusbarThicknesses.Select(
                        phaseBusbarThickness =>
                            phaseBusbarThickness.ToString("0.###"))) +
                "，三相低高度=" + string.Join(
                    "/",
                    lowerFrameProfile.PhaseRouteHeightCoordinates.Select(
                        heightCoordinate =>
                            heightCoordinate.ToString("0.###"))) +
                "，三相高高度=" + string.Join(
                    "/",
                    elevatedFrameProfile.PhaseRouteHeightCoordinates.Select(
                        heightCoordinate =>
                            heightCoordinate.ToString("0.###"))));
            return new FaceToWallVerticalRisePlan
            {
                FirstFramePhaseInsulators =
                    frameSupportProfiles[0].OrderedPhaseInsulators,
                TransitionOrdinarySegmentIndex =
                    verticalRiseOrdinarySegmentIndex,
                ElevatedPhaseRouteHeightCoordinates =
                    elevatedFrameProfile.PhaseRouteHeightCoordinates,
                TransitionLengthCoordinates =
                    elevatedFrameProfile.PhaseBsideEdgeLengthCoordinates
            };
        }

        private static List<InsulatorConnectionInfo>
            SelectStrictFrameInsulators(
            AssemblyPartCatalog partCatalog,
            Component2 frameComponent,
            IReadOnlyList<InsulatorConnectionInfo>
                availableConnectionInsulators,
            Vector3d expectedConnectionNormal,
            ISet<string> excludedComponentNames,
            int physicalFrameIndex,
            int requiredCount)
        {
            var frameCenterPoint = BusbarGeometryUtility.GetComponentCenter(
                frameComponent);
            if (frameCenterPoint == null)
            {
                throw new InvalidOperationException(
                    "基架" + (physicalFrameIndex + 1) + "“" +
                    BusbarGeometryUtility.SafeName(frameComponent) +
                    "”没有可用的装配体中心位置。");
            }

            var frameDescendantComponentNames = new HashSet<string>(
                partCatalog.GetAllDescendants(frameComponent)
                    .Select(BusbarGeometryUtility.SafeName),
                StringComparer.OrdinalIgnoreCase);
            var strictFrameInsulators = availableConnectionInsulators
                .Where(connectionInsulator =>
                {
                    var connectionInsulatorName =
                        BusbarGeometryUtility.SafeName(
                            connectionInsulator.Component);
                    return !excludedComponentNames.Contains(
                               connectionInsulatorName) &&
                           frameDescendantComponentNames.Contains(
                               connectionInsulatorName) &&
                           BusbarGeometryUtility.AreParallel(
                               connectionInsulator.ConnectionPlane.Normal,
                               expectedConnectionNormal);
                })
                .OrderBy(connectionInsulator =>
                    BusbarGeometryUtility.Distance(
                        connectionInsulator.ConnectionCircleCenter,
                        frameCenterPoint))
                .Take(requiredCount)
                .ToList();
            if (strictFrameInsulators.Count < requiredCount)
            {
                throw new InvalidOperationException(
                    "基架" + (physicalFrameIndex + 1) + "“" +
                    BusbarGeometryUtility.SafeName(frameComponent) +
                    "”自身组件树中只识别到" +
                    strictFrameInsulators.Count +
                    "个高度向绝缘子，不能借用其他基架绝缘子判断抬升。");
            }

            FaceToWallPreviewTrace.Info(
                "面对墙抬升识别-基架绝缘子",
                "位置=" + (physicalFrameIndex + 1) +
                "，基架=" + BusbarGeometryUtility.SafeName(frameComponent) +
                "，绝缘子=" + string.Join(
                    ",",
                    strictFrameInsulators.Select(connectionInsulator =>
                        BusbarGeometryUtility.SafeName(
                            connectionInsulator.Component) + "@" +
                        FormatPoint(
                            connectionInsulator.ConnectionCircleCenter))));
            return strictFrameInsulators;
        }

        private static void ValidateFaceToWallHeightPlateau(
            IReadOnlyList<FaceToWallFrameSupportProfile> frameSupportProfiles,
            int startFrameProfileIndex,
            int endFrameProfileIndex,
            IReadOnlyList<double> phaseBusbarThicknesses,
            string plateauLabel)
        {
            var referenceHeightCoordinates = frameSupportProfiles[
                startFrameProfileIndex].PhaseRouteHeightCoordinates;
            for (var frameProfileIndex = startFrameProfileIndex + 1;
                frameProfileIndex < endFrameProfileIndex;
                frameProfileIndex++)
            {
                for (var phaseIndex = 0;
                    phaseIndex < PhaseCount;
                    phaseIndex++)
                {
                    var heightDeviation = Math.Abs(
                        frameSupportProfiles[frameProfileIndex]
                            .PhaseRouteHeightCoordinates[phaseIndex] -
                        referenceHeightCoordinates[phaseIndex]);
                    if (heightDeviation >
                        phaseBusbarThicknesses[phaseIndex] +
                        GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            plateauLabel + "的基架" +
                            (startFrameProfileIndex + 1) + "到基架" +
                            (frameProfileIndex + 1) + "中，" +
                            GetPhaseName(phaseIndex) +
                            "相绝缘子高度累计偏差 " +
                            heightDeviation.ToString("0.###") +
                            " mm，已超过一个排厚 " +
                            phaseBusbarThicknesses[phaseIndex]
                                .ToString("0.###") + " mm。");
                    }
                }
            }
        }

        private static bool IsUsableConnectionInsulator(
            InsulatorConnectionInfo connectionInsulator)
        {
            return connectionInsulator?.Component != null &&
                   connectionInsulator.ComponentCenter != null &&
                   connectionInsulator.ConnectionPlane?.Normal != null &&
                   connectionInsulator.ConnectionPlane.Normal.Norm > GeometryTolerance &&
                   connectionInsulator.ConnectionCircleCenter != null &&
                   !double.IsNaN(connectionInsulator.ConnectionCircleRadius) &&
                   !double.IsInfinity(connectionInsulator.ConnectionCircleRadius) &&
                   connectionInsulator.ConnectionCircleRadius > GeometryTolerance;
        }

        internal static List<InsulatorConnectionInfo> MatchInsulatorsToPhases(
            IEnumerable<InsulatorConnectionInfo> sourceInsulators,
            IReadOnlyList<double> phaseWidthCoordinates,
            Vector3d widthDirection)
        {
            var remainingInsulators = sourceInsulators
                .Where(insulator => insulator?.ComponentCenter != null)
                .ToList();
            var result = new List<InsulatorConnectionInfo>();
            foreach (var phaseWidthCoordinate in phaseWidthCoordinates)
            {
                var matchingInsulator = remainingInsulators
                    .OrderBy(insulator => Math.Abs(
                        BusbarGeometryUtility.Project(
                            insulator.ConnectionCircleCenter ?? insulator.ComponentCenter,
                            widthDirection) - phaseWidthCoordinate))
                    .FirstOrDefault();
                if (matchingInsulator == null)
                {
                    throw new InvalidOperationException("无法将绝缘子与三相柜顶伸出排一一对应。");
                }

                result.Add(matchingInsulator);
                remainingInsulators.Remove(matchingInsulator);
            }

            return result;
        }

        internal static IReadOnlyList<IReadOnlyList<PlanarBendBusbarSetting>>
            BuildPhaseStack(
            IReadOnlyList<ModeledBusbar> sourcePhaseBusbars,
            InsulatorConnectionInfo baseInsulator,
            InsulatorConnectionInfo frameInsulator,
            BusbarBridgeAxisSetting axisSetting,
            double firstFrameMinimumLengthCoordinate,
            IReadOnlyList<double> breakpointPositions,
            double algorithmContainmentNearLengthCoordinate,
            double algorithmContainmentFarLengthCoordinate,
            int phaseIndex,
            int singlePhaseStackCount,
            double bendingRadius,
            double bendAllowance,
            bool includeOrdinarySegments,
            int terminalRouteLayerOffsetInThicknesses,
            int? verticalRiseOrdinarySegmentIndex,
            double? elevatedRouteHeightCoordinate,
            double? verticalRiseLengthCoordinate,
            bool useSideToSideFixedVerticalFrameLap,
            double? generatedBusbarWidth,
            double? generatedBusbarThickness,
            out FaceToWallPhaseRouteContext routeContext,
            bool useActualInsulatorPositions = false,
            bool useInsulatorFaceForLengthPosition = false,
            double? plannedConnectionSupportHeightCoordinate = null)
        {
            routeContext = null;
            if (terminalRouteLayerOffsetInThicknesses != 0 &&
                terminalRouteLayerOffsetInThicknesses != -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(terminalRouteLayerOffsetInThicknesses),
                    "柜顶终端搭接层只允许使用默认层或降低一个排厚。");
            }

            if (verticalRiseOrdinarySegmentIndex.HasValue !=
                elevatedRouteHeightCoordinate.HasValue)
            {
                throw new InvalidOperationException(
                    "面对墙抬升段索引与抬升后高度必须同时提供。");
            }

            if (verticalRiseOrdinarySegmentIndex.HasValue !=
                verticalRiseLengthCoordinate.HasValue)
            {
                throw new InvalidOperationException(
                    "面对墙抬升段索引与倒数第二个绝缘子折弯定位长度必须同时提供。 ");
            }

            if (!includeOrdinarySegments &&
                verticalRiseOrdinarySegmentIndex.HasValue)
            {
                throw new InvalidOperationException(
                    "不生成普通段时不能设置面对墙抬升段。");
            }

            if (sourcePhaseBusbars == null || sourcePhaseBusbars.Count == 0)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相没有可用于生成连接排的柜顶伸出排。");
            }

            ValidateInsulatorConnectionGeometry(baseInsulator, "基座", phaseIndex);
            if (!plannedConnectionSupportHeightCoordinate.HasValue)
            {
                ValidateInsulatorConnectionGeometry(
                    frameInsulator,
                    "基架",
                    phaseIndex);
            }
            if (breakpointPositions == null || breakpointPositions.Count < 2)
            {
                throw new InvalidOperationException(
                    "面对墙至少需要起始断点和固定末端断点。");
            }

            var cabinetTopBusbarThickness = sourcePhaseBusbars.Average(busbar =>
                busbar.CenterlineSetting.BusBarThickness);
            var cabinetTopBusbarWidth = sourcePhaseBusbars.Average(busbar =>
                busbar.CenterlineSetting.BusBarWidth);
            if (cabinetTopBusbarThickness <= 0d ||
                cabinetTopBusbarWidth <= cabinetTopBusbarThickness)
            {
                throw new InvalidOperationException(GetPhaseName(phaseIndex) + "相柜顶伸出排规格无效。");
            }
            var busbarThickness = generatedBusbarThickness ??
                cabinetTopBusbarThickness;
            var busbarWidth = generatedBusbarWidth ?? cabinetTopBusbarWidth;
            if (double.IsNaN(busbarThickness) ||
                double.IsInfinity(busbarThickness) ||
                double.IsNaN(busbarWidth) ||
                double.IsInfinity(busbarWidth) ||
                busbarThickness <= 0d ||
                busbarWidth <= busbarThickness)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相中间排规格无效。");
            }

            var startThicknessDirection = AverageAlignedVector(sourcePhaseBusbars
                .Select(busbar => busbar.CenterlineSetting.StartBusbarVector));
            var startWidthDirection = AverageAlignedVector(sourcePhaseBusbars
                .Select(busbar => busbar.CenterlineSetting.WidthVector));
            if (startThicknessDirection == null || startWidthDirection == null)
            {
                throw new InvalidOperationException(GetPhaseName(phaseIndex) + "相柜顶伸出排方向无效。");
            }

            if (!BusbarGeometryUtility.AreParallel(
                    startWidthDirection,
                    axisSetting.WidthVector))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相柜顶伸出排排宽方向与所选宽度轴不一致。");
            }

            var fixedExtensionUpperCenterPoint = BusbarGeometryUtility.Average(
                sourcePhaseBusbars.Select(busbar => busbar.TerminalSegment.P2));
            if (fixedExtensionUpperCenterPoint == null)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相柜顶伸出排没有可用的上端中心点。");
            }

            var stackEnvelopeThickness =
                (2d * singlePhaseStackCount - 1d) * busbarThickness;
            // 面对墙 P2、P3 的余量按内侧排的中心线搭接端头计算：中心线到
            // 内侧排还隔着 (根数 - 1) 个排厚，之后才是用户输入的折弯
            // 余量（默认 20 mm）。侧对侧第一种保留其专用的实体端头规则。
            var stackBendAllowance = useSideToSideFixedVerticalFrameLap
                ? busbarThickness + bendingRadius
                : (singlePhaseStackCount - 1d) * busbarThickness +
                  bendAllowance;
            var reserveActualInsulatorBends = useActualInsulatorPositions &&
                !useInsulatorFaceForLengthPosition && !useSideToSideFixedVerticalFrameLap;
            // 实际安装面只确定叠排层位；最不利成员仍须在安装圆之外完成折弯。
            var connectionBendAllowance = reserveActualInsulatorBends
                ? (singlePhaseStackCount - 1d) * busbarThickness +
                  Math.Max(bendAllowance, busbarThickness + bendingRadius)
                : stackBendAllowance;
            var generatedStackLapSurfaceOffset =
                CalculateCabinetTopLapReferenceOffset(
                    singlePhaseStackCount,
                    sourcePhaseBusbars.Count,
                    busbarThickness,
                    cabinetTopBusbarThickness);
            var generatedStackUpperLapReferencePoint =
                PlanarBendBusbarSetting.Translate(
                    fixedExtensionUpperCenterPoint,
                    startThicknessDirection,
                    generatedStackLapSurfaceOffset);
            var firstCenterPoint = CabinetTopExtensionBusbarReader.CalculateConnectionStart(
                fixedExtensionUpperCenterPoint, startThicknessDirection, axisSetting.HeightVector,
                singlePhaseStackCount, sourcePhaseBusbars.Count, busbarWidth,
                busbarThickness, cabinetTopBusbarThickness);
            var secondCenterPoint = PlanarBendBusbarSetting.Translate(
                generatedStackUpperLapReferencePoint,
                axisSetting.HeightVector,
                stackBendAllowance);

            var firstPathDirection = new Vector3d(firstCenterPoint, secondCenterPoint);
            if (firstPathDirection.Norm < GeometryTolerance ||
                !BusbarGeometryUtility.AreParallel(
                    firstPathDirection,
                    axisSetting.HeightVector))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相柜顶方形搭接段没有沿所选高度轴生成。");
            }

            var calculatedFirstThicknessDirection = firstPathDirection.Normalized
                .Cross(startWidthDirection);
            if (calculatedFirstThicknessDirection.Norm < GeometryTolerance)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相柜顶伸出排中心线与排宽方向平行，无法确定叠排偏移方向。");
            }

            var pathThicknessOrientationSign =
                calculatedFirstThicknessDirection.Dot(startThicknessDirection) < 0d ? -1d : 1d;
            var terminalPathDirection = useSideToSideFixedVerticalFrameLap
                ? axisSetting.HeightVector
                : axisSetting.LengthVector;
            var calculatedEndThicknessDirection = terminalPathDirection
                .Cross(startWidthDirection);
            if (calculatedEndThicknessDirection.Norm < GeometryTolerance)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    (useSideToSideFixedVerticalFrameLap
                        ? "相侧对侧第一种柜顶首段排宽方向与高度方向平行。"
                        : "相柜顶伸出排排宽方向与所选长度方向平行。"));
            }

            calculatedEndThicknessDirection = calculatedEndThicknessDirection.Normalized;
            if (pathThicknessOrientationSign < 0d)
            {
                calculatedEndThicknessDirection = calculatedEndThicknessDirection.Negate();
            }

            var baseOutwardNormal = ResolveConnectionOutwardNormal(baseInsulator);
            var frameOutwardNormal =
                plannedConnectionSupportHeightCoordinate.HasValue
                    ? calculatedEndThicknessDirection.Copy()
                    : ResolveConnectionOutwardNormal(frameInsulator);
            FaceToWallPreviewTrace.Info(
                "面对墙绝缘子定位输入",
                "相=" + GetPhaseName(phaseIndex) +
                "，基座绝缘子=" + BusbarGeometryUtility.SafeName(baseInsulator.Component) +
                "，基座绝缘子原点=" + FormatPoint(baseInsulator.ConnectionPlane.Point) +
                "，基座配合圆心=" + FormatPoint(baseInsulator.ConnectionCircleCenter) +
                "，基座组件中心=" + FormatPoint(baseInsulator.ComponentCenter) +
                "，基座法向=" + FormatVector(baseOutwardNormal) +
                "，端部高度来源=" +
                (plannedConnectionSupportHeightCoordinate.HasValue
                    ? "基座开孔及预装绝缘子支架"
                    : "首基架绝缘子") +
                "，基架绝缘子=" +
                BusbarGeometryUtility.SafeName(frameInsulator?.Component) +
                "，基架绝缘子原点=" +
                FormatPoint(frameInsulator?.ConnectionPlane?.Point) +
                "，基架配合圆心=" +
                FormatPoint(frameInsulator?.ConnectionCircleCenter) +
                "，基架组件中心=" +
                FormatPoint(frameInsulator?.ComponentCenter) +
                "，基架法向=" + FormatVector(frameOutwardNormal));
            if (!useActualInsulatorPositions &&
                !BusbarGeometryUtility.AreParallel(
                    calculatedFirstThicknessDirection,
                    baseOutwardNormal))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相竖直中心线的排厚方向与基座绝缘子配合面法向不一致。");
            }

            if (!useActualInsulatorPositions &&
                !BusbarGeometryUtility.AreParallel(
                    calculatedEndThicknessDirection,
                    frameOutwardNormal))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相末段中心线的排厚方向与基架绝缘子配合面法向不一致。");
            }

            var baseStackCenterReferencePoint = PlanarBendBusbarSetting.Translate(
                baseInsulator.ConnectionCircleCenter,
                baseOutwardNormal,
                stackEnvelopeThickness / 2d);
            var baseInsulatorFaceStackReferencePoint =
                useInsulatorFaceForLengthPosition
                    ? TranslateInsulatorConnectionFaceToStackReference(
                        baseInsulator,
                        baseOutwardNormal,
                        stackEnvelopeThickness / 2d,
                        "基座",
                        phaseIndex)
                    : baseStackCenterReferencePoint;
            var frameStackCenterReferencePoint =
                plannedConnectionSupportHeightCoordinate.HasValue
                    ? BusbarGeometryUtility.WithProjection(
                        baseStackCenterReferencePoint,
                        axisSetting.HeightVector,
                        plannedConnectionSupportHeightCoordinate.Value)
                    : PlanarBendBusbarSetting.Translate(
                        frameInsulator.ConnectionCircleCenter,
                        frameOutwardNormal,
                        stackEnvelopeThickness / 2d);
            FaceToWallPreviewTrace.Info(
                "面对墙绝缘子叠排中心参考点",
                "相=" + GetPhaseName(phaseIndex) +
                "，基座配合圆心=" + FormatPoint(baseInsulator.ConnectionCircleCenter) +
                "，基座法向偏移=" + FormatVector(baseOutwardNormal) +
                "，叠排总厚度=" + stackEnvelopeThickness.ToString("0.###") +
                "，叠排半厚度=" + (stackEnvelopeThickness / 2d).ToString("0.###") +
                "，基座叠排中心参考点=" + FormatPoint(baseStackCenterReferencePoint) +
                "，基座长度定位参考点=" + FormatPoint(baseInsulatorFaceStackReferencePoint) +
                "，基架叠排中心参考点=" + FormatPoint(frameStackCenterReferencePoint));
            var baseStackLengthCoordinate = BusbarGeometryUtility.Project(
                baseInsulatorFaceStackReferencePoint,
                axisSetting.LengthVector);
            var baseInsulatorLengthAxisHalfSpan =
                useInsulatorFaceForLengthPosition || useActualInsulatorPositions
                ? 0d
                : GetConnectionCircleLengthAxisHalfSpan(
                    baseInsulator,
                    axisSetting.LengthVector,
                    "基座",
                    phaseIndex);
            var baseInsulatorNearBCircleEdgeLengthCoordinate =
                baseStackLengthCoordinate - baseInsulatorLengthAxisHalfSpan;
            var provisionalThirdCenterPoint =
                BusbarGeometryUtility.WithProjection(
                    secondCenterPoint,
                    axisSetting.LengthVector,
                    baseInsulatorNearBCircleEdgeLengthCoordinate +
                    stackBendAllowance);
            var baseCircleHalfSpan = useActualInsulatorPositions
                ? 0d
                : GetConnectionCircleHalfSpan(
                    baseInsulator,
                    provisionalThirdCenterPoint,
                    axisSetting.WidthVector,
                    "基座",
                    phaseIndex);
            var baseCircleCenterHeightCoordinate = BusbarGeometryUtility.Project(
                baseInsulator.ConnectionCircleCenter,
                axisSetting.HeightVector);
            var frameCircleCenterHeightCoordinate =
                plannedConnectionSupportHeightCoordinate ??
                BusbarGeometryUtility.Project(
                    frameInsulator.ConnectionCircleCenter,
                    axisSetting.HeightVector);
            var heightTravelSign = Math.Sign(
                frameCircleCenterHeightCoordinate -
                baseCircleCenterHeightCoordinate);
            if (heightTravelSign == 0d && (!useActualInsulatorPositions || reserveActualInsulatorBends))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相基座绝缘子与首基架绝缘子高度相同，无法确定 P2→P3 斜边方向。");
            }
            if (heightTravelSign == 0d)
            {
                heightTravelSign = 1;
            }
            // P3 要放在绝缘子进入侧边界之外，并沿 P2→P4 方向留出
            // 内侧排折弯余量。这样 P2→P3 不再只是沿长度轴的直段，
            // 而是从柜体端头斜向绝缘子搭接起点。
            var baseCircleEntryHeightCoordinate =
                baseCircleCenterHeightCoordinate -
                heightTravelSign * baseCircleHalfSpan;
            var thirdPointHeightCoordinate =
                baseCircleEntryHeightCoordinate -
                heightTravelSign * stackBendAllowance;
            var secondPointLengthCoordinate = BusbarGeometryUtility.Project(
                secondCenterPoint,
                axisSetting.LengthVector);
            var baseInsulatorFaceStackLengthCoordinate =
                baseStackLengthCoordinate;
            if (useInsulatorFaceForLengthPosition)
            {
                var lengthPositionReferencePoint =
                    BusbarGeometryUtility.WithProjection(
                        secondCenterPoint,
                        axisSetting.HeightVector,
                        thirdPointHeightCoordinate);
                baseInsulatorFaceStackLengthCoordinate =
                    CalculateInsulatorFaceStackLengthCoordinate(
                        baseInsulator,
                        baseOutwardNormal,
                        axisSetting.LengthVector,
                        lengthPositionReferencePoint,
                        stackEnvelopeThickness / 2d,
                        "基座",
                        phaseIndex);
            }
            FaceToWallPreviewTrace.Info(
                "面对墙基座排34点定位中间量",
                "相=" + GetPhaseName(phaseIndex) +
                "，基座绝缘子原点=" + FormatPoint(baseInsulator.ConnectionPlane.Point) +
                "，基座配合圆心=" + FormatPoint(baseInsulator.ConnectionCircleCenter) +
                "，基座配合圆心长度坐标=" +
                BusbarGeometryUtility.Project(
                    baseInsulator.ConnectionCircleCenter,
                    axisSetting.LengthVector).ToString("0.###") +
                "，绝缘子法向叠排半厚度偏移=" +
                (stackEnvelopeThickness / 2d).ToString("0.###") +
                "，法向偏移后长度坐标=" +
                baseInsulatorFaceStackLengthCoordinate.ToString("0.###") +
                "，P3/P4目标长度坐标=" +
                baseInsulatorFaceStackLengthCoordinate.ToString("0.###") +
                "，长度方向未重复追加折弯余量=" +
                stackBendAllowance.ToString("0.###") +
                "，P3目标高度坐标=" + thirdPointHeightCoordinate.ToString("0.###"));
            var thirdPointLengthCoordinate =
                useSideToSideFixedVerticalFrameLap
                    ? baseStackLengthCoordinate
                    : useInsulatorFaceForLengthPosition
                        ? baseInsulatorFaceStackLengthCoordinate
                    : baseInsulatorNearBCircleEdgeLengthCoordinate +
                      stackBendAllowance;
            if (!useSideToSideFixedVerticalFrameLap &&
                elevatedRouteHeightCoordinate.HasValue &&
                Math.Abs(
                    thirdPointLengthCoordinate -
                    secondPointLengthCoordinate) <= GeometryTolerance)
            {
                // 抬高路径必须显式保留 P2→P3 的内侧排折弯余量；
                // 当绝缘子目标恰好落在 P2 上时，沿 B→A 方向补足该长度，
                // 防止归并后抬高排没有可见的第三点。
                thirdPointLengthCoordinate = secondPointLengthCoordinate +
                    stackBendAllowance;
            }
            var baseToCabinetTopLengthAxisDeviation =
                thirdPointLengthCoordinate - secondPointLengthCoordinate;
            var shouldMergeMinorLengthAxisFold =
                !includeOrdinarySegments &&
                !useInsulatorFaceForLengthPosition &&
                !elevatedRouteHeightCoordinate.HasValue &&
                ShouldMergeCabinetTopLengthAxisFold(
                    baseToCabinetTopLengthAxisDeviation,
                    busbarThickness,
                    phaseIndex);
            var thirdCenterPoint = shouldMergeMinorLengthAxisFold
                ? secondCenterPoint.Copy()
                : BusbarGeometryUtility.WithProjection(
                    provisionalThirdCenterPoint,
                    axisSetting.LengthVector,
                    thirdPointLengthCoordinate);
            thirdCenterPoint = BusbarGeometryUtility.WithProjection(
                thirdCenterPoint,
                axisSetting.HeightVector,
                thirdPointHeightCoordinate);
            if (useActualInsulatorPositions &&
                !useInsulatorFaceForLengthPosition)
            {
                // 安装面和相别位置保持实际反读值；面对面/面对墙的折弯点
                // 必须提前到安装圆外，不能将安装圆心直接作为折弯点。
                // 侧对侧第一种仍交由下方专用分支按其原有规则定位。
                thirdCenterPoint = baseStackCenterReferencePoint.Copy();
                if (reserveActualInsulatorBends)
                    thirdCenterPoint = ActualInsulatorEntryBendPoint(baseInsulator,
                        baseStackCenterReferencePoint, secondCenterPoint, axisSetting,
                        heightTravelSign, connectionBendAllowance, phaseIndex, out baseCircleHalfSpan);
                thirdPointLengthCoordinate = BusbarGeometryUtility.Project(
                    thirdCenterPoint,
                    axisSetting.LengthVector);
                thirdPointHeightCoordinate = BusbarGeometryUtility.Project(
                    thirdCenterPoint,
                    axisSetting.HeightVector);
                baseToCabinetTopLengthAxisDeviation =
                    thirdPointLengthCoordinate - secondPointLengthCoordinate;
                shouldMergeMinorLengthAxisFold = false;
            }
            if (useSideToSideFixedVerticalFrameLap)
            {
                // 侧对侧第一种不能在柜顶搭接结束处立即作直角横折。
                // 斜段一直抬到当前相最近基座绝缘子搭接圆之前，并预留
                // 一个“排厚 + 折弯半径”，随后才转为竖直段穿过绝缘子。
                var baseInsulatorCircleCenterHeightCoordinate =
                    BusbarGeometryUtility.Project(
                        baseInsulator.ConnectionCircleCenter,
                        axisSetting.HeightVector);
                var diagonalEndHeightCoordinate =
                    baseInsulatorCircleCenterHeightCoordinate -
                    baseInsulator.ConnectionCircleRadius -
                    stackBendAllowance;
                if (diagonalEndHeightCoordinate <=
                    BusbarGeometryUtility.Project(
                        secondCenterPoint,
                        axisSetting.HeightVector) + GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseIndex) +
                        "相柜顶搭接结束点到最近基座绝缘子之间没有足够空间生成斜排。 ");
                }

                thirdCenterPoint = BusbarGeometryUtility.WithProjection(
                    thirdCenterPoint,
                    axisSetting.HeightVector,
                    diagonalEndHeightCoordinate);
            }
            FaceToWallPreviewTrace.Info(
                "柜顶连接G排规划-长度轴折返检查",
                "相=" + GetPhaseName(phaseIndex) +
                "，基座中心与柜顶搭接线长度轴偏差=" +
                Math.Abs(baseToCabinetTopLengthAxisDeviation).ToString("0.###") +
                "，基座绝缘子近B侧边界=" +
                baseInsulatorNearBCircleEdgeLengthCoordinate.ToString("0.###") +
                "，基座绝缘子配合面叠排长度坐标=" +
                baseInsulatorFaceStackLengthCoordinate.ToString("0.###") +
                "，3点目标长度=" +
                thirdPointLengthCoordinate.ToString("0.###") +
                "，3点目标高度=" +
                thirdPointHeightCoordinate.ToString("0.###") +
                "，L排归并允许偏差=" +
                CabinetTopLengthAxisMergeTolerance.ToString("0.###") +
                "，处理=" + (shouldMergeMinorLengthAxisFold
                    ? "已归并P2/P3"
                    : "保留G型折返"));

            var frameStackHeightCoordinate =
                plannedConnectionSupportHeightCoordinate ??
                BusbarGeometryUtility.Project(
                    frameStackCenterReferencePoint,
                    axisSetting.HeightVector);
            thirdPointHeightCoordinate = BusbarGeometryUtility.Project(
                thirdCenterPoint,
                axisSetting.HeightVector);
            if (!useActualInsulatorPositions &&
                heightTravelSign *
                    (frameStackHeightCoordinate - thirdPointHeightCoordinate) <=
                GeometryTolerance)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相基架绝缘子确定的最终高度 {1:0.###} 未沿 P3→P4 高度方向超过基座竖直段起点 {2:0.###}。",
                        GetPhaseName(phaseIndex),
                        frameStackHeightCoordinate,
                        thirdPointHeightCoordinate));
            }

            // 面对墙模式下，P3/P4 的长度由基座绝缘子配合面和内侧折弯距离
            // 共同确定；首个基架绝缘子只提供 P4 的高度基准，不参与长度定位。
            // 其它复用场景仍沿用其原有的叠排中心定位方式。
            var ordinaryRouteReferencePoint =
                BusbarGeometryUtility.WithProjection(
                    thirdCenterPoint,
                    axisSetting.HeightVector,
                    frameStackHeightCoordinate);
            var elevatedOrdinaryRouteReferencePoint =
                ordinaryRouteReferencePoint.Copy();
            if (elevatedRouteHeightCoordinate.HasValue)
            {
                if (elevatedRouteHeightCoordinate.Value <=
                    frameStackHeightCoordinate + busbarThickness +
                    GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "{0}相抬升后高度 {1:0.###} 与首基架高度 {2:0.###} 的差值未超过一个排厚 {3:0.###}。",
                            GetPhaseName(phaseIndex),
                            elevatedRouteHeightCoordinate.Value,
                            frameStackHeightCoordinate,
                            busbarThickness));
                }

                elevatedOrdinaryRouteReferencePoint =
                    BusbarGeometryUtility.WithProjection(
                        ordinaryRouteReferencePoint,
                        axisSetting.HeightVector,
                        elevatedRouteHeightCoordinate.Value);
            }
            // P4 先定位到端部支承高度：旧路径由首基架绝缘子提供，面对面
            // 新路径由基座开孔、41.5 mm 基座支架及预装绝缘子组合高度提供；
            // 随后沿高度轴偏移一个排厚。
            // 面对面实际绝缘子位置模式不能使用 A 柜末段层位的负一层覆盖，
            // 否则会把 P4 拉回绝缘子中心并触发错误的 I 排收尾层判断。
            var cabinetConnectionHeightOffsetInThicknesses =
                useActualInsulatorPositions
                    ? 1d
                    : 1d + terminalRouteLayerOffsetInThicknesses;
            var cabinetConnectionHeightCoordinate =
                useSideToSideFixedVerticalFrameLap
                    ? frameStackHeightCoordinate +
                      heightTravelSign * busbarWidth / 2d
                    : frameStackHeightCoordinate +
                      heightTravelSign *
                      cabinetConnectionHeightOffsetInThicknesses *
                      busbarThickness;
            var fourthCenterPoint = BusbarGeometryUtility.WithProjection(
                ordinaryRouteReferencePoint,
                axisSetting.HeightVector,
                cabinetConnectionHeightCoordinate);
            if (useInsulatorFaceForLengthPosition)
            {
                // 面对墙的 3、4 点长度共用绝缘子配合面沿法向偏移叠排半厚度
                // 后的定位结果；内侧折弯余量不能再次叠加到长度坐标，4 点只
                // 在此基础上补首个基架绝缘子确定的高度。
                fourthCenterPoint = BusbarGeometryUtility.WithProjection(
                    fourthCenterPoint,
                    axisSetting.LengthVector,
                    thirdPointLengthCoordinate);
            }

            FaceToWallPreviewTrace.Info(
                "面对墙基座排34点定位结果",
                "相=" + GetPhaseName(phaseIndex) +
                "，P3=" + FormatPoint(thirdCenterPoint) +
                "，P4=" + FormatPoint(fourthCenterPoint) +
                "，P3长度坐标=" +
                BusbarGeometryUtility.Project(
                    thirdCenterPoint,
                    axisSetting.LengthVector).ToString("0.###") +
                "，P3高度坐标=" +
                BusbarGeometryUtility.Project(
                    thirdCenterPoint,
                    axisSetting.HeightVector).ToString("0.###") +
                "，P4长度坐标=" +
                BusbarGeometryUtility.Project(
                    fourthCenterPoint,
                    axisSetting.LengthVector).ToString("0.###") +
                "，P4高度坐标=" +
                BusbarGeometryUtility.Project(
                    fourthCenterPoint,
                    axisSetting.HeightVector).ToString("0.###") +
                "，P4高度基准=" + frameStackHeightCoordinate.ToString("0.###") +
                "，P4最终高度=" + cabinetConnectionHeightCoordinate.ToString("0.###"));

            var baseCircleNearHeightCoordinate =
                baseCircleCenterHeightCoordinate -
                heightTravelSign * baseCircleHalfSpan;
            var baseCircleFarHeightCoordinate =
                baseCircleCenterHeightCoordinate +
                heightTravelSign * baseCircleHalfSpan;
            var baseCircleEntryBendReserve =
                heightTravelSign *
                (baseCircleNearHeightCoordinate - thirdPointHeightCoordinate);
            if ((!useActualInsulatorPositions || reserveActualInsulatorBends) &&
                baseCircleEntryBendReserve + GeometryTolerance < connectionBendAllowance)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相基座绝缘子搭接区前仅余 {1:0.###}，小于叠排折弯余量 {2:0.###}。",
                        GetPhaseName(phaseIndex),
                        baseCircleEntryBendReserve,
                        connectionBendAllowance));
            }

            var baseCircleBendReserve =
                heightTravelSign *
                (cabinetConnectionHeightCoordinate - baseCircleFarHeightCoordinate);
            if ((!useActualInsulatorPositions || reserveActualInsulatorBends) &&
                baseCircleBendReserve + GeometryTolerance < connectionBendAllowance)
            {
                var missingBendReserve = connectionBendAllowance - baseCircleBendReserve;
                var heightAdjustment = heightTravelSign > 0d ? "抬高" : "降低";
                // 端排高度由柜体驱动参数或支承绝缘子确定；提示调整该高度，避免误移基座内的侧向绝缘子。
                var adjustmentHint = plannedConnectionSupportHeightCoordinate.HasValue
                    ? string.Format(
                        "请在柜体驱动中{0}“绝缘子+支架高度”至少 {1:0.###} mm，{2}端部铜排支承高度；重新生成柜体后点击“重读并规划”。",
                        heightTravelSign > 0d ? "增大" : "减小",
                        missingBendReserve,
                        heightAdjustment)
                    : string.Format(
                        "请将端部支承绝缘子至少{0} {1:0.###} mm 后重新规划。",
                        heightAdjustment,
                        missingBendReserve);
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相基座绝缘子搭接区到最终折弯点仅余 {1:0.###} mm，小于叠排折弯余量 {2:0.###} mm。{3}",
                        GetPhaseName(phaseIndex),
                        baseCircleBendReserve,
                        connectionBendAllowance,
                        adjustmentHint));
            }

            var editableBreakpointLengthCoordinates = breakpointPositions
                .Take(breakpointPositions.Count - 1)
                .Select(breakpointPosition =>
                    ConvertRelativeBreakpointToModelLengthCoordinate(
                        firstFrameMinimumLengthCoordinate,
                        breakpointPosition))
                .ToList();
            var firstBreakpointLengthCoordinate =
                editableBreakpointLengthCoordinates[0];
            var fourthPointLengthCoordinate = BusbarGeometryUtility.Project(
                fourthCenterPoint,
                axisSetting.LengthVector);
            var fifthPointLengthCoordinate =
                useSideToSideFixedVerticalFrameLap
                    ? fourthPointLengthCoordinate
                    : firstBreakpointLengthCoordinate + busbarWidth / 2d;
            FaceToWallPreviewTrace.Info(
                useSideToSideFixedVerticalFrameLap
                    ? "侧对侧第一种-柜顶首段垂直搭接定位"
                    : "柜顶连接G排规划-首断点换算",
                "相=" + GetPhaseName(phaseIndex) +
                "，首基架长度轴最小值=" +
                firstFrameMinimumLengthCoordinate.ToString("0.###") +
                "，界面相对断点=" +
                breakpointPositions[0].ToString("0.###") +
                "，模型断点=" +
                firstBreakpointLengthCoordinate.ToString("0.###") +
                "，端头越过基架绝缘子中心=" +
                (busbarWidth / 2d).ToString("0.###") +
                "，首段末点长度坐标=" +
                fifthPointLengthCoordinate.ToString("0.###") +
                "，最终折弯点长度坐标=" +
                fourthPointLengthCoordinate.ToString("0.###") +
                "，首基架绝缘子确定高度=" +
                frameStackHeightCoordinate.ToString("0.###") +
                "，首段末点高度=" +
                cabinetConnectionHeightCoordinate.ToString("0.###"));
            if (!useActualInsulatorPositions &&
                !useSideToSideFixedVerticalFrameLap &&
                fifthPointLengthCoordinate <=
                fourthPointLengthCoordinate + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相柜顶连接段按首断点延长半个排宽后的末点坐标 {1:0.###} 未位于最终折弯点 {2:0.###} 的长度正方向，请向长度正方向调整断点1。",
                        GetPhaseName(phaseIndex),
                        fifthPointLengthCoordinate,
                        fourthPointLengthCoordinate));
            }

            var fifthCenterPoint = BusbarGeometryUtility.WithProjection(
                fourthCenterPoint,
                axisSetting.LengthVector,
                fifthPointLengthCoordinate);

            var groupCenterlinePoints = new List<Point3d>
            {
                firstCenterPoint.Copy(),
                secondCenterPoint.Copy(),
                thirdCenterPoint.Copy(),
                fourthCenterPoint.Copy(),
                fifthCenterPoint.Copy()
            };
            if (groupCenterlinePoints.Count != 5 || groupCenterlinePoints.Any(point => point == null))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相柜顶连接排没有形成完整的 5 点中心线路径。");
            }
            FaceToWallPreviewTrace.Info(
                "柜顶连接G排规划-五点路径",
                "相=" + GetPhaseName(phaseIndex) +
                "，参数叠排数=" + singlePhaseStackCount +
                "，识别到的柜顶伸出排数=" + sourcePhaseBusbars.Count +
                "，叠排包络厚度=" + stackEnvelopeThickness.ToString("0.###") +
                "，折弯余量=" + bendAllowance.ToString("0.###") +
                "，叠排端部折弯余量=" + stackBendAllowance.ToString("0.###") +
                "，柜顶搭接=" + busbarWidth.ToString("0.###") +
                "x" + busbarWidth.ToString("0.###") +
                "，基座圆中心线搭接长度=" + (baseCircleHalfSpan * 2d).ToString("0.###") +
                "，首基架长度轴最小值=" +
                firstFrameMinimumLengthCoordinate.ToString("0.###") +
                "，首断点界面相对位置=" +
                breakpointPositions[0].ToString("0.###") +
                "，首断点模型坐标=" +
                firstBreakpointLengthCoordinate.ToString("0.###") +
                "，首段末点长度坐标=" +
                fifthPointLengthCoordinate.ToString("0.###") +
                "，首基架绝缘子确定高度=" +
                frameStackHeightCoordinate.ToString("0.###") +
                "，首段末点高度=" +
                cabinetConnectionHeightCoordinate.ToString("0.###") +
                "，首段抬高=" +
                (cabinetConnectionHeightOffsetInThicknesses *
                 busbarThickness).ToString("0.###") +
                "，首段越过断点=" + (busbarWidth / 2d).ToString("0.###") +
                "，固定伸出排中心到搭接排中心偏移=" +
                generatedStackLapSurfaceOffset.ToString("0.###") +
                "，面对墙长度定位=" +
                (useInsulatorFaceForLengthPosition
                    ? "绝缘子配合面+叠排半厚度"
                    : "通用绝缘子圆边界") +
                "，基座绝缘子近B侧边界=" +
                baseInsulatorNearBCircleEdgeLengthCoordinate.ToString("0.###") +
                "，3点目标长度=" +
                thirdPointLengthCoordinate.ToString("0.###") +
                "，3点目标高度=" +
                thirdPointHeightCoordinate.ToString("0.###") +
                "，P1=" + FormatPoint(firstCenterPoint) +
                "，P2=" + FormatPoint(secondCenterPoint) +
                "，P3=" + FormatPoint(thirdCenterPoint) +
                "，P4=" + FormatPoint(fourthCenterPoint) +
                "，P5=" + FormatPoint(fifthCenterPoint));

            var groupCenterlineSetting = new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = GetPhaseName(phaseIndex),
                StartBusbarVector = startThicknessDirection.Copy(),
                WidthVector = startWidthDirection.Copy(),
                PointList = groupCenterlinePoints,
                BusBarWidth = busbarWidth,
                BusBarThickness = busbarThickness,
                BarBendingRadius = bendingRadius,
                BendAllowance = bendAllowance,
                StartLapWidth = busbarWidth,
                EndLapWidth = busbarWidth
            };
            var stackMemberOffsets = CalculateStackMemberOffsets(
                singlePhaseStackCount,
                busbarThickness);
            routeContext = new FaceToWallPhaseRouteContext
            {
                PhaseIndex = phaseIndex,
                OrdinaryRouteReferencePoint =
                    elevatedRouteHeightCoordinate.HasValue
                        ? elevatedOrdinaryRouteReferencePoint.Copy()
                        : ordinaryRouteReferencePoint.Copy(),
                RouteWidthDirection = startWidthDirection.Copy(),
                RouteThicknessDirection =
                    calculatedEndThicknessDirection.Copy(),
                FrameOutwardNormal = frameOutwardNormal.Copy(),
                BusbarWidth = busbarWidth,
                BusbarThickness = busbarThickness,
                BendingRadius = bendingRadius,
                BendAllowance = bendAllowance,
                StackMemberOffsets = stackMemberOffsets
            };
            var phaseGeneratedBusbarGroups =
                new List<IReadOnlyList<PlanarBendBusbarSetting>>
                {
                    CreateStackMemberSettings(
                        groupCenterlineSetting,
                        stackMemberOffsets,
                        phaseIndex)
                };

            if (!includeOrdinarySegments)
            {
                return phaseGeneratedBusbarGroups.AsReadOnly();
            }

            var algorithmStartLengthCoordinate =
                algorithmContainmentNearLengthCoordinate + busbarWidth;
            if (algorithmStartLengthCoordinate >=
                algorithmContainmentFarLengthCoordinate - GeometryTolerance)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相算法始端普通段伸入允许范围一个排宽后达到 {1:0.###}，已超过允许范围远端 {2:0.###}。",
                        GetPhaseName(phaseIndex),
                        algorithmStartLengthCoordinate,
                        algorithmContainmentFarLengthCoordinate));
            }

            if (verticalRiseOrdinarySegmentIndex.HasValue &&
                (verticalRiseOrdinarySegmentIndex.Value < 0 ||
                 verticalRiseOrdinarySegmentIndex.Value >=
                    editableBreakpointLengthCoordinates.Count))
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相抬升段索引 {1} 超出普通段数量 {2}。",
                        GetPhaseName(phaseIndex),
                        verticalRiseOrdinarySegmentIndex.Value + 1,
                        editableBreakpointLengthCoordinates.Count));
            }

            for (var ordinarySegmentIndex = 0;
                ordinarySegmentIndex < editableBreakpointLengthCoordinates.Count;
                ordinarySegmentIndex++)
            {
                var ordinarySegmentStartLengthCoordinate =
                    editableBreakpointLengthCoordinates[ordinarySegmentIndex] -
                    busbarWidth / 2d;
                var ordinarySegmentEndLengthCoordinate =
                    ordinarySegmentIndex <
                    editableBreakpointLengthCoordinates.Count - 1
                        ? editableBreakpointLengthCoordinates[
                            ordinarySegmentIndex + 1] + busbarWidth / 2d
                        : algorithmStartLengthCoordinate;
                var ordinarySegmentLength = ordinarySegmentEndLengthCoordinate -
                    ordinarySegmentStartLengthCoordinate;
                if (ordinarySegmentLength <= GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "{0}相柜侧普通第{1}段长度 {2:0.###} 无效，请检查断点位置。",
                            GetPhaseName(phaseIndex),
                            ordinarySegmentIndex + 1,
                            ordinarySegmentLength));
                }

                var useBridgeCenterline = ordinarySegmentIndex % 2 == 1;
                var isVerticalRiseSegment =
                    verticalRiseOrdinarySegmentIndex.HasValue &&
                    ordinarySegmentIndex ==
                    verticalRiseOrdinarySegmentIndex.Value;
                var useElevatedOrdinaryRoute =
                    verticalRiseOrdinarySegmentIndex.HasValue &&
                    ordinarySegmentIndex >
                    verticalRiseOrdinarySegmentIndex.Value;
                var segmentOrdinaryRouteReferencePoint =
                    useElevatedOrdinaryRoute
                        ? elevatedOrdinaryRouteReferencePoint
                        : ordinaryRouteReferencePoint;
                var ordinarySegmentCenterlineSetting =
                    CreateOrdinarySegmentCenterlineSetting(
                        segmentOrdinaryRouteReferencePoint,
                        axisSetting,
                        startWidthDirection,
                        calculatedEndThicknessDirection,
                        ordinarySegmentStartLengthCoordinate,
                        ordinarySegmentEndLengthCoordinate,
                        busbarWidth,
                        busbarThickness,
                        bendingRadius,
                        bendAllowance,
                        phaseIndex,
                        ordinarySegmentIndex,
                        useBridgeCenterline);
                if (!isVerticalRiseSegment)
                {
                    phaseGeneratedBusbarGroups.Add(
                        useBridgeCenterline
                            ? CreateBridgeStackMemberSettings(
                                ordinarySegmentCenterlineSetting,
                                stackMemberOffsets,
                                frameOutwardNormal,
                                phaseIndex)
                            : CreateStackMemberSettings(
                                ordinarySegmentCenterlineSetting,
                                stackMemberOffsets,
                                phaseIndex));
                    continue;
                }

                var elevatedSegmentCenterlineSetting =
                    CreateOrdinarySegmentCenterlineSetting(
                        elevatedOrdinaryRouteReferencePoint,
                        axisSetting,
                        startWidthDirection,
                        calculatedEndThicknessDirection,
                        ordinarySegmentStartLengthCoordinate,
                        ordinarySegmentEndLengthCoordinate,
                        busbarWidth,
                        busbarThickness,
                        bendingRadius,
                        bendAllowance,
                        phaseIndex,
                        ordinarySegmentIndex,
                        useBridgeCenterline);
                phaseGeneratedBusbarGroups.Add(
                    CreateVerticalRiseStackMemberSettings(
                        ordinarySegmentCenterlineSetting,
                        elevatedSegmentCenterlineSetting,
                        stackMemberOffsets,
                        frameOutwardNormal,
                        axisSetting,
                        phaseIndex,
                        ordinarySegmentIndex,
                        verticalRiseLengthCoordinate.Value,
                        useBridgeCenterline));
            }

            FaceToWallPreviewTrace.Info(
                "面对墙单相固定普通段规划",
                "相=" + GetPhaseName(phaseIndex) +
                "，可编辑断点=" + string.Join(
                    "/",
                    editableBreakpointLengthCoordinates.Select(
                        breakpointCoordinate =>
                            breakpointCoordinate.ToString("0.###"))) +
                "，算法始端坐标=" +
                algorithmStartLengthCoordinate.ToString("0.###") +
                "，抬升段=" +
                (verticalRiseOrdinarySegmentIndex.HasValue
                    ? (verticalRiseOrdinarySegmentIndex.Value + 1).ToString()
                    : "无") +
                "，物理段数=" + phaseGeneratedBusbarGroups.Count);
            return phaseGeneratedBusbarGroups.AsReadOnly();
        }

        private static IReadOnlyList<double> NormalizeBreakpointPositions(
            IEnumerable<double> sourceBreakpointPositions)
        {
            var normalizedBreakpointPositions =
                (sourceBreakpointPositions ?? Enumerable.Empty<double>())
                .ToList();
            if (normalizedBreakpointPositions.Count < 2)
            {
                throw new InvalidOperationException(
                    "面对墙至少需要起始断点和固定末端断点。");
            }

            for (var breakpointIndex = 0;
                breakpointIndex < normalizedBreakpointPositions.Count;
                breakpointIndex++)
            {
                var breakpointPosition =
                    normalizedBreakpointPositions[breakpointIndex];
                if (double.IsNaN(breakpointPosition) ||
                    double.IsInfinity(breakpointPosition))
                {
                    throw new InvalidOperationException(
                        "断点" + (breakpointIndex + 1) +
                        "的位置不是有效数值。");
                }

                if (breakpointIndex > 0 &&
                    breakpointPosition <=
                    normalizedBreakpointPositions[breakpointIndex - 1] +
                    GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "断点" + (breakpointIndex + 1) +
                        "必须位于断点" + breakpointIndex +
                        "的长度正方向。" );
                }
            }

            return normalizedBreakpointPositions.AsReadOnly();
        }

        /// <summary>
        /// 将界面上相对全部基架长度起点的断点位置换算到装配体模型坐标。
        /// 界面零点对应长度轴最小位置的首个基架最小坐标；相对位置允许为负，
        /// 负值表示断点进入柜侧基座区域。
        /// </summary>
        internal static double ConvertRelativeBreakpointToModelLengthCoordinate(
            double firstFrameMinimumLengthCoordinate,
            double relativeBreakpointPosition)
        {
            if (double.IsNaN(firstFrameMinimumLengthCoordinate) ||
                double.IsInfinity(firstFrameMinimumLengthCoordinate))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(firstFrameMinimumLengthCoordinate),
                    "首个基架的长度轴最小坐标必须是有效数值。");
            }

            if (double.IsNaN(relativeBreakpointPosition) ||
                double.IsInfinity(relativeBreakpointPosition))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(relativeBreakpointPosition),
                    "断点相对位置必须是有效数值。");
            }

            return firstFrameMinimumLengthCoordinate + relativeBreakpointPosition;
        }

        private static PlanarBendBusbarSetting
            CreateOrdinarySegmentCenterlineSetting(
            Point3d ordinaryRouteReferencePoint,
            BusbarBridgeAxisSetting axisSetting,
            Vector3d widthDirection,
            Vector3d thicknessDirection,
            double startLengthCoordinate,
            double endLengthCoordinate,
            double busbarWidth,
            double busbarThickness,
            double bendingRadius,
            double bendAllowance,
            int phaseIndex,
            int ordinarySegmentIndex,
            bool useBridgeCenterline)
        {
            if (ordinaryRouteReferencePoint == null)
            {
                throw new ArgumentNullException(nameof(ordinaryRouteReferencePoint));
            }

            var ordinarySegmentLength =
                endLengthCoordinate - startLengthCoordinate;
            if (ordinarySegmentLength <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "普通段的长度轴范围无效。");
            }

            if (useBridgeCenterline)
            {
                var bridgeLowestEdgeStraightLength =
                    busbarWidth + BridgeLowestEdgeStraightAdditionalLength;
                var minimumBridgeLength = 2d *
                    (bridgeLowestEdgeStraightLength +
                     BridgeInnermostTransitionProjectionLength);
                if (ordinarySegmentLength <=
                    minimumBridgeLength + GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "{0}相柜侧普通第{1}段需要生成六点桥式排，但当前含搭接长度仅 {2:0.###} mm，不足桥式两端所需的 {3:0.###} mm；请调整相邻断点。",
                            GetPhaseName(phaseIndex),
                            ordinarySegmentIndex + 1,
                            ordinarySegmentLength,
                            minimumBridgeLength));
                }
            }

            var ordinaryStartPoint = BusbarGeometryUtility.WithProjection(
                ordinaryRouteReferencePoint,
                axisSetting.LengthVector,
                startLengthCoordinate);
            var ordinaryEndPoint = BusbarGeometryUtility.WithProjection(
                ordinaryRouteReferencePoint,
                axisSetting.LengthVector,
                endLengthCoordinate);
            var ordinaryCenterlinePoints = new List<Point3d>();
            if (!useBridgeCenterline)
            {
                ordinaryCenterlinePoints.Add(ordinaryStartPoint);
                ordinaryCenterlinePoints.Add(ordinaryEndPoint);
            }
            else
            {
                ordinaryCenterlinePoints.AddRange(
                    CreateBridgeReferenceCenterlinePoints(
                        ordinaryStartPoint,
                        ordinaryEndPoint,
                        axisSetting.LengthVector,
                        axisSetting.HeightVector,
                        busbarWidth,
                        busbarThickness));
            }

            FaceToWallPreviewTrace.Info(
                "面对墙普通段中心线",
                "相=" + GetPhaseName(phaseIndex) +
                "，段=" + (ordinarySegmentIndex + 1) +
                "，类型=" + (useBridgeCenterline ? "六点桥式" : "I排") +
                "，起点=" + FormatPoint(ordinaryCenterlinePoints[0]) +
                "，终点=" + FormatPoint(
                    ordinaryCenterlinePoints[
                        ordinaryCenterlinePoints.Count - 1]));
            return new PlanarBendBusbarSetting
            {
                IsCenter = true,
                GroupKey = GetPhaseName(phaseIndex),
                StartBusbarVector = thicknessDirection.Copy(),
                WidthVector = widthDirection.Copy(),
                PointList = ordinaryCenterlinePoints,
                BusBarWidth = busbarWidth,
                BusBarThickness = busbarThickness,
                BarBendingRadius = bendingRadius,
                BendAllowance = bendAllowance,
                StartLapWidth = busbarWidth,
                EndLapWidth = busbarWidth
            };
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            CreateVerticalRiseStackMemberSettings(
            PlanarBendBusbarSetting lowerRouteCenterlineSetting,
            PlanarBendBusbarSetting elevatedRouteCenterlineSetting,
            IReadOnlyList<double> stackMemberOffsets,
            Vector3d bridgeStackOutwardNormal,
            BusbarBridgeAxisSetting axisSetting,
            int phaseIndex,
            int ordinarySegmentIndex,
            double verticalRiseLengthCoordinate,
            bool useBridgeCenterline,
            bool useStackEnvelopeGeometry = true)
        {
            if (lowerRouteCenterlineSetting == null ||
                elevatedRouteCenterlineSetting == null)
            {
                throw new ArgumentNullException(
                    lowerRouteCenterlineSetting == null
                        ? nameof(lowerRouteCenterlineSetting)
                        : nameof(elevatedRouteCenterlineSetting));
            }

            if (stackMemberOffsets == null ||
                stackMemberOffsets.Count == 0)
            {
                throw new ArgumentException(
                    "面对墙抬升段缺少叠排成员偏移。",
                    nameof(stackMemberOffsets));
            }

            if (useStackEnvelopeGeometry && stackMemberOffsets.Count > 1)
            {
                var singleBusbarThickness =
                    lowerRouteCenterlineSetting.BusBarThickness;
                if (singleBusbarThickness <= 0d ||
                    double.IsNaN(singleBusbarThickness) ||
                    double.IsInfinity(singleBusbarThickness))
                {
                    throw new InvalidOperationException(
                        "面对墙立弯叠排缺少有效的单根排厚度。");
                }

                var stackEnvelopeThickness =
                    (2d * stackMemberOffsets.Count - 1d) *
                    singleBusbarThickness;
                var stackEnvelopeLowerRoute =
                    lowerRouteCenterlineSetting.Copy();
                stackEnvelopeLowerRoute.BusBarThickness =
                    stackEnvelopeThickness;
                var stackEnvelopeElevatedRoute =
                    elevatedRouteCenterlineSetting.Copy();
                stackEnvelopeElevatedRoute.BusBarThickness =
                    stackEnvelopeThickness;
                var stackEnvelopeSettings =
                    CreateVerticalRiseStackMemberSettings(
                        stackEnvelopeLowerRoute,
                        stackEnvelopeElevatedRoute,
                        new[] { 0d },
                        bridgeStackOutwardNormal,
                        axisSetting,
                        phaseIndex,
                        ordinarySegmentIndex,
                        verticalRiseLengthCoordinate,
                        useBridgeCenterline,
                        false);
                return SplitStackEnvelopeVerticalRiseSettings(
                    stackEnvelopeSettings,
                    stackMemberOffsets,
                    singleBusbarThickness,
                    phaseIndex);
            }

            IReadOnlyList<PlanarBendBusbarSetting> lowerRouteMembers;
            IReadOnlyList<PlanarBendBusbarSetting> elevatedRouteMembers;
            if (useBridgeCenterline)
            {
                lowerRouteMembers = CreateBridgeStackMemberSettings(
                    lowerRouteCenterlineSetting,
                    stackMemberOffsets,
                    bridgeStackOutwardNormal,
                    phaseIndex);
                elevatedRouteMembers = CreateBridgeStackMemberSettings(
                    elevatedRouteCenterlineSetting,
                    stackMemberOffsets,
                    bridgeStackOutwardNormal,
                    phaseIndex);
            }
            else
            {
                lowerRouteMembers = CreateStackMemberSettings(
                    lowerRouteCenterlineSetting,
                    stackMemberOffsets,
                    phaseIndex);
                elevatedRouteMembers = CreateStackMemberSettings(
                    elevatedRouteCenterlineSetting,
                    stackMemberOffsets,
                    phaseIndex);
            }

            if (lowerRouteMembers.Count != elevatedRouteMembers.Count)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相抬升段低侧与高侧叠排成员数量不一致。");
            }

            var generatedVerticalRiseSettings =
                new List<PlanarBendBusbarSetting>();
            var normalizedLengthDirection =
                axisSetting.LengthVector.Normalized;
            var normalizedHeightDirection =
                axisSetting.HeightVector.Normalized;
            for (var memberIndex = 0;
                memberIndex < lowerRouteMembers.Count;
                memberIndex++)
            {
                var lowerRouteMember = lowerRouteMembers[memberIndex];
                var elevatedRouteMember = elevatedRouteMembers[memberIndex];
                var verticalRiseMemberSetting = lowerRouteMember.Copy();
                if (useBridgeCenterline)
                {
                    var lowerBridgeInnerPoint =
                        lowerRouteMember.PointList[2];
                    var elevatedBridgeInnerPoint =
                        elevatedRouteMember.PointList[3];
                    var lowerBridgeInnerLengthCoordinate =
                        BusbarGeometryUtility.Project(
                            lowerBridgeInnerPoint,
                            normalizedLengthDirection);
                    var elevatedBridgeInnerLengthCoordinate =
                        BusbarGeometryUtility.Project(
                            elevatedBridgeInnerPoint,
                            normalizedLengthDirection);
                    if (elevatedBridgeInnerLengthCoordinate <=
                        lowerBridgeInnerLengthCoordinate +
                        GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            GetPhaseName(phaseIndex) +
                            "相桥式抬升段两端桥式斜段之间没有可用的垂直抬升空间。");
                    }

                    var verticalRiseTargetLengthCoordinate =
                        verticalRiseLengthCoordinate;
                    if (verticalRiseTargetLengthCoordinate <
                            lowerBridgeInnerLengthCoordinate - GeometryTolerance ||
                        verticalRiseTargetLengthCoordinate >
                            elevatedBridgeInnerLengthCoordinate + GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                "{0}相桥式抬升定位长度 {1:0.###} 不在倒数第二个绝缘子 B 柜侧边缘折弯范围 {2:0.###}～{3:0.###} 内。",
                                GetPhaseName(phaseIndex),
                                verticalRiseTargetLengthCoordinate,
                                lowerBridgeInnerLengthCoordinate,
                                elevatedBridgeInnerLengthCoordinate));
                    }
                    var lowerVerticalRisePoint =
                        BusbarGeometryUtility.WithProjection(
                            lowerBridgeInnerPoint,
                            normalizedLengthDirection,
                            verticalRiseTargetLengthCoordinate);
                    var elevatedVerticalRisePoint =
                        BusbarGeometryUtility.WithProjection(
                            elevatedBridgeInnerPoint,
                            normalizedLengthDirection,
                            verticalRiseTargetLengthCoordinate);
                    verticalRiseMemberSetting.PointList =
                        lowerRouteMember.PointList.Take(3)
                            .Select(verticalRisePoint =>
                                verticalRisePoint.Copy())
                            .Concat(new[]
                            {
                                lowerVerticalRisePoint,
                                elevatedVerticalRisePoint
                            })
                            .Concat(elevatedRouteMember.PointList.Skip(3)
                                .Select(verticalRisePoint =>
                                    verticalRisePoint.Copy()))
                            .ToList();
                    verticalRiseMemberSetting.PreviewBendPointKinds =
                        new List<BusbarPreviewBendPointKind_e>
                        {
                            BusbarPreviewBendPointKind_e.Planar,
                            BusbarPreviewBendPointKind_e.Planar,
                            BusbarPreviewBendPointKind_e.Planar,
                            BusbarPreviewBendPointKind_e.Planar,
                            BusbarPreviewBendPointKind_e.Vertical,
                            BusbarPreviewBendPointKind_e.Planar,
                            BusbarPreviewBendPointKind_e.Planar,
                            BusbarPreviewBendPointKind_e.Planar
                        };
                }
                else
                {
                    if (lowerRouteMember.PointList == null ||
                        lowerRouteMember.PointList.Count != 2 ||
                        elevatedRouteMember.PointList == null ||
                        elevatedRouteMember.PointList.Count != 2)
                    {
                        throw new InvalidOperationException(
                            GetPhaseName(phaseIndex) +
                            "相直排抬升段必须由低侧和高侧两条直线构造。");
                    }

                    var lowerRouteStartPoint =
                        lowerRouteMember.PointList[0];
                    var elevatedRouteEndPoint =
                        elevatedRouteMember.PointList[1];
                    var verticalEndStraightLength =
                        lowerRouteMember.BusBarWidth;
                    var verticalRiseLengthProjection =
                        BusbarGeometryUtility.Project(
                            elevatedRouteEndPoint,
                            normalizedLengthDirection) -
                        BusbarGeometryUtility.Project(
                            lowerRouteStartPoint,
                            normalizedLengthDirection);
                    if (verticalRiseLengthProjection <=
                        2d * verticalEndStraightLength + GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                "{0}相柜侧普通第{1}段长度 {2:0.###} mm，不足以在垂直抬升前后各保留一个排宽 {3:0.###} mm 的直段。",
                                GetPhaseName(phaseIndex),
                                ordinarySegmentIndex + 1,
                                verticalRiseLengthProjection,
                                verticalEndStraightLength));
                    }

                    var lowerRouteStartLengthCoordinate =
                        BusbarGeometryUtility.Project(
                            lowerRouteStartPoint,
                            normalizedLengthDirection);
                    var elevatedRouteEndLengthCoordinate =
                        BusbarGeometryUtility.Project(
                            elevatedRouteEndPoint,
                            normalizedLengthDirection);
                    var verticalRiseTargetLengthCoordinate =
                        verticalRiseLengthCoordinate;
                    var minimumVerticalRiseTargetLengthCoordinate =
                        Math.Min(
                            lowerRouteStartLengthCoordinate,
                            elevatedRouteEndLengthCoordinate) +
                        verticalEndStraightLength;
                    var maximumVerticalRiseTargetLengthCoordinate =
                        Math.Max(
                            lowerRouteStartLengthCoordinate,
                            elevatedRouteEndLengthCoordinate) -
                        verticalEndStraightLength;
                    if (verticalRiseTargetLengthCoordinate <=
                            minimumVerticalRiseTargetLengthCoordinate +
                            GeometryTolerance ||
                        verticalRiseTargetLengthCoordinate >=
                            maximumVerticalRiseTargetLengthCoordinate -
                            GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                "{0}相柜侧普通第{1}段的倒数第二个绝缘子抬升定位长度 {2:0.###} 无法在两端各保留一个排宽直段，允许范围为 {3:0.###}～{4:0.###}。",
                                GetPhaseName(phaseIndex),
                                ordinarySegmentIndex + 1,
                                verticalRiseTargetLengthCoordinate,
                                minimumVerticalRiseTargetLengthCoordinate,
                                maximumVerticalRiseTargetLengthCoordinate));
                    }
                    var firstVerticalRiseBendPoint =
                        BusbarGeometryUtility.WithProjection(
                            lowerRouteStartPoint,
                            normalizedLengthDirection,
                            verticalRiseTargetLengthCoordinate);
                    var secondVerticalRiseBendPoint =
                        BusbarGeometryUtility.WithProjection(
                            elevatedRouteEndPoint,
                            normalizedLengthDirection,
                            verticalRiseTargetLengthCoordinate);
                    verticalRiseMemberSetting.PointList =
                        new List<Point3d>
                        {
                            lowerRouteStartPoint.Copy(),
                            firstVerticalRiseBendPoint,
                            secondVerticalRiseBendPoint,
                            elevatedRouteEndPoint.Copy()
                        };
                    verticalRiseMemberSetting.PreviewBendPointKinds =
                        new List<BusbarPreviewBendPointKind_e>
                        {
                            BusbarPreviewBendPointKind_e.Planar,
                            BusbarPreviewBendPointKind_e.Planar,
                            BusbarPreviewBendPointKind_e.Vertical,
                            BusbarPreviewBendPointKind_e.Planar
                        };
                }

                var verticalRiseVector = new Vector3d(
                    verticalRiseMemberSetting.PointList[
                        useBridgeCenterline ? 3 : 1],
                    verticalRiseMemberSetting.PointList[
                        useBridgeCenterline ? 4 : 2]);
                var memberHeightRise = verticalRiseVector.Dot(
                    normalizedHeightDirection);
                if (memberHeightRise <=
                    lowerRouteMember.BusBarThickness + GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "{0}相柜侧普通第{1}段第{2}根排的实际抬升 {3:0.###} mm 未超过一个排厚 {4:0.###} mm。",
                            GetPhaseName(phaseIndex),
                            ordinarySegmentIndex + 1,
                            memberIndex + 1,
                            memberHeightRise,
                            lowerRouteMember.BusBarThickness));
                }

                verticalRiseMemberSetting.VerticalBendingRadius =
                    lowerRouteMember.BusBarWidth / 4d;
                verticalRiseMemberSetting.IsVerticalBend = true;
                generatedVerticalRiseSettings.Add(
                    verticalRiseMemberSetting);
            }

            FaceToWallPreviewTrace.Info(
                "面对墙垂直抬升段-生成",
                "相=" + GetPhaseName(phaseIndex) +
                "，普通段=" + (ordinarySegmentIndex + 1) +
                "，原类型=" +
                (useBridgeCenterline ? "六点桥式" : "I排") +
                "，倒数第二个绝缘子 B 柜侧抬升定位长度=" +
                verticalRiseLengthCoordinate.ToString("0.###") +
                "，成员数=" + generatedVerticalRiseSettings.Count);
            return generatedVerticalRiseSettings.AsReadOnly();
        }

        /// <summary>
        /// 构造六点桥式参考路径。最下层两端直段固定为“排宽 + 40 mm”，
        /// 再由最内侧排以固定 50 mm 长度轴投影下降一个排厚。
        /// </summary>
        internal static IReadOnlyList<Point3d>
            CreateBridgeReferenceCenterlinePoints(
            Point3d ordinaryStartPoint,
            Point3d ordinaryEndPoint,
            Vector3d lengthDirection,
            Vector3d heightDirection,
            double busbarWidth,
            double busbarThickness)
        {
            return CreateBridgeReferenceCenterlinePoints(
                ordinaryStartPoint,
                ordinaryEndPoint,
                lengthDirection,
                heightDirection,
                busbarWidth,
                busbarThickness,
                busbarThickness,
                busbarThickness);
        }

        public static IReadOnlyList<Point3d>
            CreateBridgeReferenceCenterlinePoints(
            Point3d ordinaryStartPoint,
            Point3d ordinaryEndPoint,
            Vector3d lengthDirection,
            Vector3d bridgeOffsetDirection,
            double busbarWidth,
            double busbarThickness,
            double bridgeStartOffsetDistance,
            double bridgeEndOffsetDistance)
        {
            if (ordinaryStartPoint == null)
            {
                throw new ArgumentNullException(nameof(ordinaryStartPoint));
            }

            if (ordinaryEndPoint == null)
            {
                throw new ArgumentNullException(nameof(ordinaryEndPoint));
            }

            if (lengthDirection == null ||
                lengthDirection.Norm <= GeometryTolerance)
            {
                throw new ArgumentException(
                    "桥式排长度方向无效。",
                    nameof(lengthDirection));
            }

            if (bridgeOffsetDirection == null ||
                bridgeOffsetDirection.Norm <= GeometryTolerance)
            {
                throw new ArgumentException(
                    "桥式排偏移方向无效。",
                    nameof(bridgeOffsetDirection));
            }

            if (double.IsNaN(busbarWidth) ||
                double.IsInfinity(busbarWidth) ||
                busbarWidth <= GeometryTolerance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(busbarWidth),
                    "桥式排宽度必须大于 0。");
            }

            if (double.IsNaN(busbarThickness) ||
                double.IsInfinity(busbarThickness) ||
                busbarThickness <= GeometryTolerance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(busbarThickness),
                    "桥式排厚度必须大于 0。");
            }
            if (double.IsNaN(bridgeStartOffsetDistance) ||
                double.IsInfinity(bridgeStartOffsetDistance) ||
                double.IsNaN(bridgeEndOffsetDistance) ||
                double.IsInfinity(bridgeEndOffsetDistance))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bridgeStartOffsetDistance),
                    "桥式排两端偏移距离必须是有效数值。");
            }

            var normalizedLengthDirection = lengthDirection.Normalized;
            var normalizedBridgeOffsetDirection =
                bridgeOffsetDirection.Normalized;
            var bridgeLapStartPoint = PlanarBendBusbarSetting.Translate(
                ordinaryStartPoint,
                normalizedBridgeOffsetDirection,
                bridgeStartOffsetDistance);
            var bridgeLapEndPoint = PlanarBendBusbarSetting.Translate(
                ordinaryEndPoint,
                normalizedBridgeOffsetDirection,
                bridgeEndOffsetDistance);
            var bridgeLowestEdgeStraightLength =
                busbarWidth + BridgeLowestEdgeStraightAdditionalLength;
            var innermostTransitionEndDistance =
                bridgeLowestEdgeStraightLength +
                BridgeInnermostTransitionProjectionLength;
            return new List<Point3d>
            {
                bridgeLapStartPoint,
                PlanarBendBusbarSetting.Translate(
                    bridgeLapStartPoint,
                    normalizedLengthDirection,
                    bridgeLowestEdgeStraightLength),
                PlanarBendBusbarSetting.Translate(
                    PlanarBendBusbarSetting.Translate(
                        bridgeLapStartPoint,
                        normalizedLengthDirection,
                        innermostTransitionEndDistance),
                    normalizedBridgeOffsetDirection,
                    -bridgeStartOffsetDistance),
                PlanarBendBusbarSetting.Translate(
                    PlanarBendBusbarSetting.Translate(
                        bridgeLapEndPoint,
                        normalizedLengthDirection,
                        -innermostTransitionEndDistance),
                    normalizedBridgeOffsetDirection,
                    -bridgeEndOffsetDistance),
                PlanarBendBusbarSetting.Translate(
                    bridgeLapEndPoint,
                    normalizedLengthDirection,
                    -bridgeLowestEdgeStraightLength),
                bridgeLapEndPoint
            }.AsReadOnly();
        }

        private static Point3d ActualInsulatorEntryBendPoint(
            InsulatorConnectionInfo insulator, Point3d stackReference, Point3d previousPoint,
            BusbarBridgeAxisSetting axis, double heightTravelSign, double bendReserve,
            int phaseIndex, out double circleHalfSpan)
        {
            if (!BusbarGeometryUtility.AreParallel(insulator.ConnectionPlane.Normal, axis.LengthVector))
                throw new InvalidOperationException(GetPhaseName(phaseIndex) +
                    "相基座绝缘子实际配合面法向与柜端排长方向不平行，不能规划安装直段。");
            circleHalfSpan = GetConnectionCircleHalfSpan(insulator, stackReference,
                axis.WidthVector, "基座", phaseIndex);
            var entryHeight = BusbarGeometryUtility.Project(insulator.ConnectionCircleCenter, axis.HeightVector) -
                heightTravelSign * (circleHalfSpan + bendReserve);
            var previousHeight = BusbarGeometryUtility.Project(previousPoint, axis.HeightVector);
            if (heightTravelSign * (entryHeight - previousHeight) <= GeometryTolerance)
                throw new InvalidOperationException(GetPhaseName(phaseIndex) +
                    "相柜顶搭接结束点到基座绝缘子安装圆之间没有足够空间保留叠排折弯余量。");
            // 只调整沿安装直段的折弯高度；实际安装面、横向位置及法向半厚补偿均保留。
            return BusbarGeometryUtility.WithProjection(stackReference, axis.HeightVector, entryHeight);
        }

        internal static IReadOnlyList<PlanarBendBusbarSetting>
            CreateStackMemberSettings(
            PlanarBendBusbarSetting groupCenterlineSetting,
            IReadOnlyList<double> stackMemberOffsets,
            int phaseIndex)
        {
            if (groupCenterlineSetting == null)
            {
                throw new ArgumentNullException(nameof(groupCenterlineSetting));
            }

            var generatedSettings = new List<PlanarBendBusbarSetting>();
            for (var memberIndex = 0;
                memberIndex < stackMemberOffsets.Count;
                memberIndex++)
            {
                var memberSetting = groupCenterlineSetting.Copy();
                memberSetting.GroupKey =
                    GetPhaseName(phaseIndex) + "-" + (memberIndex + 1);
                memberSetting.PointList =
                    GetOffsetCenterlinePreservingPointCount(
                        groupCenterlineSetting,
                        stackMemberOffsets[memberIndex]);
                generatedSettings.Add(memberSetting);
            }

            return generatedSettings.AsReadOnly();
        }

        /// <summary>
        /// 六点桥式排必须先以最内侧排确定“搭接区 + 50 mm 斜段”，
        /// 其余排再从该路径作平行偏移，不能从叠排中心向两侧对称偏移。
        /// </summary>
        internal static IReadOnlyList<PlanarBendBusbarSetting>
            CreateBridgeStackMemberSettings(
            PlanarBendBusbarSetting groupCenterlineSetting,
            IReadOnlyList<double> stackMemberOffsets,
            Vector3d bridgeStackOutwardNormal,
            int phaseIndex)
        {
            if (groupCenterlineSetting == null)
            {
                throw new ArgumentNullException(nameof(groupCenterlineSetting));
            }

            if (stackMemberOffsets == null || stackMemberOffsets.Count == 0)
            {
                throw new ArgumentException(
                    "桥式叠排缺少成员偏移。",
                    nameof(stackMemberOffsets));
            }

            if (groupCenterlineSetting.StartBusbarVector == null ||
                groupCenterlineSetting.StartBusbarVector.Norm <=
                GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "桥式叠排的排厚方向无效。");
            }

            if (groupCenterlineSetting.WidthVector == null ||
                groupCenterlineSetting.WidthVector.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "桥式叠排的排宽方向无效。");
            }

            if (bridgeStackOutwardNormal == null ||
                bridgeStackOutwardNormal.Norm <= GeometryTolerance)
            {
                throw new ArgumentException(
                    "桥式叠排缺少基架绝缘子外法向。",
                    nameof(bridgeStackOutwardNormal));
            }

            if (groupCenterlineSetting.PointList == null ||
                groupCenterlineSetting.PointList.Count != 6 ||
                groupCenterlineSetting.PointList.Any(
                    bridgeReferencePoint => bridgeReferencePoint == null))
            {
                throw new InvalidOperationException(
                    "桥式叠排必须使用完整的六点参考路径。");
            }

            if (stackMemberOffsets.Any(memberOffset =>
                    double.IsNaN(memberOffset) ||
                    double.IsInfinity(memberOffset)))
            {
                throw new InvalidOperationException(
                    "桥式叠排包含无效的成员偏移。");
            }

            var normalizedBridgeThicknessDirection = groupCenterlineSetting
                .StartBusbarVector.Normalized;
            var normalizedBridgeStackOutwardNormal =
                bridgeStackOutwardNormal.Normalized;
            if (!BusbarGeometryUtility.AreParallel(
                    normalizedBridgeThicknessDirection,
                    normalizedBridgeStackOutwardNormal))
            {
                throw new InvalidOperationException(
                    "桥式叠排的排厚方向与基架绝缘子外法向不平行。");
            }

            var bridgeThicknessDirectionPointsOutward =
                normalizedBridgeThicknessDirection.Dot(
                    normalizedBridgeStackOutwardNormal) > 0d;
            var innermostBridgeMemberOffset =
                bridgeThicknessDirectionPointsOutward
                ? stackMemberOffsets.Min()
                : stackMemberOffsets.Max();
            var bridgeReferenceLengthDirection = new Vector3d(
                groupCenterlineSetting.PointList[0],
                groupCenterlineSetting.PointList[1]);
            if (bridgeReferenceLengthDirection.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "桥式叠排的长度方向无效。");
            }

            var normalizedBridgeLengthDirection =
                bridgeReferenceLengthDirection.Normalized;
            var innermostBridgeSetting = groupCenterlineSetting.Copy();
            innermostBridgeSetting.PointList = groupCenterlineSetting.PointList
                .Select(referencePoint => PlanarBendBusbarSetting.Translate(
                    referencePoint,
                    normalizedBridgeThicknessDirection,
                    innermostBridgeMemberOffset))
                .ToList();

            var generatedBridgeSettings =
                new List<PlanarBendBusbarSetting>();
            for (var memberIndex = 0;
                memberIndex < stackMemberOffsets.Count;
                memberIndex++)
            {
                var bridgeMemberSetting = innermostBridgeSetting.Copy();
                bridgeMemberSetting.GroupKey =
                    GetPhaseName(phaseIndex) + "-" + (memberIndex + 1);
                var bridgeMemberRelativeOffset =
                    stackMemberOffsets[memberIndex] -
                    innermostBridgeMemberOffset;
                var bridgeMemberCenterlinePoints =
                    GetOffsetCenterlinePreservingPointCount(
                        innermostBridgeSetting,
                        bridgeMemberRelativeOffset);
                ValidateBridgeMemberCenterlinePoints(
                    bridgeMemberCenterlinePoints,
                    normalizedBridgeLengthDirection,
                    phaseIndex,
                    memberIndex);
                bridgeMemberSetting.PointList = bridgeMemberCenterlinePoints;
                generatedBridgeSettings.Add(bridgeMemberSetting);
            }

            return generatedBridgeSettings.AsReadOnly();
        }

        private static void ValidateBridgeMemberCenterlinePoints(
            IReadOnlyList<Point3d> bridgeMemberCenterlinePoints,
            Vector3d normalizedBridgeLengthDirection,
            int phaseIndex,
            int memberIndex)
        {
            if (bridgeMemberCenterlinePoints == null ||
                bridgeMemberCenterlinePoints.Count != 6 ||
                bridgeMemberCenterlinePoints.Any(
                    bridgeMemberPoint => bridgeMemberPoint == null))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相桥式普通段第" +
                    (memberIndex + 1) + "根叠排没有形成完整的六点路径。");
            }

            for (var bridgeSegmentIndex = 0;
                bridgeSegmentIndex < bridgeMemberCenterlinePoints.Count - 1;
                bridgeSegmentIndex++)
            {
                var bridgeSegmentDirection = new Vector3d(
                    bridgeMemberCenterlinePoints[bridgeSegmentIndex],
                    bridgeMemberCenterlinePoints[bridgeSegmentIndex + 1]);
                var bridgeSegmentLengthProjection =
                    bridgeSegmentDirection.Dot(
                        normalizedBridgeLengthDirection);
                if (bridgeSegmentDirection.Norm <= GeometryTolerance ||
                    bridgeSegmentLengthProjection <= GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        GetPhaseName(phaseIndex) + "相桥式普通段第" +
                        (memberIndex + 1) + "根叠排在平行偏移后第" +
                        (bridgeSegmentIndex + 1) +
                        "段发生重合或反向，断点间距不足以容纳完整桥式叠排。");
                }
            }
        }

        internal static IReadOnlyList<double> CalculateStackMemberOffsets(
            int singlePhaseStackCount,
            double busbarThickness)
        {
            ValidateSinglePhaseStackCount(singlePhaseStackCount);
            if (double.IsNaN(busbarThickness) ||
                double.IsInfinity(busbarThickness) ||
                busbarThickness <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(busbarThickness),
                    "铜排厚度必须大于 0。");
            }

            var stackMemberOffsets = new List<double>(singlePhaseStackCount);
            for (var stackMemberIndex = 0;
                 stackMemberIndex < singlePhaseStackCount;
                 stackMemberIndex++)
            {
                stackMemberOffsets.Add(
                    (2d * stackMemberIndex - (singlePhaseStackCount - 1d)) *
                    busbarThickness);
            }

            return stackMemberOffsets.AsReadOnly();
        }

        internal static double CalculateCabinetTopLapReferenceOffset(
            int singlePhaseStackCount,
            int modeledPhaseBusbarCount,
            double busbarThickness)
        {
            return CalculateCabinetTopLapReferenceOffset(
                singlePhaseStackCount,
                modeledPhaseBusbarCount,
                busbarThickness,
                busbarThickness);
        }

        internal static double CalculateCabinetTopLapReferenceOffset(
            int singlePhaseStackCount,
            int modeledPhaseBusbarCount,
            double generatedBusbarThickness,
            double cabinetTopBusbarThickness)
        {
            ValidateSinglePhaseStackCount(singlePhaseStackCount);
            if (modeledPhaseBusbarCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(modeledPhaseBusbarCount),
                    "柜顶伸出排识别数量必须大于 0。");
            }

            if (double.IsNaN(generatedBusbarThickness) ||
                double.IsInfinity(generatedBusbarThickness) ||
                generatedBusbarThickness <= 0d ||
                double.IsNaN(cabinetTopBusbarThickness) ||
                double.IsInfinity(cabinetTopBusbarThickness) ||
                cabinetTopBusbarThickness <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(generatedBusbarThickness),
                    "铜排厚度必须大于 0。");
            }

            return (singlePhaseStackCount - 1d) *
                       generatedBusbarThickness -
                   modeledPhaseBusbarCount * cabinetTopBusbarThickness;
        }

        internal static void ValidateSinglePhaseStackCount(int singlePhaseStackCount)
        {
            if (singlePhaseStackCount < 1 ||
                singlePhaseStackCount > MaximumSinglePhaseStackCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(singlePhaseStackCount),
                    "单相根数必须在 1 到 4 之间。");
            }
        }

        private static void GetBoundingBoxProjectionRange(
            Point3d minimumPoint,
            Point3d maximumPoint,
            Vector3d projectionDirection,
            out double minimumProjectionCoordinate,
            out double maximumProjectionCoordinate)
        {
            if (minimumPoint == null || maximumPoint == null ||
                projectionDirection == null ||
                projectionDirection.Norm < GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "算法允许范围或长度轴方向无效。");
            }

            var boundingCornerPoints = new[]
            {
                new Point3d(minimumPoint.X, minimumPoint.Y, minimumPoint.Z),
                new Point3d(minimumPoint.X, minimumPoint.Y, maximumPoint.Z),
                new Point3d(minimumPoint.X, maximumPoint.Y, minimumPoint.Z),
                new Point3d(minimumPoint.X, maximumPoint.Y, maximumPoint.Z),
                new Point3d(maximumPoint.X, minimumPoint.Y, minimumPoint.Z),
                new Point3d(maximumPoint.X, minimumPoint.Y, maximumPoint.Z),
                new Point3d(maximumPoint.X, maximumPoint.Y, minimumPoint.Z),
                new Point3d(maximumPoint.X, maximumPoint.Y, maximumPoint.Z)
            };
            var projectionCoordinates = boundingCornerPoints
                .Select(cornerPoint => BusbarGeometryUtility.Project(
                    cornerPoint,
                    projectionDirection))
                .ToList();
            minimumProjectionCoordinate = projectionCoordinates.Min();
            maximumProjectionCoordinate = projectionCoordinates.Max();
        }

        internal static bool ShouldMergeCabinetTopLengthAxisFold(
            double lengthAxisDeviation,
            double busbarThickness,
            int phaseIndex)
        {
            if (double.IsNaN(lengthAxisDeviation) ||
                double.IsInfinity(lengthAxisDeviation))
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相柜顶首段长度轴偏差不是有效数值。");
            }

            if (double.IsNaN(busbarThickness) ||
                double.IsInfinity(busbarThickness) ||
                busbarThickness <= 0d)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相柜顶伸出排厚度无效，无法判断首段形状。");
            }

            var absoluteLengthAxisDeviation = Math.Abs((float)lengthAxisDeviation);
            if (absoluteLengthAxisDeviation <=
                CabinetTopLengthAxisMergeTolerance)
            {
                return true;
            }

            var minimumValidFoldDeviation = (float)busbarThickness / 2f;
            if (absoluteLengthAxisDeviation <=
                minimumValidFoldDeviation)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相柜顶首段在长度轴存在 {1:0.0} mm 模型偏差；仅允许 {2:0.0} mm 以内归并为 L 排，当前偏差又不足以形成有效 G 型折返（需大于 {3:0.0} mm）。请修正柜顶伸出排或基座绝缘子位置。",
                        GetPhaseName(phaseIndex),
                        absoluteLengthAxisDeviation,
                        CabinetTopLengthAxisMergeTolerance,
                        minimumValidFoldDeviation));
            }

            return false;
        }

        // 面对面首段与侧对侧首段直接共用以下连接几何规则，避免校验及容差分叉。
        internal static void ValidateInsulatorConnectionGeometry(
            InsulatorConnectionInfo insulatorConnectionInfo,
            string insulatorHostLabel,
            int phaseIndex)
        {
            var insulatorComponentName = BusbarGeometryUtility.SafeName(
                insulatorConnectionInfo?.Component);
            if (insulatorConnectionInfo?.ConnectionPlane == null)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相{1}绝缘子“{2}”缺少草图“{3}”的配合平面。",
                        GetPhaseName(phaseIndex),
                        insulatorHostLabel,
                        insulatorComponentName,
                        InsulatorConnectionPlaneReader.ConnectionSketchName));
            }

            if (insulatorConnectionInfo.ConnectionCircleCenter == null ||
                double.IsNaN(insulatorConnectionInfo.ConnectionCircleRadius) ||
                double.IsInfinity(insulatorConnectionInfo.ConnectionCircleRadius) ||
                insulatorConnectionInfo.ConnectionCircleRadius <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相{1}绝缘子“{2}”的草图“{3}”没有可用圆形搭接区域。",
                        GetPhaseName(phaseIndex),
                        insulatorHostLabel,
                        insulatorComponentName,
                        InsulatorConnectionPlaneReader.ConnectionSketchName));
            }
        }

        internal static Vector3d ResolveConnectionOutwardNormal(
            InsulatorConnectionInfo insulatorConnectionInfo)
        {
            var connectionPlaneNormal = insulatorConnectionInfo.ConnectionPlane.Normal.Normalized;
            if (insulatorConnectionInfo.ComponentCenter == null ||
                insulatorConnectionInfo.ConnectionCircleCenter == null)
            {
                return connectionPlaneNormal;
            }

            var componentCenterToConnectionSurface = new Vector3d(
                insulatorConnectionInfo.ComponentCenter,
                insulatorConnectionInfo.ConnectionCircleCenter);
            return componentCenterToConnectionSurface.Norm < GeometryTolerance
                ? connectionPlaneNormal
                : BusbarGeometryUtility.Align(
                    connectionPlaneNormal,
                    componentCenterToConnectionSurface);
        }

        private static Point3d TranslateInsulatorConnectionFaceToStackReference(
            InsulatorConnectionInfo insulatorConnectionInfo,
            Vector3d outwardNormal,
            double stackEnvelopeHalfThickness,
            string insulatorHostLabel,
            int phaseIndex)
        {
            ValidateInsulatorConnectionGeometry(
                insulatorConnectionInfo,
                insulatorHostLabel,
                phaseIndex);
            if (insulatorConnectionInfo.ConnectionCircleCenter == null)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相" + insulatorHostLabel +
                    "绝缘子没有可用的配合圆心定位点。 ");
            }

            if (outwardNormal == null || outwardNormal.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相" + insulatorHostLabel +
                    "绝缘子配合面法向无效，无法定位叠排中心。 ");
            }

            return PlanarBendBusbarSetting.Translate(
                insulatorConnectionInfo.ConnectionCircleCenter,
                outwardNormal,
                stackEnvelopeHalfThickness);
        }

        /// <summary>
        /// 求基座绝缘子配合圆所在平面沿法向偏移指定定位距离后，
        /// 与长度方向基准线的交点。面对墙长度方向只使用叠排半厚度定位；
        /// 内侧排折弯余量属于其它折弯方向的几何约束，不能重复加到长度坐标。
        /// 配合面外形的圆心才是绝缘子的真实配合定位点，不能使用草图原点；
        /// 法向量只负责确定配合面的朝向。
        /// </summary>
        private static double CalculateInsulatorFaceStackLengthCoordinate(
            InsulatorConnectionInfo insulatorConnectionInfo,
            Vector3d outwardNormal,
            Vector3d lengthDirection,
            Point3d lengthPositionReferencePoint,
            double insulatorNormalPositionOffset,
            string insulatorHostLabel,
            int phaseIndex)
        {
            ValidateInsulatorConnectionGeometry(
                insulatorConnectionInfo,
                insulatorHostLabel,
                phaseIndex);
            if (insulatorConnectionInfo.ConnectionCircleCenter == null ||
                outwardNormal == null ||
                outwardNormal.Norm <= GeometryTolerance ||
                lengthDirection == null ||
                lengthDirection.Norm <= GeometryTolerance ||
                lengthPositionReferencePoint == null)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相" + insulatorHostLabel +
                    "绝缘子配合圆心或配合面法向不完整，无法确定 3、4 点长度位置。 ");
            }

            var normalizedLengthDirection = lengthDirection.Normalized;
            var normalizedOutwardNormal = outwardNormal.Normalized;
            var lengthDirectionPlaneCoefficient =
                normalizedOutwardNormal.Dot(normalizedLengthDirection);
            if (Math.Abs(lengthDirectionPlaneCoefficient) <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) + "相" + insulatorHostLabel +
                    "绝缘子配合面与长度方向平行，无法唯一确定 3、4 点长度位置。 ");
            }

            var shiftedConnectionFacePoint =
                PlanarBendBusbarSetting.Translate(
                    insulatorConnectionInfo.ConnectionCircleCenter,
                    normalizedOutwardNormal,
                    insulatorNormalPositionOffset);
            var referenceToFacePoint = new Vector3d(
                lengthPositionReferencePoint,
                shiftedConnectionFacePoint);
            var lengthAxisTravel = normalizedOutwardNormal.Dot(
                referenceToFacePoint) /
                lengthDirectionPlaneCoefficient;
            var faceIntersectionPoint = PlanarBendBusbarSetting.Translate(
                lengthPositionReferencePoint,
                normalizedLengthDirection,
                lengthAxisTravel);
            var lengthCoordinate = BusbarGeometryUtility.Project(
                faceIntersectionPoint,
                lengthDirection);
            FaceToWallPreviewTrace.Info(
                "面对墙绝缘子配合面-长度轴求交",
                "相=" + GetPhaseName(phaseIndex) +
                "，配合圆心=" + FormatPoint(
                    insulatorConnectionInfo.ConnectionCircleCenter) +
                "，法向=" + normalizedOutwardNormal +
                "，长度基准线点=" + FormatPoint(
                    lengthPositionReferencePoint) +
                "，法向定位偏移=" +
                insulatorNormalPositionOffset.ToString("0.###") +
                "，法向偏移后长度坐标=" +
                lengthCoordinate.ToString("0.###"));
            return lengthCoordinate;
        }

        internal static double GetConnectionCircleHalfSpan(
            InsulatorConnectionInfo insulatorConnectionInfo,
            Point3d routeCenterlinePoint,
            Vector3d widthDirection,
            string insulatorHostLabel,
            int phaseIndex)
        {
            var routeWidthCoordinate = BusbarGeometryUtility.Project(
                routeCenterlinePoint,
                widthDirection);
            var connectionCircleWidthCoordinate = BusbarGeometryUtility.Project(
                insulatorConnectionInfo.ConnectionCircleCenter,
                widthDirection);
            var routeToCircleWidthOffset = Math.Abs(
                routeWidthCoordinate - connectionCircleWidthCoordinate);
            if (routeToCircleWidthOffset + GeometryTolerance >=
                insulatorConnectionInfo.ConnectionCircleRadius)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相中心线与{1}绝缘子圆心沿宽度方向偏差 {2:0.###}，不在半径 {3:0.###} 的搭接圆内。",
                        GetPhaseName(phaseIndex),
                        insulatorHostLabel,
                        routeToCircleWidthOffset,
                        insulatorConnectionInfo.ConnectionCircleRadius));
            }

            return Math.Sqrt(
                insulatorConnectionInfo.ConnectionCircleRadius *
                insulatorConnectionInfo.ConnectionCircleRadius -
                routeToCircleWidthOffset * routeToCircleWidthOffset);
        }

        /// <summary>
        /// 计算绝缘子配合圆沿指定长度轴的投影半跨度。圆位于配合面内，
        /// 因此当长度轴与配合面法向平行时投影为零；当长度轴位于配合面内
        /// 时投影等于圆半径。
        /// </summary>
        private static double GetConnectionCircleLengthAxisHalfSpan(
            InsulatorConnectionInfo insulatorConnectionInfo,
            Vector3d lengthDirection,
            string insulatorHostLabel,
            int phaseIndex)
        {
            ValidateInsulatorConnectionGeometry(
                insulatorConnectionInfo,
                insulatorHostLabel,
                phaseIndex);
            if (lengthDirection == null ||
                lengthDirection.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    GetPhaseName(phaseIndex) +
                    "相长度轴方向无效，无法确定绝缘子配合圆边界。 ");
            }

            var normalizedLengthDirection = lengthDirection.Normalized;
            var normalizedConnectionPlaneNormal =
                insulatorConnectionInfo.ConnectionPlane.Normal.Normalized;
            var normalProjection = Math.Max(
                -1d,
                Math.Min(
                    1d,
                    normalizedConnectionPlaneNormal.Dot(
                        normalizedLengthDirection)));
            var inPlaneProjectionFactor = Math.Sqrt(
                Math.Max(0d, 1d - normalProjection * normalProjection));
            return insulatorConnectionInfo.ConnectionCircleRadius *
                   inPlaneProjectionFactor;
        }

        internal static Vector3d AverageAlignedVector(IEnumerable<Vector3d> sourceVectors)
        {
            var vectors = sourceVectors
                .Where(vector => vector != null && vector.Norm > 0.001d)
                .Select(vector => vector.Normalized)
                .ToList();
            if (vectors.Count == 0)
            {
                return null;
            }

            var referenceVector = vectors[0];
            var alignedVectors = vectors
                .Select(vector => BusbarGeometryUtility.Align(vector, referenceVector))
                .ToList();
            var averageVector = new Vector3d(
                alignedVectors.Average(vector => vector.X),
                alignedVectors.Average(vector => vector.Y),
                alignedVectors.Average(vector => vector.Z));
            return averageVector.Norm < 0.001d ? null : averageVector.Normalized;
        }

        private static List<Point3d> GetOffsetCenterlinePreservingPointCount(
            PlanarBendBusbarSetting groupCenterlineSetting,
            double memberOffset)
        {
            var compactCenterlinePoints = new List<Point3d>();
            var sourceToCompactPointIndexes = new List<int>();
            foreach (var sourcePoint in groupCenterlineSetting.PointList)
            {
                if (compactCenterlinePoints.Count == 0 ||
                    compactCenterlinePoints[compactCenterlinePoints.Count - 1]
                        .DistanceTo(sourcePoint) > 0.001d)
                {
                    compactCenterlinePoints.Add(sourcePoint.Copy());
                }

                sourceToCompactPointIndexes.Add(compactCenterlinePoints.Count - 1);
            }

            var compactCenterlineSetting = groupCenterlineSetting.Copy();
            compactCenterlineSetting.PointList = compactCenterlinePoints;
            var offsetCompactPoints = compactCenterlineSetting
                .GetOffsetCenterlinePointList(memberOffset);
            if (offsetCompactPoints.Count != compactCenterlinePoints.Count)
            {
                throw new InvalidOperationException(
                    "叠排中心线偏移后无法保持原有点数结构。");
            }

            return sourceToCompactPointIndexes
                .Select(compactPointIndex => offsetCompactPoints[compactPointIndex].Copy())
                .ToList();
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            SplitStackEnvelopeVerticalRiseSettings(
            IReadOnlyList<PlanarBendBusbarSetting> stackEnvelopeSettings,
            IReadOnlyList<double> stackMemberOffsets,
            double singleBusbarThickness,
            int phaseIndex)
        {
            if (stackEnvelopeSettings == null ||
                stackEnvelopeSettings.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对墙立弯没有生成有效的叠排总厚度路径。");
            }
            if (stackMemberOffsets == null ||
                stackMemberOffsets.Count == 0)
            {
                throw new InvalidOperationException(
                    "面对墙立弯缺少叠排成员偏移。");
            }
            if (singleBusbarThickness <= 0d ||
                double.IsNaN(singleBusbarThickness) ||
                double.IsInfinity(singleBusbarThickness))
            {
                throw new InvalidOperationException(
                    "面对墙立弯缺少有效的单根排厚度。");
            }

            var splitSettings = new List<PlanarBendBusbarSetting>(
                stackEnvelopeSettings.Count * stackMemberOffsets.Count);
            for (var memberIndex = 0;
                memberIndex < stackMemberOffsets.Count;
                memberIndex++)
            {
                var memberGroupKey = GetPhaseName(phaseIndex) + "-" +
                    (memberIndex + 1);
                foreach (var stackEnvelopeSetting in stackEnvelopeSettings)
                {
                    if (stackEnvelopeSetting == null)
                    {
                        throw new InvalidOperationException(
                            "面对墙立弯包含空的叠排总厚度路径。");
                    }

                    var splitMemberSetting = stackEnvelopeSetting.Copy();
                    splitMemberSetting.GroupKey = memberGroupKey;
                    splitMemberSetting.BusBarThickness =
                        singleBusbarThickness;
                    splitMemberSetting.PointList =
                        GetOffsetCenterlinePreservingPointCount(
                            stackEnvelopeSetting,
                            stackMemberOffsets[memberIndex]);
                    splitSettings.Add(splitMemberSetting);
                }
            }

            return splitSettings.AsReadOnly();
        }

        internal static string GetPhaseName(int phaseIndex)
        {
            return phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C";
        }

        private static string FormatPoint(Point3d point)
        {
            return point == null
                ? "null"
                : string.Format("({0:0.###},{1:0.###},{2:0.###})", point.X, point.Y, point.Z);
        }

        private static string FormatVector(Vector3d vector)
        {
            return vector == null
                ? "null"
                : string.Format("({0:0.###},{1:0.###},{2:0.###})", vector.X, vector.Y, vector.Z);
        }
    }
}
