using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Services;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeCabinetPlanBuilder
    {
        private readonly Func<BridgeUpstreamSnapshot, FaceToWallPlanningInput, BridgeDesignSample> _wallPreview;
        private readonly Func<string> _authorization;

        internal BridgeCabinetPlanBuilder(Func<BridgeUpstreamSnapshot, FaceToWallPlanningInput, BridgeDesignSample> wallPreview = null, Func<string> authorization = null)
        { _wallPreview = wallPreview; _authorization = authorization; }

        internal BridgeCabinetGeometry Geometry(BridgeUpstreamSnapshot upstream)
        {
            double[] start, end;
            if (upstream.FaceToWall != null)
            {
                var wall = upstream.FaceToWall;
                start = new[] { wall.Cabinet.Opening.X, wall.Cabinet.Opening.Y, wall.Cabinet.Opening.Z };
                end = new[] { wall.WallOpeningCorners.Average(p => p.X), wall.WallOpeningCorners.Average(p => p.Y),
                    wall.WallOpeningCorners.Average(p => p.Z) };
            }
            else if (upstream.SideToSide != null)
            {
                var input = upstream.SideToSide.Copper;
                start = new[] { input.B.Opening.X, input.B.Opening.Y, input.B.Opening.Z };
                end = new[] { input.A.Opening.X, input.A.Opening.Y, input.A.Opening.Z };
            }
            else
            {
                Func<string, double[]> average = side =>
                {
                    var items = upstream.Endpoints.Where(e => e.Side == side).ToArray();
                    if (items.Length == 0) throw new InvalidOperationException("缺少" + side + "柜实际接口。");
                    return Enumerable.Range(0, 3).Select(i => items.Average(e => e.Interface[i])).ToArray();
                };
                start = average("B"); end = average("A");
            }
            var specification = string.Join("\n", upstream.Fields.Where(f => f.Name.Contains("规格") ||
                f.Name.Contains("根数") || f.Name.Contains("相序") || f.Name.Contains("材质") || f.Name.Contains("绝缘距离") ||
                f.Name.Contains("相间距") || f.Name.Contains("排宽") || f.Name.Contains("排厚"))
                .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + "=" + f.Value));
            var supports = string.Join("\n", upstream.Fields.Where(f => f.Name.Contains("基座") || f.Name.Contains("绝缘子"))
                .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + "=" + f.Value));
            return new BridgeCabinetGeometry { Span = end[0] - start[0], Offset = end[2] - start[2],
                HeightDifference = end[1] - start[1], Elevation = start[1], Specification = specification,
                Supports = supports, Scenario = upstream.Scenario + (upstream.SideToSide == null ? "" :
                    upstream.SideToSide.Copper.FixedPhaseChange ? "/SideToSideBase1" : "/SideToSideBase2") };
        }

        internal BridgeDesignSample Create(BridgeUpstreamSnapshot before, BridgeUpstreamSnapshot after,
            BridgeDesignSample previous, Action<string> progress)
            => new BridgeSupportTransition().Plan(previous, baseline => CreateCore(before, after, baseline, progress), progress);

        internal BridgeDesignSample CreateDraftRecommendation(BridgeUpstreamSnapshot upstream, BridgeDesignSample previous)
        {
            var fresh = CreateTarget(upstream, previous, BridgeCabinetUpdateKind.WholeBridge, 0,
                null, preservePreviousLayout: false);
            fresh = BridgeFrameSourceIsolation.Apply(fresh);
            RequireValidPlanning(fresh, "当前参数的全新方案未通过校验：");
            return new BridgeGenerationRecipeCodec().PreserveSupportReplanning(fresh)
                .WithReadbackBaselineWarning("已按当前输入重新设计母排、基架及绝缘子；旧人工位置、长度、锁定和撤销记录已清空。");
        }
        private BridgeDesignSample CreateCore(BridgeUpstreamSnapshot before, BridgeUpstreamSnapshot after,
            BridgeDesignSample previous, Action<string> progress)
        {
            var sample = CreateTarget(before, after, previous, progress);
            if (after.FaceToWall != null && new BridgeCabinetChangeAnalyzer().Compare(Geometry(before), Geometry(after)) != BridgeCabinetUpdateKind.Unchanged)
                sample = new PrototypeBusbarBridgeDesignProvider().AdaptFaceToWallCabinetFrames(after, previous, sample, true);
            var transition = new BridgeSupportTransition();
            sample = transition.Preserve(previous, sample);
            sample = BusbarBridgeDraftEditor.CompleteSupportTargets(sample);
            sample = BridgeFrameSourceIsolation.Apply(sample);
            transition.Require(previous, sample);
            if (previous.SavedFrameLengthEditing != null)
                sample = sample.WithFrameLengthEditing(new BridgeFrameLengthEditing(sample.FrameLengthBaseline,
                    transition.RemapIds(previous, sample, previous.SavedFrameLengthEditing.IndependentFrames),
                    transition.RemapIds(previous, sample, previous.SavedFrameLengthEditing.FixedEndFrames),
                    transition.RemapIds(previous, sample, previous.SavedFrameLengthEditing.LengthAdjustedFrames),
                    sample.SavedFrameLengthEditing?.OffsetCopperMinimumLengthMm ?? previous.SavedFrameLengthEditing.OffsetCopperMinimumLengthMm).RetainFor(sample.Objects));
            BridgeCopperManufacturingValidation.RequireValid(sample);
            return new BridgeGenerationRecipeCodec().PreserveSupportReplanning(sample).WithSavedManualJoints(previous.SavedManualJointIds);
        }

        private BridgeDesignSample CreateTarget(BridgeUpstreamSnapshot before, BridgeUpstreamSnapshot after,
            BridgeDesignSample previous, Action<string> progress)
        {
            var oldGeometry = Geometry(before); var newGeometry = Geometry(after);
            var kind = new BridgeCabinetChangeAnalyzer().Compare(oldGeometry, newGeometry);
            return CreateTarget(after, previous, kind, newGeometry.Elevation - oldGeometry.Elevation, progress);
        }

        internal BridgeDesignSample CreateTarget(BridgeUpstreamSnapshot after, BridgeDesignSample previous,
            BridgeCabinetUpdateKind kind, double rise, Action<string> progress, bool preservePreviousLayout = true)
        {
            progress?.Invoke(preservePreviousLayout ? "母线桥同步：" + kind + "；优先复用已生成模型" : "正在按当前参数草稿计算全新推荐方案。");
            var provider = new PrototypeBusbarBridgeDesignProvider();
            BridgeDesignSample fresh;
            if (after.FaceToWall == null)
            {
                var result = provider.TryCreateSynchronizedPreview(after, out fresh);
                if (!result.Success) throw new InvalidOperationException(result.Message);
            }
            else fresh = PlanWall(after, previous, kind, provider, Math.Abs(rise) < .01);

            if (!preservePreviousLayout) return fresh;

            if (kind == BridgeCabinetUpdateKind.Unchanged)
                return Copy(previous, fresh.Objects.Where(IsUpstream).Concat(previous.Objects.Where(o => !IsUpstream(o))),
                    previous.Routes, previous.Parameters).WithSavedManualInsulators(previous.SavedManualInsulatorIds);

            if (kind == BridgeCabinetUpdateKind.Elevation || kind == BridgeCabinetUpdateKind.Supports)
                return ReuseShape(previous, fresh, rise);

            if (after.FaceToWall == null && (kind == BridgeCabinetUpdateKind.LocalOffset || kind == BridgeCabinetUpdateKind.WholeBridge) &&
                fresh.ReplanCopper != null && !NeedsSideToSideBreakpointRepair(previous))
            {
                // 先把普通断点的人工X位置交给新接口规划器；特殊几何由新规划器计算。
                var previousPoints = previous.Objects.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
                var ordinary = new HashSet<string>(previous.Objects.Where(o => IsOrdinaryBreakpoint(previous, o) && previous.SavedManualJointIds.Contains(o.Id)).Select(o => o.Id));
                var proposal = fresh.Objects.Select(o => IsOrdinaryBreakpoint(fresh, o) && ordinary.Contains(o.Id) &&
                    previousPoints.TryGetValue(o.Id, out var old) ? o.WithValues(o.Values.ToDictionary(p => p.Key,
                    p => p.Key == DesignField.X ? old.X : p.Value)) : o).ToArray();
                var plannedPoints = fresh.Objects.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
                if (proposal.Any(o => o.IsJointGroup && Math.Abs(o.X - plannedPoints[o.Id].X) > .000001))
                {
                    try
                    {
                        var offset = BridgeOffsetCopperChange.Create(fresh, proposal);
                        var local = fresh.ReplanCopper(proposal).WithCopperLengthBaseline(fresh.CopperLengthBaseline)
                            .WithOffsetCopperSafetyShift(fresh.OffsetCopperSafetyShift + (offset?.Shift ?? 0d));
                        if (offset != null) local = local.WithFrameLengthEditing(offset.PreserveFrameBaseline(fresh));
                        // 重规划结果自带的Issues不包含全部接缝和安全覆盖约束，接纳前重新核验。
                        RequireValidPlanning(local, "普通断点同步校验失败：");
                        fresh = local;
                    }
                    catch (InvalidOperationException error)
                    {
                        FaceToWallPreviewTrace.Error("普通断点保留后的局部同步不可行，采用新接口的全桥规划", error);
                        progress?.Invoke("普通断点保留后的局部同步不可行，采用新接口的全桥规划：" + error.Message);
                    }
                }
            }
            fresh = PreserveWallParameters(previous, fresh);
            return PreserveUnchangedSupports(previous, fresh).WithSavedManualJoints(previous.SavedManualJointIds.Where(id => fresh.Objects.Any(o => o.Id == id)));
        }

        private static bool NeedsSideToSideBreakpointRepair(BridgeDesignSample sample) =>
            sample.Issues.Any(i => i.Source == BridgeSideToSideCopperEditor.BreakpointIssueSource);

        private bool IsOrdinaryBreakpoint(BridgeDesignSample sample, BridgeDesignObject node)
        {
            if (node.Kind != DesignObjectKind.Breakpoint || !node.IsJointGroup) return false;
            var route = sample.CopperEditRoutes.SingleOrDefault(r => r.PointIds.Contains(node.Id));
            if (route == null) return false;
            var index = route.PointIds.ToList().IndexOf(node.Id);
            // 编辑路径包含整相，不能用其空CopperSectionId判断特殊段；两侧段均普通才可沿用旧坐标。
            return index > 0 && index < route.PointIds.Count - 1 &&
                sample.SpecialCopperSectionIds?.Contains(BridgeCopperSection.IdFor(index)) != true &&
                sample.SpecialCopperSectionIds?.Contains(BridgeCopperSection.IdFor(index + 1)) != true;
        }

        internal BridgeDesignSample PlanRepairFrameSeams(BridgeDesignSample sample)
        {
            var clearance = new BusbarBridgeFrameJointClearance();
            if (clearance.Validate(sample, sample.Objects).Count == 0) return sample;
            var editing = sample.SavedFrameLengthEditing;
            var fixedFrames = new HashSet<string>((editing?.IndependentFrames ?? Array.Empty<string>())
                .Concat(editing?.FixedEndFrames ?? Array.Empty<string>()).Concat(editing?.LengthAdjustedFrames ?? Array.Empty<string>()));
            fixedFrames.UnionWith(sample.Objects.Where(o => o.ParentId != null &&
                (sample.SavedLockedObjectIds.Contains(o.Id) || sample.SavedManualInsulatorIds.Contains(o.Id))).Select(o => o.ParentId));
            var placement = clearance.Plan(sample, fixedFrames);
            if (placement.Issues.Count > 0) return sample;
            // 只调整支撑分节，保留实际铜排形状、人工断点及原实物恢复记录。
            return BridgeFrameSourceIsolation.Apply(sample.WithGeometry(placement.Objects, sample.Routes));
        }

        internal void RequireValidPlanning(BridgeDesignSample sample, string context,
            IEnumerable<BridgeDesignIssue> plannedIssues = null)
        {
            var editor = new BusbarBridgeDraftEditor(); editor.Load(sample);
            var issues = sample.Issues.Concat(plannedIssues ?? Enumerable.Empty<BridgeDesignIssue>())
                .Concat(BridgeCopperManufacturingValidation.Validate(sample))
                .Concat(editor.ClearanceIssues).Concat(editor.FrameConnectionIssues)
                .Concat(new BusbarBridgeFrameJointClearance().Validate(sample, sample.Objects))
                .Where(i => i.BlocksGeneration).Select(i => i.Message).Distinct().ToArray();
            if (issues.Length > 0)
                throw new InvalidOperationException(context + string.Join("；", issues) +
                    (sample.SavedLockedObjectIds.Count == 0 ? "" : "；保留的锁定对象：" + string.Join("、", sample.SavedLockedObjectIds)));
        }

        private BridgeDesignSample PlanWall(BridgeUpstreamSnapshot upstream, BridgeDesignSample previous,
            BridgeCabinetUpdateKind kind, PrototypeBusbarBridgeDesignProvider provider, bool keepPrefix)
        {
            var stored = new BridgeWallPlanningCodec().DecodeWallPlanning(previous.WallPlanningRecipe);
            var planning = new FaceToWallPreviewPlanning();
            FaceToWallPlanningInput input;
            if (kind == BridgeCabinetUpdateKind.Unchanged || kind == BridgeCabinetUpdateKind.Elevation || kind == BridgeCabinetUpdateKind.Supports)
                return provider.RestoreFaceToWallPlanning(upstream, previous.WallPlanningRecipe);
            var local = kind == BridgeCabinetUpdateKind.LocalOffset && stored.SpecialStartX.HasValue;
            for (var attempt = 0; attempt < (local ? 2 : 1); attempt++)
            {
                try
                {
                    var start = local && attempt == 0 ? stored.SpecialStartX : null;
                    var prefix = start.HasValue && keepPrefix ? stored.Groups.TakeWhile(g => g.DisplayName != "算法中间段").ToArray() : null;
                    input = planning.CreateInputWithOptions(upstream.FaceToWall, null, start, null, prefix);
                    input.Request.MinimumSegmentLength = stored.Request.MinimumSegmentLength;
                    input.Request.BendingRadius = stored.Request.BendingRadius;
                    input.Request.BendAllowance = stored.Request.BendAllowance;
                    var sample = _wallPreview != null ? _wallPreview(upstream, input) : SolveWallPreview(upstream, input, provider);
                    if (sample.Issues.Any(i => i.BlocksGeneration)) throw new InvalidOperationException(sample.Issues.First(i => i.BlocksGeneration).Message);
                    return sample;
                }
                catch (InvalidOperationException error) when (local && attempt == 0)
                {
                    FaceToWallPreviewTrace.Error("面对墙局部同步不可行，扩大为全桥规划", error);
                }
            }
            throw new InvalidOperationException("面对墙同步规划没有返回有效方案。");
        }

        private BridgeDesignSample SolveWallPreview(BridgeUpstreamSnapshot upstream, FaceToWallPlanningInput input,
            PrototypeBusbarBridgeDesignProvider provider)
        {
            using (var worker = new BusbarAlgorithmWorkerClient(_authorization ?? throw new InvalidOperationException("面对墙规划缺少宿主算法授权入口。")))
            {
                var result = worker.SolveProjectAsync(input.Request, null, System.Threading.CancellationToken.None, upstream.ModelPath)
                    .GetAwaiter().GetResult();
                var completed = new FaceToWallMinimumSpanPlanning().Complete(upstream.FaceToWall, input, result, null,
                    (planning, solution) => provider.CreateFaceToWallPreview(upstream, planning.Request, solution, planning),
                    System.Threading.CancellationToken.None);
                return completed.Candidates[0].Sample;
            }
        }

        private BridgeDesignSample ReuseShape(BridgeDesignSample previous, BridgeDesignSample fresh, double rise)
        {
            if (!previous.Coordinates.Along.Concat(previous.Coordinates.Across).Concat(previous.Coordinates.Up)
                .Zip(fresh.Coordinates.Along.Concat(fresh.Coordinates.Across).Concat(fresh.Coordinates.Up), (a, b) => Math.Abs(a - b)).All(d => d < 1e-6))
                throw new InvalidOperationException("同步坐标基准变化，需要手动核验方案。");
            var oldFrames = previous.Objects.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray();
            var newFrames = fresh.Objects.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray();
            var plannedById = newFrames.ToDictionary(o => o.Id);
            var replacements = new Dictionary<string, BridgeDesignObject>();
            var reuseCopper = true;
            for (var i = 0; i < oldFrames.Length; i++)
            {
                var old = oldFrames[i];
                if (!plannedById.TryGetValue(old.Id, out var next))
                    throw new InvalidOperationException("同步无法核验现有基架的规划身份，请手动核验：" + old.Name);
                // 总高改变会重算柜端L排；其伸出排连接点未整体平移，不能平移旧端排后复用。
                if (old.FrameTemplateKey != next.FrameTemplateKey) return fresh;
                if (Math.Abs(old.InsulatorAssemblyHeight - next.InsulatorAssemblyHeight) >= .01) reuseCopper = false;
                var values = old.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.Height] = next.Height;
                values[DesignField.Z] = next.Z;
                replacements[old.Id] = old.WithValues(values).WithInsulatorStack(next.InsulatorPadCount,
                    next.InsulatorPadThickness, next.InsulatorPhaseSpacing, next.InsulatorAssemblyHeight).WithOffsetInsulators(next.UseOffsetInsulators);
            }
            var delta = previous.Coordinates.ToAssemblyDirection(new[] { 0d, 0d, rise });
            Func<BridgeDesignObject, bool> support = o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator;
            var retained = reuseCopper ? previous.Objects.Where(o => !IsUpstream(o)) : previous.Objects.Where(support);
            var objects = fresh.Objects.Where(o => IsUpstream(o) || !reuseCopper && !support(o)).Concat(retained.Select(o =>
            {
                if (replacements.TryGetValue(o.Id, out var frame)) return frame;
                var values = o.Values.ToDictionary(p => p.Key, p => p.Value);
                if (o.Kind == DesignObjectKind.Insulator && replacements.TryGetValue(o.ParentId ?? "", out var owner))
                {
                    var oldOwner = oldFrames.Single(f => f.Id == o.ParentId);
                    values[DesignField.Height] = owner.InsulatorAssemblyHeight;
                    values[DesignField.Z] = o.Z + (oldOwner.Height - owner.Height) / 2 +
                        (owner.InsulatorAssemblyHeight - o.Height) / 2;
                }
                else if (string.IsNullOrEmpty(o.ParentId)) values[DesignField.Z] = o.Z + rise;
                return o.WithValues(values);
            })).ToArray();
            var routes = reuseCopper ? previous.Routes.Select(r =>
            {
                var setting = r.CopyNativePreview();
                if (setting == null) throw new InvalidOperationException("旧铜排缺少原生加工参数。");
                setting.PointList = setting.PointList.Select(p => new Point3d(p.X + delta[0], p.Y + delta[1], p.Z + delta[2])).ToList();
                var translated = r.WithPhysicalGeometry(r.SectionWidth, r.SectionThickness,
                    r.PhysicalSolids.Select(s => s.Select(p => new[] { p[0], p[1], p[2] + rise }).ToArray()));
                return translated.WithNativePreview(fresh.Coordinates.ToPlanning(setting), fresh.Coordinates);
            }).ToArray() : fresh.Routes.ToArray();
            var result = Copy(fresh, objects, routes, previous.Parameters)
                .WithCopperEditing(reuseCopper ? previous.CopperEditRoutes : fresh.CopperEditRoutes, fresh.ReplanCopper)
                .WithSavedManualInsulators(previous.SavedManualInsulatorIds)
                .WithFrameLengthEditing(TranslateFrameLengthEditing(previous, newFrames, rise));
            if (!reuseCopper && new BusbarBridgeFrameJointClearance().Validate(result, result.Objects).Count > 0)
            {
                var editing = previous.SavedFrameLengthEditing;
                var constrained = previous.SavedLockedObjectIds.Count > 0 || previous.SavedManualInsulatorIds.Count > 0 ||
                    editing != null && (editing.IndependentFrames.Count > 0 || editing.FixedEndFrames.Count > 0 || editing.LengthAdjustedFrames.Count > 0);
                // 新铜排与旧支撑接缝不相容时，仅无人工约束的默认布局可采用新接口的完整规划。
                if (!constrained && oldFrames.Length == newFrames.Length && oldFrames.All(old =>
                    plannedById.TryGetValue(old.Id, out var next) && old.FrameTemplateKey == next.FrameTemplateKey &&
                    Math.Abs(old.Length - next.Length) < .01))
                {
                    RequireValidPlanning(fresh, "柜体同步完整规划校验失败：");
                    return Copy(fresh, fresh.Objects, fresh.Routes, previous.Parameters);
                }
            }
            return result;
        }

        private BridgeFrameLengthEditing TranslateFrameLengthEditing(BridgeDesignSample previous,
            IReadOnlyList<BridgeDesignObject> planned, double rise)
        {
            var editing = previous.SavedFrameLengthEditing;
            if (editing == null) return null;
            return new BridgeFrameLengthEditing(editing.BaselineFrames.Select(frame =>
            {
                var next = planned.SingleOrDefault(o => o.Id == frame.Id && o.FrameTemplateKey == frame.FrameTemplateKey);
                var old = previous.Objects.SingleOrDefault(o => o.Id == frame.Id);
                var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.Z] = frame.Z + (next != null && old != null ? next.Z - old.Z : rise);
                if (next != null) values[DesignField.Height] = next.Height;
                return frame.WithValues(values);
            }), editing.IndependentFrames, editing.FixedEndFrames, editing.LengthAdjustedFrames, editing.OffsetCopperMinimumLengthMm);
        }

        internal BridgeDesignSample PreserveWallParameters(BridgeDesignSample previous, BridgeDesignSample fresh)
        {
            if (fresh.ScenarioKey != "FaceToWall") return fresh;
            var old = previous.Objects.Single(o => o.Kind == DesignObjectKind.Frame && o.FrameTemplateKey == "FaceToWallLarge1");
            var next = fresh.Objects.Single(o => o.Kind == DesignObjectKind.Frame && o.FrameTemplateKey == "FaceToWallLarge1");
            if (next.Values.ContainsKey(DesignField.WallTopClearance)) return fresh;
            if (Math.Abs(old.Value(DesignField.WallVerticalClearance) - next.Value(DesignField.WallVerticalClearance)) > .01)
                throw new InvalidOperationException("上下墙距与柜体保存后的实际基座补偿不一致，需手动规划铜排。");
            var editor = new BusbarBridgeDraftEditor(); editor.Load(fresh);
            foreach (var field in new[] { DesignField.WallEntryDistance, DesignField.WallHorizontalClearance })
            {
                var result = editor.SetText(next.Id, field, old.Value(field).ToString("R", CultureInfo.InvariantCulture), false);
                if (!result.Success) throw new InvalidOperationException("保留墙端参数失败：" + result.Message);
            }
            return fresh.WithGeometry(editor.EffectivePlan, fresh.Routes);
        }

        private BridgeDesignSample PreserveUnchangedSupports(BridgeDesignSample previous, BridgeDesignSample fresh)
        {
            var old = previous.Objects.ToDictionary(o => o.Id);
            var objects = fresh.Objects.Select(o => old.TryGetValue(o.Id, out var p) &&
                o.Kind == p.Kind && o.FrameTemplateKey == p.FrameTemplateKey && o.Values.Count == p.Values.Count &&
                (o.Kind != DesignObjectKind.Frame || o.UseOffsetInsulators == p.UseOffsetInsulators && o.InsulatorPadCount == p.InsulatorPadCount &&
                    Math.Abs(o.InsulatorPadThickness - p.InsulatorPadThickness) < .01 &&
                    Math.Abs(o.InsulatorPhaseSpacing - p.InsulatorPhaseSpacing) < .01 &&
                    Math.Abs(o.InsulatorAssemblyHeight - p.InsulatorAssemblyHeight) < .01) &&
                o.Values.All(v => Math.Abs(v.Value - p.Value(v.Key)) < .01) ? p : o).ToArray();
            return Copy(fresh, objects, fresh.Routes, previous.Parameters);
        }

        private BridgeDesignSample Copy(BridgeDesignSample fresh, IEnumerable<BridgeDesignObject> objects,
            IEnumerable<BridgeDesignRoute> routes, BridgeDesignParameters parameters) =>
            new BridgeDesignSample(fresh.Id, fresh.Source, objects, routes, fresh.Issues, parameters)
                .WithCopperEditing(fresh.CopperEditRoutes, fresh.ReplanCopper)
                .WithSpecialCopperSectionIds(fresh.SpecialCopperSectionIds).WithCoordinates(fresh.Coordinates, fresh.ScenarioKey)
                .WithBaseJointConstraints(fresh.BaseJointConstraints).WithWallPlanningRecipe(fresh.WallPlanningRecipe)
                .WithFrameLengthEditing(fresh.SavedFrameLengthEditing)
                .WithCopperLengthBaseline(fresh.CopperLengthBaseline).WithOffsetCopperSafetyShift(fresh.OffsetCopperSafetyShift);

        private bool IsUpstream(BridgeDesignObject o) => o.Kind == DesignObjectKind.Cabinet || o.Kind == DesignObjectKind.Base ||
            o.Kind == DesignObjectKind.FixedBusbar || o.Kind == DesignObjectKind.FixedInsulator || o.Kind == DesignObjectKind.Wall || o.Kind == DesignObjectKind.WallBushing;
    }
}
