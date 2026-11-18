using System;
using System.Collections.Generic;
using RouteLinkBar.Algorithm.Contracts;

namespace RouteLinkBar.Algorithm.Transport
{
    /// <summary>SolidWorks 宿主与纯算法 Worker 之间的协议常量。</summary>
    public static class LinkageRouteWorkerProtocol
    {
        public const int CurrentVersion = 16;
        public const string Handshake = "Handshake";
        public const string HandshakeAcknowledged = "HandshakeAcknowledged";
        public const string SolveRequest = "SolveRequest";
        public const string SchemeLibraryMatchRequest = "SchemeLibraryMatchRequest";
        public const string Progress = "Progress";
        public const string SolutionPublished = "SolutionPublished";
        public const string SchemeLibrarySolutionPublished = "SchemeLibrarySolutionPublished";
        public const string Completed = "Completed";
        public const string CancelRequest = "CancelRequest";
        public const string CancelAcknowledged = "CancelAcknowledged";
        public const string PrioritizeOptimizationRequest =
            "PrioritizeOptimizationRequest";
        public const string PrioritizeOptimizationAcknowledged =
            "PrioritizeOptimizationAcknowledged";
        public const string Shutdown = "Shutdown";
        public const string ShutdownAcknowledged = "ShutdownAcknowledged";
        public const string Faulted = "Faulted";
    }

    /// <summary>算法 Worker 的版本与结构化更新日志。</summary>
    public sealed class LinkageRouteWorkerInformationDto
    {
        public int SchemaVersion { get; set; }
        public string WorkerName { get; set; }
        public string WorkerVersion { get; set; }
        public bool SupportsTerminalHoleInput { get; set; }
        public bool SupportsTerminalHoleLapConstraints { get; set; }
        public bool SupportsSupportBlockReduction { get; set; }
        public int ProtocolVersion { get; set; }
        public IList<LinkageRouteWorkerReleaseNoteDto> Releases { get; set; } =
            new List<LinkageRouteWorkerReleaseNoteDto>();
    }

    /// <summary>单个算法 Worker 版本的结构化更新记录。</summary>
    public sealed class LinkageRouteWorkerReleaseNoteDto
    {
        public string Version { get; set; }
        public string ReleaseDate { get; set; }
        public string ChangeLevel { get; set; }
        public string Compatibility { get; set; }
        public int ProtocolVersion { get; set; }
        public IList<string> Changes { get; set; } = new List<string>();
    }

    /// <summary>
    /// 单行 Json 消息包。每条消息都带协议版本与请求标识，迟到消息不得污染新一轮搜索。
    /// </summary>
    public sealed class LinkageRouteWorkerMessageDto
    {
        public int ProtocolVersion { get; set; } = LinkageRouteWorkerProtocol.CurrentVersion;
        public string MessageType { get; set; }
        public string RequestId { get; set; }
        public int ParentProcessId { get; set; }
        public string AuthorizationCode { get; set; }
        public LinkageRouteWorkerInformationDto WorkerInformation { get; set; }
        public LinkageRouteWorkerRequestDto Request { get; set; }
        public LinkageSchemeLibraryMatchRequest SchemeLibraryMatchRequest { get; set; }
        public LinkageRouteWorkerProgressDto Progress { get; set; }
        public LinkageRouteWorkerSolutionDto Solution { get; set; }
        public LinkageSchemeLibraryMatchResult SchemeLibraryMatch { get; set; }
        public LinkageRouteWorkerComparisonResultDto Result { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorDetails { get; set; }
    }

    public sealed class LinkageRouteWorkerCoordinateDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public sealed class LinkageRouteWorkerPlanarSettingDto
    {
        public bool IsCenter { get; set; }
        public LinkageRouteWorkerCoordinateDto StartBusbarVector { get; set; }
        public LinkageRouteWorkerCoordinateDto WidthVector { get; set; }
        public IList<LinkageRouteWorkerCoordinateDto> PointList { get; set; } =
            new List<LinkageRouteWorkerCoordinateDto>();
        public double BarBendingRadius { get; set; }
        public double BendAllowance { get; set; }
        public double BusBarThickness { get; set; }
        public double BusBarWidth { get; set; }
        public double StartLapWidth { get; set; }
        public double EndLapWidth { get; set; }
        public string GroupKey { get; set; }
    }

    /// <summary>固定端孔信息的纯协议快照；尺寸和坐标沿用原始毫米值。</summary>
    public sealed class LinkageRouteWorkerHoleInfoDto
    {
        public LinkageRouteWorkerCoordinateDto PlanePoint { get; set; }
        public LinkageRouteWorkerCoordinateDto PlaneNormal { get; set; }
        public LinkageRouteWorkerCoordinateDto Vector { get; set; }
        public LinkageRouteWorkerCoordinateDto HoleVectorInPart { get; set; }
        public LinkageRouteWorkerCoordinateDto HoleVector { get; set; }
        public LinkageRouteWorkerCoordinateDto CenterPointInPart { get; set; }
        public LinkageRouteWorkerCoordinateDto CenterPointInPart2 { get; set; }
        public LinkageRouteWorkerCoordinateDto CenterPoint { get; set; }
        public LinkageRouteWorkerCoordinateDto CenterPoint2 { get; set; }
        public int HoleTypeValue { get; set; }
        public double DimD { get; set; }
        public double DimX { get; set; }
        public double DimY { get; set; }
        public bool NotYahua { get; set; }
    }

    public sealed class LinkageRouteWorkerTerminalMemberDto
    {
        public string MemberKey { get; set; }
        public LinkageRouteWorkerPlanarSettingDto FixedBusbar { get; set; }
        public LinkageRouteWorkerCoordinateDto ForbiddenHorizontalExitDirection { get; set; }
        public IList<LinkageRouteWorkerHoleInfoDto> Holes { get; set; } = new List<LinkageRouteWorkerHoleInfoDto>();
    }

    public sealed class LinkageRouteWorkerTerminalAssemblyDto
    {
        public LinkageRouteWorkerCoordinateDto RouteAnchor { get; set; }
        public LinkageRouteWorkerCoordinateDto LengthDirection { get; set; }
        public LinkageRouteWorkerCoordinateDto ThicknessDirection { get; set; }
        public IList<LinkageRouteWorkerTerminalMemberDto> Members { get; set; } =
            new List<LinkageRouteWorkerTerminalMemberDto>();
    }

    public sealed class LinkageRouteWorkerGroupRequestDto
    {
        public string GroupKey { get; set; }
        public string Phase { get; set; }
        public string DisplayName { get; set; }
        public LinkageRouteWorkerTerminalAssemblyDto StartTerminal { get; set; }
        public LinkageRouteWorkerTerminalAssemblyDto EndTerminal { get; set; }
        public double BusbarWidth { get; set; }
        public double BusbarThickness { get; set; }
        public int StackCount { get; set; }
        public double StartLapBusbarWidth { get; set; }
        public double StartLapBusbarThickness { get; set; }
        public int StartLapStackCount { get; set; }
        public double CabinetTopLapBusbarWidth { get; set; }
        public double CabinetTopLapBusbarThickness { get; set; }
        public int CabinetTopLapStackCount { get; set; }
        public double StackStartSpace { get; set; }
        public double StackMiddleSpace { get; set; }
        public double StackEndSpace { get; set; }
        public LinkageRouteWorkerCoordinateDto StackMoveVector { get; set; }
        public bool StackReverse { get; set; }
    }

    public sealed class LinkageRouteWorkerObstacleDto
    {
        public string ObstacleKey { get; set; }
        public string GroupKey { get; set; }
        public LinkageRouteWorkerCoordinateDto Minimum { get; set; }
        public LinkageRouteWorkerCoordinateDto Maximum { get; set; }
    }

    /// <summary>
    /// 请求级算法规则配置。字段与 LinkageRouteSearchOptions 一一对应；
    /// 未传字段使用生产默认值，具体作用和影响见通用规则参数说明文档。
    /// </summary>
    public sealed class LinkageRouteWorkerSearchOptionsDto
    {
        public bool PrioritizeAllSolutions { get; set; }
        public int FirstSolutionTargetMilliseconds { get; set; } = 15000;
        public int TotalSearchLimitMilliseconds { get; set; } = 60000;
        public bool EnableAutomaticTotalSearchLimit { get; set; } = true;
        public int MaximumCandidatesPerGroup { get; set; } = 128;
        public int MaximumClearanceRepairCandidatesPerState { get; set; } = 64;
        public int MaximumSolutionsPerAlgorithm { get; set; } = 10;
        public int BeamWidth { get; set; } = 24;
        public int MaximumDegreeOfParallelism { get; set; } = Math.Max(
            1,
            System.Environment.ProcessorCount - 3);
        public double MinimumAdaptiveRepairStep { get; set; } = 0.5d;
        public int FastSearchStageLimitMilliseconds { get; set; } = 3000;
        public int ExtendedFastSearchStageLimitMilliseconds { get; set; } = 5000;
        public int DeepTraversalTimeSliceMilliseconds { get; set; } = 8000;
        public int ConstraintDirectedDeepRepairLimitMilliseconds { get; set; } = 40000;
        public int ConstraintDirectedRootCandidateCount { get; set; } = 2;
        public int SolutionAllocationSwitchSolutionCount { get; set; } = 10;
        public double PostSwitchMainSearchWorkerRatio { get; set; } = 0.25d;
        public double PostSwitchSolutionOptimizationWorkerRatio { get; set; } = 0.75d;
        public int PreferredSolutionOptimizationCount { get; set; } = 3;
        public int SolutionOptimizationStagnationMilliseconds { get; set; } = 120000;
        public int StagnationMainSearchWorkerCount { get; set; } = 1;
        public int MaximumSolutionOptimizationDurationMilliseconds { get; set; } = 1800000;
        public int SchedulingAndPublishingWorkerReserve { get; set; } = 1;
        public double InitialSolutionOptimizationWorkerRatio { get; set; } = 0.5d;
        public int MaximumInitialSolutionOptimizationWorkerCount { get; set; } = 2;
        public int FastSolutionOptimizationWorkerReserve { get; set; } = 1;
        public int ClearanceOptimizationLimitMilliseconds { get; set; } = 30000;
        public int ClearanceOptimizationTraversalLimitMilliseconds { get; set; } = 2000;
        public int MaximumClearanceOptimizationCandidateCount { get; set; } = 24;
        public int MaximumClearanceOptimizationPassCount { get; set; } = 4;
        public double ClearanceOptimizationStep { get; set; } = 5d;
        public int ClearanceOptimizationCatalogLimitMilliseconds { get; set; } = 15000;
        public double MinimumPublishableClearance { get; set; } = 5d;
        public int SupplementalClearanceStartDelayMilliseconds { get; set; } = 10000;
        public int RelaxedClearancePublicationMilliseconds { get; set; } = 60000;
        public int SupplementalClearanceAttemptLimitMilliseconds { get; set; } = 70000;
        public double SupplementalClearanceStep { get; set; } = 5d;
        public int SupplementalClearanceIncreaseDelayMilliseconds { get; set; } = 20000;
        public double SupplementalClearanceMaximumIncrease { get; set; } = 10d;
        public double SupplementalClearanceSeed { get; set; } = 5d;
        public int SupplementalClearanceMaximumDegreeOfParallelism { get; set; } = 1;
        public bool EnableSupplementalLegacyDoubleStartPlacementSearch { get; set; } = true;
        public int SupplementalLegacyPlacementMaximumDegreeOfParallelism { get; set; } = 1;
        public int SupplementalLegacyPlacementMaximumSolutions { get; set; } = 10;
        public int FastLayerMaximumEvaluatedSkeletonCount { get; set; } = 128;
        public int FastLayerMaximumSeedCandidateCount { get; set; } = 16;
        public int FastLayerMaximumSeedCandidateCountPerSegmentLayer { get; set; } = 8;
        public int FastLayerMaximumVisitedSkeletonCount { get; set; } = 256;
        public int FastLayerMaximumEvaluatedSkeletonCountPerTopology { get; set; } = 48;
        public int ExtendedLayerMaximumEvaluatedSkeletonCount { get; set; } = 512;
        public int ExtendedLayerMaximumSeedCandidateCount { get; set; } = 64;
        public int ExtendedLayerMaximumSeedCandidateCountPerSegmentLayer { get; set; } = 16;
        public int ExtendedLayerMaximumVisitedSkeletonCount { get; set; } = 2048;
        public int ExtendedLayerMaximumEvaluatedSkeletonCountPerTopology { get; set; } = 128;
        public int FastLayerMaximumSegmentCount { get; set; } = 6;
        public int ExtendedLayerMaximumSegmentCount { get; set; } = 8;
        public int MaximumSeedSkeletonsPerSegmentLayer { get; set; } = 8;
        public int MaximumPairRepairCandidateCount { get; set; } = 8;
        public int DoubleStartBridgeMinimumCandidateCount { get; set; } = 8;
        public int DoubleStartBridgeMaximumCandidateCount { get; set; } = 32;
        public int DoubleStartBridgeMaximumInternalLapCandidateCount { get; set; } = 8;
        public int MaximumSequenceSearchNodeCount { get; set; } = 8192;
        public int MaximumCartesianBoundaryRefinementCount { get; set; } = 4;
        public int MaximumCartesianOptimizationMotionCount { get; set; } = 3;
        public int MaximumRestrictedWholePathCandidateCount { get; set; } = 4;
    }

    public sealed class LinkageRouteWorkerRequestDto
    {
        public IList<LinkageRouteWorkerGroupRequestDto> Groups { get; set; } =
            new List<LinkageRouteWorkerGroupRequestDto>();
        public IList<LinkageRouteWorkerObstacleDto> Obstacles { get; set; } =
            new List<LinkageRouteWorkerObstacleDto>();
        public double DefaultBusbarWidth { get; set; }
        public double DefaultBusbarThickness { get; set; }
        public int DefaultStackCount { get; set; }
        public double MinimumSegmentLength { get; set; }
        public double BendAllowance { get; set; }
        public bool AllowBendGeometricLimit { get; set; }
        public bool AllowConstrainedBendAllowanceReduction { get; set; } = true;
        public double BendingRadius { get; set; }
        public double InsulationClearance { get; set; }
        public double LapInsulationClearance { get; set; } = 35d;
        public bool AllowUShapeRoutes { get; set; }
        /// <summary>是否启用宿主方案库匹配并与算法方案同步输出；默认开启。</summary>
        public bool EnableSchemeLibraryMatching { get; set; } = true;
        /// <summary>
        /// 是否执行普通路径搜索。关闭时 Worker 仍通过同一 SolveRequest
        /// 执行方案库匹配和候选路径重算；默认开启以兼容旧请求。
        /// </summary>
        public bool EnableAlgorithmCalculation { get; set; } = true;
        /// <summary>由宿主准备的方案库纯数据快照；Worker 不读取数据库。</summary>
        public LinkageSchemeLibraryMatchRequest SchemeLibraryMatchRequest { get; set; }
        public bool AllowDoubleStartMiddleLap { get; set; } = true;
        public bool RequireSingleStartMiddleLap { get; set; } = false;
        public bool EnableSingleStartConnectionBar { get; set; }
        public bool AllowPartialStartPerpendicularLap { get; set; }
        public bool MinimizeSupportBlocks { get; set; } = true;
        public bool UseSpacerlessTerminalLap { get; set; }
        public bool RotateSlotHole90Degrees { get; set; }
        public double TerminalHoleEdgeDistance { get; set; } = 5d;
        public bool RequireFixedTerminalEndLap { get; set; }
        public bool RequireAxisAlignedRoutes { get; set; }
        public bool UseExplicitContainmentBox { get; set; }
        public LinkageRouteWorkerCoordinateDto ContainmentMinimum { get; set; }
        public LinkageRouteWorkerCoordinateDto ContainmentMaximum { get; set; }
        public LinkageRouteWorkerSearchOptionsDto SearchOptions { get; set; }
    }

    public sealed class LinkageRouteWorkerFailureDiagnosticDto
    {
        public string GroupKey { get; set; }
        public int Stage { get; set; }
        public string Message { get; set; }
        public int CompletedGroupCount { get; set; }
        public int ViolationCount { get; set; }
        public double MaximumViolationDepth { get; set; }
        public IList<LinkageRouteWorkerCoordinateDto> PreviewCenterline { get; set; } =
            new List<LinkageRouteWorkerCoordinateDto>();
    }

    public sealed class LinkageRouteWorkerStackedMemberDto
    {
        public int MemberIndex { get; set; }
        public double CenterlineOffset { get; set; }
        public IList<LinkageRouteWorkerPlanarSettingDto> PlanarParts { get; set; } =
            new List<LinkageRouteWorkerPlanarSettingDto>();
    }

    public sealed class LinkageRouteWorkerAccessoryDto
    {
        public int Kind { get; set; }
        public int TerminalSide { get; set; }
        public int RelatedGeneratedMemberIndex { get; set; } = -1;
        public LinkageRouteWorkerPlanarSettingDto Setting { get; set; }
    }

    public sealed class LinkageRouteWorkerGroupSolutionDto
    {
        public string GroupKey { get; set; }
        public IList<LinkageRouteWorkerCoordinateDto> AxisSkeleton { get; set; } =
            new List<LinkageRouteWorkerCoordinateDto>();
        public IList<LinkageRouteWorkerCoordinateDto> ManufacturableCenterline { get; set; } =
            new List<LinkageRouteWorkerCoordinateDto>();
        public IList<LinkageRouteWorkerPlanarSettingDto> PlanarParts { get; set; } =
            new List<LinkageRouteWorkerPlanarSettingDto>();
        public IList<LinkageRouteWorkerStackedMemberDto> StackMembers { get; set; } =
            new List<LinkageRouteWorkerStackedMemberDto>();
        public IList<LinkageRouteWorkerAccessoryDto> Accessories { get; set; } =
            new List<LinkageRouteWorkerAccessoryDto>();
        public double ActualCopperLength { get; set; }
        public int PerpendicularLapCount { get; set; }
        public bool IsConvertedShape { get; set; }
        public string TopologySignature { get; set; }
    }

    public sealed class LinkageRouteWorkerSolutionDto
    {
        public int SchemeIndex { get; set; }
        public string OptimizationSourceTopologySignature { get; set; }
        public int AlgorithmKind { get; set; }
        public bool IsSuccess { get; set; }
        public bool IsCancelled { get; set; }
        public double ActualCopperLength { get; set; }
        public int PerpendicularLapCount { get; set; }
        public double MinimumClearance { get; set; }
        public double RequestedInsulationClearance { get; set; }
        public double RequestedLapInsulationClearance { get; set; }
        public double SearchInsulationClearance { get; set; }
        public bool IsRelaxedClearanceSolution { get; set; }
        public IList<LinkageRouteWorkerGroupSolutionDto> GroupSolutions { get; set; } =
            new List<LinkageRouteWorkerGroupSolutionDto>();
        public IList<LinkageRouteWorkerFailureDiagnosticDto> Diagnostics { get; set; } =
            new List<LinkageRouteWorkerFailureDiagnosticDto>();
        public IList<LinkageRouteWorkerClearanceFaceGroupDto> MinimumClearanceFaceGroups { get; set; } =
            new List<LinkageRouteWorkerClearanceFaceGroupDto>();
    }

    public sealed class LinkageRouteWorkerClearanceFaceGroupDto
    {
        public string GeneratedGroupKey { get; set; }
        public int GeneratedSegmentIndex { get; set; } = -1;
        public string StaticGroupKey { get; set; }
        public int StaticSegmentIndex { get; set; } = -1;
        public double Distance { get; set; }
        public LinkageRouteWorkerCoordinateDto GeneratedFaceCenter { get; set; }
        public LinkageRouteWorkerCoordinateDto GeneratedFaceNormal { get; set; }
        public LinkageRouteWorkerCoordinateDto StaticFaceCenter { get; set; }
        public LinkageRouteWorkerCoordinateDto StaticFaceNormal { get; set; }
    }

    public sealed class LinkageRouteWorkerProgressDto
    {
        public int AlgorithmKind { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long ProcessedStateCount { get; set; }
        public string Message { get; set; }
    }

    public sealed class LinkageRouteWorkerComparisonResultDto
    {
        public IList<LinkageRouteWorkerSolutionDto> RankedSolutions { get; set; } =
            new List<LinkageRouteWorkerSolutionDto>();
        public bool IsCancelled { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long FirstSolutionElapsedMilliseconds { get; set; }
    }
}
