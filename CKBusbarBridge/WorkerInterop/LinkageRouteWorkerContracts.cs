using System.Collections.Generic;

namespace CKBusbarBridge.AlgorithmWorker
{
    /// <summary>
    /// CKBusbarBridge 与独立算法 Worker 之间的轻量协议。
    /// 本项目不引用 RouteLinkBar.Algorithm.dll；Worker 目录可独立整体替换。
    /// </summary>
    public static class LinkageRouteWorkerProtocol
    {
        public const int CurrentVersion = 10;
        public const string Handshake = "Handshake";
        public const string HandshakeAcknowledged = "HandshakeAcknowledged";
        public const string SolveRequest = "SolveRequest";
        public const string Progress = "Progress";
        public const string SolutionPublished = "SolutionPublished";
        public const string Completed = "Completed";
        public const string CancelRequest = "CancelRequest";
        public const string CancelAcknowledged = "CancelAcknowledged";
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

    public sealed class LinkageRouteWorkerMessageDto
    {
        public int ProtocolVersion { get; set; } = LinkageRouteWorkerProtocol.CurrentVersion;
        public string MessageType { get; set; }
        public string RequestId { get; set; }
        public int ParentProcessId { get; set; }
        public string AuthorizationCode { get; set; }
        public LinkageRouteWorkerInformationDto WorkerInformation { get; set; }
        public LinkageRouteWorkerRequestDto Request { get; set; }
        public LinkageRouteWorkerProgressDto Progress { get; set; }
        public LinkageRouteWorkerSolutionDto Solution { get; set; }
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

    public sealed class LinkageRouteWorkerTerminalMemberDto
    {
        public string MemberKey { get; set; }
        public LinkageRouteWorkerPlanarSettingDto FixedBusbar { get; set; }
        public LinkageRouteWorkerCoordinateDto ForbiddenHorizontalExitDirection { get; set; }
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

    public sealed class LinkageRouteWorkerSearchOptionsDto
    {
        public int FirstSolutionTargetMilliseconds { get; set; }
        public int TotalSearchLimitMilliseconds { get; set; }
        public bool EnableAutomaticTotalSearchLimit { get; set; }
        public int MaximumCandidatesPerGroup { get; set; }
        public int MaximumClearanceRepairCandidatesPerState { get; set; }
        public int MaximumSolutionsPerAlgorithm { get; set; }
        public int BeamWidth { get; set; }
        public int MaximumDegreeOfParallelism { get; set; }
        public double MinimumAdaptiveRepairStep { get; set; }
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
        public double BendingRadius { get; set; }
        public double InsulationClearance { get; set; }
        public bool AllowDoubleStartMiddleLap { get; set; } = true;
        public bool RequireSingleStartMiddleLap { get; set; } = true;
        public bool AllowPartialStartPerpendicularLap { get; set; }
        public bool UseSpacerlessTerminalLap { get; set; }
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
        public int AlgorithmKind { get; set; }
        public bool IsSuccess { get; set; }
        public bool IsCancelled { get; set; }
        public double ActualCopperLength { get; set; }
        public int PerpendicularLapCount { get; set; }
        public double MinimumClearance { get; set; }
        public double RequestedInsulationClearance { get; set; }
        public double SearchInsulationClearance { get; set; }
        public bool IsRelaxedClearanceSolution { get; set; }
        public IList<LinkageRouteWorkerGroupSolutionDto> GroupSolutions { get; set; } =
            new List<LinkageRouteWorkerGroupSolutionDto>();
        public IList<LinkageRouteWorkerFailureDiagnosticDto> Diagnostics { get; set; } =
            new List<LinkageRouteWorkerFailureDiagnosticDto>();
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
