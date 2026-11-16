using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridgePreview;
using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Services
{
    /// <summary>
    /// 将面对墙实际模型中紧邻算法区的最后一组普通段和穿墙套管 I 排
    /// 转换为 Worker v7 输入。更早的固定普通段仅参与预览与实际生成。
    /// Worker 始端保留完整普通段作为固定实体，并将算法侧末点归一到首点；
    /// 穿墙套管靠路线侧的搭接边界归一到末点，并以套管中心线和中间生成排宽
    /// 构造虚拟搭接排，符合 Worker 两端的物理角色约定。
    /// </summary>
    internal static class FaceToWallAlgorithmWorkerRequestFactory
    {
        private const double GeometryTolerance = 0.000001d;
        private const double DirectionTolerance = 0.001d;
        private const double TerminalPlaneCoordinateTolerance = 0.25d;
        private static readonly string[] PhaseNames = { "A", "B", "C" };

        public static bool TryCreateWorkerRequest(
            FaceToWallModelBusbarPlan modelBusbarPlan,
            BusbarBridgeAxisSetting axisSetting,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            out LinkageRouteWorkerRequestDto workerRequest,
            out string validationMessage)
        {
            return TryCreateWorkerRequestCore(modelBusbarPlan, axisSetting,
                workerRequestOptions, false, out workerRequest, out validationMessage);
        }

        /// <summary>
        /// 新界面由已核验的驱动尺寸和接口几何提供规划范围，不读取中间基架。
        /// 墙端按已核验的模板横向端位匹配柜端逻辑组，不读取铜排相别属性。
        /// 两侧端位是否交换只由请求选项决定。
        /// </summary>
        public static bool TryCreateIndependentWorkerRequest(
            IReadOnlyList<PlanarBendBusbarSetting> startBusbars,
            IReadOnlyList<ModeledBusbar> wallBusbars,
            Point3d containmentMinimum,
            Point3d containmentMaximum,
            BusbarBridgeAxisSetting axisSetting,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            out LinkageRouteWorkerRequestDto workerRequest,
            out string validationMessage)
        {
            workerRequest = null;
            validationMessage = null;
            if (startBusbars == null || wallBusbars == null)
            {
                validationMessage = "缺少面对墙柜端固定排或墙端固定排。";
                return false;
            }

            if (startBusbars.Any(bar => ResolvePhaseName(bar?.GroupKey) == null))
            {
                validationMessage = "柜端固定排缺少有效的 A/B/C 规划逻辑分组。";
                return false;
            }

            var plan = new FaceToWallModelBusbarPlan
            {
                WallBushingBusbars = wallBusbars,
                AlgorithmContainmentMinimum = containmentMinimum,
                AlgorithmContainmentMaximum = containmentMaximum
            };
            plan.AlgorithmStartBusbars.AddRange(startBusbars);
            return TryCreateWorkerRequestCore(plan, axisSetting, workerRequestOptions,
                true, out workerRequest, out validationMessage);
        }

        private static bool TryCreateWorkerRequestCore(
            FaceToWallModelBusbarPlan modelBusbarPlan,
            BusbarBridgeAxisSetting axisSetting,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            bool independentInput,
            out LinkageRouteWorkerRequestDto workerRequest,
            out string validationMessage)
        {
            workerRequest = null;
            validationMessage = null;

            if (modelBusbarPlan == null || axisSetting == null)
            {
                validationMessage = "缺少面对墙算法始端普通段或模型坐标设置。";
                return false;
            }

            if (!IsValidContainmentBox(
                    modelBusbarPlan.AlgorithmContainmentMinimum,
                    modelBusbarPlan.AlgorithmContainmentMaximum))
            {
                validationMessage =
                    independentInput
                        ? "面对墙驱动尺寸和接口几何未形成有效的算法允许生成范围。"
                        : "最后基架内缩后没有形成有效的算法允许生成范围。";
                return false;
            }

            var normalizedWorkerRequestOptions = workerRequestOptions ??
                BusbarAlgorithmWorkerRequestOptions.CreateDefault();
            if (!normalizedWorkerRequestOptions.TryValidate(out validationMessage))
            {
                return false;
            }

            if (!normalizedWorkerRequestOptions.RequireAxisAlignedRoutes)
            {
                validationMessage = "面对墙母排方案必须开启“仅允许 XYZ 轴向”。";
                return false;
            }

            try
            {
                var startPhaseSettings = BuildStartPhaseSettings(
                    modelBusbarPlan.AlgorithmStartBusbars);
                var wallBushingPhaseSettings = BuildWallBushingPhaseSettings(modelBusbarPlan.WallBushingBusbars,
                    startPhaseSettings, axisSetting, normalizedWorkerRequestOptions.SwapPhaseAAndCAtEnd);
                var requestGroups = new List<LinkageRouteWorkerGroupRequestDto>();
                var containmentSourcePoints = new List<LinkageRouteWorkerCoordinateDto>();

                foreach (var phaseName in PhaseNames)
                {
                    var groupKey = "FACE-TO-WALL-" + phaseName;
                    var phaseStartSettings = startPhaseSettings[phaseName];
                    var phaseEndSettings = wallBushingPhaseSettings[phaseName];
                    var generatedMiddleBusbarWidth = phaseStartSettings.Average(
                        phaseSetting => phaseSetting.BusBarWidth);
                    var generatedMiddleBusbarThickness = phaseStartSettings.Average(
                        phaseSetting => phaseSetting.BusBarThickness);
                    var startTerminal = BuildStartTerminal(
                        groupKey,
                        phaseStartSettings,
                        normalizedWorkerRequestOptions,
                        containmentSourcePoints);
                    var endTerminal = BuildEndTerminal(
                        groupKey,
                        phaseEndSettings,
                        startTerminal.RouteAnchor,
                        generatedMiddleBusbarWidth,
                        normalizedWorkerRequestOptions,
                        containmentSourcePoints);

                    ValidateTerminalDirections(
                        startTerminal,
                        endTerminal,
                        normalizedWorkerRequestOptions.RequireAxisAlignedRoutes,
                        phaseName);

                    var stackMoveVector = ResolveStackMoveVector(
                        startTerminal,
                        phaseStartSettings.Count);
                    requestGroups.Add(new LinkageRouteWorkerGroupRequestDto
                    {
                        GroupKey = groupKey,
                        Phase = phaseName,
                        DisplayName = phaseName + "相",
                        StartTerminal = startTerminal,
                        EndTerminal = endTerminal,
                        BusbarWidth = generatedMiddleBusbarWidth,
                        BusbarThickness = generatedMiddleBusbarThickness,
                        StackCount = phaseStartSettings.Count,
                        // Worker 的 Stack*Space 是附加间距；0 已按单根排厚形成
                        // 默认 2t 中心距，不能把实际中心距再次作为附加值传入。
                        StackStartSpace = 0d,
                        StackMiddleSpace = 0d,
                        StackEndSpace = 0d,
                        StackMoveVector = ToCoordinate(stackMoveVector),
                        StackReverse = false
                    });
                }

                ExpandContainmentToWallBushingLapLimit(
                    modelBusbarPlan.AlgorithmContainmentMinimum,
                    modelBusbarPlan.AlgorithmContainmentMaximum,
                    startPhaseSettings,
                    wallBushingPhaseSettings,
                    axisSetting.LengthVector,
                    out var expandedContainmentMinimum,
                    out var expandedContainmentMaximum);
                ExpandContainmentToTerminalForbiddenBoundaries(
                    expandedContainmentMinimum,
                    expandedContainmentMaximum,
                    requestGroups);
                var maximumStackCount = requestGroups.Max(
                    requestGroup => requestGroup.StackCount);
                var containmentMinimum = ToCoordinate(
                    expandedContainmentMinimum);
                var containmentMaximum = ToCoordinate(
                    expandedContainmentMaximum);

                workerRequest = new LinkageRouteWorkerRequestDto
                {
                    Groups = requestGroups,
                    Obstacles = new List<LinkageRouteWorkerObstacleDto>(),
                    DefaultBusbarWidth = requestGroups.Average(
                        requestGroup => requestGroup.BusbarWidth),
                    DefaultBusbarThickness = requestGroups.Average(
                        requestGroup => requestGroup.BusbarThickness),
                    DefaultStackCount = maximumStackCount,
                    MinimumSegmentLength =
                        normalizedWorkerRequestOptions.MinimumSegmentLength,
                    BendAllowance = normalizedWorkerRequestOptions.BendAllowance,
                    BendingRadius = normalizedWorkerRequestOptions.BendingRadius,
                    InsulationClearance =
                        normalizedWorkerRequestOptions.InsulationClearance,
                    AllowDoubleStartMiddleLap =
                        normalizedWorkerRequestOptions.AllowDoubleStartMiddleLap,
                    RequireSingleStartMiddleLap =
                        normalizedWorkerRequestOptions.RequireSingleStartMiddleLap,
                    RequireAxisAlignedRoutes = true,
                    UseExplicitContainmentBox = true,
                    ContainmentMinimum = containmentMinimum,
                    ContainmentMaximum = containmentMaximum,
                    SearchOptions = CreateSearchOptions(
                        normalizedWorkerRequestOptions)
                };

                FaceToWallPreviewTrace.Info(
                    "面对墙算法输入-完成",
                    "逻辑组=" + requestGroups.Count +
                    "，始端成员=" + string.Join(
                        "/",
                        requestGroups.Select(requestGroup =>
                            requestGroup.StartTerminal.Members.Count)) +
                    "，穿墙套管成员=" + string.Join(
                        "/",
                        requestGroups.Select(requestGroup =>
                            requestGroup.EndTerminal.Members.Count)) +
                    "，穿墙套管虚拟搭接排宽=" + string.Join(
                        "/",
                        requestGroups.Select(requestGroup =>
                            string.Join(
                                ",",
                                requestGroup.EndTerminal.Members
                                    .Select(terminalMember =>
                                        terminalMember.FixedBusbar.BusBarWidth)
                                    .Distinct()
                                    .Select(virtualLapBusbarWidth =>
                                        virtualLapBusbarWidth.ToString("0.###"))))) +
                    (independentInput ? "，范围来源=独立规划空间" :
                        "，最后基架=" + (modelBusbarPlan.LastFrameComponentName ?? string.Empty)) +
                    "，允许最小=" + FormatCoordinate(containmentMinimum) +
                    "，允许最大=" + FormatCoordinate(containmentMaximum));
                return true;
            }
            catch (Exception requestBuildException)
            {
                FaceToWallPreviewTrace.Error(
                    "面对墙算法输入-失败",
                    requestBuildException);
                validationMessage =
                    "面对墙算法输入构造失败：" + requestBuildException.Message;
                return false;
            }
        }

        private static Dictionary<string, List<PlanarBendBusbarSetting>>
            BuildStartPhaseSettings(
            IEnumerable<PlanarBendBusbarSetting> generatedBusbars)
        {
            var result = PhaseNames.ToDictionary(
                phaseName => phaseName,
                ignoredPhaseName => new List<PlanarBendBusbarSetting>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var generatedBusbar in generatedBusbars ??
                Enumerable.Empty<PlanarBendBusbarSetting>())
            {
                var phaseName = ResolvePhaseName(generatedBusbar?.GroupKey);
                if (phaseName == null)
                {
                    continue;
                }

                if (!IsUsableSetting(generatedBusbar))
                {
                    throw new InvalidOperationException(
                        phaseName +
                        "相算法始端普通段的中心线点、方向、尺寸或正交关系无效。");
                }

                if (!generatedBusbar.IsCenter)
                {
                    throw new InvalidOperationException(
                        phaseName + "相算法始端普通段不是中心线数据。" );
                }

                result[phaseName].Add(generatedBusbar);
            }

            foreach (var phaseName in PhaseNames)
            {
                if (result[phaseName].Count == 0)
                {
                    throw new InvalidOperationException(
                        "没有识别到" + phaseName + "相算法始端普通段。" );
                }
            }

            return result;
        }

        private static Dictionary<string, List<PlanarBendBusbarSetting>>
            BuildWallBushingPhaseSettings(
            IEnumerable<ModeledBusbar> modeledWallBushingBusbars,
            IReadOnlyDictionary<string, List<PlanarBendBusbarSetting>>
                startPhaseSettings,
            BusbarBridgeAxisSetting axisSetting,
            bool swapPhaseAAndCAtEnd)
        {
            var widthDirection = axisSetting.WidthVector;
            var usableModeledWallBushingBusbars =
                (modeledWallBushingBusbars ?? Enumerable.Empty<ModeledBusbar>())
                .ToList();
            if (usableModeledWallBushingBusbars.Any(modeledBusbar =>
                    !IsUsableSetting(modeledBusbar?.CenterlineSetting)))
            {
                throw new InvalidOperationException(
                    "穿墙套管固定排的中心线点、方向、尺寸或正交关系无效。");
            }

            if (usableModeledWallBushingBusbars.Any(modeledBusbar =>
                    !modeledBusbar.CenterlineSetting.IsCenter))
            {
                throw new InvalidOperationException(
                    "穿墙套管固定排不是中心线数据，不能直接传给算法。");
            }

            var usableWallBushingSettings = usableModeledWallBushingBusbars
                .Select(modeledBusbar => modeledBusbar.CenterlineSetting)
                .OrderBy(wallBushingSetting => Project(
                    GetSettingCenter(wallBushingSetting),
                    widthDirection))
                .ToList();
            if (usableWallBushingSettings.Count < PhaseNames.Length)
            {
                throw new InvalidOperationException(
                    "穿墙套管铜排少于三相所需的 3 根。" );
            }

            var orderedStartPhases = PhaseNames
                .Select(phaseName => new
                {
                    PhaseName = phaseName,
                    WidthCoordinate = startPhaseSettings[phaseName].Average(
                        phaseSetting => Project(
                            GetTerminalPoint(phaseSetting),
                            widthDirection))
                })
                .OrderBy(phase => phase.WidthCoordinate)
                .ToList();
            var wallBushingClusters = SplitIntoThreeSpatialClusters(
                usableWallBushingSettings,
                widthDirection);
            var result = PhaseNames.ToDictionary(
                phaseName => phaseName,
                ignoredPhaseName => new List<PlanarBendBusbarSetting>(),
                StringComparer.OrdinalIgnoreCase);
            for (var phaseIndex = 0;
                phaseIndex < orderedStartPhases.Count;
                phaseIndex++)
            {
                var phaseName = orderedStartPhases[phaseIndex].PhaseName;
                var wallBushingClusterIndex = swapPhaseAAndCAtEnd
                    ? orderedStartPhases.Count - phaseIndex - 1
                    : phaseIndex;
                result[phaseName].AddRange(SelectRouteSideWallBushingSettings(
                    wallBushingClusters[wallBushingClusterIndex],
                    startPhaseSettings[phaseName],
                    axisSetting.LengthVector,
                    phaseName));
            }

            FaceToWallPreviewTrace.Info(
                "面对墙算法输入-穿墙套管分相",
                string.Join(
                    "；",
                    PhaseNames.Select(phaseName =>
                        phaseName + "相=" + result[phaseName].Count)) +
                "，末端相序=" +
                (swapPhaseAAndCAtEnd ? "A→C/B→B/C→A" : "A→A/B→B/C→C"));
            return result;
        }

        /// <summary>
        /// 末基架内缩盒负责约束中间路径的宽度和高度；长度方向还必须覆盖
        /// 虚拟桩头搭接后的实际铜排端面，即真实桩头端面向柜侧预留150mm。
        /// </summary>
        private static void ExpandContainmentToWallBushingLapLimit(
            Point3d sourceContainmentMinimum,
            Point3d sourceContainmentMaximum,
            IReadOnlyDictionary<string, List<PlanarBendBusbarSetting>>
                startPhaseSettings,
            IReadOnlyDictionary<string, List<PlanarBendBusbarSetting>>
                wallBushingPhaseSettings,
            Vector3d lengthDirection,
            out Point3d expandedContainmentMinimum,
            out Point3d expandedContainmentMaximum)
        {
            expandedContainmentMinimum = sourceContainmentMinimum.Copy();
            expandedContainmentMaximum = sourceContainmentMaximum.Copy();
            var normalizedLengthDirection = lengthDirection.Normalized;
            int? sharedWallBushingOutwardDirectionSign = null;
            var wallBushingLapLimitCoordinates = new List<double>();

            foreach (var phaseName in PhaseNames)
            {
                var phaseStartBoundaryPoint = AveragePoints(
                    startPhaseSettings[phaseName].Select(GetTerminalPoint));
                var phaseStartLengthCoordinate = Project(
                    phaseStartBoundaryPoint,
                    normalizedLengthDirection);
                foreach (var phaseWallBushingSetting in
                    wallBushingPhaseSettings[phaseName])
                {
                    var phaseWallBushingNearestPoint = SelectNearestEndpoint(
                        phaseWallBushingSetting,
                        phaseStartBoundaryPoint,
                        phaseName);
                    var phaseWallBushingNearestLengthCoordinate = Project(
                        phaseWallBushingNearestPoint,
                        normalizedLengthDirection);
                    var phaseWallBushingDirectionDifference =
                        phaseWallBushingNearestLengthCoordinate -
                        phaseStartLengthCoordinate;
                    if (Math.Abs(phaseWallBushingDirectionDifference) <=
                        GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            phaseName +
                            "相穿墙套管搭接端与算法始端普通段处于同一长度坐标，无法确定末端范围向外方向。");
                    }

                    var phaseWallBushingOutwardDirectionSign =
                        phaseWallBushingDirectionDifference > 0d ? 1 : -1;
                    if (sharedWallBushingOutwardDirectionSign.HasValue &&
                        sharedWallBushingOutwardDirectionSign.Value !=
                        phaseWallBushingOutwardDirectionSign)
                    {
                        throw new InvalidOperationException(
                            "三相穿墙套管搭接端不在算法始端普通段的同一长度方向，无法形成统一的算法末端范围。");
                    }

                    sharedWallBushingOutwardDirectionSign =
                        phaseWallBushingOutwardDirectionSign;
                    // 虚拟端向柜侧加长L+150，Worker再搭接L，实际铜排停在真实端面前150。
                    wallBushingLapLimitCoordinates.Add(
                        phaseWallBushingNearestLengthCoordinate -
                        phaseWallBushingOutwardDirectionSign *
                        FaceToWallWallEndRule.ClearanceMm);
                }
            }

            if (wallBushingLapLimitCoordinates.Count == 0)
            {
                throw new InvalidOperationException(
                    "没有可用于扩展算法末端范围的穿墙套管搭接位置。");
            }

            foreach (var wallBushingLapLimitCoordinate in
                wallBushingLapLimitCoordinates)
            {
                ExpandAxisAlignedContainmentToProjectionCoordinate(
                    expandedContainmentMinimum,
                    expandedContainmentMaximum,
                    normalizedLengthDirection,
                    wallBushingLapLimitCoordinate);
            }

            if (!IsValidContainmentBox(
                    expandedContainmentMinimum,
                    expandedContainmentMaximum))
            {
                throw new InvalidOperationException(
                    "加入穿墙套管末端搭接位置后没有形成有效的算法允许生成范围。");
            }

            FaceToWallPreviewTrace.Info(
                "面对墙算法范围-加入穿墙套管末端",
                "原始最小=" + FormatPoint(sourceContainmentMinimum) +
                "，原始最大=" + FormatPoint(sourceContainmentMaximum) +
                "，真实桩头端面预留150mm的长度坐标=" +
                string.Join(
                    "/",
                    wallBushingLapLimitCoordinates.Select(
                        wallBushingLapLimitCoordinate =>
                            wallBushingLapLimitCoordinate.ToString("0.###"))) +
                "，最终最小=" + FormatPoint(expandedContainmentMinimum) +
                "，最终最大=" + FormatPoint(expandedContainmentMaximum));
        }

        /// <summary>
        /// Worker 会从每个固定端离 RouteAnchor 更远的端点建立不可伸出侧整体禁区，
        /// 该端点必须先位于容纳盒内。这里只沿端点的排长外向轴扩展边界，避免
        /// 把始端普通段其余坐标无条件并入盒子后放宽末基架在其他方向上的限制。
        /// </summary>
        private static void ExpandContainmentToTerminalForbiddenBoundaries(
            Point3d expandedContainmentMinimum,
            Point3d expandedContainmentMaximum,
            IEnumerable<LinkageRouteWorkerGroupRequestDto> requestGroups)
        {
            var terminalForbiddenBoundaryMessages = new List<string>();
            foreach (var requestGroup in requestGroups ??
                Enumerable.Empty<LinkageRouteWorkerGroupRequestDto>())
            {
                var terminalAssemblies = new[]
                {
                    new
                    {
                        TerminalLabel = "始端",
                        TerminalAssembly = requestGroup.StartTerminal
                    },
                    new
                    {
                        TerminalLabel = "末端",
                        TerminalAssembly = requestGroup.EndTerminal
                    }
                };
                foreach (var terminalAssemblyEntry in terminalAssemblies)
                {
                    var terminalAssembly = terminalAssemblyEntry.TerminalAssembly;
                    if (terminalAssembly?.RouteAnchor == null)
                    {
                        continue;
                    }

                    var terminalRouteAnchorPoint = ToPoint(
                        terminalAssembly.RouteAnchor);
                    foreach (var terminalMember in terminalAssembly.Members ??
                        new List<LinkageRouteWorkerTerminalMemberDto>())
                    {
                        var fixedBusbarPoints =
                            terminalMember?.FixedBusbar?.PointList;
                        if (fixedBusbarPoints == null ||
                            fixedBusbarPoints.Count < 2)
                        {
                            continue;
                        }

                        var firstFixedEndpoint = ToPoint(fixedBusbarPoints[0]);
                        var lastFixedEndpoint = ToPoint(
                            fixedBusbarPoints[fixedBusbarPoints.Count - 1]);
                        var firstEndpointDistanceToRouteAnchor =
                            firstFixedEndpoint.DistanceTo(terminalRouteAnchorPoint);
                        var lastEndpointDistanceToRouteAnchor =
                            lastFixedEndpoint.DistanceTo(terminalRouteAnchorPoint);
                        if (Math.Abs(
                                firstEndpointDistanceToRouteAnchor -
                                lastEndpointDistanceToRouteAnchor) <=
                            GeometryTolerance)
                        {
                            throw new InvalidOperationException(
                                (requestGroup.GroupKey ?? "未分组") +
                                terminalAssemblyEntry.TerminalLabel +
                                "固定排两端到搭接锚点等距，无法确定不可伸出端面。");
                        }

                        var useFirstForbiddenEndpoint =
                            firstEndpointDistanceToRouteAnchor >
                            lastEndpointDistanceToRouteAnchor;
                        var terminalForbiddenBoundaryPoint =
                            useFirstForbiddenEndpoint
                                ? firstFixedEndpoint
                                : lastFixedEndpoint;
                        var terminalForbiddenOutwardDirection =
                            ResolveEndpointOutwardDirection(
                                fixedBusbarPoints,
                                useFirstForbiddenEndpoint);
                        if (terminalForbiddenOutwardDirection == null ||
                            !IsGlobalAxisDirection(
                                terminalForbiddenOutwardDirection))
                        {
                            throw new InvalidOperationException(
                                (requestGroup.GroupKey ?? "未分组") +
                                terminalAssemblyEntry.TerminalLabel +
                                "固定排不可伸出方向不是有效的 XYZ 轴向。");
                        }

                        var normalizedTerminalForbiddenOutwardDirection =
                            terminalForbiddenOutwardDirection.Normalized;
                        var terminalForbiddenBoundaryProjectionCoordinate =
                            Project(
                                terminalForbiddenBoundaryPoint,
                                normalizedTerminalForbiddenOutwardDirection);
                        ExpandAxisAlignedContainmentToProjectionCoordinate(
                            expandedContainmentMinimum,
                            expandedContainmentMaximum,
                            normalizedTerminalForbiddenOutwardDirection,
                            terminalForbiddenBoundaryProjectionCoordinate);
                        terminalForbiddenBoundaryMessages.Add(
                            (requestGroup.GroupKey ?? "未分组") +
                            terminalAssemblyEntry.TerminalLabel + "=" +
                            FormatPoint(terminalForbiddenBoundaryPoint) + "@" +
                            FormatVector(
                                normalizedTerminalForbiddenOutwardDirection));
                    }
                }
            }

            FaceToWallPreviewTrace.Info(
                "面对墙算法范围-加入固定端不可伸出端面",
                "端面=" + string.Join(
                    "；",
                    terminalForbiddenBoundaryMessages) +
                "，最终最小=" + FormatPoint(expandedContainmentMinimum) +
                "，最终最大=" + FormatPoint(expandedContainmentMaximum));
        }

        private static Vector3d ResolveEndpointOutwardDirection(
            IList<LinkageRouteWorkerCoordinateDto> fixedBusbarPoints,
            bool useFirstEndpoint)
        {
            var fixedEndpointIndex = useFirstEndpoint
                ? 0
                : fixedBusbarPoints.Count - 1;
            var fixedEndpointPoint = ToPoint(
                fixedBusbarPoints[fixedEndpointIndex]);
            for (var fixedPointOffset = 1;
                fixedPointOffset < fixedBusbarPoints.Count;
                fixedPointOffset++)
            {
                var fixedInteriorPointIndex = useFirstEndpoint
                    ? fixedPointOffset
                    : fixedEndpointIndex - fixedPointOffset;
                var fixedEndpointOutwardVector = new Vector3d(
                    ToPoint(fixedBusbarPoints[fixedInteriorPointIndex]),
                    fixedEndpointPoint);
                if (fixedEndpointOutwardVector.Norm > GeometryTolerance)
                {
                    return fixedEndpointOutwardVector.Normalized;
                }
            }

            return null;
        }

        private static void ExpandAxisAlignedContainmentToProjectionCoordinate(
            Point3d containmentMinimum,
            Point3d containmentMaximum,
            Vector3d normalizedAxisDirection,
            double targetProjectionCoordinate)
        {
            if (Math.Abs(normalizedAxisDirection.X) > DirectionTolerance)
            {
                var targetWorldXCoordinate =
                    targetProjectionCoordinate / normalizedAxisDirection.X;
                containmentMinimum.X = Math.Min(
                    containmentMinimum.X,
                    targetWorldXCoordinate);
                containmentMaximum.X = Math.Max(
                    containmentMaximum.X,
                    targetWorldXCoordinate);
                return;
            }

            if (Math.Abs(normalizedAxisDirection.Y) > DirectionTolerance)
            {
                var targetWorldYCoordinate =
                    targetProjectionCoordinate / normalizedAxisDirection.Y;
                containmentMinimum.Y = Math.Min(
                    containmentMinimum.Y,
                    targetWorldYCoordinate);
                containmentMaximum.Y = Math.Max(
                    containmentMaximum.Y,
                    targetWorldYCoordinate);
                return;
            }

            if (Math.Abs(normalizedAxisDirection.Z) > DirectionTolerance)
            {
                var targetWorldZCoordinate =
                    targetProjectionCoordinate / normalizedAxisDirection.Z;
                containmentMinimum.Z = Math.Min(
                    containmentMinimum.Z,
                    targetWorldZCoordinate);
                containmentMaximum.Z = Math.Max(
                    containmentMaximum.Z,
                    targetWorldZCoordinate);
                return;
            }

            throw new InvalidOperationException(
                "长度方向不是有效的 XYZ 轴向，无法扩展穿墙套管末端范围。");
        }

        private static IReadOnlyList<PlanarBendBusbarSetting>
            SelectRouteSideWallBushingSettings(
            IReadOnlyList<PlanarBendBusbarSetting> phaseWallBushingSettings,
            IReadOnlyList<PlanarBendBusbarSetting> phaseStartSettings,
            Vector3d lengthDirection,
            string phaseName)
        {
            if (phaseWallBushingSettings == null ||
                phaseWallBushingSettings.Count == 0)
            {
                throw new InvalidOperationException(
                    phaseName + "相没有可用的穿墙套管铜排。");
            }

            var startBoundaryPoint = AveragePoints(
                phaseStartSettings.Select(GetTerminalPoint));
            var startBoundaryCoordinate = Project(
                startBoundaryPoint,
                lengthDirection);
            var routeCandidates = phaseWallBushingSettings
                .Select(wallBushingSetting =>
                {
                    var routeBoundaryPoint = SelectNearestEndpoint(
                        wallBushingSetting,
                        startBoundaryPoint,
                        phaseName);
                    var routeBoundaryCoordinate = Project(
                        routeBoundaryPoint,
                        lengthDirection);
                    return new WallBushingRouteCandidate
                    {
                        Setting = wallBushingSetting,
                        BoundaryPoint = routeBoundaryPoint,
                        BoundaryCoordinate = routeBoundaryCoordinate,
                        LongitudinalDistance = Math.Abs(
                            routeBoundaryCoordinate - startBoundaryCoordinate)
                    };
                })
                .OrderBy(routeCandidate => routeCandidate.BoundaryCoordinate)
                .ToList();
            var terminalPlaneClusters = BuildTerminalPlaneClusters(routeCandidates);
            var orderedTerminalPlaneClusters = terminalPlaneClusters
                .OrderBy(terminalPlaneCluster =>
                    terminalPlaneCluster.AverageLongitudinalDistance)
                .ToList();
            var selectedTerminalPlaneCluster = orderedTerminalPlaneClusters[0];
            if (orderedTerminalPlaneClusters.Count > 1 &&
                Math.Abs(
                    selectedTerminalPlaneCluster.AverageLongitudinalDistance -
                    orderedTerminalPlaneClusters[1].AverageLongitudinalDistance) <=
                TerminalPlaneCoordinateTolerance)
            {
                throw new InvalidOperationException(
                    phaseName +
                    "相穿墙套管两侧端面到算法始端普通段等距，无法唯一确定柜体侧。");
            }

            FaceToWallPreviewTrace.Info(
                "面对墙算法输入-选择套管柜体侧",
                phaseName + "相候选=" + phaseWallBushingSettings.Count +
                "，端面簇=" + string.Join(
                    "/",
                    terminalPlaneClusters.Select(terminalPlaneCluster =>
                        terminalPlaneCluster.AverageBoundaryCoordinate
                            .ToString("0.###") +
                        "(" + terminalPlaneCluster.Candidates.Count + "层)")) +
                "，选中端面投影=" +
                selectedTerminalPlaneCluster.AverageBoundaryCoordinate
                    .ToString("0.###") +
                "，中心=" + string.Join(
                    "/",
                    selectedTerminalPlaneCluster.Candidates.Select(
                        routeCandidate => FormatPoint(
                            GetSettingCenter(routeCandidate.Setting)))));
            return selectedTerminalPlaneCluster.Candidates
                .Select(routeCandidate => routeCandidate.Setting)
                .ToList()
                .AsReadOnly();
        }

        private static List<WallBushingTerminalPlaneCluster>
            BuildTerminalPlaneClusters(
            IEnumerable<WallBushingRouteCandidate> routeCandidates)
        {
            var result = new List<WallBushingTerminalPlaneCluster>();
            foreach (var routeCandidate in routeCandidates ??
                Enumerable.Empty<WallBushingRouteCandidate>())
            {
                var matchingCluster = result.FirstOrDefault(
                    terminalPlaneCluster => Math.Abs(
                        terminalPlaneCluster.AverageBoundaryCoordinate -
                        routeCandidate.BoundaryCoordinate) <=
                        TerminalPlaneCoordinateTolerance);
                if (matchingCluster == null)
                {
                    matchingCluster = new WallBushingTerminalPlaneCluster();
                    result.Add(matchingCluster);
                }

                matchingCluster.Candidates.Add(routeCandidate);
            }

            return result;
        }

        private static Point3d SelectNearestEndpoint(
            PlanarBendBusbarSetting wallBushingSetting,
            Point3d startBoundaryPoint,
            string phaseName)
        {
            var firstEndpoint = wallBushingSetting.PointList[0];
            var lastEndpoint = wallBushingSetting.PointList[
                wallBushingSetting.PointList.Count - 1];
            var firstEndpointDistance = firstEndpoint.DistanceTo(startBoundaryPoint);
            var lastEndpointDistance = lastEndpoint.DistanceTo(startBoundaryPoint);
            if (Math.Abs(firstEndpointDistance - lastEndpointDistance) <=
                GeometryTolerance)
            {
                throw new InvalidOperationException(
                    phaseName +
                    "相穿墙套管铜排两端到算法始端普通段等距，无法确定搭接端。");
            }

            return firstEndpointDistance < lastEndpointDistance
                ? firstEndpoint
                : lastEndpoint;
        }

        private static List<List<PlanarBendBusbarSetting>>
            SplitIntoThreeSpatialClusters(
            IReadOnlyList<PlanarBendBusbarSetting> orderedSettings,
            Vector3d widthDirection)
        {
            var bestFirstSplitIndex = 1;
            var bestSecondSplitIndex = 2;
            var bestScore = double.PositiveInfinity;
            for (var firstSplitIndex = 1;
                firstSplitIndex <= orderedSettings.Count - 2;
                firstSplitIndex++)
            {
                for (var secondSplitIndex = firstSplitIndex + 1;
                    secondSplitIndex <= orderedSettings.Count - 1;
                    secondSplitIndex++)
                {
                    var currentScore = GetClusterScore(
                        orderedSettings,
                        0,
                        firstSplitIndex,
                        widthDirection) +
                        GetClusterScore(
                            orderedSettings,
                            firstSplitIndex,
                            secondSplitIndex,
                            widthDirection) +
                        GetClusterScore(
                            orderedSettings,
                            secondSplitIndex,
                            orderedSettings.Count,
                            widthDirection);
                    if (currentScore >= bestScore)
                    {
                        continue;
                    }

                    bestScore = currentScore;
                    bestFirstSplitIndex = firstSplitIndex;
                    bestSecondSplitIndex = secondSplitIndex;
                }
            }

            return new List<List<PlanarBendBusbarSetting>>
            {
                orderedSettings.Take(bestFirstSplitIndex).ToList(),
                orderedSettings.Skip(bestFirstSplitIndex)
                    .Take(bestSecondSplitIndex - bestFirstSplitIndex)
                    .ToList(),
                orderedSettings.Skip(bestSecondSplitIndex).ToList()
            };
        }

        private static double GetClusterScore(
            IReadOnlyList<PlanarBendBusbarSetting> orderedSettings,
            int startIndex,
            int endIndex,
            Vector3d widthDirection)
        {
            var coordinates = orderedSettings
                .Skip(startIndex)
                .Take(endIndex - startIndex)
                .Select(setting => Project(GetSettingCenter(setting), widthDirection))
                .ToList();
            var averageCoordinate = coordinates.Average();
            return coordinates.Sum(coordinate =>
                Math.Pow(coordinate - averageCoordinate, 2d));
        }

        private static LinkageRouteWorkerTerminalAssemblyDto BuildStartTerminal(
            string groupKey,
            IReadOnlyList<PlanarBendBusbarSetting> phaseStartSettings,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            ICollection<LinkageRouteWorkerCoordinateDto> containmentSourcePoints)
        {
            var preparedMembers = phaseStartSettings
                .Select(startSetting => PrepareStartMember(
                    groupKey,
                    startSetting,
                    workerRequestOptions))
                .ToList();
            AlignPreparedTerminalMemberThicknessDirections(
                preparedMembers,
                groupKey);
            var referenceThicknessDirection = preparedMembers[0]
                .TerminalThicknessDirection;
            preparedMembers = preparedMembers
                .OrderBy(preparedMember => Project(
                    preparedMember.BoundaryPoint,
                    referenceThicknessDirection))
                .ToList();

            foreach (var preparedMember in preparedMembers)
            {
                AddSettingPoints(
                    preparedMember.TerminalSetting,
                    containmentSourcePoints);
            }

            return new LinkageRouteWorkerTerminalAssemblyDto
            {
                RouteAnchor = AverageCoordinates(preparedMembers.Select(
                    preparedMember => ToCoordinate(
                        preparedMember.BoundaryPoint))),
                LengthDirection = ToCoordinate(AverageAlignedVectors(
                    preparedMembers.Select(preparedMember =>
                        preparedMember.TerminalLengthDirection))),
                ThicknessDirection = ToCoordinate(AverageAlignedVectors(
                    preparedMembers.Select(preparedMember =>
                        preparedMember.TerminalThicknessDirection))),
                Members = preparedMembers.Select((preparedMember, memberIndex) =>
                    new LinkageRouteWorkerTerminalMemberDto
                    {
                        MemberKey = groupKey + "-S" + (memberIndex + 1),
                        FixedBusbar = preparedMember.TerminalSetting,
                        ForbiddenHorizontalExitDirection = null
                    }).ToList()
            };
        }

        private static PreparedTerminalMember PrepareStartMember(
            string groupKey,
            PlanarBendBusbarSetting startSetting,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions)
        {
            var terminalSegmentIndex = FindLastNonZeroSegmentIndex(
                startSetting.PointList);
            if (terminalSegmentIndex < 0)
            {
                throw new InvalidOperationException(
                    groupKey + "算法始端普通段没有可用的末段。" );
            }

            var terminalInteriorPoint = startSetting.PointList[terminalSegmentIndex];
            var terminalBoundaryPoint = startSetting.PointList[
                terminalSegmentIndex + 1];
            var terminalLengthDirection = new Vector3d(
                terminalInteriorPoint,
                terminalBoundaryPoint).Normalized;
            var terminalThicknessDirection = ResolveSegmentThicknessDirection(
                startSetting,
                terminalSegmentIndex);
            // Worker 固定端副本必须压缩相邻重复点，否则实体扫描会收到
            // 零长度直段（柜顶连接段的 P2=P3 仍可能出现在预览集合中）。
            var endpointFirstCenterlinePoints = CloneNonDuplicatePoints(
                startSetting.PointList.AsEnumerable().Reverse());
            var terminalSetting = ToWorkerSetting(
                startSetting,
                groupKey,
                workerRequestOptions,
                endpointFirstCenterlinePoints,
                terminalThicknessDirection,
                true);
            return new PreparedTerminalMember
            {
                BoundaryPoint = terminalBoundaryPoint,
                TerminalLengthDirection = terminalLengthDirection,
                TerminalThicknessDirection = terminalThicknessDirection,
                TerminalSetting = terminalSetting
            };
        }

        private static LinkageRouteWorkerTerminalAssemblyDto BuildEndTerminal(
            string groupKey,
            IReadOnlyList<PlanarBendBusbarSetting> wallBushingSettings,
            LinkageRouteWorkerCoordinateDto startRouteAnchor,
            double virtualWallBushingLapBusbarWidth,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            ICollection<LinkageRouteWorkerCoordinateDto> containmentSourcePoints)
        {
            var startAnchorPoint = ToPoint(startRouteAnchor);
            var preparedMembers = wallBushingSettings
                .Select(wallBushingSetting => PrepareEndMember(
                    groupKey,
                    wallBushingSetting,
                    startAnchorPoint,
                    virtualWallBushingLapBusbarWidth,
                    workerRequestOptions))
                .ToList();
            AlignPreparedTerminalMemberThicknessDirections(
                preparedMembers,
                groupKey);
            var referenceThicknessDirection = preparedMembers[0]
                .TerminalThicknessDirection;
            preparedMembers = preparedMembers
                .OrderBy(preparedMember => Project(
                    preparedMember.BoundaryPoint,
                    referenceThicknessDirection))
                .ToList();
            foreach (var preparedMember in preparedMembers)
            {
                AddSettingPoints(
                    preparedMember.TerminalSetting,
                    containmentSourcePoints);
            }

            return new LinkageRouteWorkerTerminalAssemblyDto
            {
                RouteAnchor = AverageCoordinates(preparedMembers.Select(
                    preparedMember => ToCoordinate(
                        preparedMember.BoundaryPoint))),
                LengthDirection = ToCoordinate(AverageAlignedVectors(
                    preparedMembers.Select(preparedMember =>
                        preparedMember.TerminalLengthDirection))),
                ThicknessDirection = ToCoordinate(AverageAlignedVectors(
                    preparedMembers.Select(preparedMember =>
                        preparedMember.TerminalThicknessDirection))),
                Members = preparedMembers.Select((preparedMember, memberIndex) =>
                    new LinkageRouteWorkerTerminalMemberDto
                    {
                        MemberKey = groupKey + "-E" + (memberIndex + 1),
                        FixedBusbar = preparedMember.TerminalSetting,
                        ForbiddenHorizontalExitDirection = null
                    }).ToList()
            };
        }

        private static PreparedTerminalMember PrepareEndMember(
            string groupKey,
            PlanarBendBusbarSetting wallBushingSetting,
            Point3d startAnchorPoint,
            double virtualWallBushingLapBusbarWidth,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions)
        {
            var firstNonZeroSegmentIndex = FindFirstNonZeroSegmentIndex(
                wallBushingSetting.PointList);
            var lastNonZeroSegmentIndex = FindLastNonZeroSegmentIndex(
                wallBushingSetting.PointList);
            if (firstNonZeroSegmentIndex < 0 || lastNonZeroSegmentIndex < 0)
            {
                throw new InvalidOperationException(
                    groupKey + "穿墙套管铜排中心线无效。" );
            }

            var firstBoundaryPoint = wallBushingSetting.PointList[0];
            var lastBoundaryPoint = wallBushingSetting.PointList[
                wallBushingSetting.PointList.Count - 1];
            var routeBoundaryIsFirst = firstBoundaryPoint.DistanceTo(startAnchorPoint) <=
                lastBoundaryPoint.DistanceTo(startAnchorPoint);
            // Worker 对物理 EndTerminal 的搭接、足迹和附件统一读取固定排
            // PointList 的最后一段，因此不论模型原始点序如何，都必须把靠路线侧
            // 的唯一搭接端放到最后一点，不能只依赖 RouteAnchor 选端。
            var orderedPoints = routeBoundaryIsFirst
                ? ClonePoints(
                    wallBushingSetting.PointList.AsEnumerable().Reverse())
                : ClonePoints(wallBushingSetting.PointList);
            var sourceSegmentIndex = routeBoundaryIsFirst
                ? firstNonZeroSegmentIndex
                : lastNonZeroSegmentIndex;
            var terminalThicknessDirection = ResolveSegmentThicknessDirection(
                wallBushingSetting,
                sourceSegmentIndex);
            var routeBoundaryPointIndex = orderedPoints.Count - 1;
            var terminalLengthDirection = new Vector3d(
                orderedPoints[routeBoundaryPointIndex],
                orderedPoints[routeBoundaryPointIndex - 1]).Normalized;
            var realBoundary = orderedPoints[routeBoundaryPointIndex];
            var extension = virtualWallBushingLapBusbarWidth + FaceToWallWallEndRule.ClearanceMm;
            // orderedPoints是深副本；真实桩头、反读快照和预览仍使用原始几何。
            orderedPoints[routeBoundaryPointIndex] = new Point3d(
                realBoundary.X - terminalLengthDirection.X * extension,
                realBoundary.Y - terminalLengthDirection.Y * extension,
                realBoundary.Z - terminalLengthDirection.Z * extension);
            if (new Vector3d(startAnchorPoint, orderedPoints[routeBoundaryPointIndex])
                    .Dot(terminalLengthDirection) <= GeometryTolerance)
                throw new InvalidOperationException(groupKey + "墙端预留150mm及搭接后，特殊段可用空间不足。");
            var terminalSetting = ToWorkerSetting(
                wallBushingSetting,
                groupKey,
                workerRequestOptions,
                orderedPoints,
                terminalThicknessDirection,
                routeBoundaryIsFirst,
                virtualWallBushingLapBusbarWidth);
            return new PreparedTerminalMember
            {
                BoundaryPoint = orderedPoints[routeBoundaryPointIndex],
                TerminalLengthDirection = terminalLengthDirection,
                TerminalThicknessDirection = terminalThicknessDirection,
                TerminalSetting = terminalSetting
            };
        }

        private static LinkageRouteWorkerPlanarSettingDto ToWorkerSetting(
            PlanarBendBusbarSetting sourceSetting,
            string groupKey,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            IEnumerable<Point3d> replacementPoints,
            Vector3d replacementStartThicknessDirection,
            bool reverseLapEnds = false,
            double? replacementBusbarWidth = null)
        {
            var sourcePoints = replacementPoints ?? sourceSetting.PointList;
            var hasReplacementBusbarWidth =
                replacementBusbarWidth.HasValue &&
                IsFinitePositive(replacementBusbarWidth.Value);
            var effectiveBusbarWidth = hasReplacementBusbarWidth
                ? replacementBusbarWidth.Value
                : sourceSetting.BusBarWidth;
            return new LinkageRouteWorkerPlanarSettingDto
            {
                IsCenter = true,
                StartBusbarVector = ToCoordinate(
                    replacementStartThicknessDirection ??
                    sourceSetting.StartBusbarVector.Normalized),
                WidthVector = ToCoordinate(sourceSetting.WidthVector.Normalized),
                PointList = sourcePoints.Select(ToCoordinate).ToList(),
                BarBendingRadius = IsFinitePositive(
                        sourceSetting.BarBendingRadius)
                    ? sourceSetting.BarBendingRadius
                    : workerRequestOptions.BendingRadius,
                BendAllowance = IsFiniteNonNegative(
                        sourceSetting.BendAllowance)
                    ? sourceSetting.BendAllowance
                    : workerRequestOptions.BendAllowance,
                BusBarThickness = sourceSetting.BusBarThickness,
                BusBarWidth = effectiveBusbarWidth,
                StartLapWidth = hasReplacementBusbarWidth
                    ? effectiveBusbarWidth
                    : IsFinitePositive(reverseLapEnds
                        ? sourceSetting.EndLapWidth
                        : sourceSetting.StartLapWidth)
                    ? (reverseLapEnds
                        ? sourceSetting.EndLapWidth
                        : sourceSetting.StartLapWidth)
                    : sourceSetting.BusBarWidth,
                EndLapWidth = hasReplacementBusbarWidth
                    ? effectiveBusbarWidth
                    : IsFinitePositive(reverseLapEnds
                        ? sourceSetting.StartLapWidth
                        : sourceSetting.EndLapWidth)
                    ? (reverseLapEnds
                        ? sourceSetting.StartLapWidth
                        : sourceSetting.EndLapWidth)
                    : sourceSetting.BusBarWidth,
                GroupKey = groupKey
            };
        }

        private static Vector3d ResolveSegmentThicknessDirection(
            PlanarBendBusbarSetting sourceSetting,
            int targetSegmentIndex)
        {
            var firstSegmentIndex = FindFirstNonZeroSegmentIndex(
                sourceSetting.PointList);
            if (firstSegmentIndex < 0 || targetSegmentIndex < 0)
            {
                throw new InvalidOperationException(
                    "铜排中心线缺少可用直段，无法计算排厚方向。" );
            }

            var normalizedWidthDirection = sourceSetting.WidthVector.Normalized;
            var firstSegmentDirection = new Vector3d(
                sourceSetting.PointList[firstSegmentIndex],
                sourceSetting.PointList[firstSegmentIndex + 1]).Normalized;
            var firstCalculatedThicknessDirection = firstSegmentDirection
                .Cross(normalizedWidthDirection).Normalized;
            var orientationSign = firstCalculatedThicknessDirection.Dot(
                    sourceSetting.StartBusbarVector.Normalized) < 0d
                ? -1d
                : 1d;
            var targetSegmentDirection = new Vector3d(
                sourceSetting.PointList[targetSegmentIndex],
                sourceSetting.PointList[targetSegmentIndex + 1]).Normalized;
            var targetThicknessDirection = targetSegmentDirection
                .Cross(normalizedWidthDirection).Normalized;
            return orientationSign < 0d
                ? targetThicknessDirection.Negate()
                : targetThicknessDirection;
        }

        private static Vector3d ResolveStackMoveVector(
            LinkageRouteWorkerTerminalAssemblyDto startTerminal,
            int stackCount)
        {
            var fallbackThicknessDirection = ToVector(
                startTerminal.ThicknessDirection).Normalized;
            if (stackCount <= 1 || startTerminal.Members.Count <= 1)
            {
                return fallbackThicknessDirection;
            }

            var firstBoundaryPoint = ToPoint(
                startTerminal.Members.First().FixedBusbar.PointList.First());
            var lastBoundaryPoint = ToPoint(
                startTerminal.Members.Last().FixedBusbar.PointList.First());
            var memberSpreadDirection = new Vector3d(
                firstBoundaryPoint,
                lastBoundaryPoint);
            return memberSpreadDirection.Norm > GeometryTolerance
                ? memberSpreadDirection.Normalized
                : fallbackThicknessDirection;
        }

        private static void AlignPreparedTerminalMemberThicknessDirections(
            IList<PreparedTerminalMember> preparedMembers,
            string groupKey)
        {
            if (preparedMembers == null || preparedMembers.Count == 0)
            {
                throw new InvalidOperationException(
                    groupKey + "固定端没有可用成员。");
            }

            var referenceThicknessDirection = preparedMembers[0]
                .TerminalThicknessDirection.Normalized;
            foreach (var preparedMember in preparedMembers)
            {
                var memberThicknessDirection = preparedMember
                    .TerminalThicknessDirection;
                if (memberThicknessDirection == null ||
                    memberThicknessDirection.Norm <= GeometryTolerance ||
                    memberThicknessDirection.Normalized
                        .Cross(referenceThicknessDirection).Norm >
                        DirectionTolerance)
                {
                    throw new InvalidOperationException(
                        groupKey + "同一固定端成员的排厚方向不平行。");
                }

                var alignedThicknessDirection =
                    memberThicknessDirection.Normalized.Dot(
                        referenceThicknessDirection) < 0d
                        ? memberThicknessDirection.Normalized.Negate()
                        : memberThicknessDirection.Normalized;
                preparedMember.TerminalThicknessDirection =
                    alignedThicknessDirection;
                preparedMember.TerminalSetting.StartBusbarVector =
                    ToCoordinate(alignedThicknessDirection);
            }
        }

        private static void ValidateTerminalDirections(
            LinkageRouteWorkerTerminalAssemblyDto startTerminal,
            LinkageRouteWorkerTerminalAssemblyDto endTerminal,
            bool requireAxisAlignedRoutes,
            string phaseName)
        {
            var startLengthDirection = ToVector(startTerminal.LengthDirection);
            var startThicknessDirection = ToVector(startTerminal.ThicknessDirection);
            var endLengthDirection = ToVector(endTerminal.LengthDirection);
            var endThicknessDirection = ToVector(endTerminal.ThicknessDirection);
            if (!AreOrthogonal(startLengthDirection, startThicknessDirection) ||
                !AreOrthogonal(endLengthDirection, endThicknessDirection))
            {
                throw new InvalidOperationException(
                    phaseName + "相固定端排长与排厚方向不正交。" );
            }

            if (!requireAxisAlignedRoutes)
            {
                return;
            }

            var allSettings = startTerminal.Members
                .Concat(endTerminal.Members)
                .Select(terminalMember => terminalMember.FixedBusbar)
                .ToList();
            foreach (var fixedSetting in allSettings)
            {
                var fixedWidthDirection = ToVector(fixedSetting.WidthVector);
                if (!IsGlobalAxisDirection(fixedWidthDirection))
                {
                    throw new InvalidOperationException(
                        phaseName + "相固定端的排宽方向不是 XYZ 轴向。");
                }
            }

            if (!IsGlobalAxisDirection(startLengthDirection) ||
                !IsGlobalAxisDirection(startThicknessDirection) ||
                !IsGlobalAxisDirection(endLengthDirection) ||
                !IsGlobalAxisDirection(endThicknessDirection))
            {
                throw new InvalidOperationException(
                    phaseName + "相固定端的出入口排长或排厚方向不是 XYZ 轴向。");
            }

            foreach (var fixedSetting in allSettings)
            {
                var fixedThicknessDirection = ToVector(
                    fixedSetting.StartBusbarVector);
                if (!IsGlobalAxisDirection(fixedThicknessDirection))
                {
                    throw new InvalidOperationException(
                        phaseName + "相固定端的排厚方向不是 XYZ 轴向。");
                }
            }
        }

        private static LinkageRouteWorkerSearchOptionsDto CreateSearchOptions(
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions)
        {
            return new LinkageRouteWorkerSearchOptionsDto
            {
                FirstSolutionTargetMilliseconds =
                    workerRequestOptions.FirstSolutionTargetMilliseconds,
                TotalSearchLimitMilliseconds =
                    workerRequestOptions.TotalSearchLimitMilliseconds,
                EnableAutomaticTotalSearchLimit =
                    workerRequestOptions.EnableAutomaticTotalSearchLimit,
                MaximumCandidatesPerGroup =
                    workerRequestOptions.MaximumCandidatesPerGroup,
                MaximumClearanceRepairCandidatesPerState =
                    workerRequestOptions.MaximumClearanceRepairCandidatesPerState,
                MaximumSolutionsPerAlgorithm =
                    workerRequestOptions.MaximumSolutionsPerAlgorithm,
                BeamWidth = workerRequestOptions.BeamWidth,
                MaximumDegreeOfParallelism =
                    workerRequestOptions.MaximumDegreeOfParallelism,
                MinimumAdaptiveRepairStep =
                    workerRequestOptions.MinimumAdaptiveRepairStep
            };
        }

        private static bool IsValidContainmentBox(
            Point3d containmentMinimum,
            Point3d containmentMaximum)
        {
            return containmentMinimum != null &&
                   containmentMaximum != null &&
                   IsFinite(containmentMinimum.X) &&
                   IsFinite(containmentMinimum.Y) &&
                   IsFinite(containmentMinimum.Z) &&
                   IsFinite(containmentMaximum.X) &&
                   IsFinite(containmentMaximum.Y) &&
                   IsFinite(containmentMaximum.Z) &&
                   containmentMaximum.X > containmentMinimum.X &&
                   containmentMaximum.Y > containmentMinimum.Y &&
                   containmentMaximum.Z > containmentMinimum.Z;
        }

        private static string FormatCoordinate(
            LinkageRouteWorkerCoordinateDto coordinate)
        {
            return coordinate == null
                ? "null"
                : string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "({0:0.###},{1:0.###},{2:0.###})",
                    coordinate.X,
                    coordinate.Y,
                    coordinate.Z);
        }

        private static void AddSettingPoints(
            LinkageRouteWorkerPlanarSettingDto setting,
            ICollection<LinkageRouteWorkerCoordinateDto> targetPoints)
        {
            foreach (var settingPoint in setting.PointList ??
                Array.Empty<LinkageRouteWorkerCoordinateDto>())
            {
                if (settingPoint != null)
                {
                    targetPoints.Add(settingPoint);
                }
            }
        }

        private static List<Point3d> ClonePoints(
            IEnumerable<Point3d> sourcePoints)
        {
            return (sourcePoints ?? Enumerable.Empty<Point3d>())
                .Where(sourcePoint => sourcePoint != null)
                .Select(sourcePoint => new Point3d(
                    sourcePoint.X,
                    sourcePoint.Y,
                    sourcePoint.Z))
                .ToList();
        }

        private static List<Point3d> CloneNonDuplicatePoints(
            IEnumerable<Point3d> sourcePoints)
        {
            var result = new List<Point3d>();
            foreach (var sourcePoint in sourcePoints ?? Enumerable.Empty<Point3d>())
            {
                if (sourcePoint == null ||
                    (result.Count > 0 &&
                     result[result.Count - 1].DistanceTo(sourcePoint) <=
                     GeometryTolerance))
                {
                    continue;
                }

                result.Add(new Point3d(
                    sourcePoint.X,
                    sourcePoint.Y,
                    sourcePoint.Z));
            }

            return result;
        }

        private static bool IsUsableSetting(PlanarBendBusbarSetting setting)
        {
            if (setting == null ||
                setting.PointList == null ||
                setting.PointList.Count < 2 ||
                setting.PointList.Any(point => !IsFinite(point)) ||
                !IsFinite(setting.StartBusbarVector) ||
                !IsFinite(setting.WidthVector) ||
                !IsFinitePositive(setting.BusBarWidth) ||
                !IsFinitePositive(setting.BusBarThickness))
            {
                return false;
            }

            var nonZeroSegmentDirections = new List<Vector3d>();
            for (var pointIndex = 0;
                pointIndex + 1 < setting.PointList.Count;
                pointIndex++)
            {
                var segmentDirection = new Vector3d(
                    setting.PointList[pointIndex],
                    setting.PointList[pointIndex + 1]);
                if (!IsFinite(segmentDirection) ||
                    !IsFinite(segmentDirection.Norm))
                {
                    return false;
                }

                if (segmentDirection.Norm <= GeometryTolerance)
                {
                    continue;
                }

                if (!AreOrthogonal(segmentDirection, setting.WidthVector))
                {
                    return false;
                }

                nonZeroSegmentDirections.Add(segmentDirection);
            }

            if (nonZeroSegmentDirections.Count == 0)
            {
                return false;
            }

            var firstLengthDirection = nonZeroSegmentDirections[0];
            return AreOrthogonal(
                    firstLengthDirection,
                    setting.WidthVector) &&
                AreOrthogonal(
                    firstLengthDirection,
                    setting.StartBusbarVector) &&
                AreOrthogonal(
                    setting.WidthVector,
                    setting.StartBusbarVector);
        }

        private static bool IsFinite(Point3d point)
        {
            return point != null &&
                IsFinite(point.X) &&
                IsFinite(point.Y) &&
                IsFinite(point.Z);
        }

        private static bool IsFinite(Vector3d vector)
        {
            return vector != null &&
                IsFinite(vector.X) &&
                IsFinite(vector.Y) &&
                IsFinite(vector.Z);
        }

        private static bool IsFinitePositive(double scalarValue)
        {
            return IsFinite(scalarValue) && scalarValue > GeometryTolerance;
        }

        private static bool IsFiniteNonNegative(double scalarValue)
        {
            return IsFinite(scalarValue) && scalarValue >= 0d;
        }

        private static bool IsFinite(double scalarValue)
        {
            return !double.IsNaN(scalarValue) &&
                !double.IsInfinity(scalarValue);
        }

        private static string ResolvePhaseName(string groupKey)
        {
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                return null;
            }

            var normalizedGroupKey = groupKey.Trim();
            return PhaseNames.FirstOrDefault(phaseName =>
                normalizedGroupKey.StartsWith(
                    phaseName,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static int FindFirstNonZeroSegmentIndex(
            IReadOnlyList<Point3d> points)
        {
            for (var pointIndex = 0;
                pointIndex + 1 < (points?.Count ?? 0);
                pointIndex++)
            {
                if (points[pointIndex].DistanceTo(points[pointIndex + 1]) >
                    GeometryTolerance)
                {
                    return pointIndex;
                }
            }

            return -1;
        }

        private static int FindLastNonZeroSegmentIndex(
            IReadOnlyList<Point3d> points)
        {
            for (var pointIndex = (points?.Count ?? 0) - 2;
                pointIndex >= 0;
                pointIndex--)
            {
                if (points[pointIndex].DistanceTo(points[pointIndex + 1]) >
                    GeometryTolerance)
                {
                    return pointIndex;
                }
            }

            return -1;
        }

        private static Point3d GetTerminalPoint(
            PlanarBendBusbarSetting setting)
        {
            return setting.PointList[setting.PointList.Count - 1];
        }

        private static Point3d GetSettingCenter(
            PlanarBendBusbarSetting setting)
        {
            return new Point3d(
                setting.PointList.Average(point => point.X),
                setting.PointList.Average(point => point.Y),
                setting.PointList.Average(point => point.Z));
        }

        private static Vector3d AverageAlignedVectors(
            IEnumerable<Vector3d> sourceVectors)
        {
            var usableVectors = sourceVectors
                .Where(sourceVector =>
                    sourceVector != null &&
                    sourceVector.Norm > GeometryTolerance)
                .Select(sourceVector => sourceVector.Normalized)
                .ToList();
            if (usableVectors.Count == 0)
            {
                throw new InvalidOperationException(
                    "固定端没有可用的公共方向。" );
            }

            var referenceVector = usableVectors[0];
            var alignedVectors = usableVectors.Select(sourceVector =>
                sourceVector.Dot(referenceVector) < 0d
                    ? sourceVector.Negate()
                    : sourceVector).ToList();
            if (alignedVectors.Any(sourceVector =>
                sourceVector.Cross(referenceVector).Norm >
                DirectionTolerance))
            {
                throw new InvalidOperationException(
                    "同一固定端成员的方向不一致。" );
            }

            return new Vector3d(
                alignedVectors.Average(sourceVector => sourceVector.X),
                alignedVectors.Average(sourceVector => sourceVector.Y),
                alignedVectors.Average(sourceVector => sourceVector.Z)).Normalized;
        }

        private static LinkageRouteWorkerCoordinateDto AverageCoordinates(
            IEnumerable<LinkageRouteWorkerCoordinateDto> sourceCoordinates)
        {
            var coordinates = sourceCoordinates
                .Where(sourceCoordinate => sourceCoordinate != null)
                .ToList();
            if (coordinates.Count == 0)
            {
                throw new InvalidOperationException(
                    "固定端没有可用的中心线锚点。" );
            }

            return new LinkageRouteWorkerCoordinateDto
            {
                X = coordinates.Average(coordinate => coordinate.X),
                Y = coordinates.Average(coordinate => coordinate.Y),
                Z = coordinates.Average(coordinate => coordinate.Z)
            };
        }

        private static Point3d AveragePoints(IEnumerable<Point3d> sourcePoints)
        {
            var points = (sourcePoints ?? Enumerable.Empty<Point3d>())
                .Where(sourcePoint => sourcePoint != null)
                .ToList();
            if (points.Count == 0)
            {
                throw new InvalidOperationException(
                    "没有可用于计算固定端位置的中心线点。");
            }

            return new Point3d(
                points.Average(point => point.X),
                points.Average(point => point.Y),
                points.Average(point => point.Z));
        }

        private static bool AreOrthogonal(Vector3d firstVector, Vector3d secondVector)
        {
            return firstVector != null && secondVector != null &&
                firstVector.Norm > GeometryTolerance &&
                secondVector.Norm > GeometryTolerance &&
                Math.Abs(firstVector.Normalized.Dot(secondVector.Normalized)) <=
                DirectionTolerance;
        }

        private static bool IsGlobalAxisDirection(Vector3d direction)
        {
            var normalizedDirection = direction.Normalized;
            var nonZeroCoordinateCount = 0;
            if (Math.Abs(normalizedDirection.X) > DirectionTolerance)
            {
                nonZeroCoordinateCount++;
            }

            if (Math.Abs(normalizedDirection.Y) > DirectionTolerance)
            {
                nonZeroCoordinateCount++;
            }

            if (Math.Abs(normalizedDirection.Z) > DirectionTolerance)
            {
                nonZeroCoordinateCount++;
            }

            return nonZeroCoordinateCount == 1;
        }

        private static double Project(Point3d point, Vector3d direction)
        {
            return point.X * direction.X +
                point.Y * direction.Y +
                point.Z * direction.Z;
        }

        private static string FormatPoint(Point3d point)
        {
            return point == null
                ? "null"
                : "(" + point.X.ToString("0.###") +
                  "," + point.Y.ToString("0.###") +
                  "," + point.Z.ToString("0.###") + ")";
        }

        private static string FormatVector(Vector3d vector)
        {
            return vector == null
                ? "null"
                : "(" + vector.X.ToString("0.###") +
                  "," + vector.Y.ToString("0.###") +
                  "," + vector.Z.ToString("0.###") + ")";
        }

        private static LinkageRouteWorkerCoordinateDto ToCoordinate(Point3d point)
        {
            return new LinkageRouteWorkerCoordinateDto
            {
                X = point.X,
                Y = point.Y,
                Z = point.Z
            };
        }

        private static LinkageRouteWorkerCoordinateDto ToCoordinate(Vector3d vector)
        {
            return new LinkageRouteWorkerCoordinateDto
            {
                X = vector.X,
                Y = vector.Y,
                Z = vector.Z
            };
        }

        private static Point3d ToPoint(LinkageRouteWorkerCoordinateDto coordinate)
        {
            return new Point3d(coordinate.X, coordinate.Y, coordinate.Z);
        }

        private static Vector3d ToVector(LinkageRouteWorkerCoordinateDto coordinate)
        {
            return new Vector3d(coordinate.X, coordinate.Y, coordinate.Z);
        }

        private sealed class PreparedTerminalMember
        {
            public Point3d BoundaryPoint { get; set; }
            public Vector3d TerminalLengthDirection { get; set; }
            public Vector3d TerminalThicknessDirection { get; set; }
            public LinkageRouteWorkerPlanarSettingDto TerminalSetting { get; set; }
        }

        private sealed class WallBushingRouteCandidate
        {
            public PlanarBendBusbarSetting Setting { get; set; }
            public Point3d BoundaryPoint { get; set; }
            public double BoundaryCoordinate { get; set; }
            public double LongitudinalDistance { get; set; }
        }

        private sealed class WallBushingTerminalPlaneCluster
        {
            public List<WallBushingRouteCandidate> Candidates { get; } =
                new List<WallBushingRouteCandidate>();

            public double AverageBoundaryCoordinate =>
                Candidates.Average(routeCandidate =>
                    routeCandidate.BoundaryCoordinate);

            public double AverageLongitudinalDistance =>
                Candidates.Average(routeCandidate =>
                    routeCandidate.LongitudinalDistance);
        }
    }
}
