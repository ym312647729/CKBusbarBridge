using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Services
{
    internal sealed class FaceToWallPreviewCandidate
    {
        internal LinkageRouteWorkerSolutionDto Solution { get; }
        internal BridgeDesignSample Sample { get; }
        internal FaceToWallPreviewCandidate(LinkageRouteWorkerSolutionDto solution, BridgeDesignSample sample)
        { Solution = solution; Sample = sample; }
    }

    internal sealed class FaceToWallWorkerSolutions
    {
        internal IReadOnlyList<FaceToWallPreviewCandidate> PlanSuccessful(LinkageRouteWorkerComparisonResultDto result,
            LinkageRouteWorkerRequestDto request, Func<LinkageRouteWorkerSolutionDto, BridgeDesignSample> plan)
        {
            var accepted = new List<FaceToWallPreviewCandidate>();
            var failures = new List<string>();
            foreach (var solution in ReadSuccessful(result, request))
            {
                try
                {
                    var sample = plan(solution);
                    if (sample == null) throw new InvalidOperationException("未返回完整预览草稿。");
                    var clearance = new BusbarBridgeFrameJointClearance();
                    var jointIssues = clearance.Validate(sample, sample.Objects);
                    if (jointIssues.Any(i => i.BlocksGeneration) && sample.ReplanCopper != null)
                    {
                        // 面对墙抬高时特殊基架位置固定，但铜排断点仍可能有可行窗口。
                        // 先让统一避让搜索联动调整断点和支撑，再决定该Worker候选是否失败。
                        try
                        {
                            var fixedFrames = new HashSet<string>(sample.Objects
                                .Where(o => o.Kind == DesignObjectKind.Frame &&
                                    o.FrameTemplateKey.StartsWith("FaceToWallLarge", StringComparison.Ordinal))
                                .Select(o => o.Id), StringComparer.Ordinal);
                            var fixedJoints = new HashSet<string>(sample.BaseJointConstraints
                                .Select(constraint => constraint.JointGroupId), StringComparer.Ordinal);
                            var repaired = new BusbarBridgeFrameJointClearance()
                                .PlanWithCopper(sample, fixedFrames, fixedJoints, null, retainSupports: false);
                            if (repaired != null) sample = repaired;
                        }
                        catch (InvalidOperationException)
                        {
                            // 保留原候选诊断；候选不可修复时由下方统一汇总。
                        }
                        jointIssues = clearance.Validate(sample, sample.Objects);
                    }
                    var issues = sample.Issues.Where(i => i.Source != "基架连接避让")
                        .Concat(jointIssues)
                        .Concat(new FaceToWallSupportValidation().Validate(sample, sample.Objects))
                        .Where(i => i.BlocksGeneration).Select(i => i.Message).Distinct().ToArray();
                    if (issues.Length > 0) throw new InvalidOperationException(string.Join("；", issues));
                    accepted.Add(new FaceToWallPreviewCandidate(solution, sample));
                }
                catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
                {
                    var context = "面对墙候选" + solution.SchemeIndex + "铜排及安装校验失败";
                    FaceToWallPreviewTrace.Error(context, error);
                    failures.Add(context + "：" + error.Message);
                }
            }
            if (accepted.Count == 0)
                throw new InvalidOperationException("面对墙候选均未通过铜排及安装校验：" + string.Join("；", failures));
            return accepted.AsReadOnly();
        }

        internal IReadOnlyList<LinkageRouteWorkerSolutionDto> ReadSuccessful(
            LinkageRouteWorkerComparisonResultDto result, LinkageRouteWorkerRequestDto request)
        {
            var successful = ReadSuccessful(result);
            var accepted = new List<LinkageRouteWorkerSolutionDto>();
            var failures = new List<string>();
            var wallEnd = new FaceToWallWallEndPlanner();
            foreach (var solution in successful)
            {
                if (solution.IsRelaxedClearanceSolution)
                {
                    failures.Add(FormattableString.Invariant($"候选{solution.SchemeIndex}：实际绝缘净距{solution.MinimumClearance:0.###}mm，未满足原要求{request.InsulationClearance:0.###}mm。"));
                    continue;
                }
                var planned = wallEnd.CreateStraightEnds(request, solution, out var problem);
                if (planned != null) accepted.Add(planned);
                else failures.Add("候选" + solution.SchemeIndex + "：" + problem);
            }
            if (accepted.Count == 0)
                throw new InvalidOperationException("Worker候选均未同时满足墙端同向直头夹接和原绝缘距离：" + string.Join("；", failures));
            return accepted.AsReadOnly();
        }

        internal bool HasStraightWallLap(LinkageRouteWorkerRequestDto request, LinkageRouteWorkerSolutionDto solution)
        {
            if (request?.Groups == null || request.Groups.Count != 3 || solution?.GroupSolutions == null) return false;
            foreach (var group in request.Groups)
            {
                var matches = solution.GroupSolutions.Where(g => g != null && g.GroupKey == group.GroupKey).ToArray();
                if (matches.Length != 1 || matches[0].StackMembers == null || matches[0].StackMembers.Count != group.StackCount)
                    return false;
                var terminal = group.EndTerminal;
                if (terminal?.Members == null || terminal.Members.Count == 0) return false;
                var length = Vector(terminal.LengthDirection).Normalized;
                var thickness = Vector(terminal.ThicknessDirection).Normalized;
                var fixedBars = terminal.Members.Select(m => m.FixedBusbar).ToArray();
                var width = Vector(fixedBars[0].WidthVector).Normalized;
                var near = fixedBars.Average(b => Project(b.PointList.Last(), length));
                var far = fixedBars.Min(b => Project(b.PointList.First(), length));
                var across = fixedBars.Average(b => Project(b.PointList.Last(), width));
                var levels = new List<double>();
                foreach (var member in matches[0].StackMembers)
                {
                    var bar = member?.PlanarParts?.LastOrDefault();
                    if (bar?.PointList == null || bar.PointList.Count < 2 || !bar.IsCenter) return false;
                    var end = bar.PointList.Last();
                    var previous = bar.PointList[bar.PointList.Count - 2];
                    var delta = Vector(end) - Vector(previous);
                    var lap = bar.EndLapWidth;
                    // 墙端只接受沿固定板长度轴的直头夹接，不能用横向交叉搭接替代。
                    if (delta.Norm < lap - .01d || lap <= 0d ||
                        fixedBars.Any(fixedBar => Math.Abs(fixedBar.EndLapWidth - lap) > .01d) ||
                        delta.Normalized.Dot(length) < .999999d ||
                        Math.Abs(Vector(bar.WidthVector).Normalized.Dot(width)) < .999999d ||
                        Math.Abs(bar.BusBarThickness - group.BusbarThickness) > .01d ||
                        Math.Abs(Project(end, length) - near - lap) > .01d ||
                        Project(end, length) > far + .01d || Math.Abs(Project(end, width) - across) > .01d)
                        return false;
                    levels.Add(Project(end, thickness));
                }
                if (group.StackCount == fixedBars.Length + 1)
                {
                    var fixedLevels = fixedBars.Select(b => Project(b.PointList.Last(), thickness)).OrderBy(v => v).ToArray();
                    levels.Sort();
                    for (var i = 0; i < levels.Count; i++)
                        if (Math.Abs(levels[i] - (fixedLevels[0] - group.BusbarThickness + i * 2d * group.BusbarThickness)) > .01d)
                            return false;
                }
            }
            return true;
        }

        private Vector3d Vector(LinkageRouteWorkerCoordinateDto value) => new Vector3d(value.X, value.Y, value.Z);
        private double Project(LinkageRouteWorkerCoordinateDto point, Vector3d direction) => Vector(point).Dot(direction);

        internal IReadOnlyList<LinkageRouteWorkerSolutionDto> ReadSuccessful(LinkageRouteWorkerComparisonResultDto result)
        {
            if (result == null) throw new InvalidOperationException("Worker没有返回求解结果。");
            if (result.IsCancelled) throw new OperationCanceledException("面对墙规划已取消。");
            var records = result.RankedSolutions ?? new List<LinkageRouteWorkerSolutionDto>();
            // 排名结果包含失败算法的诊断记录；单条失败不能否定其他算法的可行结果。
            var successful = records.Where(s => s != null && s.IsSuccess && !s.IsCancelled).ToArray();
            if (successful.Length > 0) return Array.AsReadOnly(successful);
            var reasons = records.Where(s => s != null).SelectMany(s => s.Diagnostics ?? new List<LinkageRouteWorkerFailureDiagnosticDto>())
                .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Message))
                .Select(d => (string.IsNullOrWhiteSpace(d.GroupKey) ? "" : d.GroupKey + "：") + d.Message.Trim())
                .Distinct(StringComparer.Ordinal).ToArray();
            throw new InvalidOperationException("Worker未返回可行方案。" + (reasons.Length > 0
                ? string.Join("；", reasons) : "请检查两端接口、孔口通道及绝缘距离。"));
        }
    }

    internal sealed class FaceToWallPlanningInput
    {
        internal LinkageRouteWorkerRequestDto Request { get; }
        internal IReadOnlyList<AlgorithmBusbarGenerationGroup> PrefixGroups { get; }
        internal double SpecialStartX { get; }
        internal bool IsPhaseSwitched { get; }
        internal double? BaseJointRearX { get; }
        internal bool RequiresMinimumSpan { get; set; }
        internal double? MinimumSpecialSpan { get; set; }
        internal double? RequestedSpecialSpan { get; set; }
        internal int AbsorbedOrdinaryCount { get; set; }
        internal bool AdjustRisingFrameSeam { get; set; }
        internal FaceToWallCopperDraft RetainedDraft { get; set; }
        internal bool DirectBaseLap => PrefixGroups.Count == 1 && PrefixGroups[0].DisplayName == "柜端连接段";
        internal FaceToWallPlanningInput(LinkageRouteWorkerRequestDto request,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> prefixGroups, double specialStartX, bool isPhaseSwitched = false)
        {
            Request = request; PrefixGroups = prefixGroups; SpecialStartX = specialStartX;
            IsPhaseSwitched = isPhaseSwitched;
            var cabinet = prefixGroups.SingleOrDefault(g => g.DisplayName == "柜端连接段");
            if (cabinet != null)
            {
                var positions = cabinet.Busbars.Select(b => b.PointList.Last().X).ToArray();
                if (positions.Length == 0 || positions.Any(x => double.IsNaN(x) || double.IsInfinity(x)) ||
                    positions.Max() - positions.Min() > .01d)
                    throw new InvalidOperationException("基座L排断点必须具有三相叠排共同的后截面。");
                BaseJointRearX = positions.Average();
            }
        }

        internal IReadOnlyList<AlgorithmBusbarGenerationGroup> Compose(IReadOnlyList<AlgorithmBusbarGenerationGroup> extracted)
        {
            var last = PrefixGroups.Last();
            // Worker成员按截面排序；按真实端点匹配既有普通排，不能沿用COM或生成列表顺序。
            var ordered = new List<PlanarBendBusbarSetting>();
            foreach (var group in Request.Groups)
                foreach (var member in group.StartTerminal.Members)
                {
                    var p = member.FixedBusbar.PointList[0];
                    var matches = last.Busbars.Where(b => b.GroupKey.StartsWith(group.Phase + "-", StringComparison.OrdinalIgnoreCase) &&
                        b.PointList.Last().DistanceTo(new Point3d(p.X, p.Y, p.Z)) <= .01d).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("普通排与Worker始端成员未唯一对应。");
                    ordered.Add(BridgeNativePreviewCopy.Copy(matches[0]));
                }
            var result = PrefixGroups.Take(PrefixGroups.Count - 1).Select(CopyGroup).ToList();
            result.Add(new AlgorithmBusbarGenerationGroup { DisplayName = last.DisplayName, IsBridge = last.IsBridge, Busbars = ordered.AsReadOnly() });
            result.AddRange(extracted.Where(g => g.DisplayName != "算法始端普通段").Select(CopyGroup));
            return result.AsReadOnly();
        }
        private AlgorithmBusbarGenerationGroup CopyGroup(AlgorithmBusbarGenerationGroup group) =>
            new AlgorithmBusbarGenerationGroup { DisplayName = group.DisplayName, IsBridge = group.IsBridge,
                RestrictSourceReuseToCurrentGroup = group.RestrictSourceReuseToCurrentGroup,
                Busbars = Array.AsReadOnly(group.Busbars.Select(b => BridgeNativePreviewCopy.Copy(b)).ToArray()) };
    }

    internal sealed partial class FaceToWallPreviewPlanning
    {
        internal double SpecialSpan(FaceToWallPlanningInput input, double overlap)
        {
            var spans = input.Request.Groups.Select(group => {
                var terminal = group.EndTerminal;
                var lap = terminal.Members[0].FixedBusbar.EndLapWidth;
                var end = terminal.RouteAnchor.X + terminal.LengthDirection.X * lap;
                return end - group.StartTerminal.RouteAnchor.X + overlap;
            }).ToArray();
            if (spans.Length != 3 || spans.Any(x => double.IsNaN(x) || double.IsInfinity(x) || x <= overlap) ||
                spans.Max() - spans.Min() > .01d)
                throw new InvalidOperationException("三组实际接口未形成共同的有效特殊段长度。");
            return spans.Average();
        }

        internal FaceToWallPlanningInput CreateInputForSpecialSpan(FaceToWallUpstreamGeometry upstream,
            FaceToWallPlanningInput previous, double span, BusbarAlgorithmWorkerRequestOptions options)
            => CreateSpanInput(upstream, previous, span, options);

        internal LinkageRouteWorkerRequestDto CreateRequest(FaceToWallUpstreamGeometry upstream) => CreateInput(upstream).Request;

        internal FaceToWallPlanningInput CreateInput(FaceToWallUpstreamGeometry upstream,
            IReadOnlyList<double> ordinaryBreakpoints = null, double? specialStartX = null)
            => CreateInputWithOptions(upstream, ordinaryBreakpoints, specialStartX, null);

        internal FaceToWallPlanningInput CreateInputWithOptions(FaceToWallUpstreamGeometry upstream,
            IReadOnlyList<double> ordinaryBreakpoints, double? specialStartX, BusbarAlgorithmWorkerRequestOptions workerOptions,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> retainedPrefix = null)
            => CreateInputWithSupportHeight(upstream, ordinaryBreakpoints, specialStartX, workerOptions, retainedPrefix, 0,
                upstream?.AppliedWallVerticalClearance ?? 0);

        internal FaceToWallPlanningInput CreateInputWithSupportHeight(FaceToWallUpstreamGeometry upstream,
            IReadOnlyList<double> ordinaryBreakpoints, double? specialStartX, BusbarAlgorithmWorkerRequestOptions workerOptions,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> retainedPrefix, double supportHeightOffset, double verticalClearance,
            double? baseJointRearX = null)
        {
            if (upstream == null) throw new InvalidOperationException("面对墙固定接口反读未完成。");
            if (double.IsNaN(supportHeightOffset) || double.IsInfinity(supportHeightOffset) ||
                double.IsNaN(verticalClearance) || double.IsInfinity(verticalClearance))
                throw new InvalidOperationException("铜排支承高度及上下墙距必须为有限数值。");
            new FaceToWallPreviewFrameLayoutPlanner().ValidateInput(upstream);
            var targetBaseJoint = ResolveBaseJointRearX(upstream, baseJointRearX, retainedPrefix);
            var source = upstream.Cabinet;
            var starts = new List<PlanarBendBusbarSetting>();
            var prefix = new List<AlgorithmBusbarGenerationGroup>();
            var corners = upstream.WallOpeningCorners;
            var inset = FaceToWallModelBusbarPlanner.CalculateAlgorithmContainmentInsetDistance(upstream.Clearance);
            var wallX = corners.Average(p => p.X);
            var widthMaximum = source.Extensions.Max(b => b.Shape.BusBarWidth);
            var automaticMinimum = retainedPrefix == null && ordinaryBreakpoints == null && !specialStartX.HasValue;
            // 这里只保留普通排固定接口所需的最短空间。Worker先提供完整布局，随后求其最小长度并反推最终分界。
            var searchOptions = workerOptions ?? BusbarAlgorithmWorkerRequestOptions.CreateDefault();
            var radius = Math.Max(10d, source.Extensions.Max(b => b.Shape.BusBarThickness));
            var minimumOrdinary = Math.Ceiling(Math.Max(searchOptions.MinimumSegmentLength,
                CabinetTopExtensionBusbarReader.MinimumBridgeHeadStraightTailLength(widthMaximum, radius) + .01d));
            var specialNearX = specialStartX ?? Math.Max(source.Opening.X + 2d * widthMaximum,
                Math.Max(targetBaseJoint - upstream.OverlapLength - widthMaximum + minimumOrdinary,
                    targetBaseJoint - upstream.OverlapLength + widthMaximum / 2d + Math.Max(.02d, searchOptions.MinimumSegmentLength)));
            var directBaseLap = retainedPrefix?.Count == 1 && retainedPrefix[0].DisplayName == "柜端连接段";
            if (double.IsNaN(specialNearX) || double.IsInfinity(specialNearX) ||
                !directBaseLap && specialNearX < source.Opening.X + 2d * widthMaximum)
                throw new InvalidOperationException("特殊段起点须为有限数值，并保留柜端普通段搭接长度。");
            if (specialNearX + 2d * widthMaximum >= wallX)
                throw new InvalidOperationException("面对墙可用长度不足以同时容纳普通段及特殊段搭接。");
            var breakpoints = ordinaryBreakpoints ?? new[] { 0d, specialNearX - source.Opening.X };
            if (breakpoints.Count < 2 || Math.Abs(breakpoints[0]) > .01d ||
                breakpoints.Zip(breakpoints.Skip(1), (a, b) => b > a + .01d).Any(valid => !valid) ||
                breakpoints.Take(breakpoints.Count - 1).Last() >= specialNearX - source.Opening.X - .01d)
                throw new InvalidOperationException("普通段断点须从柜端接口开始严格递增，并位于特殊区之前。");
            if (retainedPrefix == null)
            {
                var usedConnections = new HashSet<SideToSideConnectionGeometry>();
                for (var index = 0; index < 3; index++)
                {
                    var phase = (BusbarPhase_e)Enum.Parse(typeof(BusbarPhase_e), new[] { "A", "B", "C" }[index]);
                    var bars = source.Extensions.Where(b => b.Phase == phase).Select(ToBar).ToArray();
                    if (bars.Length == 0) throw new InvalidOperationException("柜端缺少" + phase + "相实际伸出排。");
                    var phaseZ = bars.Average(b => b.TerminalSegment.P2.Z);
                    var width = bars.Average(b => b.CenterlineSetting.BusBarWidth);
                    var connections = source.OrdinaryConnections.Where(c => Math.Abs(c.Center.Z - phaseZ) <= width / 2d + .01d).ToArray();
                    if (connections.Length != 1 || !usedConnections.Add(connections[0]))
                        throw new InvalidOperationException(phase + "相伸出排未唯一对应实际基座绝缘子配合面。");
                    var matched = connections[0];
                    var connection = new InsulatorConnectionInfo { ConnectionCircleCenter = matched.Center.Copy(), ComponentCenter = matched.SolidCenter.Copy(),
                        ConnectionPlane = new Plane3d(matched.Center.Copy(), matched.Normal.Copy()), ConnectionCircleRadius = matched.Radius };
                    var phaseGroups = CreatePreplannedPrefix(upstream, bars, connection, index, breakpoints,
                        specialNearX, wallX, supportHeightOffset, targetBaseJoint);
                    for (var g = 0; g < phaseGroups.Count; g++)
                    {
                        if (prefix.Count <= g) prefix.Add(new AlgorithmBusbarGenerationGroup { DisplayName = g == 0 ? "柜端连接段" : "普通段" + g,
                            IsBridge = g > 0, Busbars = new List<PlanarBendBusbarSetting>() });
                        var accumulated = prefix[g].Busbars.ToList(); accumulated.AddRange(phaseGroups[g].Select(b => BridgeNativePreviewCopy.Copy(b)));
                        prefix[g].Busbars = accumulated.AsReadOnly();
                    }
                    // 普通排只作为固定搭接边界，Worker仅求解特殊铜排的实际加工几何。
                    starts.AddRange(phaseGroups.Last().Select(ClipTerminal));
                }
            }
            else
            {
                if (retainedPrefix.Count == 0 || retainedPrefix.Last().Busbars.Count == 0)
                    throw new InvalidOperationException("当前普通排的特殊段连接边界缺失。");
                prefix = retainedPrefix.Select(group => new AlgorithmBusbarGenerationGroup
                {
                    DisplayName = group.DisplayName, IsBridge = group.IsBridge,
                    RestrictSourceReuseToCurrentGroup = group.RestrictSourceReuseToCurrentGroup,
                    Busbars = Array.AsReadOnly(group.Busbars.Select(BridgeNativePreviewCopy.Copy).ToArray())
                }).ToList();
                starts = prefix.Last().Busbars.Select(ClipTerminal).ToList();
            }
            if (automaticMinimum && prefix.Count == 2)
            {
                var origin = prefix.Last().Busbars[0].PointList.First().X;
                var actualNear = Math.Ceiling(prefix.Last().Busbars.Max(bar =>
                    LastStraightStart(bar).X + TerminalStraightReserve(bar)) - origin - .0000001d) + origin - widthMaximum;
                if (actualNear < specialNearX - .01d)
                {
                    var actual = CreateInputWithSupportHeight(upstream, null, actualNear, workerOptions, null,
                        supportHeightOffset, verticalClearance, targetBaseJoint);
                    actual.RequiresMinimumSpan = true;
                    return actual;
                }
            }
            // 搜索空间取已核验的两端孔口通道，保留原40mm结构和绝缘内缩。
            // 普通段始端已经对齐新支承面；特殊区保留实际孔口，折弯升高点由 Worker 求解。
            var minimum = new Point3d(specialNearX,
                Math.Min(source.Opening.Y - source.OpeningHeight / 2d + verticalClearance - upstream.AppliedWallVerticalClearance,
                    corners.Min(p => p.Y) + verticalClearance) + inset,
                Math.Min(-source.BaseWidth / 2d, corners.Min(p => p.Z)) + inset);
            var maximum = new Point3d(corners.Average(p => p.X),
                Math.Max(source.Opening.Y + source.OpeningHeight / 2d - verticalClearance + upstream.AppliedWallVerticalClearance,
                    corners.Max(p => p.Y) - verticalClearance) - inset,
                Math.Max(source.BaseWidth / 2d, corners.Max(p => p.Z)) - inset);
            var options = workerOptions ?? BusbarAlgorithmWorkerRequestOptions.CreateDefault();
            options.InsulationClearance = upstream.Clearance;
            options.SwapPhaseAAndCAtEnd = upstream.IsPhaseSwitched;
            options.EnableAutomaticTotalSearchLimit = true;
            if (!FaceToWallAlgorithmWorkerRequestFactory.TryCreateIndependentWorkerRequest(starts,
                upstream.WallConductors.Select(ToBar).ToArray(), minimum, maximum,
                BusbarBridgeAxisSetting.CreateDefault(), options, out var request, out var message))
                throw new InvalidOperationException(message);
            ValidateWallStack(request);
            return new FaceToWallPlanningInput(request, prefix.AsReadOnly(), specialNearX, upstream.IsPhaseSwitched)
                { RequiresMinimumSpan = automaticMinimum };
        }

        private double ResolveBaseJointRearX(FaceToWallUpstreamGeometry upstream, double? requested,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> retainedPrefix)
        {
            var owners = upstream.FixedObjects.Where(o => o.Id == "wall-base" && o.Kind == DesignObjectKind.Base).ToArray();
            if (owners.Length != 1)
                throw new InvalidOperationException("面对墙铜排规划缺少唯一的普通基座映射，不能确定固定接缝及L排断点。");
            if (!requested.HasValue && retainedPrefix != null)
            {
                var cabinet = retainedPrefix.SingleOrDefault(g => g.DisplayName == "柜端连接段");
                var positions = cabinet?.Busbars.Select(b => b.PointList.Last().X).ToArray();
                if (positions == null || positions.Length == 0 || positions.Max() - positions.Min() > .01d)
                    throw new InvalidOperationException("保留的普通铜排缺少三相共同的基座L排后截面。");
                requested = positions.Average();
            }
            var minimum = upstream.BaseInstallationSeamX + upstream.OverlapLength + BusbarBridgeFrameJointClearance.MinimumLapDistanceMm;
            var result = requested ?? minimum;
            if (double.IsNaN(result) || double.IsInfinity(result) || result < minimum - .000001d)
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"基座L排断点距基座边不得小于搭接长度+200mm（{upstream.OverlapLength + BusbarBridgeFrameJointClearance.MinimumLapDistanceMm:0.0} mm）；基座与基架接缝固定。"));
            return result;
        }

        private IReadOnlyList<IReadOnlyList<PlanarBendBusbarSetting>> CreatePreplannedPrefix(
            FaceToWallUpstreamGeometry upstream, IReadOnlyList<ModeledBusbar> bars, InsulatorConnectionInfo connection,
            int phaseIndex, IReadOnlyList<double> breakpoints, double specialNearX, double wallX,
            double supportHeightOffset, double baseJointRearX)
        {
            var positions = breakpoints.ToArray();
            var width = bars.Average(b => b.CenterlineSetting.BusBarWidth);
            // 旧几何入口使用半排宽延伸的中心基准；先定位搭接近边，再换算，避免重复加半搭接。
            positions[0] = baseJointRearX - upstream.OverlapLength + width / 2d - upstream.Cabinet.Opening.X;
            if (positions[0] >= positions[1] - .01d)
                throw new InvalidOperationException("基座L排断点与下一断点或特殊段入口之间空间不足，请重新规划铜排；固定接缝不能移动。");
            // 旧入口按双桥头分配六点参考路径；直尾的零高差端头不应占用一次50mm过渡及第二段保留。
            var extension = positions.Length == 2 ? Math.Max(0d,
                baseJointRearX - upstream.OverlapLength +
                Math.Ceiling(CabinetTopExtensionBusbarReader.MinimumBridgeHeadStraightTailLength(width,
                    Math.Max(10d, bars[0].CenterlineSetting.BusBarThickness)) + .01d) - specialNearX - width) : 0d;
            positions[positions.Length - 1] += extension;
            var groups = CabinetTopExtensionBusbarReader.CreateOrdinaryPrefix(bars, connection,
                upstream.Cabinet.Opening.X, upstream.Cabinet.CalculateConnectionHeight() + supportHeightOffset,
                phaseIndex, upstream.RootCount, positions, specialNearX + extension, wallX);
            foreach (var bar in groups[0])
            {
                bar.PointList.Last().X = baseJointRearX;
                bar.EndLapWidth = upstream.OverlapLength;
            }
            foreach (var bar in groups[1]) bar.StartLapWidth = upstream.OverlapLength;
            if (positions.Length == 2)
                foreach (var bar in groups[1])
                {
                    var bend = LastStraightStart(bar);
                    var end = bar.PointList.Last().Copy(); end.X -= extension;
                    var index = bar.PointList.FindIndex(point => point.DistanceTo(bend) <= .000001d);
                    if (end.X - bend.X < TerminalStraightReserve(bar) - .01d)
                        throw new InvalidOperationException(FormattableString.Invariant($"普通排桥头实际直尾{end.X - bend.X:0.######}mm不足以保留搭接和折弯余量{TerminalStraightReserve(bar):0.######}mm。"));
                    bar.PointList = bar.PointList.Take(index + 1).Concat(new[] { end }).ToList();
                }
            return groups;
        }

        private double TerminalStraightReserve(PlanarBendBusbarSetting bar)
            => Math.Max(bar.EndLapWidth, bar.BusBarWidth) + bar.BendAllowance + bar.BarBendingRadius;


        internal Point3d LastStraightStart(PlanarBendBusbarSetting bar)
        {
            var points = bar.PointList;
            var index = points.Count - 2;
            var direction = new Vector3d(points[index], points.Last()).Normalized;
            while (index > 0)
            {
                var previous = new Vector3d(points[index - 1], points[index]);
                if (previous.Norm > .000001d && previous.Normalized.Dot(direction) < .999999d) break;
                index--;
            }
            return points[index];
        }

        private PlanarBendBusbarSetting ClipTerminal(PlanarBendBusbarSetting source)
        {
            var end = source.PointList.Last(); var inner = LastStraightStart(source);
            var length = end.DistanceTo(inner);
            var required = TerminalStraightReserve(source);
            if (length < required - .01d) throw new InvalidOperationException("最后普通段直头不足以容纳特殊段搭接及折弯余量。");
            var firstDirection = new Vector3d(source.PointList[0], source.PointList[1]).Normalized;
            var lastDirection = new Vector3d(inner, end).Normalized;
            var sign = firstDirection.Cross(source.WidthVector).Dot(source.StartBusbarVector) < 0 ? -1d : 1d;
            var result = BridgeNativePreviewCopy.Copy(source);
            result.StartBusbarVector = lastDirection.Cross(source.WidthVector).Normalized * sign;
            result.PointList = new List<Point3d> { new Point3d(end.X - lastDirection.X * required,
                end.Y - lastDirection.Y * required, end.Z - lastDirection.Z * required), end.Copy() };
            return result;
        }

        private void ValidateWallStack(LinkageRouteWorkerRequestDto request)
        {
            foreach (var group in request.Groups)
            {
                var members = group.EndTerminal.Members;
                // 4排/3板是夹接，根数不能被墙端数量覆盖。
                if (group.StackCount != members.Count + 1) continue;
                var normal = group.EndTerminal.ThicknessDirection;
                var direction = new Vector3d(normal.X, normal.Y, normal.Z).Normalized;
                var levels = members.Select(m => m.FixedBusbar.PointList.Average(p => p.X * direction.X + p.Y * direction.Y + p.Z * direction.Z)).OrderBy(v => v).ToArray();
                if (members.Any(m => Math.Abs(m.FixedBusbar.BusBarThickness - group.BusbarThickness) > .01d) ||
                    levels.Zip(levels.Skip(1), (a, b) => Math.Abs(b - a - 2d * group.BusbarThickness) <= .01d).Any(valid => !valid))
                    throw new InvalidOperationException(group.Phase + "相墙端板厚或层间距不支持无垫块交错夹接。");
            }
        }

        private ModeledBusbar ToBar(SideToSideExtensionGeometry source) => new ModeledBusbar
        {
            Phase = source.Phase, CenterlineSetting = BridgeNativePreviewCopy.Copy(source.Shape),
            TerminalSegment = new Segment3d(source.TerminalStart.Copy(), source.TerminalEnd.Copy()),
            CenterPoint = new Point3d(source.Shape.PointList.Average(p => p.X), source.Shape.PointList.Average(p => p.Y), source.Shape.PointList.Average(p => p.Z))
        };
    }
}
