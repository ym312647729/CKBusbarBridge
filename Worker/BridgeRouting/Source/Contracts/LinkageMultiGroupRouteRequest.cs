using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Contracts
{
    public sealed class LinkageMultiGroupRouteRequest
    {
        // 仅搜索阶段策略，MemberwiseClone沿用；不属于JSON/协议输入，也不改变SearchOptions。
        internal bool RequireCenteredTerminalHoleGroups { get; set; }
        // 仅搜索阶段策略：异规格通用骨架/排宽包络辅助分支，与主分支并行；不属于JSON/协议输入。
        internal bool IsSectionSpecificationAuxiliarySearch { get; set; }

        internal LinkageMultiGroupRouteRequest WithCenteredTerminalHoleGroups()
        {
            var centeredRequest = (LinkageMultiGroupRouteRequest)MemberwiseClone();
            centeredRequest.RequireCenteredTerminalHoleGroups = true;
            return centeredRequest;
        }
        internal LinkageMultiGroupRouteRequest WithCabinetGroups(IList<LinkageRouteGroupRequest> groups)
        {
            var copy = (LinkageMultiGroupRouteRequest)MemberwiseClone();
            copy.Groups = groups;
            return copy;
        }

        public IList<LinkageRouteGroupRequest> Groups { get; set; } = new List<LinkageRouteGroupRequest>();
        public IList<LinkageRouteObstacle> Obstacles { get; set; } = new List<LinkageRouteObstacle>();
        public double DefaultBusbarWidth { get; set; }
        public double DefaultBusbarThickness { get; set; }
        public int DefaultStackCount { get; set; } = 1;
        public double MinimumSegmentLength { get; set; } = 40d;
        public double BendAllowance { get; set; }
        /// <summary>忽略设置余量，直接按真实圆弧及搭接几何极限计算；默认关闭。</summary>
        public bool AllowBendGeometricLimit { get; set; }
        /// <summary>仅受限的实际铜排允许局部减小余量；不改变其它排，默认开启。</summary>
        public bool AllowConstrainedBendAllowanceReduction { get; set; } = true;

        internal double GetEffectiveBendAllowance()
        {
            return AllowBendGeometricLimit ? 0d : BendAllowance;
        }
        public double BendingRadius { get; set; } = 10d;
        public double InsulationClearance { get; set; }
        /// <summary>实际搭接面单侧绝缘净距；只用于搭接面膨胀长方体。</summary>
        public double LapInsulationClearance { get; set; } = 35d;
        /// <summary>是否允许最终实体保留 U 型路径；默认关闭。</summary>
        public bool AllowUShapeRoutes { get; set; }
        /// <summary>
        /// 是否在本次 Worker 搜索会话中并行启用方案库匹配。
        /// 数据库访问由宿主负责，Worker 会话以该字段协调并行输出开关。
        /// </summary>
        public bool EnableSchemeLibraryMatching { get; set; } = true;
        /// <summary>
        /// 是否在本次 Worker SolveRequest 中执行普通路径搜索。
        /// 关闭时仍保留完整路径请求，供方案库候选按当前固定端重算中间排；
        /// 仅跳过扫描修复算法，不退回匹配-only 消息。
        /// </summary>
        public bool EnableAlgorithmCalculation { get; set; } = true;
        /// <summary>
        /// 方案库匹配的纯数据输入。数据库读取和模板数据保留由宿主负责，
        /// Worker 只在收到该快照后执行组级匹配评分。
        /// </summary>
        public LinkageSchemeLibraryMatchRequest SchemeLibraryMatchRequest { get; set; }
        /// <summary>
        /// 允许多始端（包含双始端）按整体始端范围进行中间排搭接、连接排
        /// 和垫块计算；始端成员数量为 1 时不启用整体搭接。
        /// </summary>
        public bool AllowDoubleStartMiddleLap { get; set; } = true;
        /// <summary>
        /// 开启后，物理始端恰好一根且采用平行搭接时，生成排沿固定
        /// 始端排宽方向固定居中。垂直搭接始终按允许搭接端头定位，
        /// 不受此开关影响。双始端、柜顶和桥接后的虚拟固定端不使用此规则。
        /// </summary>
        public bool RequireSingleStartMiddleLap { get; set; } = false;
        /// <summary>
        /// 仅通用算法：单始端水平搭接时增加连接排。单根始端搭接排只在
        /// 实际搭接侧加一根，两根及以上在两侧夹排；垂直搭接不增加。
        /// 默认关闭，方案库原路径重算不使用此选项。
        /// </summary>
        public bool EnableSingleStartConnectionBar { get; set; }
        /// <summary>
        /// 开启后，普通满搭接方案完成全部校验后，再把物理始端侧的
        /// 垂直搭接统一缩短到每根固定排至少保留一个生成排排宽的搭接。
        /// 此选项不得参与路径、姿态或候选枚举；柜顶、水平搭接和中间
        /// 接口继续保留普通满搭接几何。
        /// </summary>
        public bool AllowPartialStartPerpendicularLap { get; set; }
        /// <summary>
        /// 开启后继续沿用普通固定排—生成排夹排层位，只删除普通垫块并把
        /// 原垫块相邻的生成排组件局部贴到最近真实搭接面。双始端水平横向
        /// 桥排仍必须保留；默认关闭且不改变普通垫块规则。
        /// </summary>
        public bool UseSpacerlessTerminalLap { get; set; }
        /// <summary>完整方案形成后，非I排或接口垫块总厚超过移动排单层厚度时尝试贴面；默认开启。</summary>
        public bool MinimizeSupportBlocks { get; set; } = true;
        /// <summary>本次请求的腰孔映射旋转90度开关；仅承载输入，默认关闭。</summary>
        public bool RotateSlotHole90Degrees { get; set; }
        public double TerminalHoleEdgeDistance { get; set; } = 5d;
        public bool RequireFixedTerminalEndLap { get; set; }
        /// <summary>
        /// 仅供算法内部的桥接虚拟请求覆盖物理始端行为；外部协议不传输。
        /// </summary>
        internal bool? UseSpacerlessStartTerminalLapOverride { get; set; }
        /// <summary>
        /// 仅供算法内部的桥接虚拟请求覆盖物理柜顶行为；外部协议不传输。
        /// </summary>
        internal bool? UseSpacerlessEndTerminalLapOverride { get; set; }
        /// <summary>
        /// 仅供补充净距搜索恢复历史双始端水平桥排层位候选。开启后只用于
        /// 枚举历史候选；完整候选在进入方案前必须按当前连接排规则重新实体化，
        /// 并重新通过普通搭接、附件、逐排实体和绝缘净距终检。该标记不会改变
        /// 原绝缘主搜索的默认层位规则，也绝不能直接进入发布结果。
        /// </summary>
        internal bool UseLegacyDoubleStartHorizontalTopologyProposal { get; set; }
        /// <summary>
        /// 仅供当前求解会话使用的运行时调度信号；不进入原始 Json 或 Worker
        /// 请求 DTO，由比较调度器在 Worker 内绑定。
        /// </summary>
        internal LinkageRouteSearchRuntimeControl RuntimeControl { get; set; }

        internal bool ShouldUseSpacerlessTerminalLap(bool useStartTerminal)
        {
            var terminalSpecificOverride = useStartTerminal
                ? UseSpacerlessStartTerminalLapOverride
                : UseSpacerlessEndTerminalLapOverride;
            return terminalSpecificOverride ?? UseSpacerlessTerminalLap;
        }

        internal bool RequiresOutputPostProcessing()
        {
            return AllowPartialStartPerpendicularLap ||
                ShouldUseSpacerlessTerminalLap(true) ||
                ShouldUseSpacerlessTerminalLap(false);
        }

        /// <summary>
        /// 创建只供路径搜索、候选转换和方案优化使用的请求视图。不满垂直
        /// 搭接与端头无垫块都是完整普通方案通过终检后的输出后处理，不能
        /// 改变路径候选、层位枚举、候选排序或搜索剪枝。始端搭接排、中间排、
        /// 柜顶搭接排的独立规格属于候选本身，必须继续保留并参与转换、贴合、
        /// 折弯余量和实体校验。其余集合与只读搜索参数保持共享，调用方不得修改。
        /// </summary>
        internal LinkageMultiGroupRouteRequest CreatePathSearchRequest()
        {
            if (!MinimizeSupportBlocks && !AllowPartialStartPerpendicularLap &&
                !ShouldUseSpacerlessTerminalLap(true) &&
                !ShouldUseSpacerlessTerminalLap(false))
            {
                return this;
            }

            var pathSearchRequest = (LinkageMultiGroupRouteRequest)MemberwiseClone();
            pathSearchRequest.MinimizeSupportBlocks = false;
            // 骨架枚举不按此开关剔除U；保留实体许可，使候选转换阶段
            // 先构造合法LL并以真实实体筛选/计长，不能延迟到发布才拆U。
            pathSearchRequest.AllowPartialStartPerpendicularLap = false;
            pathSearchRequest.UseSpacerlessTerminalLap = false;
            pathSearchRequest.UseSpacerlessStartTerminalLapOverride = false;
            pathSearchRequest.UseSpacerlessEndTerminalLapOverride = false;
            return pathSearchRequest;
        }

        /// <summary>
        /// 把历史双始端层位提案恢复为当前正式连接排规则的请求视图。
        /// 仅关闭内部提案标记，其他规格、净距及搜索参数保持不变。
        /// </summary>
        internal LinkageMultiGroupRouteRequest
            CreatePhysicalDoubleStartBridgeValidationRequest()
        {
            var physicalBridgeRequest =
                (LinkageMultiGroupRouteRequest)MemberwiseClone();
            physicalBridgeRequest
                .UseLegacyDoubleStartHorizontalTopologyProposal = false;
            return physicalBridgeRequest;
        }

        /// <summary>
        /// 创建只供已经形成的完整方案按 Json 请求净距做实体复核的请求视图。
        /// 绝缘距离保持不变；其余输出选项和只读集合保持不变，调用方不得修改共享集合。
        /// </summary>
        internal LinkageMultiGroupRouteRequest
            CreateRequestedClearanceValidationRequest()
        {
            return (LinkageMultiGroupRouteRequest)MemberwiseClone();
        }

        /// <summary>
        /// 创建终端输出局部保留垫块的请求视图。只用于无垫块后处理产生
        /// 实体正重叠时回退对应终端，不改变普通方案和其他输出选项。
        /// </summary>
        internal LinkageMultiGroupRouteRequest
            CreateSpacerlessTerminalFallbackRequest(
            bool retainStartTerminalSpacers,
            bool retainEndTerminalSpacers)
        {
            var fallbackRequest =
                (LinkageMultiGroupRouteRequest)MemberwiseClone();
            if (retainStartTerminalSpacers)
            {
                fallbackRequest.UseSpacerlessStartTerminalLapOverride = false;
            }

            if (retainEndTerminalSpacers)
            {
                fallbackRequest.UseSpacerlessEndTerminalLapOverride = false;
            }

            return fallbackRequest;
        }

        internal string ResolveClearanceGroupKey(string groupKey)
        {
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                return null;
            }

            var matchingRouteGroup = (Groups ??
                    new List<LinkageRouteGroupRequest>())
                .FirstOrDefault(routeGroupRequest =>
                    routeGroupRequest != null &&
                    string.Equals(
                        routeGroupRequest.GroupKey,
                        groupKey,
                        StringComparison.OrdinalIgnoreCase));
            return matchingRouteGroup?.GetClearanceGroupKey() ?? groupKey.Trim();
        }
        /// <summary>
        /// 开启后只允许最终生成排的每一段沿全局 X、Y 或 Z 轴。
        /// 默认关闭，继续保留现有 N/M 斜排转换能力。
        /// </summary>
        public bool RequireAxisAlignedRoutes { get; set; }
        /// <summary>
        /// 标识 ContainmentMinimum/ContainmentMaximum 来自用户显式包围盒。
        /// 内部自动搜索范围继续使用同一对坐标，但不会触发显式箱体内壁枚举。
        /// </summary>
        public bool UseExplicitContainmentBox { get; set; }
        public Point3d ContainmentMinimum { get; set; }
        public Point3d ContainmentMaximum { get; set; }
        public LinkageRouteSearchOptions SearchOptions { get; set; } = new LinkageRouteSearchOptions();

        public void Validate()
        {
            if (Groups == null || Groups.Count == 0)
            {
                throw new ArgumentException("至少需要一个联络排逻辑组。", nameof(Groups));
            }

            if (MinimumSegmentLength < 0d || BendAllowance < 0d ||
                BendingRadius <= 0d ||
                double.IsNaN(InsulationClearance) ||
                double.IsInfinity(InsulationClearance) ||
                InsulationClearance < 0d ||
                double.IsNaN(LapInsulationClearance) ||
                double.IsInfinity(LapInsulationClearance) ||
                LapInsulationClearance < 0d || double.IsNaN(TerminalHoleEdgeDistance) ||
                double.IsInfinity(TerminalHoleEdgeDistance) || TerminalHoleEdgeDistance < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(MinimumSegmentLength), "路径尺寸与距离参数必须为非负有效值。" );
            }

            ValidateContainmentBox();

            if (SearchOptions == null ||
                SearchOptions.TotalSearchLimitMilliseconds <= 0 ||
                SearchOptions.MaximumCandidatesPerGroup <= 0 ||
                SearchOptions.MaximumClearanceRepairCandidatesPerState <= 0 ||
                SearchOptions.MaximumSolutionsPerAlgorithm <= 0 ||
                SearchOptions.MaximumDegreeOfParallelism <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SearchOptions),
                    "搜索时限、候选数量、方案数量和并行度必须为正数。" );
            }

            SearchOptions.Validate();

            var groupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var routeGroupRequest in Groups)
            {
                if (routeGroupRequest == null)
                {
                    throw new ArgumentException("逻辑组不能为空。", nameof(Groups));
                }

                routeGroupRequest.Validate(this);
                if (!groupKeys.Add(routeGroupRequest.GroupKey))
                {
                    throw new ArgumentException("逻辑组标识重复：" + routeGroupRequest.GroupKey, nameof(Groups));
                }
            }
        }

        private void ValidateContainmentBox()
        {
            var hasContainmentMinimum = ContainmentMinimum != null;
            var hasContainmentMaximum = ContainmentMaximum != null;
            if (!UseExplicitContainmentBox &&
                !hasContainmentMinimum &&
                !hasContainmentMaximum)
            {
                return;
            }

            if (!hasContainmentMinimum || !hasContainmentMaximum)
            {
                throw new ArgumentException(
                    "路径包围范围必须同时提供两个有效对角点。",
                    nameof(ContainmentMinimum));
            }

            if (!IsFinitePoint(ContainmentMinimum) ||
                !IsFinitePoint(ContainmentMaximum))
            {
                throw new ArgumentException(
                    "路径包围盒对角点坐标必须是有限数值。",
                    nameof(ContainmentMinimum));
            }

            if (ContainmentMaximum.X - ContainmentMinimum.X <= 1e-8d ||
                ContainmentMaximum.Y - ContainmentMinimum.Y <= 1e-8d ||
                ContainmentMaximum.Z - ContainmentMinimum.Z <= 1e-8d)
            {
                throw new ArgumentException(
                    "路径包围盒两个对角点必须在 X、Y、Z 三个方向形成有效范围。",
                    nameof(ContainmentMaximum));
            }
        }

        private static bool IsFinitePoint(Point3d point)
        {
            return point != null &&
                !double.IsNaN(point.X) && !double.IsInfinity(point.X) &&
                !double.IsNaN(point.Y) && !double.IsInfinity(point.Y) &&
                !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);
        }
    }

    public sealed class LinkageRouteGroupRequest
    {
        internal LinkageRouteGroupRequest WithCabinetTerminal(LinkageTerminalAssembly terminal)
        {
            var copy = (LinkageRouteGroupRequest)MemberwiseClone();
            copy.EndTerminal = terminal;
            return copy;
        }

        public string GroupKey { get; set; }
        /// <summary>
        /// 当前逻辑组的电气相别。同一相别即使拆成不同 GroupKey，也不参与
        /// 相间绝缘距离计算；未提供时继续按 GroupKey 区分，兼容旧请求。
        /// </summary>
        public string Phase { get; set; }
        public string DisplayName { get; set; }
        public LinkageTerminalAssembly StartTerminal { get; set; } = new LinkageTerminalAssembly();
        public LinkageTerminalAssembly EndTerminal { get; set; } = new LinkageTerminalAssembly();
        public double BusbarWidth { get; set; }
        public double BusbarThickness { get; set; }
        public int StackCount { get; set; }
        /// <summary>始端侧首根生成搭接排宽；非正数时继承中间排宽。</summary>
        public double StartLapBusbarWidth { get; set; }
        /// <summary>始端侧首根生成搭接排厚；非正数时继承中间排厚。</summary>
        public double StartLapBusbarThickness { get; set; }
        /// <summary>始端侧首根生成搭接排叠排数；非正数时继承中间排叠排数，不受固定端根数限制。</summary>
        public int StartLapStackCount { get; set; }
        /// <summary>柜顶侧末根生成搭接排宽；非正数时继承中间排宽。</summary>
        public double CabinetTopLapBusbarWidth { get; set; }
        /// <summary>柜顶侧末根生成搭接排厚；非正数时继承中间排厚。</summary>
        public double CabinetTopLapBusbarThickness { get; set; }
        /// <summary>柜顶侧末根生成搭接排叠排数；非正数时继承中间排叠排数，不受固定端根数限制。</summary>
        public int CabinetTopLapStackCount { get; set; }
        public double StackStartSpace { get; set; }
        public double StackMiddleSpace { get; set; }
        public double StackEndSpace { get; set; }
        public Vector3d StackMoveVector { get; set; }
        public bool StackReverse { get; set; }

        internal double PathSearchActualStartLapBusbarWidth { get; set; }
        internal double PathSearchPhysicalStartLapWidth { get; set; }
        internal bool PathSearchIsReversed { get; set; }

        internal double GetPhysicalStartParallelLapLength(LinkageMultiGroupRouteRequest request, bool useTraversalStart)
            => useTraversalStart == PathSearchIsReversed ? 0d : PathSearchPhysicalStartLapWidth > 0d
                ? PathSearchPhysicalStartLapWidth : PathSearchIsReversed
                    ? GetCabinetTopLapBusbarWidth(request) : GetStartLapBusbarWidth(request);

        internal double GetShortPhysicalStartLapLength(LinkageMultiGroupRouteRequest routeRequest,
            bool useTraversalStart)
        {
            if (useTraversalStart == PathSearchIsReversed) return 0d;
            var physicalStartWidth = PathSearchPhysicalStartLapWidth > 0d
                ? PathSearchPhysicalStartLapWidth
                : PathSearchIsReversed ? GetCabinetTopLapBusbarWidth(routeRequest) : GetStartLapBusbarWidth(routeRequest);
            var physicalStartTerminal = PathSearchIsReversed ? EndTerminal : StartTerminal;
            var oppositeAnchor = (PathSearchIsReversed ? StartTerminal : EndTerminal)?.RouteAnchor;
            if (Routing.LinkageRouteTerminalPoseFactory.TryGetCommonContactSegmentLength(
                physicalStartTerminal, oppositeAnchor, out var contactLength))
                return contactLength < physicalStartWidth - 1e-8d ? contactLength : 0d;
            // 某成员确为短始端但共同接触段无法证明时，不能回退成“不受限制”。
            return (physicalStartTerminal?.Members ?? new List<LinkageTerminalMember>()).Any(member =>
                Routing.LinkageRouteTerminalPoseFactory.TryGetCommonContactSegmentLength(
                    new LinkageTerminalAssembly { Members = new List<LinkageTerminalMember> { member },
                        ThicknessDirection = physicalStartTerminal.ThicknessDirection }, oppositeAnchor, out var memberLength) &&
                    memberLength < physicalStartWidth - 1e-8d) ? double.NaN : 0d;
        }
        internal double PathSearchActualCabinetTopLapBusbarWidth { get; set; }

        public double GetBusbarWidth(LinkageMultiGroupRouteRequest routeRequest)
        {
            return BusbarWidth > 0d ? BusbarWidth : routeRequest.DefaultBusbarWidth;
        }

        public double GetBusbarThickness(LinkageMultiGroupRouteRequest routeRequest)
        {
            return BusbarThickness > 0d ? BusbarThickness : routeRequest.DefaultBusbarThickness;
        }

        public int GetStackCount(LinkageMultiGroupRouteRequest routeRequest)
        {
            return StackCount > 0 ? StackCount : Math.Max(1, routeRequest.DefaultStackCount);
        }

        public double GetStartLapBusbarWidth(LinkageMultiGroupRouteRequest routeRequest)
        {
            return StartLapBusbarWidth > 0d
                ? StartLapBusbarWidth
                : GetBusbarWidth(routeRequest);
        }

        public double GetStartLapBusbarThickness(LinkageMultiGroupRouteRequest routeRequest)
        {
            return StartLapBusbarThickness > 0d
                ? StartLapBusbarThickness
                : GetBusbarThickness(routeRequest);
        }

        public int GetStartLapStackCount(LinkageMultiGroupRouteRequest routeRequest)
        {
            return StartLapStackCount > 0
                ? StartLapStackCount
                : GetStackCount(routeRequest);
        }

        public double GetCabinetTopLapBusbarWidth(LinkageMultiGroupRouteRequest routeRequest)
        {
            return CabinetTopLapBusbarWidth > 0d
                ? CabinetTopLapBusbarWidth
                : GetBusbarWidth(routeRequest);
        }

        public double GetCabinetTopLapBusbarThickness(LinkageMultiGroupRouteRequest routeRequest)
        {
            return CabinetTopLapBusbarThickness > 0d
                ? CabinetTopLapBusbarThickness
                : GetBusbarThickness(routeRequest);
        }

        public int GetCabinetTopLapStackCount(LinkageMultiGroupRouteRequest routeRequest)
        {
            return CabinetTopLapStackCount > 0
                ? CabinetTopLapStackCount
                : GetStackCount(routeRequest);
        }

        /// <summary>
        /// 创建兼容旧版路径骨架枚举的规格视图。仅用于在分段规格导致
        /// 候选为空时补充搜索；正式候选仍使用当前始端、柜顶规格做转换
        /// 和实体终检，不能把该视图用于输出。
        /// </summary>
        internal LinkageRouteGroupRequest
            CreateGenericPathSearchSpecificationView(
                LinkageMultiGroupRouteRequest routeRequest)
        {
            var genericSpecificationView =
                (LinkageRouteGroupRequest)MemberwiseClone();
            var startLapWidth = GetStartLapBusbarWidth(routeRequest);
            genericSpecificationView.PathSearchPhysicalStartLapWidth = PathSearchPhysicalStartLapWidth > 0d
                ? PathSearchPhysicalStartLapWidth : PathSearchIsReversed
                    ? GetCabinetTopLapBusbarWidth(routeRequest) : startLapWidth;
            var cabinetTopLapWidth = GetCabinetTopLapBusbarWidth(routeRequest);
            var terminalWidthDifference = Math.Abs(
                startLapWidth - cabinetTopLapWidth) > 1e-8d;
            if (terminalWidthDifference)
            {
                genericSpecificationView.PathSearchActualStartLapBusbarWidth =
                    startLapWidth;
                genericSpecificationView.PathSearchActualCabinetTopLapBusbarWidth =
                    cabinetTopLapWidth;
            }
            genericSpecificationView.StartLapBusbarWidth = 0d;
            genericSpecificationView.StartLapBusbarThickness = 0d;
            genericSpecificationView.StartLapStackCount = 0;
            genericSpecificationView.CabinetTopLapBusbarWidth = 0d;
            genericSpecificationView.CabinetTopLapBusbarThickness = 0d;
            genericSpecificationView.CabinetTopLapStackCount = 0;
            return genericSpecificationView;
        }

        /// <summary>
        /// 按物理拆排段取得排宽。单段同时接触两端时遵循柜顶优先规则。
        /// </summary>
        internal double GetSectionBusbarWidth(
            LinkageMultiGroupRouteRequest routeRequest,
            int sectionIndex,
            int sectionCount)
        {
            if (sectionCount <= 1 || sectionIndex >= sectionCount - 1)
            {
                return GetCabinetTopLapBusbarWidth(routeRequest);
            }

            return sectionIndex <= 0
                ? GetStartLapBusbarWidth(routeRequest)
                : GetBusbarWidth(routeRequest);
        }

        /// <summary>
        /// 按物理拆排段取得排厚。单段同时接触两端时遵循柜顶优先规则。
        /// </summary>
        internal double GetSectionBusbarThickness(
            LinkageMultiGroupRouteRequest routeRequest,
            int sectionIndex,
            int sectionCount)
        {
            if (sectionCount <= 1 || sectionIndex >= sectionCount - 1)
            {
                return GetCabinetTopLapBusbarThickness(routeRequest);
            }

            return sectionIndex <= 0
                ? GetStartLapBusbarThickness(routeRequest)
                : GetBusbarThickness(routeRequest);
        }

        /// <summary>
        /// 按物理拆排段取得叠排数量。单段同时接触两端时遵循柜顶优先规则。
        /// </summary>
        internal int GetSectionStackCount(
            LinkageMultiGroupRouteRequest routeRequest,
            int sectionIndex,
            int sectionCount)
        {
            if (sectionCount <= 1 || sectionIndex >= sectionCount - 1)
            {
                return GetCabinetTopLapStackCount(routeRequest);
            }

            return sectionIndex <= 0
                ? GetStartLapStackCount(routeRequest)
                : GetStackCount(routeRequest);
        }

        public double GetDoubleStartBridgeBusbarWidth(LinkageMultiGroupRouteRequest routeRequest)
        {
            // 只派生连接排尺寸，不打开连接排开关，也不改变生成搭接排规格。
            var requestedStartWidth = GetStartLapBusbarWidth(routeRequest);
            return Routing.LinkageRouteTerminalPoseFactory.TryGetCommonContactSegmentLength(
                StartTerminal, EndTerminal?.RouteAnchor, out var actualContactLength)
                ? Math.Min(requestedStartWidth, actualContactLength) : requestedStartWidth;
        }

        internal double GetStartTerminalRequiredLapLength(LinkageMultiGroupRouteRequest routeRequest)
        {
            // 水平搭接长度由设置决定，不能随实际排宽或固定端短段缩小。
            return GetStartLapBusbarWidth(routeRequest);
        }

        public double GetDoubleStartBridgeBusbarThickness(LinkageMultiGroupRouteRequest routeRequest)
        {
            // Keep bridge thickness consistent with the start-lap busbar for
            // both single-member and multi-member stacks.
            return GetStartLapBusbarThickness(routeRequest);
        }

        internal double GetMiddleSectionStackBendAllowance(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return LinkageRouteStackGeometry.GetStackBendAllowance(
                GetBusbarThickness(routeRequest),
                GetStackCount(routeRequest),
                StackMiddleSpace,
                routeRequest.GetEffectiveBendAllowance());
        }

        internal double GetStartLapSectionStackBendAllowance(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return LinkageRouteStackGeometry.GetStackBendAllowance(
                GetStartLapBusbarThickness(routeRequest),
                GetStartLapStackCount(routeRequest),
                StackMiddleSpace,
                routeRequest.GetEffectiveBendAllowance());
        }

        internal double GetCabinetTopLapSectionStackBendAllowance(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return LinkageRouteStackGeometry.GetStackBendAllowance(
                GetCabinetTopLapBusbarThickness(routeRequest),
                GetCabinetTopLapStackCount(routeRequest),
                StackMiddleSpace,
                routeRequest.GetEffectiveBendAllowance());
        }

        /// <summary>
        /// 搜索骨架只保留一套内部临界坐标，因此取两处规格交界、两个折弯侧
        /// 的最不利值。实际完整端段长度仍是“对方排宽 + 当前侧折弯余量”；
        /// 此处从搭接中心量到折弯点，所以使用对方半个排宽。
        /// </summary>
        internal double GetMaximumSectionInternalLapBendContinuationDistance(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            var middleSectionStackBendAllowance =
                GetMiddleSectionStackBendAllowance(routeRequest);
            var startLapSectionStackBendAllowance =
                GetStartLapSectionStackBendAllowance(routeRequest);
            var cabinetTopLapSectionStackBendAllowance =
                GetCabinetTopLapSectionStackBendAllowance(routeRequest);
            var boundaryCenterToBendDistance = new[]
                {
                    GetBusbarWidth(routeRequest) / 2d +
                        startLapSectionStackBendAllowance,
                    GetStartLapBusbarWidth(routeRequest) / 2d +
                        middleSectionStackBendAllowance,
                    GetCabinetTopLapBusbarWidth(routeRequest) / 2d +
                        middleSectionStackBendAllowance,
                    GetBusbarWidth(routeRequest) / 2d +
                        cabinetTopLapSectionStackBendAllowance
                }
                .Max();
            if (!HasSectionSpecificBusbarSettings(routeRequest))
            {
                return boundaryCenterToBendDistance;
            }

            // 相邻分段最多允许相差一层。斜段在按实际层位偏移后，搭接
            // 边界到首个折弯点可能再缩短一个成员中心距；搜索骨架只在
            // 规格确实不同时补这一层余量，默认规格保持历史搜索范围。
            var maximumMixedSectionMemberPitch = new[]
                {
                    GetBusbarThickness(routeRequest),
                    GetStartLapBusbarThickness(routeRequest),
                    GetCabinetTopLapBusbarThickness(routeRequest)
                }
                .Max() + Math.Max(0d, StackMiddleSpace);
            return boundaryCenterToBendDistance +
                maximumMixedSectionMemberPitch;
        }

        internal bool HasSectionSpecificBusbarSettings(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            return Math.Abs(GetStartLapBusbarWidth(routeRequest) -
                            GetBusbarWidth(routeRequest)) > 1e-8d ||
                Math.Abs(GetStartLapBusbarThickness(routeRequest) -
                         GetBusbarThickness(routeRequest)) > 1e-8d ||
                GetStartLapStackCount(routeRequest) != GetStackCount(routeRequest) ||
                Math.Abs(GetCabinetTopLapBusbarWidth(routeRequest) -
                         GetBusbarWidth(routeRequest)) > 1e-8d ||
                Math.Abs(GetCabinetTopLapBusbarThickness(routeRequest) -
                         GetBusbarThickness(routeRequest)) > 1e-8d ||
                GetCabinetTopLapStackCount(routeRequest) != GetStackCount(routeRequest);
        }

        public double GetRequiredInsulationClearance(
            LinkageMultiGroupRouteRequest routeRequest)
        {
            // 绝缘距离的业务含义是实际铜排实体表面之间的净距离。
            // 双始端、叠排以及搭接排的排厚已经由各自的扫描实体体现，
            // 不能再次追加到阈值中，否则会把 20mm 错误放大为 20mm + 排厚。
            return routeRequest.InsulationClearance;
        }

        internal string GetClearanceGroupKey()
        {
            var normalizedPhase = (Phase ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalizedPhase)
                ? (GroupKey ?? string.Empty).Trim()
                : normalizedPhase.ToUpperInvariant();
        }

        internal void Validate(LinkageMultiGroupRouteRequest routeRequest)
        {
            if (StartTerminal == null || EndTerminal == null)
            {
                throw new ArgumentException("每个逻辑组必须同时提供始端和末端固定排。", nameof(StartTerminal));
            }

            if (string.IsNullOrWhiteSpace(GroupKey))
            {
                throw new ArgumentException("逻辑组必须提供唯一标识。", nameof(GroupKey));
            }

            if (GetBusbarWidth(routeRequest) <= 0d || GetBusbarThickness(routeRequest) <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(BusbarWidth), "每个逻辑组必须有有效排宽和排厚。" );
            }

            if (GetStartLapBusbarWidth(routeRequest) <= 0d ||
                GetStartLapBusbarThickness(routeRequest) <= 0d ||
                GetCabinetTopLapBusbarWidth(routeRequest) <= 0d ||
                GetCabinetTopLapBusbarThickness(routeRequest) <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(StartLapBusbarWidth),
                    "始端搭接排、中间排和柜顶搭接排的排宽和排厚必须大于 0。" );
            }

            var startLapStackCount = GetStartLapStackCount(routeRequest);
            var middleStackCount = GetStackCount(routeRequest);
            var cabinetTopLapStackCount = GetCabinetTopLapStackCount(
                routeRequest);

            // 固定端 Members 只描述实际始端/柜顶铜排实体，不能拿来限制
            // 生成的端部搭接排数量。这里仅校验三段“生成排”自身的数量，
            // 且必须两两相差不超过一层。
            ValidateGeneratedStackCountDifference(
                startLapStackCount,
                "始端搭接排",
                middleStackCount,
                "中间排");
            ValidateGeneratedStackCountDifference(
                middleStackCount,
                "中间排",
                cabinetTopLapStackCount,
                "柜顶搭接排");
            ValidateGeneratedStackCountDifference(
                startLapStackCount,
                "始端搭接排",
                cabinetTopLapStackCount,
                "柜顶搭接排");

            if (StackStartSpace < 0d || StackMiddleSpace < 0d || StackEndSpace < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(StackStartSpace),
                    "每个逻辑组的叠排始端、中间和末端间距不能为负数。" );
            }

            if (StackMoveVector != null && StackMoveVector.Norm < 1e-8d)
            {
                throw new ArgumentException(
                    "叠排偏移向量必须留空或提供非零向量。",
                    nameof(StackMoveVector));
            }

            StartTerminal.Validate("始端");
            EndTerminal.Validate("末端");
        }

        private void ValidateGeneratedStackCountDifference(
            int firstSectionStackCount,
            string firstSectionName,
            int secondSectionStackCount,
            string secondSectionName)
        {
            if (Math.Abs(firstSectionStackCount - secondSectionStackCount) <= 1)
            {
                return;
            }

            var phaseOrGroupName = string.IsNullOrWhiteSpace(Phase)
                ? string.IsNullOrWhiteSpace(DisplayName) ? GroupKey : DisplayName
                : Phase.Trim().ToUpperInvariant() + " 相";
            throw new ArgumentException(
                phaseOrGroupName + "的" + firstSectionName + "叠排数 " +
                firstSectionStackCount + " 与" + secondSectionName + "叠排数 " +
                secondSectionStackCount + " 的差值大于 1，不允许搭接。",
                nameof(StackCount));
        }
    }

    public sealed class LinkageTerminalAssembly
    {
        public Point3d RouteAnchor { get; set; }
        public Vector3d LengthDirection { get; set; }
        public Vector3d ThicknessDirection { get; set; }
        public IList<LinkageTerminalMember> Members { get; set; } = new List<LinkageTerminalMember>();

        // 仅供算法进程内已经实体化的中间 I 排使用。它不是输入契约字段，
        // 因此不参与 Worker/Json 传输，也不会改变普通固定端的姿态枚举。
        internal IList<Point3d> PreferredPerpendicularLapReferencePoints { get; set; }

        // 只由双始端实体桥的第二阶段创建，不进入输入/Worker契约。
        // 桥是生成排，允许内部搭接；另一头真实柜顶仍受固定端限制。
        internal bool IsMaterializedRoutingBridge { get; set; }
        // 仅进程内保留原固定实体及实际桥排；不改变 v3/v16 输入字段。
        internal Routing.LinkageCabinetBridgeAssembly CabinetBridgeAssembly { get; set; }

        internal void Validate(string terminalLabel)
        {
            if (RouteAnchor == null)
            {
                throw new ArgumentException(terminalLabel + "缺少中心线锚点。", nameof(RouteAnchor));
            }

            if (LengthDirection == null || LengthDirection.Norm < 1e-8 ||
                ThicknessDirection == null || ThicknessDirection.Norm < 1e-8)
            {
                throw new ArgumentException(terminalLabel + "缺少有效排长或排厚方向。" );
            }

            if (Math.Abs(LengthDirection.Normalized.Dot(ThicknessDirection.Normalized)) > 1e-6)
            {
                throw new ArgumentException(terminalLabel + "排长与排厚方向必须正交。" );
            }
            if (Members == null || Members.Count == 0)
            {
                throw new ArgumentException(terminalLabel + "必须至少提供一个固定端排实体。", nameof(Members));
            }

            for (var memberIndex = 0; memberIndex < Members.Count; memberIndex++)
            {
                var terminalMember = Members[memberIndex];
                var fixedBusbarSetting = terminalMember?.FixedBusbar;
                if (fixedBusbarSetting == null ||
                    fixedBusbarSetting.PointList == null ||
                    fixedBusbarSetting.PointList.Count < 2 ||
                    fixedBusbarSetting.BusBarWidth <= 0d ||
                    fixedBusbarSetting.BusBarThickness <= 0d ||
                    fixedBusbarSetting.StartBusbarVector == null ||
                    fixedBusbarSetting.StartBusbarVector.Norm < 1e-8 ||
                    fixedBusbarSetting.WidthVector == null ||
                    fixedBusbarSetting.WidthVector.Norm < 1e-8)
                {
                    throw new ArgumentException(
                        string.Format("{0}固定排成员 {1} 的中心线、尺寸或方向无效。", terminalLabel, memberIndex + 1),
                        nameof(Members));
                }

                for (var pointIndex = 0; pointIndex < fixedBusbarSetting.PointList.Count; pointIndex++)
                {
                    if (fixedBusbarSetting.PointList[pointIndex] == null)
                    {
                        throw new ArgumentException(
                            string.Format("{0}固定排成员 {1} 包含空中心线点。", terminalLabel, memberIndex + 1),
                            nameof(Members));
                    }
                }

                ValidateForbiddenExitDirection(
                    terminalLabel,
                    memberIndex,
                    terminalMember.ForbiddenHorizontalExitDirection,
                    fixedBusbarSetting);
            }

            ValidateMemberExitDirectionConsistency(terminalLabel);
        }

        private static void ValidateForbiddenExitDirection(
            string terminalLabel,
            int memberIndex,
            Vector3d forbiddenExitDirection,
            PlanarBendBusbarSetting fixedBusbarSetting)
        {
            const double directionTolerance = 1e-6d;
            if (forbiddenExitDirection == null)
            {
                return;
            }

            if (forbiddenExitDirection.Norm <= directionTolerance)
            {
                throw new ArgumentException(string.Format(
                    "{0}固定排成员 {1} 的不可伸出方向不能为零向量。",
                    terminalLabel,
                    memberIndex + 1));
            }

            var normalizedForbiddenExitDirection = forbiddenExitDirection.Normalized;
            var normalizedBusbarThicknessDirection = fixedBusbarSetting.StartBusbarVector.Normalized;
            if (Math.Abs(normalizedForbiddenExitDirection.Dot(
                    normalizedBusbarThicknessDirection)) > directionTolerance)
            {
                throw new ArgumentException(string.Format(
                    "{0}固定排成员 {1} 的不可伸出方向作为排长轴时必须与排厚方向正交。",
                    terminalLabel,
                    memberIndex + 1));
            }

            var resolvedBusbarWidthDirection = normalizedBusbarThicknessDirection
                .Cross(normalizedForbiddenExitDirection);
            if (resolvedBusbarWidthDirection == null ||
                resolvedBusbarWidthDirection.Norm <= directionTolerance)
            {
                throw new ArgumentException(string.Format(
                    "{0}固定排成员 {1} 无法由不可伸出方向和排厚方向重算排宽方向。",
                    terminalLabel,
                    memberIndex + 1));
            }

            fixedBusbarSetting.WidthVector = resolvedBusbarWidthDirection.Normalized;

            var firstUsableSegmentDirection = default(Vector3d);
            for (var pointIndex = 0;
                pointIndex + 1 < fixedBusbarSetting.PointList.Count;
                pointIndex++)
            {
                var segmentDirection = new Vector3d(
                    fixedBusbarSetting.PointList[pointIndex],
                    fixedBusbarSetting.PointList[pointIndex + 1]);
                if (segmentDirection.Norm <= directionTolerance)
                {
                    continue;
                }

                firstUsableSegmentDirection = segmentDirection.Normalized;
                break;
            }

            if (firstUsableSegmentDirection == null ||
                Math.Abs(normalizedForbiddenExitDirection.Dot(firstUsableSegmentDirection)) <
                1d - directionTolerance)
            {
                throw new ArgumentException(string.Format(
                    "{0}固定排成员 {1} 的不可伸出方向必须与排长方向平行。",
                    terminalLabel,
                    memberIndex + 1));
            }
        }

        private void ValidateMemberExitDirectionConsistency(string terminalLabel)
        {
            const double directionTolerance = 1e-6d;
            var configuredDirections = Members
                .Select(member => member?.ForbiddenHorizontalExitDirection)
                .Where(direction => direction != null && direction.Norm > directionTolerance)
                .Select(direction => direction.Normalized)
                .ToList();
            if (configuredDirections.Count <= 1)
            {
                return;
            }

            var referenceDirection = configuredDirections[0];
            if (configuredDirections.Skip(1).Any(direction =>
                direction.Dot(referenceDirection) < 1d - directionTolerance))
            {
                throw new ArgumentException(
                    terminalLabel + "同组固定排的不可伸出方向必须一致，不能要求从相反两端出发。");
            }
        }
    }

    public sealed class LinkageTerminalMember
    {
        public string MemberKey { get; set; }
        public PlanarBendBusbarSetting FixedBusbar { get; set; }
        public Vector3d ForbiddenHorizontalExitDirection { get; set; }
        /// <summary>固定端逐根孔快照（HoleInto）；未提供表示未开孔。</summary>
        public IList<LinkageTerminalHoleInfo> Holes { get; set; } = new List<LinkageTerminalHoleInfo>();
    }

    public sealed class LinkageRouteObstacle
    {
        public string ObstacleKey { get; set; }
        public string GroupKey { get; set; }
        public Point3d Minimum { get; set; }
        public Point3d Maximum { get; set; }
    }

    /// <summary>
    /// 联络排算法统一规则配置。该对象属于单次请求，可由算法调用方或
    /// Worker 客户端覆盖；未赋值时使用生产默认值。配置只影响搜索资源、
    /// 阶段切换、候选预算和成功方案优化策略，不改变几何精度容差。
    /// </summary>
    public sealed class LinkageRouteSearchOptions
    {
        /// <summary>
        /// 开启后跳过首方案快速层并穷尽请求净距下的普通正反向路径与组合；
        /// 不会启动降低绝缘距离的补充搜索。
        /// </summary>
        public bool PrioritizeAllSolutions { get; set; }

        /// <summary>期望首个方案出现的时间，单位毫秒；各快速阶段不会超过该值。</summary>
        public int FirstSolutionTargetMilliseconds { get; set; } = 15000;

        /// <summary>启用自动总时限时的整次算法搜索上限，单位毫秒。</summary>
        public int TotalSearchLimitMilliseconds { get; set; } = 60000;

        /// <summary>是否由算法在 TotalSearchLimitMilliseconds 到达时主动取消搜索。</summary>
        public bool EnableAutomaticTotalSearchLimit { get; set; } = true;

        /// <summary>每个逻辑组最终保留的普通路径候选上限；越大覆盖率越高但组合量越大。</summary>
        public int MaximumCandidatesPerGroup { get; set; } = 128;

        /// <summary>每个组合状态允许生成的绝缘定向修复候选上限。</summary>
        public int MaximumClearanceRepairCandidatesPerState { get; set; } = 64;

        /// <summary>每个算法保留并发布的成功方案上限。</summary>
        public int MaximumSolutionsPerAlgorithm { get; set; } = 10;

        /// <summary>候选排序后的束宽；越大保留的并行拓扑越多，同时增加内存和校验量。</summary>
        public int BeamWidth { get; set; } = 24;

        /// <summary>请求允许算法使用的最大工作并行度；最终还会扣除调度发布保留线程。</summary>
        public int MaximumDegreeOfParallelism { get; set; } = Math.Max(
            1,
            System.Environment.ProcessorCount - 1);

        /// <summary>连续坐标逼近和局部修复允许停止细分的最小步长，单位毫米。</summary>
        public double MinimumAdaptiveRepairStep { get; set; } = 0.5d;

        /// <summary>普通快速正向/反向层和快速组合的单阶段上限，单位毫秒。</summary>
        public int FastSearchStageLimitMilliseconds { get; set; } = 3000;

        /// <summary>快速层失败后扩展绝缘候选与再次组合的单阶段上限，单位毫秒。</summary>
        public int ExtendedFastSearchStageLimitMilliseconds { get; set; } = 5000;

        /// <summary>深层正反向绝缘候选单个遍历方向的时间片，单位毫秒。</summary>
        public int DeepTraversalTimeSliceMilliseconds { get; set; } = 8000;

        /// <summary>最受约束组的整轮深层定向修复时限，单位毫秒。</summary>
        public int ConstraintDirectedDeepRepairLimitMilliseconds { get; set; } = 40000;

        /// <summary>深层定向修复优先固定的根候选数量。</summary>
        public int ConstraintDirectedRootCandidateCount { get; set; } = 2;

        /// <summary>保留方案数达到该值后切换主搜索与方案优化的算力比例。</summary>
        public int SolutionAllocationSwitchSolutionCount { get; set; } = 10;

        /// <summary>切换后分配给继续搜索新方案的工作线程比例，范围 (0,1)。</summary>
        public double PostSwitchMainSearchWorkerRatio { get; set; } = 0.25d;

        /// <summary>切换后分配给已有方案优化的工作线程比例，范围 (0,1)，须与主搜索比例之和为 1。</summary>
        public double PostSwitchSolutionOptimizationWorkerRatio { get; set; } = 0.75d;

        /// <summary>
        /// 切换后每类进入动态优化队列的方案数量；优化方案和未优化方案分别取该数量，
        /// 新结果只替换对应类别的末位，因此默认最多共同优化 3 个优化方案和 3 个未优化方案。
        /// </summary>
        public int PreferredSolutionOptimizationCount { get; set; } = 3;

        /// <summary>
        /// 已有成功方案后，连续多久没有保留新的普通或优化方案便进入停滞分配，
        /// 单位毫秒；进入后只保留 StagnationMainSearchWorkerCount 路继续找新方案。
        /// </summary>
        public int SolutionOptimizationStagnationMilliseconds { get; set; } =
            120000;

        /// <summary>
        /// 方案停滞分配阶段继续执行普通方案搜索的工作线程数；其余可用线程
        /// 转给当前最优方案的后台优化，调度发布预留线程不计入该数值。
        /// </summary>
        public int StagnationMainSearchWorkerCount { get; set; } = 1;

        /// <summary>
        /// 同一成功方案谱系从首次进入最短化开始允许持续优化的最长时间，
        /// 单位毫秒；默认 30 分钟，到期只停止该谱系优化，不取消普通搜索。
        /// </summary>
        public int MaximumSolutionOptimizationDurationMilliseconds { get; set; } =
            1800000;

        /// <summary>从逻辑处理器中预留给任务调度、结果发布和宿主响应的线程数。</summary>
        public int SchedulingAndPublishingWorkerReserve { get; set; } = 1;

        /// <summary>切换前允许后台方案优化占用的可用工作线程比例，范围 (0,1]。</summary>
        public double InitialSolutionOptimizationWorkerRatio { get; set; } = 0.5d;

        /// <summary>切换前后台方案优化允许使用的最大线程数。</summary>
        public int MaximumInitialSolutionOptimizationWorkerCount { get; set; } = 2;

        /// <summary>方案优化线程中为快速几何优化保留、不得被深度任务占用的线程数。</summary>
        public int FastSolutionOptimizationWorkerReserve { get; set; } = 1;

        /// <summary>后处理几何恢复到固定请求净距的总时限，单位毫秒。</summary>
        public int ClearanceOptimizationLimitMilliseconds { get; set; } = 30000;

        /// <summary>固定净距几何恢复回退到路径枚举时，每个方向的遍历时间片，单位毫秒。</summary>
        public int ClearanceOptimizationTraversalLimitMilliseconds { get; set; } = 2000;

        /// <summary>每轮固定净距几何恢复最多评估的局部替换候选数量。</summary>
        public int MaximumClearanceOptimizationCandidateCount { get; set; } = 24;

        /// <summary>后处理几何恢复到 Json 请求净距的最大轮数。</summary>
        public int MaximumClearanceOptimizationPassCount { get; set; } = 4;

        /// <summary>固定净距几何恢复的内部位移步长，单位毫米；不改变请求净距。</summary>
        public double ClearanceOptimizationStep { get; set; } = 5d;

        /// <summary>使用已登记候选目录组合满足原请求净距方案的最长时间，单位毫秒。</summary>
        public int ClearanceOptimizationCatalogLimitMilliseconds { get; set; } = 15000;

        /// <summary>历史降距发布下限，保留用于协议兼容；当前不参与发布。</summary>
        public double MinimumPublishableClearance { get; set; } = 5d;

        /// <summary>历史降距搜索参数，保留用于协议兼容；当前不执行。</summary>
        public int SupplementalClearanceStartDelayMilliseconds { get; set; } = 10000;

        /// <summary>历史降距发布参数，保留用于协议兼容；当前不执行。</summary>
        public int RelaxedClearancePublicationMilliseconds { get; set; } = 60000;

        /// <summary>历史降距搜索参数，保留用于协议兼容；当前不执行。</summary>
        public int SupplementalClearanceAttemptLimitMilliseconds { get; set; } = 70000;

        /// <summary>历史降距搜索参数，保留用于协议兼容；当前不执行。</summary>
        public double SupplementalClearanceStep { get; set; } = 5d;

        /// <summary>历史降距搜索参数，保留用于协议兼容；当前不执行。</summary>
        public int SupplementalClearanceIncreaseDelayMilliseconds { get; set; } = 20000;

        /// <summary>历史降距搜索参数，保留用于协议兼容；当前不执行。</summary>
        public double SupplementalClearanceMaximumIncrease { get; set; } = 10d;

        /// <summary>历史降距搜索参数，保留用于协议兼容；当前不执行。</summary>
        public double SupplementalClearanceSeed { get; set; } = 5d;

        /// <summary>历史降距搜索参数，保留用于协议兼容；当前不执行。</summary>
        public int SupplementalClearanceMaximumDegreeOfParallelism { get; set; } = 1;

        /// <summary>
        /// 历史降距分流参数，保留用于协议兼容；当前生产调度不执行。
        /// </summary>
        public bool EnableSupplementalLegacyDoubleStartPlacementSearch { get; set; } = true;

        /// <summary>历史双始端水平桥排层位兼容分流可占用的最大工作线程数。</summary>
        public int SupplementalLegacyPlacementMaximumDegreeOfParallelism { get; set; } = 1;

        /// <summary>历史双始端水平桥排层位兼容分流最多保留的成功方案数。</summary>
        public int SupplementalLegacyPlacementMaximumSolutions { get; set; } = 10;

        /// <summary>快速层每种段数最多执行完整转换校验的骨架数量。</summary>
        public int FastLayerMaximumEvaluatedSkeletonCount { get; set; } = 128;

        /// <summary>快速首解层最终最多收集的有效绝缘种子候选数量。</summary>
        public int FastLayerMaximumSeedCandidateCount { get; set; } = 16;

        /// <summary>快速首解层每个段数层最多收集的有效种子候选数量。</summary>
        public int FastLayerMaximumSeedCandidateCountPerSegmentLayer { get; set; } = 8;

        /// <summary>快速层每种段数最多访问的原始骨架数量。</summary>
        public int FastLayerMaximumVisitedSkeletonCount { get; set; } = 256;

        /// <summary>快速层同一轴方向拓扑最多执行完整转换校验的骨架数量。</summary>
        public int FastLayerMaximumEvaluatedSkeletonCountPerTopology { get; set; } = 48;

        /// <summary>扩展层每种段数最多执行完整转换校验的骨架数量。</summary>
        public int ExtendedLayerMaximumEvaluatedSkeletonCount { get; set; } = 512;

        /// <summary>扩展首解层最终最多收集的有效绝缘种子候选数量。</summary>
        public int ExtendedLayerMaximumSeedCandidateCount { get; set; } = 64;

        /// <summary>扩展首解层每个段数层最多收集的有效种子候选数量。</summary>
        public int ExtendedLayerMaximumSeedCandidateCountPerSegmentLayer { get; set; } = 16;

        /// <summary>扩展层每种段数最多访问的原始骨架数量。</summary>
        public int ExtendedLayerMaximumVisitedSkeletonCount { get; set; } = 2048;

        /// <summary>扩展层同一轴方向拓扑最多执行完整转换校验的骨架数量。</summary>
        public int ExtendedLayerMaximumEvaluatedSkeletonCountPerTopology { get; set; } = 128;

        /// <summary>快速层允许枚举的最大 XYZ 线段数，范围 2～8。</summary>
        public int FastLayerMaximumSegmentCount { get; set; } = 6;

        /// <summary>扩展和深层允许枚举的最大 XYZ 线段数，范围为快速层上限～8。</summary>
        public int ExtendedLayerMaximumSegmentCount { get; set; } = 8;

        /// <summary>单个段数层为寻找首个有效候选最多转换的种子骨架数量。</summary>
        public int MaximumSeedSkeletonsPerSegmentLayer { get; set; } = 8;

        /// <summary>同一候选对发生绝缘冲突时最多保留的定向修复候选数量。</summary>
        public int MaximumPairRepairCandidateCount { get; set; } = 8;

        /// <summary>双始端桥接第二阶段至少允许保留的普通路径候选数量。</summary>
        public int DoubleStartBridgeMinimumCandidateCount { get; set; } = 8;

        /// <summary>双始端桥接第二阶段最多允许保留的普通路径候选数量。</summary>
        public int DoubleStartBridgeMaximumCandidateCount { get; set; } = 32;

        /// <summary>双始端桥接内部搭接坐标最多保留的离散候选数量。</summary>
        public int DoubleStartBridgeMaximumInternalLapCandidateCount { get; set; } = 8;

        /// <summary>轴坐标递归生成单条序列时允许访问的搜索节点上限。</summary>
        public int MaximumSequenceSearchNodeCount { get; set; } = 8192;

        /// <summary>始端水平搭接和笛卡尔极限优化的二分边界细化轮数。</summary>
        public int MaximumCartesianBoundaryRefinementCount { get; set; } = 4;

        /// <summary>一次笛卡尔极限优化最多尝试的独立线段移动数量。</summary>
        public int MaximumCartesianOptimizationMotionCount { get; set; } = 3;

        /// <summary>排长恰好重叠一个排宽时，专用整路径 N/M 转换最多保留的候选数。</summary>
        public int MaximumRestrictedWholePathCandidateCount { get; set; } = 4;

        public LinkageRouteSearchOptions Copy()
        {
            return (LinkageRouteSearchOptions)MemberwiseClone();
        }

        public void Validate()
        {
            if (FirstSolutionTargetMilliseconds <= 0 ||
                TotalSearchLimitMilliseconds <= 0 ||
                MaximumCandidatesPerGroup <= 0 ||
                MaximumClearanceRepairCandidatesPerState <= 0 ||
                MaximumSolutionsPerAlgorithm <= 0 ||
                BeamWidth <= 0 ||
                MaximumDegreeOfParallelism <= 0 ||
                MinimumAdaptiveRepairStep <= 0d ||
                FastSearchStageLimitMilliseconds <= 0 ||
                ExtendedFastSearchStageLimitMilliseconds <= 0 ||
                DeepTraversalTimeSliceMilliseconds <= 0 ||
                ConstraintDirectedDeepRepairLimitMilliseconds <= 0 ||
                ConstraintDirectedRootCandidateCount <= 0 ||
                SolutionAllocationSwitchSolutionCount <= 0 ||
                PreferredSolutionOptimizationCount <= 0 ||
                SolutionOptimizationStagnationMilliseconds <= 0 ||
                StagnationMainSearchWorkerCount <= 0 ||
                MaximumSolutionOptimizationDurationMilliseconds <= 0 ||
                MaximumInitialSolutionOptimizationWorkerCount <= 0 ||
                ClearanceOptimizationLimitMilliseconds <= 0 ||
                ClearanceOptimizationTraversalLimitMilliseconds <= 0 ||
                MaximumClearanceOptimizationCandidateCount <= 0 ||
                MaximumClearanceOptimizationPassCount <= 0 ||
                ClearanceOptimizationStep <= 0d ||
                ClearanceOptimizationCatalogLimitMilliseconds <= 0 ||
                SupplementalClearanceStartDelayMilliseconds < 0 ||
                RelaxedClearancePublicationMilliseconds <= 0 ||
                SupplementalClearanceAttemptLimitMilliseconds <= 0 ||
                SupplementalClearanceStep <= 0d ||
                SupplementalClearanceIncreaseDelayMilliseconds < 0 ||
                SupplementalClearanceMaximumIncrease < 0d ||
                SupplementalClearanceMaximumDegreeOfParallelism <= 0 ||
                SupplementalLegacyPlacementMaximumDegreeOfParallelism <= 0 ||
                SupplementalLegacyPlacementMaximumSolutions <= 0 ||
                FastLayerMaximumEvaluatedSkeletonCount <= 0 ||
                FastLayerMaximumSeedCandidateCount <= 0 ||
                FastLayerMaximumSeedCandidateCountPerSegmentLayer <= 0 ||
                FastLayerMaximumVisitedSkeletonCount <= 0 ||
                FastLayerMaximumEvaluatedSkeletonCountPerTopology <= 0 ||
                ExtendedLayerMaximumEvaluatedSkeletonCount <= 0 ||
                ExtendedLayerMaximumSeedCandidateCount <= 0 ||
                ExtendedLayerMaximumSeedCandidateCountPerSegmentLayer <= 0 ||
                ExtendedLayerMaximumVisitedSkeletonCount <= 0 ||
                ExtendedLayerMaximumEvaluatedSkeletonCountPerTopology <= 0 ||
                FastLayerMaximumSegmentCount < 2 ||
                FastLayerMaximumSegmentCount > 8 ||
                ExtendedLayerMaximumSegmentCount <
                    FastLayerMaximumSegmentCount ||
                ExtendedLayerMaximumSegmentCount > 8 ||
                MaximumSeedSkeletonsPerSegmentLayer <= 0 ||
                MaximumPairRepairCandidateCount <= 0 ||
                DoubleStartBridgeMinimumCandidateCount <= 0 ||
                DoubleStartBridgeMaximumCandidateCount <
                    DoubleStartBridgeMinimumCandidateCount ||
                DoubleStartBridgeMaximumInternalLapCandidateCount <= 0 ||
                MaximumSequenceSearchNodeCount <= 0 ||
                MaximumCartesianBoundaryRefinementCount <= 0 ||
                MaximumCartesianOptimizationMotionCount <= 0 ||
                MaximumRestrictedWholePathCandidateCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(LinkageRouteSearchOptions),
                    "算法通用规则中的时限、数量、步长和并行度不符合有效范围。" );
            }

            if (SchedulingAndPublishingWorkerReserve < 0 ||
                FastSolutionOptimizationWorkerReserve < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SchedulingAndPublishingWorkerReserve),
                    "调度发布线程和快速优化线程的保留数量不能为负数。" );
            }

            if (PostSwitchMainSearchWorkerRatio <= 0d ||
                PostSwitchMainSearchWorkerRatio >= 1d ||
                PostSwitchSolutionOptimizationWorkerRatio <= 0d ||
                PostSwitchSolutionOptimizationWorkerRatio >= 1d ||
                Math.Abs(
                    PostSwitchMainSearchWorkerRatio +
                    PostSwitchSolutionOptimizationWorkerRatio - 1d) > 1e-8d ||
                InitialSolutionOptimizationWorkerRatio <= 0d ||
                InitialSolutionOptimizationWorkerRatio > 1d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(PostSwitchMainSearchWorkerRatio),
                    "主搜索与方案优化比例必须位于 0 到 1 之间且两者之和为 1；初始优化比例必须位于 0 到 1 之间。" );
            }

            if (MinimumPublishableClearance < 5d ||
                SupplementalClearanceSeed < 5d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MinimumPublishableClearance),
                    "可发布绝缘下限和补充搜索种子绝缘距离都不得小于 5 mm。" );
            }

            if (RelaxedClearancePublicationMilliseconds <
                SupplementalClearanceStartDelayMilliseconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RelaxedClearancePublicationMilliseconds),
                    "绝缘不足候选发布时间不能早于补充种子搜索启动时间。" );
            }
        }
    }
}
