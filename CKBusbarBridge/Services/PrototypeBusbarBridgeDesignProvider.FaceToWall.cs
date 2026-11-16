using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.DesignGeometry;
using CKBusbarBridge.Services;
using CKBusbarBridgePreview;
using CKBusbarBridge.Models;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Prototype
{
    public sealed partial class PrototypeBusbarBridgeDesignProvider
    {
        internal BridgeDesignSample CreateFaceToWallPreview(BridgeUpstreamSnapshot upstream,
            LinkageRouteWorkerRequestDto request, LinkageRouteWorkerSolutionDto solution)
            => CreateFaceToWallPreview(upstream, request, solution, null);

        internal BridgeDesignSample CreateFaceToWallPreview(BridgeUpstreamSnapshot upstream,
            LinkageRouteWorkerRequestDto request, LinkageRouteWorkerSolutionDto solution, FaceToWallPlanningInput planning)
        {
            var input = upstream.FaceToWall ?? throw new InvalidOperationException("面对墙固定接口快照缺失。");
            if (!new FaceToWallWorkerSolutions().HasStraightWallLap(request, solution))
                throw new InvalidOperationException("面对墙候选不符合墙端同向直头夹接规则。");
            if (!BusbarWorkerSolutionBusbarExtractor.TryExtractGenerationGroups(request, solution, true, out var groups, out var error))
                throw new InvalidOperationException(error);
            if (planning != null) groups = planning.Compose(groups);
            // 首搭接及普通桥头已在Worker请求之前确定，不能再平移端点挤占既有折弯直段。
            var draft = new FaceToWallCopperDraft(groups, request, solution);
            if (planning?.RetainedDraft != null) draft.WithReplannedJointIds(planning.RetainedDraft);
            draft.SpecialStartX = planning?.SpecialStartX;
            return CreateFaceToWallDraft(upstream, request, solution, draft, null, null,
                planning?.IsPhaseSwitched ?? input.IsPhaseSwitched, planning?.MinimumSpecialSpan,
                new BridgeWallPlanningCodec.WallPlanningData { RequestedSpecialSpan = planning?.RequestedSpecialSpan,
                    AbsorbedOrdinaryCount = planning?.AbsorbedOrdinaryCount ?? 0 },
                adjustRisingFrameSeam: planning?.AdjustRisingFrameSeam == true);
        }

        private BridgeDesignSample CreateFaceToWallDraft(BridgeUpstreamSnapshot upstream, LinkageRouteWorkerRequestDto request,
            LinkageRouteWorkerSolutionDto solution, FaceToWallCopperDraft draft, IReadOnlyList<BridgeDesignObject> edits,
            IReadOnlyList<BridgeDesignObject> retainedSupports, bool isPhaseSwitched, double? minimumSpecialSpan = null,
            BridgeWallPlanningCodec.WallPlanningData metadata = null, bool adjustRisingFrameSeam = false)
        {
            var input = upstream.FaceToWall;
            if (edits != null)
            {
                var repartitioned = draft.Repartition(edits);
                if (!ReferenceEquals(repartitioned, draft)) { draft = repartitioned; edits = null; }
            }
            var groups = draft.Apply(edits);
            var bendInterference = new BridgeRaisedLapGeometry().FindRoundedMemberInterference(
                groups.SelectMany(group => group.Busbars).ToArray(), bar => FaceToWallCopperDraft.Phase(bar.GroupKey));
            if (bendInterference != null) throw new InvalidOperationException(bendInterference);
            var objects = input.FixedObjects.ToList();
            var routes = new FaceToWallPreviewRouteFactory().Create(input, groups, objects, draft.CopperSectionIndex);
            var issues = new List<BridgeDesignIssue>();
            var fingerprint = metadata?.InputFingerprint ?? new BridgeWallPlanningCodec().InputFingerprint(input);
            if (metadata?.InputFingerprint != null && metadata.InputFingerprint != new BridgeWallPlanningCodec().InputFingerprint(input))
                issues.Add(new BridgeDesignIssue("保存方案的固定接口或关键参数已变化，请重新规划铜排。", null, "面对墙接口变化"));
            if (input.IsPhaseSwitched != isPhaseSwitched)
                issues.Add(new BridgeDesignIssue("面对墙“是否换相”参数已变化，请重新规划铜排后生成。", null, "面对墙换相"));
            var wallEndProblem = new FaceToWallWallEndRule().FindProblem(input, request, groups.SelectMany(group => group.Busbars));
            if (wallEndProblem != null) issues.Add(new BridgeDesignIssue(wallEndProblem, null, "墙端预留"));
            try
            {
                if (retainedSupports != null) objects.AddRange(retainedSupports);
                else AddFaceToWallSupports(input, groups, routes, objects, adjustRisingFrameSeam);
            }
            catch (InvalidOperationException ex) { FaceToWallPreviewTrace.Error("面对墙支撑适配失败", ex); issues.Add(new BridgeDesignIssue(ex.Message + "；真实铜排已保留。", null, "面对墙支撑适配")); }
            catch (ArgumentException ex) { FaceToWallPreviewTrace.Error("面对墙支撑尺寸不支持", ex); issues.Add(new BridgeDesignIssue(ex.Message + "；真实铜排已保留。", null, "面对墙支撑适配")); }
            var wallAcross = input.TargetWallCenterAcross;
            foreach (var frame in objects.Where(o => o.Kind == DesignObjectKind.Frame && o.FrameTemplateKey == "FaceToWallLarge1"))
                if (Math.Abs(frame.Y - wallAcross) > .01d)
                    issues.Add(new BridgeDesignIssue(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "按入口配合链定位的大基架横向中心与墙洞中心相差{0:+0.0;-0.0;0.0} mm，请核验墙端安装；未自动移动基架。",
                        frame.Y - wallAcross), frame.Id, "面对墙安装校验", false, false));
            if (solution.IsRelaxedClearanceSolution)
                issues.Add(new BridgeDesignIssue("Worker使用放宽绝缘距离的方案，尚未达到所需绝缘距离。", null, "Worker绝缘校验"));
            var source = new BridgeDesignSource("面对墙场景", upstream.ModelName + " · " + input.Position +
                (isPhaseSwitched ? " · 换相（两侧端位交换）" : " · 不换相"),
                request.DefaultBusbarWidth, request.DefaultBusbarThickness, "固定接口反读 + 普通段 + Worker特殊段 · 墙端预留150mm", true);
            var editRoutes = new List<BridgeDesignRoute>();
            draft.AddControls(objects, editRoutes, edits);
            var sample = new BridgeDesignSample("face-to-wall-model", source, objects, routes, issues,
                new BridgeDesignParameters(input.OverlapLength, request.InsulationClearance)).WithCoordinates(input.Coordinates, "FaceToWall");
            if (retainedSupports == null)
            {
                var connections = new BusbarBridgeFrameJointClearance().Plan(sample);
                var placement = new BusbarBridgeInsulatorClearance().PlaceIndependently(sample, connections.Objects, null);
                sample = new BridgeDesignSample(sample.Id, source, AssignInitialFrameSources(placement.Objects), routes,
                    issues.Concat(connections.Issues).Concat(placement.Issues), sample.Parameters)
                    .WithCoordinates(input.Coordinates, "FaceToWall");
            }
            var supports = sample.Objects.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator).ToArray();
            var state = new BridgeWallPlanningCodec.WallPlanningData { LengthRuleVersion = 1,
                RequestedSpecialSpan = metadata?.RequestedSpecialSpan, AbsorbedOrdinaryCount = metadata?.AbsorbedOrdinaryCount ?? 0,
                InputFingerprint = fingerprint, PhaseSwitchRuleVersion = 1, IsPhaseSwitched = isPhaseSwitched,
                WallEndRuleVersion = 1, WallEndClearanceMm = FaceToWallWallEndRule.ClearanceMm,
                Request = request, Solution = solution, Groups = draft.CopyBaseline().ToArray(),
                SpecialStartX = draft.SpecialStartX, JointIds = draft.JointIds, MinimumSpecialSpan = minimumSpecialSpan,
                MinimumSpanScope = minimumSpecialSpan.HasValue ? FaceToWallMinimumSpan.Scope : null };
            if (metadata != null && ReferenceEquals(request, metadata.Request) && ReferenceEquals(solution, metadata.Solution))
            { state.RuntimeCandidates = metadata.RuntimeCandidates; state.RuntimeSelectedIndex = metadata.RuntimeSelectedIndex; }
            sample = sample.WithCopperEditing(editRoutes, current => CreateFaceToWallDraft(upstream, request, solution, draft, current, supports, isPhaseSwitched, minimumSpecialSpan, state))
                .WithBaseJointConstraints(draft.BaseJointConstraints(input.FixedObjects, input.OverlapLength, input.BaseInstallationSeamX))
                .WithWallPlanningState(state);
            // 最小长度已取十位并反推分界，后续普通排整数优化不得再移动这个固定接口。
            return retainedSupports == null && !minimumSpecialSpan.HasValue ? FinishFrameJointPlanning(sample) : sample;
        }

        internal BridgeDesignSample RestoreFaceToWallPlanning(BridgeUpstreamSnapshot upstream, string planningRecipe,
            IReadOnlyList<BridgeDesignObject> savedSupports = null)
        {
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(planningRecipe);
            if (!new FaceToWallWorkerSolutions().HasStraightWallLap(stored.Request, stored.Solution))
                throw new InvalidOperationException("保存的面对墙方案墙端搭接无效。");
            var input = upstream.FaceToWall;
            if (input.WallInstallation?.IsLegacyRead == true && savedSupports != null)
                savedSupports = savedSupports.Select(node =>
                {
                    if (node.FrameTemplateKey != "FaceToWallLarge1" || node.Values.ContainsKey(DesignField.WallTopClearance)) return node;
                    var values = node.Values.ToDictionary(p => p.Key, p => p.Value);
                    ApplyInstallationValues(values, input.WallInstallation);
                    return node.WithValues(values);
                }).ToArray();
            upstream = upstream.WithFaceToWall(new FaceToWallUpstreamGeometry(input.Coordinates, input.Cabinet,
                input.WallConductors, input.FixedObjects, input.WallOpeningCorners, input.Position,
                input.PreviewReferenceComponentName, input.RootCount, stored.Request.InsulationClearance,
                input.OverlapLength, input.WallCabinetEccentricity, input.ModelClearance, input.AppliedWallVerticalClearance,
                input.WallInstallation, input.IsPhaseSwitched));
            return CreateFaceToWallDraft(upstream, stored.Request, stored.Solution,
                new FaceToWallCopperDraft(stored.Groups, stored.Request, stored.Solution) { SpecialStartX = stored.SpecialStartX }
                    .WithJointIds(stored.JointIds), null, savedSupports, stored.IsPhaseSwitched, stored.MinimumSpecialSpan, stored);
        }

        internal FaceToWallPlanningInput CaptureWallReplacementInput(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> objects)
        {
            var stored = sample.WallPlanningState;
            if (!stored.SpecialStartX.HasValue) throw new InvalidOperationException("当前方案缺少特殊段固定入口。");
            var draft = new FaceToWallCopperDraft(stored.Groups, stored.Request, stored.Solution)
                { SpecialStartX = stored.SpecialStartX }.WithJointIds(stored.JointIds).Snapshot(objects);
            var prefix = draft.CopyBaseline().TakeWhile(group => group.DisplayName != "算法中间段" && group.DisplayName != "特殊段入口桥头").ToArray();
            if (prefix.Length == 0) throw new InvalidOperationException("当前方案缺少可保留的普通铜排。");
            return new FaceToWallPlanningInput(stored.Request, prefix, stored.SpecialStartX.Value, stored.IsPhaseSwitched)
                { MinimumSpecialSpan = stored.MinimumSpecialSpan, RequestedSpecialSpan = stored.RequestedSpecialSpan,
                    AbsorbedOrdinaryCount = stored.AbsorbedOrdinaryCount, RetainedDraft = draft };
        }

        internal FaceToWallPlanningInput CreateWallClearanceInput(BridgeUpstreamSnapshot upstream,
            BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, double clearance,
            BusbarAlgorithmWorkerRequestOptions options)
        {
            var current = CaptureWallReplacementInput(sample, objects);
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(sample.WallPlanningRecipe);
            var draft = new FaceToWallCopperDraft(stored.Groups, stored.Request, stored.Solution)
                { SpecialStartX = stored.SpecialStartX }.WithJointIds(stored.JointIds).Snapshot(objects);
            var breakpoints = draft.OrdinaryBreakpoints(upstream.FaceToWall.Cabinet.Opening.X);
            if (current.DirectBaseLap)
            {
                var planner = new FaceToWallPreviewPlanning();
                var seed = planner.CreateInputWithSupportHeight(upstream.FaceToWall, null, null, options, null,
                    clearance - upstream.FaceToWall.AppliedWallVerticalClearance, clearance);
                var cabinet = seed.PrefixGroups.Take(1).ToArray();
                foreach (var bar in cabinet[0].Busbars) bar.PointList.Last().X = current.BaseJointRearX.Value;
                return planner.CreateInputWithSupportHeight(upstream.FaceToWall, null, current.SpecialStartX, options, cabinet,
                    clearance - upstream.FaceToWall.AppliedWallVerticalClearance, clearance, current.BaseJointRearX);
            }
            return new FaceToWallPreviewPlanning().CreateInputWithSupportHeight(upstream.FaceToWall,
                breakpoints, current.SpecialStartX, options, null,
                clearance - upstream.FaceToWall.AppliedWallVerticalClearance, clearance, current.BaseJointRearX);
        }

        internal BridgeDesignSample CreateWallClearancePreview(BridgeUpstreamSnapshot upstream,
            BridgeDesignSample current, IReadOnlyList<BridgeDesignObject> objects,
            FaceToWallPlanningInput planning, LinkageRouteWorkerSolutionDto solution)
        {
            if (planning.IsPhaseSwitched != upstream.FaceToWall.IsPhaseSwitched)
                throw new InvalidOperationException("候选方案与当前“是否换相”参数不一致，请重新规划铜排。");
            if (solution.IsRelaxedClearanceSolution || !new FaceToWallWorkerSolutions().HasStraightWallLap(planning.Request, solution))
                throw new InvalidOperationException("上下墙距候选不满足严格绝缘或墙端夹接，原草稿保留。");
            if (!BusbarWorkerSolutionBusbarExtractor.TryExtractGenerationGroups(planning.Request, solution, true, out var groups, out var error))
                throw new InvalidOperationException(error);
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(current.WallPlanningRecipe);
            var draft = new FaceToWallCopperDraft(stored.Groups, stored.Request, stored.Solution)
                { SpecialStartX = stored.SpecialStartX }.WithJointIds(stored.JointIds).Snapshot(objects)
                .ReplanForSupportHeight(planning.Compose(groups), planning.Request, solution);
            return CreateFaceToWallDraft(upstream, planning.Request, solution, draft, null,
                current.Objects.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator).ToArray(), planning.IsPhaseSwitched,
                metadata: stored)
                .WithSavedManualInsulators(current.SavedManualInsulatorIds)
                .WithReadbackBaseline(current.ReadbackBaseline)
                .WithReadbackBaselineWarning(current.ReadbackBaselineWarning);
        }

        internal bool WallSpecialMatchesStoredGeometry(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(sample.WallPlanningRecipe);
            var draft = new FaceToWallCopperDraft(stored.Groups, stored.Request, stored.Solution).WithJointIds(stored.JointIds);
            var actual = draft.Snapshot(objects).CopyBaseline().Single(group => group.DisplayName == "算法中间段").Busbars;
            var baseline = stored.Groups.Single(group => group.DisplayName == "算法中间段").Busbars;
            return Newtonsoft.Json.JsonConvert.SerializeObject(actual, BridgeWallPlanningCodec.CreateSettings()) ==
                Newtonsoft.Json.JsonConvert.SerializeObject(baseline, BridgeWallPlanningCodec.CreateSettings());
        }

        internal BridgeDesignSample ReplaceWallSpecialScheme(BridgeUpstreamSnapshot upstream, BridgeDesignSample current,
            IReadOnlyList<BridgeDesignObject> objects, FaceToWallPlanningInput planning, LinkageRouteWorkerSolutionDto solution)
        {
            if (planning.IsPhaseSwitched != upstream.FaceToWall.IsPhaseSwitched)
                throw new InvalidOperationException("候选方案与当前“是否换相”参数不一致，请重新规划铜排。");
            if (!new FaceToWallWorkerSolutions().HasStraightWallLap(planning.Request, solution) || solution.IsRelaxedClearanceSolution)
                throw new InvalidOperationException("候选方案不满足墙端夹接或严格绝缘要求，当前草稿保留。");
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(current.WallPlanningRecipe);
            if (!stored.SpecialStartX.HasValue || Math.Abs(stored.SpecialStartX.Value - planning.SpecialStartX) > .01)
                throw new InvalidOperationException("候选方案改变了特殊段入口，不能局部替换。");
            if (!BusbarWorkerSolutionBusbarExtractor.TryExtractGenerationGroups(planning.Request, solution, true, out var groups, out var error))
                throw new InvalidOperationException(error);
            var draft = new FaceToWallCopperDraft(stored.Groups, stored.Request, stored.Solution)
                { SpecialStartX = stored.SpecialStartX }.WithJointIds(stored.JointIds).Snapshot(objects);
            var next = draft.ReplaceSpecial(planning.Compose(groups), planning.Request, solution);
            // 支撑使用原草稿基线，编辑器继续保留覆盖、增删、同源、锁定和人工安装状态。
            return CreateFaceToWallDraft(upstream, planning.Request, solution, next, null,
                current.Objects.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator).ToArray(), planning.IsPhaseSwitched,
                planning.MinimumSpecialSpan, stored)
                .WithSavedManualInsulators(current.SavedManualInsulatorIds)
                .WithReadbackBaseline(current.ReadbackBaseline)
                .WithReadbackBaselineWarning(current.ReadbackBaselineWarning);
        }

        internal BridgeDesignSample RestoreFaceToWallCabinetTransition(BridgeUpstreamSnapshot upstream, BridgeDesignSample actual)
        {
            var target = AdaptFaceToWallCabinetFrames(upstream, actual, actual, false);
            return target.WithReadbackBaseline(CabinetTransitionBaseline(actual.ReadbackBaseline, true))
                .WithHangers(actual.SavedHangers).WithHangerRoof(actual.HangerRoof, actual.HangerRoofProblem)
                .WithReadbackBaselineWarning("柜端接口已变化；基架目标已按当前接口更新。请重新规划铜排，核验027/026安装链后生成。");
        }

        internal BridgeDesignSample CompleteFaceToWallCabinetReplan(BridgeUpstreamSnapshot upstream,
            BridgeDesignSample previous, BridgeDesignSample planned)
        {
            return AdaptFaceToWallCabinetFrames(upstream, previous, planned, true)
                .WithReadbackBaseline(CabinetTransitionBaseline(previous.ReadbackBaseline, false))
                .WithHangers(previous.SavedHangers).WithHangerRoof(previous.HangerRoof, previous.HangerRoofProblem)
                .WithReadbackBaselineWarning(null);
        }

        private BridgeReadbackBaseline CabinetTransitionBaseline(BridgeReadbackBaseline actual, bool requiresReplan)
        {
            if (actual == null) throw new InvalidOperationException("柜体同步缺少真实桥体反读基线。");
            return new BridgeReadbackBaseline { Signature = actual.Signature, Recipe = actual.Recipe,
                Material = actual.Material, CopperCount = actual.CopperCount, SuppressedInstances = actual.SuppressedInstances,
                RequiresCabinetSynchronization = true, RequiresWallCopperReplan = requiresReplan,
                RequiresBridgeRepair = actual.RequiresBridgeRepair, RepairPlanned = actual.RepairPlanned,
                RepairStart = actual.RepairStart, RepairEnd = actual.RepairEnd, RepairProblems = actual.RepairProblems,
                RepairUpstreamFingerprint = actual.RepairUpstreamFingerprint };
        }

        internal BridgeDesignSample AdaptFaceToWallCabinetFrames(BridgeUpstreamSnapshot upstream,
            BridgeDesignSample previous, BridgeDesignSample target, bool planSupports)
        {
            if (upstream.FaceToWall == null || target.ScenarioKey != "FaceToWall") return target;
            var input = upstream.FaceToWall;
            var planner = new FaceToWallPreviewFrameLayoutPlanner();
            var rising = planner.ValidateInput(input);
            var old = previous.Objects.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray();
            if (old.Length == 0) throw new InvalidOperationException("面对墙二次驱动缺少真实基架基线。");
            var wall = old.Single(o => o.FrameTemplateKey == "FaceToWallLarge1");
            var entry = wall.Value(DesignField.WallEntryDistance);
            var items = old.Select(o => new FaceToWallFrameLengthPlanItem(
                o.FrameTemplateKey == "Regular" ? FaceToWallFrameTemplateKind.Regular :
                o.FrameTemplateKey == "FaceToWallLarge2" ? FaceToWallFrameTemplateKind.Large2 :
                o.FrameTemplateKey == "FaceToWallLarge1" ? FaceToWallFrameTemplateKind.Large1 :
                throw new InvalidOperationException("面对墙原模板身份无效：" + o.Name),
                o.Length - (input.WallInstallation == null && o.Id == wall.Id ? entry : 0))).ToArray();
            var length = input.TargetWallEnd - input.Cabinet.Opening.X;
            var layout = Math.Abs(items.Sum(i => i.Length) - length) <= .2
                ? FaceToWallFrameLengthPlanner.ReconcileExisting(items, rising) : null;
            // 正式生成组把普通段和特殊段合为“中间排”；绝缘范围必须使用规划分件身份。
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(target.WallPlanningRecipe);
            var groups = new FaceToWallCopperDraft(stored.Groups, stored.Request, stored.Solution)
                .WithJointIds(stored.JointIds).Apply(target.Objects);
            var required = planSupports ? planner.Create(input, groups) : planner.Create(length, rising);
            if (layout == null || planSupports && layout.Last().Length < required.Last().Length - .01)
                layout = required;
            var objects = target.Objects.Where(o => o.Kind != DesignObjectKind.Frame && o.Kind != DesignObjectKind.Insulator).ToList();
            var cursor = input.Cabinet.Opening.X;
            for (var i = 0; i < layout.Count; i++)
            {
                // 026身份始终保留；只有确实新增027时分配新ID，不能占用原026的实例映射。
                var original = i == layout.Count - 1 ? wall : i < old.Length - 1 ? old[i] : null;
                var frame = CreateCabinetTransitionFrame(input, original, wall, layout[i], i + 1, cursor,
                    objects.Select(o => o.Id).Concat(old.Select(o => o.Id)));
                cursor += layout[i].Length;
                AddCabinetTransitionSupports(target, previous, frame, planSupports, objects);
            }
            // 原024若与前方024共享文件，027必须独立，不能把整个共享模型都替换掉。
            var used = new HashSet<string>(old.Select(o => o.SourceName), StringComparer.OrdinalIgnoreCase);
            var models = new List<BridgeDesignObject>();
            foreach (var frame in objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray())
            {
                var same = models.FirstOrDefault(o => o.SharesFrameSourceWith(frame));
                if (same != null && !SameFrameModel(same, frame, objects))
                {
                    var index = 1; string name;
                    do { name = "基架" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture); } while (!used.Add(name));
                    var independent = frame.WithSourceName(name);
                    objects[objects.IndexOf(frame)] = independent; models.Add(independent);
                }
                else { used.Add(frame.SourceName); models.Add(frame); }
            }
            var result = target.WithGeometry(objects, target.Routes);
            planner.ValidateFrames(input, result.Objects.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray());
            var supports = objects.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator).ToArray();
            var baseline = previous.ReadbackBaseline == null ? null : CabinetTransitionBaseline(previous.ReadbackBaseline, !planSupports);
            var warning = planSupports ? null : "柜端接口已变化；基架目标已按当前接口更新。请重新规划铜排，核验027/026安装链后生成。";
            // 后续人工断点编辑沿用适配后的实例/模板和实物基线，不能回到Worker初始分段。
            result = result.WithCopperEditing(target.CopperEditRoutes, edits =>
                CreateFaceToWallDraft(upstream, stored.Request, stored.Solution,
                    new FaceToWallCopperDraft(stored.Groups, stored.Request, stored.Solution) {
                        SpecialStartX = stored.SpecialStartX }.WithJointIds(stored.JointIds), edits, supports, stored.IsPhaseSwitched)
                    .WithReadbackBaseline(baseline).WithReadbackBaselineWarning(warning));
            return result;
        }

        private BridgeDesignObject CreateCabinetTransitionFrame(FaceToWallUpstreamGeometry input,
            BridgeDesignObject original, BridgeDesignObject wall, FaceToWallFrameLengthPlanItem item,
            int position, double cursor, IEnumerable<string> occupiedIds)
        {
            var large1 = item.TemplateKind == FaceToWallFrameTemplateKind.Large1;
            var regular = item.TemplateKind == FaceToWallFrameTemplateKind.Regular;
            var vertical = wall.Value(DesignField.WallVerticalClearance);
            if (input.WallInstallation != null) vertical = 0;
            if (Math.Abs(vertical - input.AppliedWallVerticalClearance) > .01)
                throw new InvalidOperationException("上下墙距与已保存基座补偿不一致，请重读后规划。");
            var height = regular ? input.Cabinet.OpeningHeight : input.WallInstallation == null ? input.WallOpeningHeight - 2 * vertical : input.TargetWallHeight;
            var width = large1 ? input.TargetWallWidth - (input.WallInstallation == null ? 2 * wall.Value(DesignField.WallHorizontalClearance) : 0) : input.Cabinet.BaseWidth;
            var entry = large1 ? input.WallInstallation?.EntryDistance ?? wall.Value(DesignField.WallEntryDistance) : 0;
            var lengthEntry = input.WallInstallation == null ? entry : 0;
            var id = original?.Id ?? "wall-sync-frame-" + position;
            while (original == null && occupiedIds.Contains(id)) id += "-new";
            var values = new Dictionary<DesignField, double> {
                [DesignField.X] = cursor + (item.Length + lengthEntry) / 2, [DesignField.Length] = item.Length + lengthEntry,
                [DesignField.Y] = large1 ? new FaceToWallPreviewFrameLayoutPlanner().Large1CenterAcross(
                    input.Cabinet.Opening.Z, input.EffectiveWallEccentricity, width) : input.Cabinet.Opening.Z,
                [DesignField.Z] = regular ? input.Cabinet.Opening.Y : input.TargetWallCenterHeight,
                [DesignField.Width] = width, [DesignField.Height] = height,
                [DesignField.Direction] = 0, [DesignField.Installation] = regular && original?.IsReversed == true ? 1 : 0,
                [DesignField.InsulatorCount] = 0 };
            if (large1)
                foreach (var field in new[] { DesignField.WallEntryDistance, DesignField.WallVerticalClearance, DesignField.WallHorizontalClearance })
                    values[field] = wall.Value(field);
            if (large1 && input.WallInstallation != null) ApplyInstallationValues(values, input.WallInstallation);
            var key = regular ? "Regular" : large1 ? "FaceToWallLarge1" : "FaceToWallLarge2";
            return new BridgeDesignObject(id, original?.SourceName ?? "新增升高基架" + position,
                DesignObjectKind.Frame, values, new[] { DesignField.Length, DesignField.InsulatorCount },
                regular ? "面对墙常规024" : large1 ? "面对墙大基架1 026" : "面对墙大基架2 027")
                .WithFrameTemplate(key).WithFramePosition(position).WithComponentBinding(original?.ComponentBinding)
                .WithWallFramePreview(new BridgeWallFramePreview(input.Cabinet.BaseWidth,
                    large1 ? height : input.Cabinet.OpeningHeight, large1 ? input.EffectiveWallEccentricity : 0))
                .WithInsulatorStack(input.RootCount - 1, input.Cabinet.Extensions[0].Shape.BusBarThickness,
                    original?.InsulatorPhaseSpacing > 0 ? original.InsulatorPhaseSpacing :
                    (input.Cabinet.OrdinaryConnections.Max(c => c.Center.Z) - input.Cabinet.OrdinaryConnections.Min(c => c.Center.Z)) / 2,
                    original?.InsulatorAssemblyHeight > 0 ? original.InsulatorAssemblyHeight : input.Cabinet.InsulatorWithBracketHeight);
        }

        private void AddCabinetTransitionSupports(BridgeDesignSample target, BridgeDesignSample previous,
            BridgeDesignObject frame, bool planSupports, List<BridgeDesignObject> objects)
        {
            if (!planSupports)
            {
                var children = previous.Objects.Where(o => o.Kind == DesignObjectKind.Insulator && o.ParentId == frame.Id).ToArray();
                var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.InsulatorCount] = children.Length / 3;
                objects.Add(frame.WithValues(values)); objects.AddRange(children); return;
            }
            var support = new FaceToWallInternalSupportPlanner().Create(target, frame);
            if (support == null)
            {
                var empty = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                empty[DesignField.InsulatorCount] = 0;
                objects.Add(frame.WithValues(empty)); return;
            }
            frame = frame.WithInsulatorStack(frame.InsulatorPadCount, frame.InsulatorPadThickness,
                (support.PhaseCenters[2] - support.PhaseCenters[0]) / 2, support.Height);
            var next = frame.Values.ToDictionary(p => p.Key, p => p.Value); next[DesignField.InsulatorCount] = 1;
            frame = frame.WithValues(next); objects.Add(frame);
            for (var phase = 0; phase < 3; phase++)
            {
                var child = CreateInsulator(frame, phase + 1, support.PhaseCenters[phase] - frame.Y);
                var values = child.Values.ToDictionary(p => p.Key, p => p.Value); values[DesignField.X] = support.X - frame.X;
                objects.Add(child.WithValues(values));
            }
        }

        private void ApplyInstallationValues(Dictionary<DesignField, double> values, FaceToWallInstallationValues installation)
        {
            values[DesignField.WallTopClearance] = installation.Top;
            values[DesignField.WallBottomClearance] = installation.Bottom;
            values[DesignField.WallLeftClearance] = installation.Left;
            values[DesignField.WallRightClearance] = installation.Right;
            values[DesignField.WallEntryDistance] = installation.EntryDistance;
            values[DesignField.WallVerticalClearance] = 0;
            values[DesignField.WallHorizontalClearance] = 0;
        }

        private void AddFaceToWallSupports(FaceToWallUpstreamGeometry input,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> groups, IReadOnlyList<BridgeDesignRoute> routes,
            List<BridgeDesignObject> objects, bool adjustRisingFrameSeam)
        {
            var supportPlanner = new FaceToWallInternalSupportPlanner();
            var source = new BridgeDesignSource("面对墙", "真实支承面",
                input.Cabinet.Extensions[0].Shape.BusBarWidth, input.Cabinet.Extensions[0].Shape.BusBarThickness);
            var parameters = adjustRisingFrameSeam
                ? new BridgeDesignParameters(input.OverlapLength, input.Clearance)
                : new BridgeDesignParameters(source.BusbarWidth);
            var copperSample = new BridgeDesignSample("wall-support-input", source,
                objects, routes, Array.Empty<BridgeDesignIssue>(), parameters)
                .WithCoordinates(input.Coordinates, "FaceToWall");
            var cabinet = input.Cabinet;
            var wallCenterY = input.TargetWallCenterHeight;
            var wallCenterZ = input.WallOpeningCorners.Average(p => p.Z);
            var wallWidth = input.TargetWallWidth;
            var endX = input.WallOpeningCorners.Average(p => p.X);
            // 027实模入口Y=(H-侧封板高)/2，出口Y=H/2；两端共底面。
            // 已应用的上下墙距来自反读标记；使用实际补偿尺寸，不再次扣减基座。
            var layoutPlanner = new FaceToWallPreviewFrameLayoutPlanner();
            var layout = layoutPlanner.Create(input, groups, adjustRisingFrameSeam ? copperSample : null);
            var supports = new List<BridgeDesignObject>(); var cursor = cabinet.Opening.X;
            foreach (var item in layout)
            {
                var index = supports.Count(o => o.Kind == DesignObjectKind.Frame) + 1;
                var large1 = item.TemplateKind == FaceToWallFrameTemplateKind.Large1;
                var regular = item.TemplateKind == FaceToWallFrameTemplateKind.Regular;
                var width = large1 ? wallWidth : cabinet.BaseWidth;
                var height = regular ? cabinet.OpeningHeight : input.TargetWallHeight;
                var centerY = regular ? cabinet.Opening.Y : wallCenterY;
                var centerZ = large1
                    ? layoutPlanner.Large1CenterAcross(cabinet.Opening.Z, input.EffectiveWallEccentricity, width)
                    : cabinet.Opening.Z;
                var key = regular ? "Regular" : large1 ? "FaceToWallLarge1" : "FaceToWallLarge2";
                var id = "wall-frame-" + index;
                var values = new Dictionary<DesignField, double> { [DesignField.X] = cursor + item.Length / 2,
                    [DesignField.Y] = centerZ, [DesignField.Z] = centerY, [DesignField.Length] = item.Length,
                    [DesignField.Width] = width, [DesignField.Height] = height, [DesignField.InsulatorCount] = 1,
                    [DesignField.Direction] = 0, [DesignField.Installation] = 0 };
                if (large1) values[DesignField.WallVerticalClearance] = input.AppliedWallVerticalClearance;
                if (large1 && input.WallInstallation != null) ApplyInstallationValues(values, input.WallInstallation);
                var editable = regular
                    ? new[] { DesignField.Length, DesignField.InsulatorCount, DesignField.Installation }
                    : new[] { DesignField.Length, DesignField.InsulatorCount };
                var phaseCenters = cabinet.OrdinaryConnections.Select(c => c.Center.Z).OrderBy(z => z).ToArray();
                var spacing = phaseCenters[2] - phaseCenters[0];
                var thickness = cabinet.Extensions[0].Shape.BusBarThickness;
                var frame = new BridgeDesignObject(id, "基架" + index, DesignObjectKind.Frame, values, editable,
                    regular ? "面对墙常规024" : large1 ? "面对墙大基架1 026" : "面对墙大基架2 027")
                    .WithFrameTemplate(key).WithFramePosition(index)
                    .WithWallFramePreview(new BridgeWallFramePreview(cabinet.BaseWidth, large1 ? height : cabinet.OpeningHeight,
                        large1 ? input.EffectiveWallEccentricity : 0))
                    .WithInsulatorStack(input.RootCount - 1, thickness, spacing / 2, cabinet.InsulatorWithBracketHeight);
                var supportLayout = supportPlanner.Create(copperSample, frame);
                if (supportLayout != null)
                {
                    phaseCenters = supportLayout.PhaseCenters;
                    frame = frame.WithInsulatorStack(input.RootCount - 1, thickness,
                        (phaseCenters[2] - phaseCenters[0]) / 2, supportLayout.Height);
                }
                else
                {
                    var emptyValues = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                    emptyValues[DesignField.InsulatorCount] = 0;
                    frame = frame.WithValues(emptyValues);
                }
                // 调用旧纯值驱动计划验证模板、尺寸和最低段长，不进入文件/SW执行器。
                FaceToWallFrameDrivePlanner.CreatePlans(new[] { new FaceToWallFramePlanInput("基架" + index,
                    width, item.Length, height, false, item.TemplateKind, supportLayout == null ? 0 : 1,
                    supportLayout == null ? Array.Empty<double>() : new[] { supportLayout.X - cursor }) }, cabinet.BaseWidth,
                    input.WallCabinetEccentricity, cabinet.OpeningHeight);
                // 封板宽和分盖板尺寸也必须合法；完整成功后一次性并入草稿。
                new BusbarBridgeDraftPreviewSceneFactory().Solids3D(frame, new[] { frame });
                supports.Add(frame);
                for (var phase = 0; supportLayout != null && phase < 3; phase++)
                {
                    var child = CreateInsulator(frame, phase + 1, phaseCenters[phase] - centerZ);
                    var childValues = child.Values.ToDictionary(p => p.Key, p => p.Value);
                    childValues[DesignField.X] = supportLayout.X - frame.X;
                    supports.Add(child.WithValues(childValues));
                }
                cursor += item.Length;
            }
            new FaceToWallPreviewFrameLayoutPlanner().ValidateFrames(input,
                supports.Where(o => o.Kind == DesignObjectKind.Frame).ToArray());
            objects.AddRange(AssignInitialFrameSources(supports));
        }

    }

    internal sealed class FaceToWallSupportValidation
    {
        internal IReadOnlyList<BridgeDesignIssue> Validate(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (sample?.ScenarioKey != "FaceToWall") return Array.Empty<BridgeDesignIssue>();
            var issues = new List<BridgeDesignIssue>();
            var geometry = new BusbarBridgeDraftPreviewSceneFactory();
            foreach (var frame in objects.Where(o => o.Kind == DesignObjectKind.Frame))
            {
                var children = objects.Where(o => o.Kind == DesignObjectKind.Insulator && o.ParentId == frame.Id).ToArray();
                var solids = sample.Routes.SelectMany(r => r.PhysicalSolids).ToArray();
                var inside = solids.SelectMany(s => Clip(s, frame.X - frame.Length / 2, frame.X + frame.Length / 2)).ToArray();
                if (inside.Any(p => Math.Abs(p[1] - frame.Y) > frame.Width / 2 - 40 + .01 ||
                    p[2] < frame.Z - frame.Height / 2 + 40 - .01 || p[2] > frame.Z + frame.Height / 2 - 40 + .01))
                    issues.Add(new BridgeDesignIssue("Worker铜排超出当前基架角钢内通道，请调整基架分节；铜排几何保持不变。", frame.Id, "面对墙支撑校验"));
                var inlet = frame.WallFramePreview;
                if (inlet != null)
                {
                    // 两端范围并集允许Worker在高/宽通道搜索，但不能让铜排穿过027上封板或026侧封板。
                    // 从实际入口面向架内截取，不能把柜侧尚未进入基架的G排斜段计入内通道。
                    var start = frame.X - frame.Length / 2d;
                    var atInlet = solids.SelectMany(s => Clip(s, start, start + 1d)).ToArray();
                    var bottom = frame.Z - frame.Height / 2d;
                    if (atInlet.Any(p => Math.Abs(p[1] - frame.Y - inlet.InletOffset) > inlet.InletWidth / 2d - 40d + .01d ||
                        p[2] < bottom + 40d - .01d || p[2] > bottom + inlet.InletHeight - 40d + .01d))
                        issues.Add(new BridgeDesignIssue(FormattableString.Invariant($"{frame.Name}（{frame.FrameTemplateKey}）实际铜排穿过基架入口封板或角钢：入口X={start:0.###} mm，截面横向{atInlet.Min(p => p[1]):0.###}～{atInlet.Max(p => p[1]):0.###} mm、高度{atInlet.Min(p => p[2]):0.###}～{atInlet.Max(p => p[2]):0.###} mm；请调整升高/横移位置或基架分节。"),
                            frame.Id, "面对墙支撑校验"));
                }
                foreach (var child in children)
                {
                    var center = geometry.WorldPosition(child, objects);
                    var top = center[2] + frame.InsulatorAssemblyHeight / 2;
                    var supported = solids.Select(s => Clip(s, center[0], center[0]))
                        .Any(section => section.Count > 0 && section.Min(p => p[1]) <= center[1] + .01 &&
                            section.Max(p => p[1]) >= center[1] - .01 && Math.Abs(section.Min(p => p[2]) - top) <= .1);
                    if (!supported)
                        issues.Add(new BridgeDesignIssue("内部绝缘子与实际铜排支承面未闭合，请调整组位置；升高后的高度/相间距需适配。", child.Id, "面对墙支撑校验"));
                }
            }
            return issues.AsReadOnly();
        }

        // 截取真实加工实体的凸单元；不读取或回退到任何SolidWorks组件Box。
        private IReadOnlyList<double[]> Clip(double[][] vertices, double minimum, double maximum)
        {
            var result = vertices.Where(p => p[0] >= minimum - .001 && p[0] <= maximum + .001).ToList();
            foreach (var x in new[] { minimum, maximum })
                for (var i = 0; i < vertices.Length; i++)
                    for (var j = i + 1; j < vertices.Length; j++)
                    {
                        var a = vertices[i]; var b = vertices[j]; var delta = b[0] - a[0];
                        if (Math.Abs(delta) < .000001) continue;
                        var t = (x - a[0]) / delta;
                        if (t < 0 || t > 1) continue;
                        result.Add(new[] { x, a[1] + t * (b[1] - a[1]), a[2] + t * (b[2] - a[2]) });
                    }
            return result;
        }
    }
}
