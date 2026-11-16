using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.models;
using CKBusbarBridgePreview;
using GeometRi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Services
{
    /// <summary>
    /// 母排设置界面可调整的 Worker v7 根请求与搜索参数。
    /// 默认值与 Worker 输入参数说明中的交互生产推荐值保持一致。
    /// </summary>
    internal sealed class BusbarAlgorithmWorkerRequestOptions
    {
        public double MinimumSegmentLength { get; set; } = 40;
        public double BendingRadius { get; set; } = 10;
        public double BendAllowance { get; set; } = 20;
        public double InsulationClearance { get; set; } = 125;
        public bool AllowDoubleStartMiddleLap { get; set; }
        public bool RequireSingleStartMiddleLap { get; set; } = true;
        public bool RequireAxisAlignedRoutes { get; set; } = true;
        public bool UseExplicitContainmentBox { get; set; } = true;
        /// <summary>面对墙场景仅交换末端穿墙套管的 A/C 相搭接目标。</summary>
        public bool SwapPhaseAAndCAtEnd { get; set; }
        public int FirstSolutionTargetMilliseconds { get; set; } = 15000;
        public int TotalSearchLimitMilliseconds { get; set; } = 60000;
        public bool EnableAutomaticTotalSearchLimit { get; set; }
        public int MaximumCandidatesPerGroup { get; set; } = 128;
        public int MaximumClearanceRepairCandidatesPerState { get; set; } = 64;
        public int MaximumSolutionsPerAlgorithm { get; set; } = 10;
        public int BeamWidth { get; set; } = 24;
        public int MaximumDegreeOfParallelism { get; set; } = Math.Max(
            1,
            System.Environment.ProcessorCount - 1);
        public double MinimumAdaptiveRepairStep { get; set; } = 0.5;

        public static BusbarAlgorithmWorkerRequestOptions CreateDefault()
        {
            return new BusbarAlgorithmWorkerRequestOptions();
        }

        public bool TryValidate(out string validationMessage)
        {
            validationMessage = null;
            if (!IsFiniteNonNegative(MinimumSegmentLength))
            {
                validationMessage = "最小段长必须是大于或等于 0 的有效数值。";
                return false;
            }

            if (!IsFinitePositive(BendingRadius))
            {
                validationMessage = "折弯半径必须是大于 0 的有效数值。";
                return false;
            }

            if (!IsFiniteNonNegative(BendAllowance))
            {
                validationMessage = "折弯余量必须是大于或等于 0 的有效数值。";
                return false;
            }

            if (!IsFiniteNonNegative(InsulationClearance))
            {
                validationMessage = "绝缘距离必须是大于或等于 0 的有效数值。";
                return false;
            }

            if (FirstSolutionTargetMilliseconds <= 0)
            {
                validationMessage = "首方案目标时间必须大于 0 毫秒。";
                return false;
            }

            if (TotalSearchLimitMilliseconds <= 0)
            {
                validationMessage = "搜索总时限必须大于 0 毫秒。";
                return false;
            }

            if (MaximumCandidatesPerGroup <= 0 ||
                MaximumClearanceRepairCandidatesPerState <= 0 ||
                MaximumSolutionsPerAlgorithm <= 0 ||
                BeamWidth <= 0 ||
                MaximumDegreeOfParallelism <= 0)
            {
                validationMessage = "候选数、方案数、Beam 宽度和并行度都必须大于 0。";
                return false;
            }

            if (!IsFinitePositive(MinimumAdaptiveRepairStep))
            {
                validationMessage = "最小自适应修复步长必须是大于 0 的有效数值。";
                return false;
            }

            return true;
        }

        private static bool IsFinitePositive(double parameterValue)
        {
            return !double.IsNaN(parameterValue) &&
                !double.IsInfinity(parameterValue) &&
                parameterValue > 0;
        }

        private static bool IsFiniteNonNegative(double parameterValue)
        {
            return !double.IsNaN(parameterValue) &&
                !double.IsInfinity(parameterValue) &&
                parameterValue >= 0;
        }
    }

    /// <summary>
    /// 将当前母排分段的真实预览中心线转换为 Worker v7 请求。
    /// 选定段由 Worker 生成，前后相邻直排作为物理固定端。
    /// </summary>
    internal static class BusbarAlgorithmWorkerRequestFactory
    {
        private const double PreviewBusbarInsulatorDistance = 271.5;
        private const double CoordinateTolerance = 0.000001;

        private static readonly string[] PhaseNames = { "A", "B", "C" };

        public static bool TryCreateWorkerRequest(
            BusDuctData busDuctData,
            IReadOnlyList<BusbarSegmentItem> currentBusbarSegments,
            BusbarSegmentItem algorithmTargetSegment,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            out LinkageRouteWorkerRequestDto workerRequest,
            out string validationMessage)
        {
            workerRequest = null;
            validationMessage = null;

            if (busDuctData == null || algorithmTargetSegment == null)
            {
                validationMessage = "请选择需要由算法生成的内部母排段。";
                return false;
            }

            var normalizedWorkerRequestOptions = workerRequestOptions ??
                BusbarAlgorithmWorkerRequestOptions.CreateDefault();
            if (!normalizedWorkerRequestOptions.TryValidate(out validationMessage))
            {
                return false;
            }

            if (!BusDuctPreview.TryBuildPositionList(
                busDuctData,
                PreviewBusbarInsulatorDistance,
                out var busbarPositionList,
                out validationMessage))
            {
                return false;
            }

            var busbarSegmentCount = busbarPositionList.Count - 1;
            var algorithmTargetSegmentIndex = algorithmTargetSegment.Index - 1;
            if (algorithmTargetSegmentIndex <= 0 ||
                algorithmTargetSegmentIndex >= busbarSegmentCount - 1)
            {
                validationMessage =
                    "算法生成段必须位于首段与末段之间，且两侧都保留一段固定直排。";
                return false;
            }

            var normalizedBusbarSegments = (currentBusbarSegments ??
                    Array.Empty<BusbarSegmentItem>())
                .Where(busbarSegmentItem => busbarSegmentItem != null)
                .OrderBy(busbarSegmentItem => busbarSegmentItem.Index)
                .ToList();
            var leftFixedBusbarSegment = normalizedBusbarSegments.ElementAtOrDefault(
                algorithmTargetSegmentIndex - 1);
            var rightFixedBusbarSegment = normalizedBusbarSegments.ElementAtOrDefault(
                algorithmTargetSegmentIndex + 1);
            if (leftFixedBusbarSegment?.IsPhaseSwitched == true ||
                rightFixedBusbarSegment?.IsPhaseSwitched == true)
            {
                validationMessage =
                    "算法生成段左右相邻段必须是普通固定直排，不能使用换相排作为固定端。";
                return false;
            }

            if (!TryReadBusbarGeometry(
                busDuctData,
                out var bridgeDirection,
                out var phaseDirection,
                out var thicknessDirection,
                out var widthDirection,
                out var busbarWidth,
                out var busbarThickness,
                out var stackCount,
                out validationMessage))
            {
                return false;
            }

            var phaseSpacing = busDuctData.PhaseDis;
            if (!IsFinitePositive(phaseSpacing))
            {
                validationMessage = "当前相间距无效，无法构造三相算法输入。";
                return false;
            }

            var requestGroups = new List<LinkageRouteWorkerGroupRequestDto>();
            var containmentSourcePoints = new List<LinkageRouteWorkerCoordinateDto>();
            for (var phaseIndex = 0; phaseIndex < PhaseNames.Length; phaseIndex++)
            {
                var phaseName = PhaseNames[phaseIndex];
                var groupKey = "PHASE-" + phaseName;
                var phaseOffsetDistance = phaseSpacing * phaseIndex;
                var leftTerminalMembers = BuildTerminalMembers(
                    groupKey,
                    "S",
                    busbarPositionList,
                    algorithmTargetSegmentIndex - 1,
                    phaseDirection,
                    phaseOffsetDistance,
                    bridgeDirection,
                    thicknessDirection,
                    widthDirection,
                    busbarWidth,
                    busbarThickness,
                    stackCount,
                    normalizedWorkerRequestOptions,
                    containmentSourcePoints);
                var rightTerminalMembers = BuildTerminalMembers(
                    groupKey,
                    "E",
                    busbarPositionList,
                    algorithmTargetSegmentIndex + 1,
                    phaseDirection,
                    phaseOffsetDistance,
                    bridgeDirection,
                    thicknessDirection,
                    widthDirection,
                    busbarWidth,
                    busbarThickness,
                    stackCount,
                    normalizedWorkerRequestOptions,
                    containmentSourcePoints);

                requestGroups.Add(new LinkageRouteWorkerGroupRequestDto
                {
                    GroupKey = groupKey,
                    Phase = phaseName,
                    DisplayName = phaseName + "相",
                    StartTerminal = new LinkageRouteWorkerTerminalAssemblyDto
                    {
                        RouteAnchor = AverageCoordinates(
                            leftTerminalMembers
                                .Select(terminalMember =>
                                    terminalMember.FixedBusbar.PointList.Last())
                                .ToList()),
                        LengthDirection = ToCoordinate(bridgeDirection),
                        ThicknessDirection = ToCoordinate(thicknessDirection),
                        Members = leftTerminalMembers
                    },
                    EndTerminal = new LinkageRouteWorkerTerminalAssemblyDto
                    {
                        RouteAnchor = AverageCoordinates(
                            rightTerminalMembers
                                .Select(terminalMember =>
                                    terminalMember.FixedBusbar.PointList.First())
                                .ToList()),
                        LengthDirection = ToCoordinate(bridgeDirection),
                        ThicknessDirection = ToCoordinate(thicknessDirection),
                        Members = rightTerminalMembers
                    },
                    BusbarWidth = busbarWidth,
                    BusbarThickness = busbarThickness,
                    StackCount = stackCount,
                    StackStartSpace = busbarThickness,
                    StackMiddleSpace = busbarThickness,
                    StackEndSpace = busbarThickness,
                    StackMoveVector = ToCoordinate(thicknessDirection),
                    StackReverse = false
                });
            }

            var stackEnvelopeThickness = busbarThickness +
                (stackCount - 1) * (busbarThickness + busbarThickness);
            var containmentMargin = normalizedWorkerRequestOptions.InsulationClearance +
                busbarWidth +
                stackEnvelopeThickness +
                normalizedWorkerRequestOptions.BendAllowance +
                normalizedWorkerRequestOptions.MinimumSegmentLength;
            BuildContainmentBox(
                containmentSourcePoints,
                containmentMargin,
                out var containmentMinimum,
                out var containmentMaximum);

            workerRequest = new LinkageRouteWorkerRequestDto
            {
                Groups = requestGroups,
                Obstacles = new List<LinkageRouteWorkerObstacleDto>(),
                DefaultBusbarWidth = busbarWidth,
                DefaultBusbarThickness = busbarThickness,
                DefaultStackCount = stackCount,
                MinimumSegmentLength = normalizedWorkerRequestOptions.MinimumSegmentLength,
                BendAllowance = normalizedWorkerRequestOptions.BendAllowance,
                BendingRadius = normalizedWorkerRequestOptions.BendingRadius,
                InsulationClearance = normalizedWorkerRequestOptions.InsulationClearance,
                AllowDoubleStartMiddleLap = normalizedWorkerRequestOptions.AllowDoubleStartMiddleLap,
                RequireSingleStartMiddleLap = normalizedWorkerRequestOptions.RequireSingleStartMiddleLap,
                RequireAxisAlignedRoutes = normalizedWorkerRequestOptions.RequireAxisAlignedRoutes,
                UseExplicitContainmentBox = normalizedWorkerRequestOptions.UseExplicitContainmentBox,
                ContainmentMinimum = containmentMinimum,
                ContainmentMaximum = containmentMaximum,
                SearchOptions = new LinkageRouteWorkerSearchOptionsDto
                {
                    FirstSolutionTargetMilliseconds = normalizedWorkerRequestOptions.FirstSolutionTargetMilliseconds,
                    TotalSearchLimitMilliseconds = normalizedWorkerRequestOptions.TotalSearchLimitMilliseconds,
                    EnableAutomaticTotalSearchLimit = normalizedWorkerRequestOptions.EnableAutomaticTotalSearchLimit,
                    MaximumCandidatesPerGroup = normalizedWorkerRequestOptions.MaximumCandidatesPerGroup,
                    MaximumClearanceRepairCandidatesPerState = normalizedWorkerRequestOptions.MaximumClearanceRepairCandidatesPerState,
                    MaximumSolutionsPerAlgorithm = normalizedWorkerRequestOptions.MaximumSolutionsPerAlgorithm,
                    BeamWidth = normalizedWorkerRequestOptions.BeamWidth,
                    MaximumDegreeOfParallelism = normalizedWorkerRequestOptions.MaximumDegreeOfParallelism,
                    MinimumAdaptiveRepairStep = normalizedWorkerRequestOptions.MinimumAdaptiveRepairStep
                }
            };

            return true;
        }

        private static bool TryReadBusbarGeometry(
            BusDuctData busDuctData,
            out Vector3d bridgeDirection,
            out Vector3d phaseDirection,
            out Vector3d thicknessDirection,
            out Vector3d widthDirection,
            out double busbarWidth,
            out double busbarThickness,
            out int stackCount,
            out string validationMessage)
        {
            bridgeDirection = busDuctData.XVector?.Normalized;
            phaseDirection = busDuctData.YVector?.Negate().Normalized;
            thicknessDirection = busDuctData.BendSetting?.StartBusbarVector?.Normalized;
            widthDirection = busDuctData.BendSetting?.WidthVector?.Normalized;
            busbarWidth = busDuctData.BendSetting?.BusBarWidth ?? 0;
            busbarThickness = busDuctData.BendSetting?.BusBarThickness ?? 0;
            stackCount = Math.Max(1, busDuctData.BusbarNum);
            validationMessage = null;

            if (bridgeDirection == null || phaseDirection == null ||
                thicknessDirection == null || widthDirection == null)
            {
                validationMessage = "无法从当前母排预览读取有效的三维方向。";
                return false;
            }

            if (!IsGlobalAxisDirection(bridgeDirection) ||
                !IsGlobalAxisDirection(phaseDirection) ||
                !IsGlobalAxisDirection(thicknessDirection) ||
                !IsGlobalAxisDirection(widthDirection))
            {
                validationMessage =
                    "当前母排固定端必须与全局 XYZ 轴对齐，无法构造有效算法输入。";
                return false;
            }

            if (!IsFinitePositive(busbarWidth) || !IsFinitePositive(busbarThickness))
            {
                validationMessage = "当前母排排宽或排厚无效。";
                return false;
            }

            return true;
        }

        private static IList<LinkageRouteWorkerTerminalMemberDto> BuildTerminalMembers(
            string groupKey,
            string terminalKey,
            IReadOnlyList<Point3d> busbarPositionList,
            int fixedSegmentIndex,
            Vector3d phaseDirection,
            double phaseOffsetDistance,
            Vector3d bridgeDirection,
            Vector3d thicknessDirection,
            Vector3d widthDirection,
            double busbarWidth,
            double busbarThickness,
            int stackCount,
            BusbarAlgorithmWorkerRequestOptions workerRequestOptions,
            ICollection<LinkageRouteWorkerCoordinateDto> containmentSourcePoints)
        {
            var terminalMembers = new List<LinkageRouteWorkerTerminalMemberDto>();
            var alternatingStackOffsetIndex = fixedSegmentIndex % 2 == 0 ? 0 : 1;
            for (var stackMemberIndex = 0;
                 stackMemberIndex < stackCount;
                 stackMemberIndex++)
            {
                var stackOffsetDistance = busbarThickness *
                    (stackMemberIndex * 2 + alternatingStackOffsetIndex);
                var fixedBusbarStartPoint = busbarPositionList[fixedSegmentIndex]
                    .Move(phaseDirection, phaseOffsetDistance)
                    .Move(thicknessDirection, stackOffsetDistance)
                    .Move(bridgeDirection.Negate(), busbarWidth / 2);
                var fixedBusbarEndPoint = busbarPositionList[fixedSegmentIndex + 1]
                    .Move(phaseDirection, phaseOffsetDistance)
                    .Move(thicknessDirection, stackOffsetDistance)
                    .Move(bridgeDirection, busbarWidth / 2);
                var workerStartCoordinate = ToCoordinate(fixedBusbarStartPoint);
                var workerEndCoordinate = ToCoordinate(fixedBusbarEndPoint);
                containmentSourcePoints.Add(workerStartCoordinate);
                containmentSourcePoints.Add(workerEndCoordinate);

                terminalMembers.Add(new LinkageRouteWorkerTerminalMemberDto
                {
                    MemberKey = string.Format(
                        "{0}-{1}{2}",
                        groupKey,
                        terminalKey,
                        stackMemberIndex + 1),
                    FixedBusbar = new LinkageRouteWorkerPlanarSettingDto
                    {
                        IsCenter = true,
                        StartBusbarVector = ToCoordinate(thicknessDirection),
                        WidthVector = ToCoordinate(widthDirection),
                        PointList = new List<LinkageRouteWorkerCoordinateDto>
                        {
                            workerStartCoordinate,
                            workerEndCoordinate
                        },
                        BarBendingRadius = workerRequestOptions.BendingRadius,
                        BendAllowance = workerRequestOptions.BendAllowance,
                        BusBarThickness = busbarThickness,
                        BusBarWidth = busbarWidth,
                        StartLapWidth = busbarWidth,
                        EndLapWidth = busbarWidth,
                        GroupKey = groupKey
                    },
                    // 现有模型没有可靠保存不可伸出方向；留空后由 Worker 按对端最近点
                    // 选择搭接边界，不能用零向量或猜测方向代替。
                    ForbiddenHorizontalExitDirection = null
                });
            }

            return terminalMembers;
        }

        private static void BuildContainmentBox(
            IReadOnlyCollection<LinkageRouteWorkerCoordinateDto> sourcePoints,
            double containmentMargin,
            out LinkageRouteWorkerCoordinateDto containmentMinimum,
            out LinkageRouteWorkerCoordinateDto containmentMaximum)
        {
            containmentMinimum = new LinkageRouteWorkerCoordinateDto
            {
                X = sourcePoints.Min(sourcePoint => sourcePoint.X) - containmentMargin,
                Y = sourcePoints.Min(sourcePoint => sourcePoint.Y) - containmentMargin,
                Z = sourcePoints.Min(sourcePoint => sourcePoint.Z) - containmentMargin
            };
            containmentMaximum = new LinkageRouteWorkerCoordinateDto
            {
                X = sourcePoints.Max(sourcePoint => sourcePoint.X) + containmentMargin,
                Y = sourcePoints.Max(sourcePoint => sourcePoint.Y) + containmentMargin,
                Z = sourcePoints.Max(sourcePoint => sourcePoint.Z) + containmentMargin
            };
        }

        private static LinkageRouteWorkerCoordinateDto AverageCoordinates(
            IReadOnlyCollection<LinkageRouteWorkerCoordinateDto> coordinates)
        {
            return new LinkageRouteWorkerCoordinateDto
            {
                X = coordinates.Average(coordinate => coordinate.X),
                Y = coordinates.Average(coordinate => coordinate.Y),
                Z = coordinates.Average(coordinate => coordinate.Z)
            };
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

        private static bool IsGlobalAxisDirection(Vector3d direction)
        {
            var nonZeroComponentCount = 0;
            if (Math.Abs(direction.X) > CoordinateTolerance)
            {
                nonZeroComponentCount++;
            }

            if (Math.Abs(direction.Y) > CoordinateTolerance)
            {
                nonZeroComponentCount++;
            }

            if (Math.Abs(direction.Z) > CoordinateTolerance)
            {
                nonZeroComponentCount++;
            }

            return nonZeroComponentCount == 1;
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) &&
                   !double.IsInfinity(value) &&
                   value > CoordinateTolerance;
        }
    }

    internal sealed class BusbarWorkerCenterlinePreviewPath
    {
        public string GroupKey { get; set; }
        public IList<LinkageRouteWorkerCoordinateDto> Points { get; set; } =
            new List<LinkageRouteWorkerCoordinateDto>();
    }

    /// <summary>
    /// 实际生成时的一组普通段排。每组生成完成后共同组成一个子装配体；
    /// 算法中间段组可以包含各相返回的不同数量、不同序号的全部排。
    /// </summary>
    internal sealed class AlgorithmBusbarGenerationGroup
    {
        public string DisplayName { get; set; }
        public bool IsBridge { get; set; }
        public bool RestrictSourceReuseToCurrentGroup { get; set; }

        public IReadOnlyList<PlanarBendBusbarSetting> Busbars { get; set; } =
            Array.Empty<PlanarBendBusbarSetting>();
    }

    internal static class BusbarWorkerSolutionBusbarExtractor
    {
        private const double DirectionSquaredTolerance = 0.000000000001d;
        private const double GenerationSegmentLengthTolerance = 0.000001d;

        public static bool TryExtractCompletePreview(
            LinkageRouteWorkerRequestDto workerRequest,
            LinkageRouteWorkerSolutionDto workerSolution,
            out IReadOnlyList<PlanarBendBusbarSetting> previewBusbars,
            out string validationMessage)
        {
            var extractedPreviewBusbars = new List<PlanarBendBusbarSetting>();
            previewBusbars = Array.Empty<PlanarBendBusbarSetting>();
            validationMessage = null;
            if (workerSolution == null)
            {
                validationMessage = "请选择需要预览的方案。";
                return false;
            }

            if (!TryAddTerminalBusbars(
                    extractedPreviewBusbars,
                    workerRequest,
                    requestGroup => requestGroup.StartTerminal,
                    "始端固定排",
                    out validationMessage))
            {
                return false;
            }

            if (!TryAddGeneratedBusbars(
                    extractedPreviewBusbars,
                    workerSolution,
                    out validationMessage))
            {
                return false;
            }

            if (!TryAddTerminalBusbars(
                    extractedPreviewBusbars,
                    workerRequest,
                    requestGroup => requestGroup.EndTerminal,
                    "末端固定排",
                    out validationMessage))
            {
                return false;
            }

            if (extractedPreviewBusbars.Count == 0)
            {
                validationMessage = "选中方案没有可预览的铜排实体参数。";
                return false;
            }

            previewBusbars = extractedPreviewBusbars.AsReadOnly();
            return true;
        }

        /// <summary>
        /// 提取实际需要落盘生成的铜排。面对墙的 EndTerminal 是按穿墙套管
        /// 构造的虚拟定位排，不能生成；StartTerminal 则需要从 Worker 协议所需的
        /// 反向点序恢复为算法始端普通段的模型方向。
        /// </summary>
        public static bool TryExtractGenerationBusbars(
            LinkageRouteWorkerRequestDto workerRequest,
            LinkageRouteWorkerSolutionDto workerSolution,
            bool isFaceToWallScenario,
            out IReadOnlyList<PlanarBendBusbarSetting> generationBusbars,
            out string validationMessage)
        {
            generationBusbars = Array.Empty<PlanarBendBusbarSetting>();
            if (!TryExtractGenerationGroups(
                    workerRequest,
                    workerSolution,
                    isFaceToWallScenario,
                    out var generationGroups,
                    out validationMessage))
            {
                return false;
            }

            generationBusbars = generationGroups
                .SelectMany(generationGroup => generationGroup.Busbars)
                .ToList()
                .AsReadOnly();
            return true;
        }

        /// <summary>
        /// 保留 Worker 的普通段层级提取实际生成组。始端、算法中间段和非面对墙
        /// 的末端分别成组；算法中间段不要求 A/B/C 各相返回相同数量的
        /// PlanarParts，所有算法返回的铜排统一放入一个独立子装配体组。
        /// </summary>
        public static bool TryExtractGenerationGroups(
            LinkageRouteWorkerRequestDto workerRequest,
            LinkageRouteWorkerSolutionDto workerSolution,
            bool isFaceToWallScenario,
            out IReadOnlyList<AlgorithmBusbarGenerationGroup> generationGroups,
            out string validationMessage)
        {
            var extractedGenerationGroups =
                new List<AlgorithmBusbarGenerationGroup>();
            generationGroups = Array.Empty<AlgorithmBusbarGenerationGroup>();
            validationMessage = null;
            if (workerSolution == null)
            {
                validationMessage = "请选择需要生成的方案。";
                return false;
            }

            if (!TryOrderAndValidateGenerationPhaseData(
                    workerRequest,
                    workerSolution,
                    isFaceToWallScenario,
                    out var orderedRequestGroups,
                    out var orderedSolutionGroups,
                    out validationMessage))
            {
                return false;
            }

            var orderedWorkerRequest = new LinkageRouteWorkerRequestDto
            {
                Groups = orderedRequestGroups.ToList()
            };
            var orderedWorkerSolution = new LinkageRouteWorkerSolutionDto
            {
                GroupSolutions = orderedSolutionGroups.ToList()
            };

            if (!TryCreateTerminalGenerationGroup(
                    orderedWorkerRequest,
                    requestGroup => requestGroup.StartTerminal,
                    isFaceToWallScenario ? "算法始端普通段" : "始端普通段",
                    "始端固定排",
                    restoreFaceToWallStartDirection: isFaceToWallScenario,
                    out var startGenerationGroup,
                    out validationMessage))
            {
                return false;
            }

            if (startGenerationGroup != null)
            {
                extractedGenerationGroups.Add(startGenerationGroup);
            }

            if (!TryAddGeneratedBusbarGroups(
                    extractedGenerationGroups,
                    orderedWorkerSolution,
                    out validationMessage))
            {
                return false;
            }

            if (!isFaceToWallScenario)
            {
                if (!TryCreateTerminalGenerationGroup(
                        orderedWorkerRequest,
                        requestGroup => requestGroup.EndTerminal,
                        "末端普通段",
                        "末端固定排",
                        restoreFaceToWallStartDirection: false,
                        out var endGenerationGroup,
                        out validationMessage))
                {
                    return false;
                }

                if (endGenerationGroup != null)
                {
                    extractedGenerationGroups.Add(endGenerationGroup);
                }
            }

            if (extractedGenerationGroups.Count == 0 ||
                extractedGenerationGroups.All(generationGroup =>
                    generationGroup.Busbars == null ||
                    generationGroup.Busbars.Count == 0))
            {
                validationMessage = "选中方案没有可生成的铜排实体参数。";
                return false;
            }

            generationGroups = extractedGenerationGroups.AsReadOnly();
            return true;
        }

        private static bool TryOrderAndValidateGenerationPhaseData(
            LinkageRouteWorkerRequestDto workerRequest,
            LinkageRouteWorkerSolutionDto workerSolution,
            bool isFaceToWallScenario,
            out IReadOnlyList<LinkageRouteWorkerGroupRequestDto>
                orderedRequestGroups,
            out IReadOnlyList<LinkageRouteWorkerGroupSolutionDto>
                orderedSolutionGroups,
            out string validationMessage)
        {
            orderedRequestGroups =
                Array.Empty<LinkageRouteWorkerGroupRequestDto>();
            orderedSolutionGroups =
                Array.Empty<LinkageRouteWorkerGroupSolutionDto>();
            validationMessage = null;

            var requestGroupsByPhase = new Dictionary<int,
                LinkageRouteWorkerGroupRequestDto>();
            foreach (var workerRequestGroup in workerRequest?.Groups ??
                Array.Empty<LinkageRouteWorkerGroupRequestDto>())
            {
                if (workerRequestGroup == null)
                {
                    continue;
                }

                var requestPhaseIndex = ResolveGenerationPhaseIndex(
                    workerRequestGroup.GroupKey);
                if (requestPhaseIndex < 0)
                {
                    validationMessage = "算法请求包含无法识别相序的组：" +
                        ResolvePreviewGroupName(workerRequestGroup.GroupKey) +
                        "。";
                    return false;
                }

                if (requestGroupsByPhase.ContainsKey(requestPhaseIndex))
                {
                    validationMessage = "算法请求中存在重复的" +
                        ResolveGenerationPhaseName(requestPhaseIndex) +
                        "相组。";
                    return false;
                }

                requestGroupsByPhase.Add(
                    requestPhaseIndex,
                    workerRequestGroup);
            }

            if (requestGroupsByPhase.Count != 3 ||
                !Enumerable.Range(0, 3).All(requestGroupsByPhase.ContainsKey))
            {
                validationMessage =
                    "母排生成要求算法请求同时且仅包含 A、B、C 三相组。";
                return false;
            }

            var solutionGroupsByPhase = new Dictionary<int,
                LinkageRouteWorkerGroupSolutionDto>();
            foreach (var workerSolutionGroup in workerSolution?.GroupSolutions ??
                Array.Empty<LinkageRouteWorkerGroupSolutionDto>())
            {
                if (workerSolutionGroup == null)
                {
                    continue;
                }

                var solutionPhaseIndex = ResolveGenerationPhaseIndex(
                    workerSolutionGroup.GroupKey);
                if (solutionPhaseIndex < 0)
                {
                    validationMessage = "算法方案包含无法识别相序的组：" +
                        ResolvePreviewGroupName(workerSolutionGroup.GroupKey) +
                        "。";
                    return false;
                }

                if (solutionGroupsByPhase.ContainsKey(solutionPhaseIndex))
                {
                    validationMessage = "算法方案中存在重复的" +
                        ResolveGenerationPhaseName(solutionPhaseIndex) +
                        "相组。";
                    return false;
                }

                solutionGroupsByPhase.Add(
                    solutionPhaseIndex,
                    workerSolutionGroup);
            }

            if (solutionGroupsByPhase.Count != 3 ||
                !Enumerable.Range(0, 3).All(solutionGroupsByPhase.ContainsKey))
            {
                validationMessage =
                    "母排生成要求选中方案同时且仅返回 A、B、C 三相组。";
                return false;
            }

            var validatedRequestGroups =
                new List<LinkageRouteWorkerGroupRequestDto>();
            var validatedSolutionGroups =
                new List<LinkageRouteWorkerGroupSolutionDto>();
            for (var generationPhaseIndex = 0;
                generationPhaseIndex < 3;
                generationPhaseIndex++)
            {
                var validatedRequestGroup =
                    requestGroupsByPhase[generationPhaseIndex];
                var validatedSolutionGroup =
                    solutionGroupsByPhase[generationPhaseIndex];
                if (!string.Equals(
                        validatedRequestGroup.GroupKey?.Trim(),
                        validatedSolutionGroup.GroupKey?.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    validationMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}相算法方案组键与请求不一致：请求={1}，方案={2}。",
                        ResolveGenerationPhaseName(generationPhaseIndex),
                        ResolvePreviewGroupName(
                            validatedRequestGroup.GroupKey),
                        ResolvePreviewGroupName(
                            validatedSolutionGroup.GroupKey));
                    return false;
                }

                if (validatedRequestGroup.StackCount <= 0)
                {
                    validationMessage =
                        ResolveGenerationPhaseName(generationPhaseIndex) +
                        "相算法请求的叠排数量无效。";
                    return false;
                }

                var startTerminalMemberCount =
                    validatedRequestGroup.StartTerminal?.Members?
                        .Count(terminalMember =>
                            terminalMember?.FixedBusbar != null) ?? 0;
                if (startTerminalMemberCount !=
                    validatedRequestGroup.StackCount)
                {
                    validationMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}相始端叠排数量不一致：请求={1}，始端成员={2}。",
                        ResolveGenerationPhaseName(generationPhaseIndex),
                        validatedRequestGroup.StackCount,
                        startTerminalMemberCount);
                    return false;
                }

                if (!isFaceToWallScenario)
                {
                    var endTerminalMemberCount =
                        validatedRequestGroup.EndTerminal?.Members?
                            .Count(terminalMember =>
                                terminalMember?.FixedBusbar != null) ?? 0;
                    if (endTerminalMemberCount !=
                        validatedRequestGroup.StackCount)
                    {
                        validationMessage = string.Format(
                            CultureInfo.CurrentCulture,
                            "{0}相末端叠排数量不一致：请求={1}，末端成员={2}。",
                            ResolveGenerationPhaseName(generationPhaseIndex),
                            validatedRequestGroup.StackCount,
                            endTerminalMemberCount);
                        return false;
                    }
                }

                var orderedStackMembers =
                    (validatedSolutionGroup.StackMembers ??
                        Array.Empty<LinkageRouteWorkerStackedMemberDto>())
                    .Where(stackMember => stackMember != null)
                    .OrderBy(stackMember => stackMember.MemberIndex)
                    .ToList();
                if (orderedStackMembers.Count !=
                        validatedRequestGroup.StackCount ||
                    orderedStackMembers.Select(stackMember =>
                            stackMember.MemberIndex)
                        .Distinct()
                        .Count() != validatedRequestGroup.StackCount ||
                    !orderedStackMembers.Select(stackMember =>
                            stackMember.MemberIndex)
                        .SequenceEqual(Enumerable.Range(
                            0,
                            validatedRequestGroup.StackCount)))
                {
                    validationMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}相算法方案叠排成员不完整：请求={1}层，方案成员序号=[{2}]。",
                        ResolveGenerationPhaseName(generationPhaseIndex),
                        validatedRequestGroup.StackCount,
                        string.Join(",",
                            orderedStackMembers.Select(stackMember =>
                                stackMember.MemberIndex)));
                    return false;
                }

                validatedRequestGroups.Add(validatedRequestGroup);
                validatedSolutionGroups.Add(validatedSolutionGroup);
            }

            orderedRequestGroups = validatedRequestGroups.AsReadOnly();
            orderedSolutionGroups = validatedSolutionGroups.AsReadOnly();
            return true;
        }

        private static int ResolveGenerationPhaseIndex(string groupKey)
        {
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                return -1;
            }

            var normalizedGroupKey = groupKey.Trim().ToUpperInvariant();
            if (normalizedGroupKey == "A" ||
                normalizedGroupKey.EndsWith("-A", StringComparison.Ordinal))
            {
                return 0;
            }

            if (normalizedGroupKey == "B" ||
                normalizedGroupKey.EndsWith("-B", StringComparison.Ordinal))
            {
                return 1;
            }

            if (normalizedGroupKey == "C" ||
                normalizedGroupKey.EndsWith("-C", StringComparison.Ordinal))
            {
                return 2;
            }

            return -1;
        }

        private static string ResolveGenerationPhaseName(int phaseIndex)
        {
            return phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C";
        }

        private static bool TryCreateTerminalGenerationGroup(
            LinkageRouteWorkerRequestDto workerRequest,
            Func<LinkageRouteWorkerGroupRequestDto,
                LinkageRouteWorkerTerminalAssemblyDto> terminalSelector,
            string generationGroupDisplayName,
            string terminalDisplayName,
            bool restoreFaceToWallStartDirection,
            out AlgorithmBusbarGenerationGroup generationGroup,
            out string validationMessage)
        {
            generationGroup = null;
            var terminalBusbars = new List<PlanarBendBusbarSetting>();
            if (!TryAddTerminalBusbars(
                    terminalBusbars,
                    workerRequest,
                    terminalSelector,
                    terminalDisplayName,
                    out validationMessage,
                    restoreFaceToWallStartDirection))
            {
                return false;
            }

            if (terminalBusbars.Count == 0)
            {
                return true;
            }

            generationGroup = new AlgorithmBusbarGenerationGroup
            {
                DisplayName = generationGroupDisplayName,
                Busbars = terminalBusbars.AsReadOnly()
            };
            return true;
        }

        private static bool TryAddGeneratedBusbarGroups(
            ICollection<AlgorithmBusbarGenerationGroup> targetGroups,
            LinkageRouteWorkerSolutionDto workerSolution,
            out string validationMessage)
        {
            validationMessage = null;
            var planarMemberSets = new List<WorkerPlanarMemberSet>();
            foreach (var workerGroupSolution in workerSolution.GroupSolutions ??
                Array.Empty<LinkageRouteWorkerGroupSolutionDto>())
            {
                if (workerGroupSolution == null)
                {
                    continue;
                }

                var orderedStackMembers = (workerGroupSolution.StackMembers ??
                        Array.Empty<LinkageRouteWorkerStackedMemberDto>())
                    .Where(stackedMember => stackedMember != null)
                    .OrderBy(stackedMember => stackedMember.MemberIndex)
                    .ToList();
                if (orderedStackMembers.Count == 0)
                {
                    validationMessage =
                        ResolvePreviewGroupName(workerGroupSolution.GroupKey) +
                        "方案没有返回叠排成员，无法按普通段分组生成。";
                    return false;
                }

                foreach (var orderedStackMember in orderedStackMembers)
                {
                    var orderedPlanarParts =
                        (orderedStackMember.PlanarParts ??
                            Array.Empty<LinkageRouteWorkerPlanarSettingDto>())
                        .ToList();
                    if (orderedPlanarParts.Count == 0 ||
                        orderedPlanarParts.Any(planarPart => planarPart == null))
                    {
                        validationMessage = string.Format(
                            CultureInfo.CurrentCulture,
                            "{0}相第{1}层叠排缺少完整的普通段数据。",
                            ResolvePreviewGroupName(
                                workerGroupSolution.GroupKey),
                            orderedStackMember.MemberIndex + 1);
                        return false;
                    }

                    planarMemberSets.Add(new WorkerPlanarMemberSet
                    {
                        GroupKey = workerGroupSolution.GroupKey,
                        MemberIndex = orderedStackMember.MemberIndex,
                        PlanarParts = orderedPlanarParts
                    });
                }
            }

            if (planarMemberSets.Count == 0)
            {
                return true;
            }

            var algorithmGenerationBusbars =
                new List<PlanarBendBusbarSetting>();
            foreach (var planarMemberSet in planarMemberSets)
            {
                for (var planarPartIndex = 0;
                    planarPartIndex < planarMemberSet.PlanarParts.Count;
                    planarPartIndex++)
                {
                    if (!TryConvertPlanarSetting(
                            planarMemberSet.PlanarParts[planarPartIndex],
                            planarMemberSet.GroupKey,
                            string.Format(
                                CultureInfo.CurrentCulture,
                                "{0}相第{1}层第{2}普通段",
                                ResolvePreviewGroupName(
                                    planarMemberSet.GroupKey),
                                planarMemberSet.MemberIndex + 1,
                                planarPartIndex + 1),
                            out var generatedAlgorithmBusbar,
                            out validationMessage))
                    {
                        return false;
                    }

                    algorithmGenerationBusbars.Add(generatedAlgorithmBusbar);
                }
            }

            if (algorithmGenerationBusbars.Count > 0)
            {
                targetGroups.Add(new AlgorithmBusbarGenerationGroup
                {
                    DisplayName = "算法中间段",
                    Busbars = algorithmGenerationBusbars.AsReadOnly()
                });
            }

            return true;
        }

        public static bool TryExtractGeneratedPreview(
            LinkageRouteWorkerSolutionDto workerSolution,
            out IReadOnlyList<PlanarBendBusbarSetting> previewBusbars,
            out string validationMessage)
        {
            var extractedPreviewBusbars = new List<PlanarBendBusbarSetting>();
            previewBusbars = Array.Empty<PlanarBendBusbarSetting>();
            validationMessage = null;
            if (workerSolution == null)
            {
                validationMessage = "请选择需要预览的方案。";
                return false;
            }

            if (!TryAddGeneratedBusbars(
                    extractedPreviewBusbars,
                    workerSolution,
                    out validationMessage))
            {
                return false;
            }

            if (extractedPreviewBusbars.Count == 0)
            {
                validationMessage = "选中方案没有可预览的算法中间铜排实体参数。";
                return false;
            }

            previewBusbars = extractedPreviewBusbars.AsReadOnly();
            return true;
        }

        private static bool TryAddGeneratedBusbars(
            ICollection<PlanarBendBusbarSetting> targetBusbars,
            LinkageRouteWorkerSolutionDto workerSolution,
            out string validationMessage)
        {
            validationMessage = null;
            foreach (var workerGroupSolution in workerSolution.GroupSolutions ??
                Array.Empty<LinkageRouteWorkerGroupSolutionDto>())
            {
                if (workerGroupSolution == null)
                {
                    continue;
                }

                var stackedPlanarParts = (workerGroupSolution.StackMembers ??
                        Array.Empty<LinkageRouteWorkerStackedMemberDto>())
                    .Where(stackedMember => stackedMember != null)
                    .OrderBy(stackedMember => stackedMember.MemberIndex)
                    .SelectMany(stackedMember => stackedMember.PlanarParts ??
                        Array.Empty<LinkageRouteWorkerPlanarSettingDto>())
                    .Where(planarPart => planarPart != null)
                    .ToList();
                var generatedPlanarParts = stackedPlanarParts.Count > 0
                    ? stackedPlanarParts
                    : (workerGroupSolution.PlanarParts ??
                        Array.Empty<LinkageRouteWorkerPlanarSettingDto>())
                        .Where(planarPart => planarPart != null)
                        .ToList();
                if (generatedPlanarParts.Count == 0)
                {
                    validationMessage =
                        ResolvePreviewGroupName(workerGroupSolution.GroupKey) +
                        "方案没有可生成铜排实体的平弯参数。";
                    return false;
                }

                for (var generatedPartIndex = 0;
                    generatedPartIndex < generatedPlanarParts.Count;
                    generatedPartIndex++)
                {
                    if (!TryConvertPlanarSetting(
                            generatedPlanarParts[generatedPartIndex],
                            workerGroupSolution.GroupKey,
                            ResolvePreviewGroupName(workerGroupSolution.GroupKey) +
                            "方案铜排" + (generatedPartIndex + 1),
                            out var generatedPreviewBusbar,
                            out validationMessage))
                    {
                        return false;
                    }

                    targetBusbars.Add(generatedPreviewBusbar);
                }
            }

            return true;
        }

        private static bool TryAddTerminalBusbars(
            ICollection<PlanarBendBusbarSetting> targetBusbars,
            LinkageRouteWorkerRequestDto workerRequest,
            Func<LinkageRouteWorkerGroupRequestDto,
                LinkageRouteWorkerTerminalAssemblyDto> terminalSelector,
            string terminalDisplayName,
            out string validationMessage,
            bool restoreFaceToWallStartDirection = false)
        {
            validationMessage = null;
            foreach (var workerRequestGroup in workerRequest?.Groups ??
                Array.Empty<LinkageRouteWorkerGroupRequestDto>())
            {
                if (workerRequestGroup == null)
                {
                    continue;
                }

                var terminalAssembly = terminalSelector(workerRequestGroup);
                var terminalMembers = (terminalAssembly?.Members ??
                        Array.Empty<LinkageRouteWorkerTerminalMemberDto>())
                    .Where(terminalMember => terminalMember?.FixedBusbar != null)
                    .ToList();
                for (var terminalMemberIndex = 0;
                    terminalMemberIndex < terminalMembers.Count;
                    terminalMemberIndex++)
                {
                    if (!TryConvertPlanarSetting(
                            terminalMembers[terminalMemberIndex].FixedBusbar,
                            workerRequestGroup.GroupKey,
                            ResolvePreviewGroupName(workerRequestGroup.GroupKey) +
                            terminalDisplayName + (terminalMemberIndex + 1),
                            out var terminalPreviewBusbar,
                            out validationMessage))
                    {
                        return false;
                    }

                    if (restoreFaceToWallStartDirection &&
                        !TryRestoreFaceToWallStartDirection(
                            terminalPreviewBusbar,
                            ResolvePreviewGroupName(workerRequestGroup.GroupKey) +
                            terminalDisplayName + (terminalMemberIndex + 1),
                            out validationMessage))
                    {
                        return false;
                    }

                    targetBusbars.Add(terminalPreviewBusbar);
                }
            }

            return true;
        }

        private static bool TryRestoreFaceToWallStartDirection(
            PlanarBendBusbarSetting workerStartBusbar,
            string settingDisplayName,
            out string validationMessage)
        {
            validationMessage = null;
            var workerProtocolPoints = workerStartBusbar?.PointList;
            if (workerProtocolPoints == null || workerProtocolPoints.Count < 2 ||
                workerStartBusbar.WidthVector == null ||
                workerStartBusbar.WidthVector.Norm <= 0d ||
                workerStartBusbar.StartBusbarVector == null ||
                workerStartBusbar.StartBusbarVector.Norm <= 0d)
            {
                validationMessage =
                    settingDisplayName + "无法恢复固定普通段的模型方向。";
                return false;
            }

            var restoredModelPoints = workerProtocolPoints
                .AsEnumerable()
                .Reverse()
                .Select(workerProtocolPoint => new Point3d(
                    workerProtocolPoint.X,
                    workerProtocolPoint.Y,
                    workerProtocolPoint.Z))
                .ToList();
            var restoredFirstSegmentIndex = FindFirstUsableSegmentIndex(
                restoredModelPoints);
            var restoredLastSegmentIndex = FindLastUsableSegmentIndex(
                restoredModelPoints);
            if (restoredFirstSegmentIndex < 0 || restoredLastSegmentIndex < 0)
            {
                validationMessage =
                    settingDisplayName + "恢复模型方向后没有有效直段。";
                return false;
            }

            var normalizedWidthDirection =
                workerStartBusbar.WidthVector.Normalized;
            var restoredLastSegmentDirection = new Vector3d(
                restoredModelPoints[restoredLastSegmentIndex],
                restoredModelPoints[restoredLastSegmentIndex + 1]).Normalized;
            var calculatedLastThicknessDirection =
                restoredLastSegmentDirection.Cross(normalizedWidthDirection);
            if (calculatedLastThicknessDirection == null ||
                calculatedLastThicknessDirection.Norm <= 0d)
            {
                validationMessage =
                    settingDisplayName + "末段与排宽方向无法确定排厚方向。";
                return false;
            }

            var pathThicknessOrientationSign =
                calculatedLastThicknessDirection.Normalized.Dot(
                    workerStartBusbar.StartBusbarVector.Normalized) < 0d
                    ? -1d
                    : 1d;
            var restoredFirstSegmentDirection = new Vector3d(
                restoredModelPoints[restoredFirstSegmentIndex],
                restoredModelPoints[restoredFirstSegmentIndex + 1]).Normalized;
            var restoredStartThicknessDirection =
                restoredFirstSegmentDirection.Cross(normalizedWidthDirection);
            if (restoredStartThicknessDirection == null ||
                restoredStartThicknessDirection.Norm <= 0d)
            {
                validationMessage =
                    settingDisplayName + "首段与排宽方向无法确定排厚方向。";
                return false;
            }

            workerStartBusbar.PointList = restoredModelPoints;
            workerStartBusbar.StartBusbarVector =
                pathThicknessOrientationSign < 0d
                    ? restoredStartThicknessDirection.Normalized.Negate()
                    : restoredStartThicknessDirection.Normalized;
            var workerProtocolStartLapWidth =
                workerStartBusbar.StartLapWidth;
            workerStartBusbar.StartLapWidth = workerStartBusbar.EndLapWidth;
            workerStartBusbar.EndLapWidth = workerProtocolStartLapWidth;
            return true;
        }

        private static int FindFirstUsableSegmentIndex(
            IReadOnlyList<Point3d> sourcePoints)
        {
            for (var sourcePointIndex = 0;
                sourcePointIndex + 1 < (sourcePoints?.Count ?? 0);
                sourcePointIndex++)
            {
                if (sourcePoints[sourcePointIndex] != null &&
                    sourcePoints[sourcePointIndex + 1] != null &&
                    sourcePoints[sourcePointIndex].DistanceTo(
                        sourcePoints[sourcePointIndex + 1]) >
                    GenerationSegmentLengthTolerance)
                {
                    return sourcePointIndex;
                }
            }

            return -1;
        }

        private static int FindLastUsableSegmentIndex(
            IReadOnlyList<Point3d> sourcePoints)
        {
            for (var sourcePointIndex = (sourcePoints?.Count ?? 0) - 2;
                sourcePointIndex >= 0;
                sourcePointIndex--)
            {
                if (sourcePoints[sourcePointIndex] != null &&
                    sourcePoints[sourcePointIndex + 1] != null &&
                    sourcePoints[sourcePointIndex].DistanceTo(
                        sourcePoints[sourcePointIndex + 1]) >
                    GenerationSegmentLengthTolerance)
                {
                    return sourcePointIndex;
                }
            }

            return -1;
        }

        private static bool TryConvertPlanarSetting(
            LinkageRouteWorkerPlanarSettingDto sourcePlanarSetting,
            string fallbackGroupKey,
            string settingDisplayName,
            out PlanarBendBusbarSetting convertedPlanarSetting,
            out string validationMessage)
        {
            convertedPlanarSetting = null;
            validationMessage = null;
            var sourcePointList = sourcePlanarSetting?.PointList?.ToList();
            if (sourcePointList == null || sourcePointList.Count < 2 ||
                sourcePointList.Any(sourcePoint => !IsFiniteCoordinate(sourcePoint)))
            {
                validationMessage = settingDisplayName + "缺少有效的铜排点集合。";
                return false;
            }

            if (!IsFiniteDirection(sourcePlanarSetting.StartBusbarVector) ||
                !IsFiniteDirection(sourcePlanarSetting.WidthVector))
            {
                validationMessage = settingDisplayName + "缺少有效的排厚或排宽方向。";
                return false;
            }

            if (!IsFinitePositive(sourcePlanarSetting.BusBarWidth) ||
                !IsFinitePositive(sourcePlanarSetting.BusBarThickness) ||
                !IsFiniteNonNegative(sourcePlanarSetting.BarBendingRadius) ||
                !IsFiniteNonNegative(sourcePlanarSetting.BendAllowance) ||
                !IsFiniteNonNegative(sourcePlanarSetting.StartLapWidth) ||
                !IsFiniteNonNegative(sourcePlanarSetting.EndLapWidth))
            {
                validationMessage = settingDisplayName + "包含无效的铜排尺寸或折弯参数。";
                return false;
            }

            convertedPlanarSetting = new PlanarBendBusbarSetting
            {
                IsCenter = sourcePlanarSetting.IsCenter,
                StartBusbarVector = new Vector3d(
                    sourcePlanarSetting.StartBusbarVector.X,
                    sourcePlanarSetting.StartBusbarVector.Y,
                    sourcePlanarSetting.StartBusbarVector.Z),
                WidthVector = new Vector3d(
                    sourcePlanarSetting.WidthVector.X,
                    sourcePlanarSetting.WidthVector.Y,
                    sourcePlanarSetting.WidthVector.Z),
                PointList = sourcePointList.Select(sourcePoint => new Point3d(
                    sourcePoint.X,
                    sourcePoint.Y,
                    sourcePoint.Z)).ToList(),
                BarBendingRadius = sourcePlanarSetting.BarBendingRadius,
                BendAllowance = sourcePlanarSetting.BendAllowance,
                BusBarThickness = sourcePlanarSetting.BusBarThickness,
                BusBarWidth = sourcePlanarSetting.BusBarWidth,
                StartLapWidth = sourcePlanarSetting.StartLapWidth,
                EndLapWidth = sourcePlanarSetting.EndLapWidth,
                // 方案所属逻辑组是相序的权威来源；Worker 的分段 GroupKey
                // 可能是内部标识，不能覆盖 A/B/C 逻辑组导致预览丢失相序。
                GroupKey = string.IsNullOrWhiteSpace(fallbackGroupKey)
                    ? sourcePlanarSetting.GroupKey
                    : fallbackGroupKey
            };
            return true;
        }

        private sealed class WorkerPlanarMemberSet
        {
            public string GroupKey { get; set; }

            public int MemberIndex { get; set; }

            public IReadOnlyList<LinkageRouteWorkerPlanarSettingDto>
                PlanarParts { get; set; } =
                    Array.Empty<LinkageRouteWorkerPlanarSettingDto>();
        }

        private static bool IsFiniteDirection(
            LinkageRouteWorkerCoordinateDto sourceDirection)
        {
            return IsFiniteCoordinate(sourceDirection) &&
                sourceDirection.X * sourceDirection.X +
                sourceDirection.Y * sourceDirection.Y +
                sourceDirection.Z * sourceDirection.Z >
                DirectionSquaredTolerance;
        }

        private static bool IsFiniteCoordinate(
            LinkageRouteWorkerCoordinateDto sourceCoordinate)
        {
            return sourceCoordinate != null &&
                IsFinite(sourceCoordinate.X) &&
                IsFinite(sourceCoordinate.Y) &&
                IsFinite(sourceCoordinate.Z);
        }

        private static bool IsFinitePositive(double sourceValue)
        {
            return IsFinite(sourceValue) && sourceValue > 0d;
        }

        private static bool IsFiniteNonNegative(double sourceValue)
        {
            return IsFinite(sourceValue) && sourceValue >= 0d;
        }

        private static bool IsFinite(double sourceValue)
        {
            return !double.IsNaN(sourceValue) &&
                !double.IsInfinity(sourceValue);
        }

        private static string ResolvePreviewGroupName(string sourceGroupKey)
        {
            return string.IsNullOrWhiteSpace(sourceGroupKey)
                ? "未分组"
                : sourceGroupKey.Trim();
        }
    }

    internal static class BusbarWorkerSolutionCenterlineExtractor
    {
        public static IReadOnlyList<BusbarWorkerCenterlinePreviewPath>
            ExtractCompletePreview(
            LinkageRouteWorkerRequestDto workerRequest,
            LinkageRouteWorkerSolutionDto workerSolution)
        {
            var completePreviewPaths = new List<BusbarWorkerCenterlinePreviewPath>();
            AddTerminalPaths(
                completePreviewPaths,
                workerRequest,
                requestGroup => requestGroup.StartTerminal);
            completePreviewPaths.AddRange(Extract(workerSolution));
            AddTerminalPaths(
                completePreviewPaths,
                workerRequest,
                requestGroup => requestGroup.EndTerminal);
            return completePreviewPaths;
        }

        public static IReadOnlyList<BusbarWorkerCenterlinePreviewPath> Extract(
            LinkageRouteWorkerSolutionDto workerSolution)
        {
            var previewPaths = new List<BusbarWorkerCenterlinePreviewPath>();
            foreach (var groupSolution in workerSolution?.GroupSolutions ??
                     Array.Empty<LinkageRouteWorkerGroupSolutionDto>())
            {
                var addedGeneratedBusbarPath = false;
                var stackedMemberParts = (groupSolution.StackMembers ??
                        Array.Empty<LinkageRouteWorkerStackedMemberDto>())
                    .Where(stackMember => stackMember != null)
                    .SelectMany(stackMember => stackMember.PlanarParts ??
                        Array.Empty<LinkageRouteWorkerPlanarSettingDto>())
                    .Where(HasPreviewPoints)
                    .ToList();
                if (stackedMemberParts.Count > 0)
                {
                    AddPlanarParts(previewPaths, groupSolution.GroupKey, stackedMemberParts);
                    addedGeneratedBusbarPath = true;
                }

                if (!addedGeneratedBusbarPath)
                {
                    var planarParts = (groupSolution.PlanarParts ??
                            Array.Empty<LinkageRouteWorkerPlanarSettingDto>())
                        .Where(HasPreviewPoints)
                        .ToList();
                    if (planarParts.Count > 0)
                    {
                        AddPlanarParts(
                            previewPaths,
                            groupSolution.GroupKey,
                            planarParts);
                        addedGeneratedBusbarPath = true;
                    }
                }

                if (!addedGeneratedBusbarPath &&
                    HasPreviewPoints(groupSolution.ManufacturableCenterline))
                {
                    previewPaths.Add(new BusbarWorkerCenterlinePreviewPath
                    {
                        GroupKey = groupSolution.GroupKey,
                        Points = groupSolution.ManufacturableCenterline.ToList()
                    });
                    addedGeneratedBusbarPath = true;
                }

                if (!addedGeneratedBusbarPath &&
                    HasPreviewPoints(groupSolution.AxisSkeleton))
                {
                    previewPaths.Add(new BusbarWorkerCenterlinePreviewPath
                    {
                        GroupKey = groupSolution.GroupKey,
                        Points = groupSolution.AxisSkeleton.ToList()
                    });
                }

                var accessorySettings = (groupSolution.Accessories ??
                        Array.Empty<LinkageRouteWorkerAccessoryDto>())
                    .Where(workerAccessory =>
                        HasPreviewPoints(workerAccessory?.Setting))
                    .Select(workerAccessory => workerAccessory.Setting)
                    .ToList();
                AddPlanarParts(
                    previewPaths,
                    groupSolution.GroupKey,
                    accessorySettings);
            }

            if (previewPaths.Count > 0)
            {
                return previewPaths;
            }

            foreach (var failureDiagnostic in workerSolution?.Diagnostics ??
                     Array.Empty<LinkageRouteWorkerFailureDiagnosticDto>())
            {
                if (failureDiagnostic != null &&
                    HasPreviewPoints(failureDiagnostic.PreviewCenterline))
                {
                    previewPaths.Add(new BusbarWorkerCenterlinePreviewPath
                    {
                        GroupKey = failureDiagnostic.GroupKey,
                        Points = failureDiagnostic.PreviewCenterline.ToList()
                    });
                }
            }

            return previewPaths;
        }

        private static void AddTerminalPaths(
            ICollection<BusbarWorkerCenterlinePreviewPath> targetPaths,
            LinkageRouteWorkerRequestDto workerRequest,
            Func<LinkageRouteWorkerGroupRequestDto,
                LinkageRouteWorkerTerminalAssemblyDto> terminalSelector)
        {
            foreach (var requestGroup in workerRequest?.Groups ??
                Array.Empty<LinkageRouteWorkerGroupRequestDto>())
            {
                if (requestGroup == null)
                {
                    continue;
                }

                var terminalAssembly = terminalSelector(requestGroup);
                foreach (var terminalMember in terminalAssembly?.Members ??
                    Array.Empty<LinkageRouteWorkerTerminalMemberDto>())
                {
                    if (!HasPreviewPoints(terminalMember?.FixedBusbar))
                    {
                        continue;
                    }

                    targetPaths.Add(new BusbarWorkerCenterlinePreviewPath
                    {
                        GroupKey = requestGroup.GroupKey,
                        Points = terminalMember.FixedBusbar.PointList.ToList()
                    });
                }
            }
        }

        private static void AddPlanarParts(
            ICollection<BusbarWorkerCenterlinePreviewPath> targetPaths,
            string groupKey,
            IEnumerable<LinkageRouteWorkerPlanarSettingDto> planarParts)
        {
            foreach (var planarPart in planarParts)
            {
                targetPaths.Add(new BusbarWorkerCenterlinePreviewPath
                {
                    GroupKey = groupKey,
                    // Worker 输出已声明为中心线；此处不叠加半排宽或半排厚偏移。
                    Points = planarPart.PointList.ToList()
                });
            }
        }

        private static bool HasPreviewPoints(LinkageRouteWorkerPlanarSettingDto planarPart)
        {
            return planarPart != null && HasPreviewPoints(planarPart.PointList);
        }

        private static bool HasPreviewPoints(
            IEnumerable<LinkageRouteWorkerCoordinateDto> points)
        {
            return points != null && points.Count(point => point != null) >= 2;
        }
    }
}
