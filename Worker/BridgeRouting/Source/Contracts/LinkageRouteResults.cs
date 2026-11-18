using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Contracts
{
    public enum LinkageRouteAlgorithmKind
    {
        AxisGraph = 0,
        TopologyBeam = 1,
        SweptRepair = 2
    }

    public enum LinkageRouteFailureStage
    {
        None = 0,
        Input = 1,
        AxisSkeleton = 2,
        Conversion = 3,
        Lap = 4,
        MinimumSegment = 5,
        BendParity = 6,
        Containment = 7,
        Clearance = 8,
        PlanarConversion = 9,
        Cancelled = 10,
        TimeLimit = 11
    }

    public sealed class LinkageRouteSolution
    {
        public int SchemeIndex { get; set; }
        /// <summary>
        /// 非空表示本方案由该方向拓扑的既有方案继续优化得到。
        /// 仅用于发布来源展示，不参与搜索、排序或几何校验。
        /// </summary>
        public string OptimizationSourceTopologySignature { get; set; }
        public LinkageRouteAlgorithmKind AlgorithmKind { get; set; }
        public bool IsSuccess { get; set; }
        public bool IsCancelled { get; set; }
        /// <summary>最终生成排、连接排及厚度大于整套最薄铜排的各块垫块的展开长度之和；固定输入排不计长。</summary>
        public double ActualCopperLength { get; set; }
        public int PerpendicularLapCount { get; set; }
        public int GeneratedBusbarPartCount => GroupSolutions.Sum(
            groupSolution => groupSolution.GeneratedBusbarPartCount);
        public double MinimumClearance { get; set; }
        /// <summary>用户本次请求的原始绝缘净距。</summary>
        public double RequestedInsulationClearance { get; set; }
        /// <summary>用户本次请求的搭接面绝缘净距。</summary>
        public double RequestedLapInsulationClearance { get; set; }
        /// <summary>生成当前候选时采用的绝缘坐标枚举值。</summary>
        public double SearchInsulationClearance { get; set; }
        /// <summary>
        /// 兼容历史输出的诊断标记；当前生产调度不会发布低于请求净距的方案。
        /// </summary>
        public bool IsRelaxedClearanceSolution { get; set; }
        public IList<LinkageGroupRouteSolution> GroupSolutions { get; } = new List<LinkageGroupRouteSolution>();
        /// <summary>所有逻辑组的最短净距虚拟面标记集合。</summary>
        public IList<LinkageRouteClearanceFaceGroup> MinimumClearanceFaceGroups =>
            GroupSolutions
                .Where(groupSolution => groupSolution?.MinimumClearanceFaceGroups != null)
                .SelectMany(groupSolution => groupSolution.MinimumClearanceFaceGroups)
                .ToList();
        /// <summary>
        /// 全部垫块按实际排厚汇总数量和展开长度；其中厚度严格大于整套
        /// 最薄铜排的各块垫块也计入最终 ActualCopperLength，其余仅在此汇总。
        /// 双始端连接排属于中间排，不会进入该统计。
        /// </summary>
        public IList<LinkageSupportBlockSummary> SupportBlockSummaries =>
            GroupSolutions
                .Where(groupSolution => groupSolution?.Accessories != null)
                .SelectMany(groupSolution => groupSolution.Accessories)
                .Where(routeAccessory =>
                    routeAccessory?.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                    routeAccessory.Setting != null)
                .GroupBy(routeAccessory => Math.Round(
                    routeAccessory.Setting.BusBarThickness,
                    6))
                .OrderBy(supportBlockGroup => supportBlockGroup.Key)
                .Select(supportBlockGroup => new LinkageSupportBlockSummary
                {
                    Thickness = supportBlockGroup.Key,
                    Count = supportBlockGroup.Count(),
                    TotalLength = supportBlockGroup.Sum(routeAccessory =>
                        routeAccessory.Setting.GetDevelopedLength())
                })
                .ToList();
        public IList<LinkageRouteFailureDiagnostic> Diagnostics { get; } = new List<LinkageRouteFailureDiagnostic>();
    }

    public sealed class LinkageSupportBlockSummary
    {
        public double Thickness { get; set; }
        public int Count { get; set; }
        public double TotalLength { get; set; }
    }

    public sealed class LinkageGroupRouteSolution
    {
        // Internal actual-pose identity only, deliberately absent from Worker DTO.
        internal int USplitContactSide { get; set; } = 1;
        // 仅用于进程内发布门禁，不加入 Worker JSON。
        internal bool InternalSupportRebuildFailed { get; set; }
        public string GroupKey { get; set; }
        public IList<Point3d> AxisSkeleton { get; set; } = new List<Point3d>();
        public IList<Point3d> ManufacturableCenterline { get; set; } = new List<Point3d>();
        public IList<PlanarBendBusbarSetting> PlanarParts { get; set; } = new List<PlanarBendBusbarSetting>();
        public IList<LinkageStackedRouteMember> StackMembers { get; set; } = new List<LinkageStackedRouteMember>();
        /// <summary>
        /// 端部搭接所需的附加铜排。垫块与普通铜排采用相同几何契约，
        /// 仅通过 Kind 区分后续零件属性。
        /// </summary>
        public IList<LinkageRouteAccessory> Accessories { get; set; } =
            new List<LinkageRouteAccessory>();
        /// <summary>
        /// 标记已经合并到物理首段的双始端连接排。该元数据仅供算法最终
        /// 结构校验使用，不参与 Worker 公共协议序列化。
        /// </summary>
        internal ISet<int> DoubleStartPhysicalBridgeSectionIndices { get; } =
            new HashSet<int>();
        /// <summary>
        /// 从候选端部层位保留的双始端水平连接排关联成员。用于最终输出
        /// 深拷贝后仍能校验连接排数量，不能因层位对象未进入结果协议而丢失。
        /// </summary>
        internal ISet<int> ExpectedDoubleStartBridgeMemberIndices { get; } =
            new HashSet<int>();
        /// <summary>
        /// 当前逻辑组与其他相序带电铜排之间的最短净距面组。面中心和法向为
        /// 虚拟标记几何，仅用于诊断与预览，不参与路径校验。
        /// </summary>
        public IList<LinkageRouteClearanceFaceGroup> MinimumClearanceFaceGroups { get; set; } =
            new List<LinkageRouteClearanceFaceGroup>();
        public double ActualCopperLength { get; set; }
        public int PerpendicularLapCount { get; set; }
        public int GeneratedBusbarPartCount =>
            (PlanarParts?.Count ?? 0) + (Accessories?.Count ?? 0);
        public bool IsConvertedShape { get; set; }
        public string TopologySignature { get; set; }
    }

    public sealed class LinkageRouteClearanceFaceGroup
    {
        public string GeneratedGroupKey { get; set; }
        public int GeneratedSegmentIndex { get; set; } = -1;
        public string StaticGroupKey { get; set; }
        public int StaticSegmentIndex { get; set; } = -1;
        public double Distance { get; set; }
        public Point3d GeneratedFaceCenter { get; set; }
        public Vector3d GeneratedFaceNormal { get; set; }
        public Point3d StaticFaceCenter { get; set; }
        public Vector3d StaticFaceNormal { get; set; }
    }

    public sealed class LinkageStackedRouteMember
    {
        public int MemberIndex { get; set; }
        public double CenterlineOffset { get; set; }
        public IList<PlanarBendBusbarSetting> PlanarParts { get; set; } = new List<PlanarBendBusbarSetting>();
    }

    public enum LinkageRouteAccessoryKind
    {
        SupportBlock = 0,
        DoubleStartHorizontalBridge = 1,
        CabinetTopConnectionBridge = 2
    }

    public enum LinkageRouteTerminalSide
    {
        Start = 0,
        End = 1
    }

    /// <summary>端部搭接附件；几何仍按直铜排/折弯铜排统一表达。</summary>
    public sealed class LinkageRouteAccessory
    {
        public LinkageRouteAccessoryKind Kind { get; set; }
        public LinkageRouteTerminalSide TerminalSide { get; set; }
        public int RelatedGeneratedMemberIndex { get; set; } = -1;
        public PlanarBendBusbarSetting Setting { get; set; }
        public string SupportBlockKey { get; set; }
    }

    public sealed class LinkageRouteFailureDiagnostic
    {
        public string GroupKey { get; set; }
        public LinkageRouteFailureStage Stage { get; set; }
        public string Message { get; set; }
        public int CompletedGroupCount { get; set; }
        public int ViolationCount { get; set; }
        public double MaximumViolationDepth { get; set; }
        public IList<Point3d> PreviewCenterline { get; set; } = new List<Point3d>();
    }

    public sealed class LinkageRouteProgress
    {
        public LinkageRouteAlgorithmKind AlgorithmKind { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long ProcessedStateCount { get; set; }
        public LinkageRouteSolution PublishedSolution { get; set; }
        public string Message { get; set; }
    }

    public sealed class LinkageRouteComparisonResult
    {
        public IList<LinkageRouteSolution> RankedSolutions { get; } = new List<LinkageRouteSolution>();
        public bool IsCancelled { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public long FirstSolutionElapsedMilliseconds { get; set; }
    }
}
