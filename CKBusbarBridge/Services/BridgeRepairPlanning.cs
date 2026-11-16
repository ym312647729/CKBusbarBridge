using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridgePreview;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Prototype
{
    // 修复只求目标；实物签名与实例绑定留给原差异执行器和事务核验。
    internal sealed class BridgeRepairPlanning
    {
        private const double PortTolerance = 1.0;
        internal const string PlannedWarning = "修复目标已规划；实物尚未修改。请核验预览后生成正式模型。";

        internal string CompletionNotice(string planningNotice) => string.IsNullOrWhiteSpace(planningNotice) ? string.Empty :
            string.Join("\n", planningNotice.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line != PlannedWarning));

        internal bool CopperNeedsWrite(BridgeCopperPartBinding old, PlanarBendBusbarSetting target, bool materialChanged = false)
        {
            if (target == null || materialChanged || old.Shape != new BridgeCopperShapeIdentity().Signature(target) || old.Anchor?.Length != 3)
                return true;
            var anchor = target.PointList.First();
            return BridgeBusinessGeometry.Distance(old.Anchor, new[] { anchor.X, anchor.Y, anchor.Z }) > .01;
        }

        internal IReadOnlyDictionary<string, BridgeCopperPartBinding> RebindCopper(BridgeDesignSample target,
            IReadOnlyDictionary<string, BridgeCopperPartBinding> registered)
        {
            var result = new Dictionary<string, BridgeCopperPartBinding>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            var identity = new BridgeCopperShapeIdentity();
            foreach (var unit in AlgorithmBusbarGenerationService.DescribeBusinessParts(target))
            {
                var source = unit.Routes[0].ComponentBinding;
                BridgeCopperPartBinding binding = null;
                if (source != null)
                {
                    var name = source.Instance.Substring(source.Instance.LastIndexOf('/') + 1);
                    binding = registered.Values.SingleOrDefault(p => p.Instance == name &&
                        string.Equals(p.Path, source.Path, StringComparison.OrdinalIgnoreCase) && p.Configuration == source.Configuration);
                }
                else registered.TryGetValue(unit.Key, out binding);
                if (binding == null || binding.Shape != identity.Signature(unit.Native)) continue;
                if (!used.Add(binding.Instance)) throw new InvalidOperationException("修复铜排复用实例重复：" + binding.Instance);
                result.Add(unit.Key, binding);
            }
            return result;
        }

        internal string[] InspectFrames(BridgeDesignSample sample, double[] start, double[] end)
        {
            var issues = new List<string>();
            var frames = Frames(sample);
            var previous = start;
            foreach (var frame in frames)
            {
                var ports = Ports(sample, frame);
                if (previous != null && BridgeBusinessGeometry.Distance(previous, ports[0]) > PortTolerance)
                    issues.Add(frame.DisplayName + "入口与前序接口未贯通：" + PortDifference(previous, ports[0]));
                previous = ports[1];
            }
            if (end != null && (previous == null || BridgeBusinessGeometry.Distance(previous, end) > PortTolerance))
                issues.Add("末端基架与当前A侧基座接口未贯通：" + (previous == null ? "缺少末端基架出口。" : PortDifference(previous, end)));
            return issues.ToArray();
        }
        private string Format(double[] point) => string.Join(",", point.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)));
        private string PortDifference(double[] actual, double[] expected) =>
            "位置差" + BridgeBusinessGeometry.Distance(actual, expected).ToString("0.0", CultureInfo.InvariantCulture) +
            " mm（当前出口[" + Format(actual) + "] mm → 目标接口[" + Format(expected) + "] mm）。";

        internal BridgeCabinetUpdateKind Classify(BridgeDesignSample actual)
        {
            var frames = Frames(actual);
            var baseline = actual.ReadbackBaseline;
            if (frames.Length == 0 || baseline.RepairStart == null || baseline.RepairEnd == null)
                return BridgeCabinetUpdateKind.WholeBridge;
            var first = actual.Coordinates.ToDraft(Ports(actual, frames.First())[0]);
            var last = actual.Coordinates.ToDraft(Ports(actual, frames.Last())[1]);
            var start = actual.Coordinates.ToDraft(baseline.RepairStart);
            var end = actual.Coordinates.ToDraft(baseline.RepairEnd);
            return Math.Abs((last[0] - first[0]) - (end[0] - start[0])) > PortTolerance
                ? BridgeCabinetUpdateKind.WholeBridge : BridgeCabinetUpdateKind.LocalOffset;
        }

        internal BridgeDesignSample Complete(BridgeDesignSample actual, BridgeDesignSample target, BridgeUpstreamSnapshot upstream = null)
        {
            target = OrderPlannedFrames(target);
            target = Rebase(actual, target);
            if (actual.ScenarioKey == target.ScenarioKey) target = PreserveCopper(actual, target);
            target = PreserveSupports(actual, target);
            var special = new SideToSideZeroOffsetPhaseChangeReadback().AppliesTo(upstream);
            var compact = special || actual.ScenarioKey != target.ScenarioKey ? null : TryKeepFrameChain(actual, target);
            if (compact != null) target = compact;
            target = IsolateConflictingSources(target);
            var transition = new BridgeSupportTransition();
            target = target.WithSavedLocks(transition.RemapIds(actual, target, actual.SavedLockedObjectIds))
                .WithSavedManualInsulators(transition.RemapIds(actual, target, actual.SavedManualInsulatorIds));
            RequireConstraints(actual, target);
            RequireFrameContinuity(target, actual.ReadbackBaseline, upstream);
            var old = actual.ReadbackBaseline;
            var baseline = new BridgeReadbackBaseline { Signature = old.Signature, Recipe = old.Recipe,
                Material = old.Material, CopperCount = old.CopperCount, SuppressedInstances = old.SuppressedInstances,
                RequiresCabinetSynchronization = true, RequiresBridgeRepair = true, RepairPlanned = true,
                RepairStart = old.RepairStart, RepairEnd = old.RepairEnd,
                RepairProblems = old.RepairProblems, RepairUpstreamFingerprint = old.RepairUpstreamFingerprint };
            return target.WithReadbackBaseline(baseline)
                .WithHangers(actual.SavedHangers).WithHangerRoof(actual.HangerRoof, actual.HangerRoofProblem)
                .WithFrameLengthEditing(actual.SavedFrameLengthEditing == null ? target.SavedFrameLengthEditing :
                    new BridgeFrameLengthEditing(target.FrameLengthBaseline,
                        transition.RemapIds(actual, target, actual.SavedFrameLengthEditing.IndependentFrames),
                        transition.RemapIds(actual, target, actual.SavedFrameLengthEditing.FixedEndFrames),
                        transition.RemapIds(actual, target, actual.SavedFrameLengthEditing.LengthAdjustedFrames),
                        target.SavedFrameLengthEditing?.OffsetCopperMinimumLengthMm).RetainFor(target.Objects))
                .WithReadbackBaselineWarning(PlannedWarning);
        }

        internal void RequireFrameContinuity(BridgeDesignSample target, BridgeReadbackBaseline baseline, BridgeUpstreamSnapshot upstream = null)
        {
            var problems = new SideToSideZeroOffsetPhaseChangeReadback().AppliesTo(upstream)
                ? new SideToSideZeroOffsetPhaseChangeValidator().InspectFrames(target, baseline.RepairStart, baseline.RepairEnd)
                : InspectFrames(target, baseline.RepairStart, baseline.RepairEnd);
            if (problems.Length > 0) throw new InvalidOperationException("修复目标仍不贯通：" + string.Join("；", problems));
        }

        internal BridgeDesignSample RetainOrdinaryBreakpoints(BridgeDesignSample actual, BridgeDesignSample target)
        {
            if (target.ScenarioKey == "FaceToWall" || target.ReplanCopper == null) return target;
            var terminals = new Dictionary<string, BridgeDesignObject>();
            // 侧对侧柜端由本轮原生铜排几何固定，没有面对面可调搭接的基座断点约束。
            // 只迁移面对面的人工柜端调整；普通断点仍沿下方共用逻辑保留。
            foreach (var route in actual.CopperEditRoutes.Where(r => !target.IsSideToSide))
            {
                var next = target.CopperEditRoutes.SingleOrDefault(r => r.Phase == route.Phase);
                if (next == null || route.PointIds.Count < 2 || next.PointIds.Count < 2) continue;
                foreach (var end in new[] { false, true })
                {
                    var old = actual.Objects.Single(o => o.Id == (end ? route.PointIds.Last() : route.PointIds.First()));
                    var node = target.Objects.Single(o => o.Id == (end ? next.PointIds.Last() : next.PointIds.First()));
                    var constraint = target.BaseJointConstraints.SingleOrDefault(c => c.JointGroupId == node.JointGroupId);
                    var reference = actual.CopperLengthBaseline?.SingleOrDefault(o => o.Id == old.Id);
                    // 柜端按本相路径的B/A端及显式基座约束匹配；旧绝对坐标不能成为新柜端的位置。
                    if (old.Kind != DesignObjectKind.FixedJoint || node.Kind != DesignObjectKind.FixedJoint ||
                        reference?.Kind != DesignObjectKind.FixedJoint || reference.Phase != route.Phase ||
                        constraint == null || constraint.BaseId != (end ? "base-a" : "base-b"))
                        throw new InvalidOperationException("柜端断点缺少可核验的原始推荐基线或目标基座归属：" + old.Id);
                    var x = node.X + (old.X - reference.X);
                    if (double.IsNaN(x) || double.IsInfinity(x) ||
                        x < constraint.MinimumX - 1.0 || x > constraint.MaximumX + 1.0)
                        throw new InvalidOperationException("柜端断点的相对调整超出本次基座允许范围：" + node.Id +
                            "；目标 " + x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " mm。");
                    if (actual.SavedLockedObjectIds.Contains(old.Id) && BridgeBusinessGeometry.Distance(
                        actual.Coordinates.ToAssembly(new[] { old.X, old.Y, old.Z }),
                        target.Coordinates.ToAssembly(new[] { x, node.Y, node.Z })) > 1.0)
                        throw new InvalidOperationException("修复目标将移动已锁定柜端断点，请先解锁；当前草稿保留：" + old.Id);
                    if (x == node.X) continue;
                    var values = node.Values.ToDictionary(p => p.Key, p => p.Value); values[DesignField.X] = x;
                    terminals[node.Id] = node.WithValues(values);
                }
            }
            Func<BridgeDesignSample, double, bool> special = (sample, x) => sample.Routes
                .Where(r => sample.SpecialCopperSectionIds?.Contains(r.CopperSectionId) == true)
                .Select(r => r.CopyNativePreview().PointList.Select(p => sample.Coordinates.ToDraft(new[] { p.X, p.Y, p.Z })[0]).ToArray())
                .Any(path => x >= path.Min() - .15 && x <= path.Max() + .15);
            var retained = new List<BridgeDesignObject>();
            foreach (var old in actual.Objects.Where(o => o.Kind == DesignObjectKind.Breakpoint && o.IsJointGroup && !special(actual, o.X)))
            {
                var point = target.Coordinates.ToDraft(actual.Coordinates.ToAssembly(new[] { old.X, old.Y, old.Z }));
                if (special(target, point[0])) continue;
                var controls = target.Objects.Where(o => o.IsJointGroup && o.Phase == old.Phase).OrderBy(o => o.X).ToArray();
                var segments = Enumerable.Range(0, Math.Max(0, controls.Length - 1)).Where(i =>
                    point[0] > controls[i].X + .01 && point[0] < controls[i + 1].X - .01).ToArray();
                var coincident = controls.SingleOrDefault(o => Math.Abs(o.X - point[0]) <= .01 && o.Kind == DesignObjectKind.Breakpoint);
                if (coincident != null) { point[1] = coincident.Y; point[2] = coincident.Z; }
                else
                {
                    if (segments.Length != 1) continue;
                    var a = controls[segments[0]]; var b = controls[segments[0] + 1];
                    var ratio = (point[0] - a.X) / (b.X - a.X);
                    point[1] = a.Y + ratio * (b.Y - a.Y); point[2] = a.Z + ratio * (b.Z - a.Z);
                }
                var values = old.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = point[0]; values[DesignField.Y] = point[1]; values[DesignField.Z] = point[2];
                // 跨结构后旧编号可能已属于新的特殊边界，普通断点必须保留独立身份。
                var collision = target.Objects.Any(o => o.Id == old.Id && special(target, o.X));
                var node = collision ? new BridgeDesignObject("user-joint-repair-" + old.Id, old.Name, old.Kind,
                    values, old.EditableFields, old.Role, old.ParentId, old.Phase) : old.WithValues(values);
                retained.Add(node.WithJointGroup("user-joint-repair-" + old.JointGroupId));
            }
            if (retained.Count == 0 && terminals.Count == 0) return target;
            // 相同侧对侧分段已由新接口规划器完成，不再为相同控制点重复分段并丢掉接缝规划。
            // 精确相等仅用于跳过无变化的计算；任何保留下来的人工小数调整仍进入重规划。
            var ordinaryTarget = target.Objects.Where(o => o.Kind == DesignObjectKind.Breakpoint && !special(target, o.X)).ToArray();
            if (target.IsSideToSide && retained.Count == ordinaryTarget.Length && retained.All(o =>
                ordinaryTarget.Any(n => n.Id == o.Id && n.Values.Count == o.Values.Count &&
                    o.Values.All(p => n.Values.TryGetValue(p.Key, out var value) && value == p.Value))))
                return target;
            var proposal = target.Objects.Where(o => retained.Count == 0 || o.Kind != DesignObjectKind.Breakpoint || special(target, o.X))
                .Select(o => terminals.TryGetValue(o.Id, out var terminal) ? terminal : o).Concat(retained).ToArray();
            // 用已有分段规划器接受实际普通断点；不可行时明确报告，不能静默重置人工调整。
            // 此处只保留普通断点和柜端搭接，特殊段边界不变；新增普通点可有待重编号的旧ID。
            var result = target.ReplanCopper(proposal).WithCopperLengthBaseline(target.CopperLengthBaseline)
                .WithFrameLengthEditing(target.SavedFrameLengthEditing).WithOffsetCopperSafetyShift(target.OffsetCopperSafetyShift);
            var blocking = result.Issues.FirstOrDefault(i => i.BlocksGeneration);
            if (blocking != null) throw new InvalidOperationException("保留普通断点的修复规划不可行：" + blocking.Message);
            return result;
        }

        private BridgeDesignSample OrderPlannedFrames(BridgeDesignSample target)
        {
            // 规划器生成的逻辑位置沿用编辑器的B→A顺序；实物顺序始终来自已核验配合链。
            var corner = target.Objects.FirstOrDefault(o => o.Kind == DesignObjectKind.Frame && o.CornerPreview?.ClosedEnd > 0);
            var sign = corner == null ? 1 : -corner.CornerPreview.ClosedSide;
            var positions = target.Objects.Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.X)
                .ThenBy(o => sign * o.Y).ThenBy(o => o.Id, StringComparer.Ordinal)
                .Select((o, i) => new { o.Id, Position = i + 1 }).ToDictionary(p => p.Id, p => p.Position);
            return target.WithGeometry(target.Objects.Select(o => positions.TryGetValue(
                o.Kind == DesignObjectKind.Frame ? o.Id : o.ParentId ?? "", out var position)
                ? o.WithFramePosition(position) : o), target.Routes);
        }

        private BridgeDesignSample Rebase(BridgeDesignSample actual, BridgeDesignSample target)
        {
            if (actual.Coordinates.Matches(target.Coordinates)) return target;
            var axis = actual.Coordinates.ToDraftDirection(target.Coordinates.Along);
            if (Math.Abs(axis[2]) > 1e-6 || Math.Abs(BridgeBusinessGeometry.Dot(actual.Coordinates.Up.ToArray(), target.Coordinates.Up.ToArray()) - 1) > 1e-6)
                throw new InvalidOperationException("修复期间业务高度方向发生变化，请重新核验安装姿态。");
            var angle = Math.Atan2(axis[1], axis[0]) * 180 / Math.PI;
            var objects = target.Objects.Select(o =>
            {
                if (!string.IsNullOrEmpty(o.ParentId)) return o;
                var point = actual.Coordinates.ToDraft(target.Coordinates.ToAssembly(new[] { o.X, o.Y, o.Z }));
                var values = o.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = point[0]; values[DesignField.Y] = point[1]; values[DesignField.Z] = point[2];
                if (o.Kind == DesignObjectKind.Frame) values[DesignField.Direction] = o.Value(DesignField.Direction) + angle;
                return o.WithValues(values);
            }).ToArray();
            var routes = target.Routes.Select(r => r.WithPhysicalGeometry(r.SectionWidth, r.SectionThickness,
                r.PhysicalSolids.Select(s => s.Select(p => actual.Coordinates.ToDraft(target.Coordinates.ToAssembly(p))).ToArray()))
                .WithNativePreview(actual.Coordinates.ToPlanning(r.CopyNativePreview()), actual.Coordinates)).ToArray();
            return target.WithGeometry(objects, routes).WithCoordinates(actual.Coordinates, target.ScenarioKey);
        }

        private BridgeDesignSample PreserveCopper(BridgeDesignSample actual, BridgeDesignSample target)
        {
            var used = new HashSet<string>();
            var routes = target.Routes.Select(route =>
            {
                var candidates = actual.Routes.Where(old => !used.Contains(old.Id) && old.Phase == route.Phase &&
                    old.GenerationGroup == route.GenerationGroup && CompatibleEnds(old, route)).ToArray();
                var previous = candidates.SingleOrDefault(old => old.Id == route.Id);
                if (previous == null && candidates.Length == 1) previous = candidates[0];
                if (previous == null) return route;
                used.Add(previous.Id);
                // 端部加工关系相同，保留实际内部折弯和加工参数，不用默认路线抹掉手工几何。
                var solids = previous.PhysicalSolids.Select(s => s.Select(p =>
                    target.Coordinates.ToDraft(actual.Coordinates.ToAssembly(p))).ToArray());
                var native = target.Coordinates.ToPlanning(previous.CopyNativePreview());
                if (target.IsSideToSide && route.GenerationGroup == "中间排")
                {
                    var planned = route.CopyNativePreview();
                    native.GroupKey = planned.GroupKey;
                    native.PlanningSegmentIndex = planned.PlanningSegmentIndex;
                    native.StartLapWidth = planned.StartLapWidth;
                    native.EndLapWidth = planned.EndLapWidth;
                }
                return route.WithPhysicalGeometry(previous.SectionWidth, previous.SectionThickness, solids)
                    .WithNativePreview(native, target.Coordinates)
                    .WithComponentBinding(previous.ComponentBinding);
            }).ToArray();
            return target.WithGeometry(target.Objects, routes);
        }

        private bool CompatibleEnds(BridgeDesignRoute a, BridgeDesignRoute b)
        {
            var x = a.CopyNativePreview(); var y = b.CopyNativePreview();
            if (x == null || y == null || x.IsMilledBar != y.IsMilledBar || x.IsVerticalBend != y.IsVerticalBend ||
                Math.Abs(x.BusBarWidth - y.BusBarWidth) > .001 || Math.Abs(x.BusBarThickness - y.BusBarThickness) > .001 ||
                (x.WidthVector - y.WidthVector).Norm > 1e-6 || (x.StartBusbarVector - y.StartBusbarVector).Norm > 1e-6)
                return false;
            return x.PointList.First().DistanceTo(y.PointList.First()) < .01 &&
                x.PointList.Last().DistanceTo(y.PointList.Last()) < .01 &&
                SameDirection(x.PointList[0], x.PointList[1], y.PointList[0], y.PointList[1]) &&
                SameDirection(x.PointList[x.PointList.Count - 2], x.PointList.Last(), y.PointList[y.PointList.Count - 2], y.PointList.Last());
        }

        private bool SameDirection(GeometRi.Point3d a, GeometRi.Point3d b, GeometRi.Point3d c, GeometRi.Point3d d) =>
            BridgeBusinessGeometry.Dot(BridgeBusinessGeometry.Unit(new[] { b.X - a.X, b.Y - a.Y, b.Z - a.Z }),
                BridgeBusinessGeometry.Unit(new[] { d.X - c.X, d.Y - c.Y, d.Z - c.Z })) > .999999;

        private BridgeDesignSample PreserveSupports(BridgeDesignSample actual, BridgeDesignSample target)
        {
            return new BridgeSupportTransition().Preserve(actual, target);
        }

        internal BridgeDesignSample IsolateConflictingSources(BridgeDesignSample target) => BridgeFrameSourceIsolation.Apply(target);

        internal bool FramePlacementsUnchanged(BridgeDesignSample actual, BridgeDesignSample target)
        {
            var old = Frames(actual); var next = Frames(target);
            if (old.Length != next.Length) return false;
            return old.Select((f, i) => f.Id == next[i].Id && f.FramePositionIndex == next[i].FramePositionIndex &&
                f.SourceName == next[i].SourceName && f.FrameTemplateKey == next[i].FrameTemplateKey &&
                Math.Abs(f.Value(DesignField.Installation) - next[i].Value(DesignField.Installation)) < .000001 &&
                f.ComponentBinding != null && next[i].ComponentBinding != null &&
                f.ComponentBinding.Instance == next[i].ComponentBinding.Instance && f.ComponentBinding.DocumentKey == next[i].ComponentBinding.DocumentKey &&
                Ports(actual, f).Zip(Ports(target, next[i]), (a, b) => BridgeBusinessGeometry.Distance(a, b) < .01).All(equal => equal)).All(equal => equal);
        }

        private BridgeDesignSample TryKeepFrameChain(BridgeDesignSample actual, BridgeDesignSample target)
        {
            if (actual.ScenarioKey == "FaceToWall" || !actual.Coordinates.Matches(target.Coordinates)) return null;
            var frames = Frames(actual); var baseline = actual.ReadbackBaseline;
            if (Math.Abs(actual.Source.BusbarWidth - target.Source.BusbarWidth) > .01 ||
                Math.Abs(actual.Source.BusbarThickness - target.Source.BusbarThickness) > .01) return null;
            if (frames.Length == 0 || baseline.RepairEnd == null || baseline.RepairStart == null ||
                InspectFrames(actual, Ports(actual, frames.First())[0], Ports(actual, frames.Last())[1]).Length > 0)
                return null;
            var first = Ports(actual, frames.First())[0]; var last = Ports(actual, frames.Last())[1];
            if (BridgeBusinessGeometry.Distance(first, baseline.RepairStart) > .01) return null;
            var delta = BridgeBusinessGeometry.Sub(baseline.RepairEnd, last);
            var local = actual.Coordinates.ToDraftDirection(delta);
            // 跨度变化先尝试仅改变末端普通节；不把横向变化强塞进普通直节。
            if (Math.Abs(local[1]) > .01 || Math.Abs(local[2]) > .01) return null;
            var final = frames.Last();
            if (final.FrameTemplateKey != "Regular" || final.SmallOffsetPreview != null ||
                Math.Abs(final.Value(DesignField.Direction)) > .01 || final.IsReversed) return null;
            var length = final.Length + local[0];
            if (length < 100 || length > 1400) return null;
            var values = final.Values.ToDictionary(p => p.Key, p => p.Value);
            values[DesignField.Length] = length; values[DesignField.X] = final.X + local[0] / 2;
            var replacement = final.WithValues(values);
            // Complete统一按最终模型参数拆分来源，沿用“基架N”命名。
            var supports = actual.Objects.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator)
                .Select(o =>
                {
                    if (o.Id == final.Id) return replacement;
                    if (o.ParentId != final.Id) return o;
                    var child = o.Values.ToDictionary(p => p.Key, p => p.Value);
                    child[DesignField.X] = o.X - local[0] / 2;
                    return o.WithValues(child);
                }).ToArray();
            var candidate = target.WithGeometry(target.Objects.Where(o => o.Kind != DesignObjectKind.Frame &&
                o.Kind != DesignObjectKind.Insulator).Concat(supports), target.Routes);
            if (InspectFrames(candidate, baseline.RepairStart, baseline.RepairEnd).Length > 0) return null;
            try
            {
                foreach (var frame in supports.Where(o => o.Kind == DesignObjectKind.Frame))
                    new BridgeDraftGenerationPlanFactory().FrameInput(frame, supports);
                RequireConstraints(actual, candidate);
            }
            catch (InvalidOperationException error)
            {
                FaceToWallPreviewTrace.Error("保留现有基架链的局部候选不可行，采用同步规划目标", error);
                return null;
            }
            return candidate;
        }

        private void RequireConstraints(BridgeDesignSample actual, BridgeDesignSample target)
        {
            new BridgeSupportTransition().Require(actual, target);
        }

        private BridgeDesignObject[] Frames(BridgeDesignSample sample) => sample.Objects
            .Where(o => o.Kind == DesignObjectKind.Frame).OrderBy(o => o.FramePositionIndex).ToArray();
        private double[][] Ports(BridgeDesignSample sample, BridgeDesignObject frame) =>
            new BridgeDraftGenerationPlanFactory().InterfacePoints(frame, sample.Coordinates).ToArray();
    }

    internal sealed class BridgeCopperContinuity
    {
        internal string[] Inspect(BridgeDesignSample sample, BridgeBusinessObservation observation, BridgeUpstreamSnapshot upstream)
        {
            if (new SideToSideZeroOffsetPhaseChangeReadback().AppliesTo(upstream))
                return new SideToSideZeroOffsetPhaseChangeValidator().InspectCopper(sample, observation, upstream);
            var problems = new List<string>();
            var parts = observation.Copper.OrderBy(p => p.Binding.Instance, StringComparer.Ordinal).ToArray();
            if (parts.Length == 0) return new[] { "母线桥缺少可贯通的实际铜排。" };
            var phases = parts.Select(p => sample.Routes.Where(r => r.ComponentBinding?.Instance == p.Binding.Instance)
                .Select(r => r.Phase).Distinct().Single()).ToArray();
            var graph = parts.Select(p => new List<int>()).ToArray();
            for (var i = 0; i < parts.Length; i++) for (var j = i + 1; j < parts.Length; j++)
            {
                if (!Touches(parts[i], parts[j], sample.Coordinates)) continue;
                if (phases[i] != phases[j]) { problems.Add("不同相位铜排存在实际接触：" + parts[i].Binding.Instance + " / " + parts[j].Binding.Instance); continue; }
                graph[i].Add(j); graph[j].Add(i);
            }
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var indices = Enumerable.Range(0, parts.Length).Where(i => phases[i] == phase).ToArray();
                var sides = sample.ScenarioKey == "FaceToWall" ? new[] { "A" } : new[] { "B", "A" };
                foreach (var side in sides)
                {
                    var seeds = indices.Where(i => sample.Routes.Any(r => r.ComponentBinding?.Instance == parts[i].Binding.Instance &&
                        IsCabinetRoute(r, side))).ToArray();
                    var visited = new HashSet<int>(seeds); var queue = new Queue<int>(seeds);
                    while (queue.Count > 0) foreach (var next in graph[queue.Dequeue()]) if (visited.Add(next)) queue.Enqueue(next);
                    if (indices.Length == 0 || seeds.Length == 0 || indices.Any(i => !visited.Contains(i)))
                        problems.Add(phase + "相铜排未贯通到" + side + "柜连接段。");
                    problems.AddRange(new BridgeCabinetEndpointContinuity().Inspect(
                        upstream.Endpoints.Where(e => e.Side == side && e.Phase == phase), seeds.Select(i => parts[i])));
                }
            }
            return problems.Distinct().ToArray();
        }

        private bool IsCabinetRoute(BridgeDesignRoute route, string side) => route.GenerationGroup != null
            ? route.GenerationGroup == side + "柜连接段"
            : route.CopperSectionId == null && (route.CopyNativePreview()?.GroupKey ?? "").EndsWith(side + "基座L排", StringComparison.Ordinal);

        private bool Touches(BridgeReadbackCopper a, BridgeReadbackCopper b, BridgeCoordinateFrame coordinates)
        {
            // 原路径及排宽只用于筛选候选；真正贯通依赖实际搭接面，不使用Box或中心线相交。
            for (var axis = 0; axis < 3; axis++)
                if (a.Path.Max(p => p[axis]) + a.Width + b.Width < b.Path.Min(p => p[axis]) ||
                    b.Path.Max(p => p[axis]) + a.Width + b.Width < a.Path.Min(p => p[axis])) return false;
            a.Faces = a.Faces ?? a.ReadFaces(); b.Faces = b.Faces ?? b.ReadFaces();
            return a.Faces.Any(f => b.Faces.Any(g => BridgeBusinessGeometry.Contact(f, g, coordinates) != null));
        }
    }

    internal sealed class BridgeCabinetEndpointContinuity
    {
        private const double EndpointTolerance = .15;

        // 调用方先按柜号和相别限定候选，再以真实草图端点为每个叠排成员建立唯一对应。
        internal string[] Inspect(IEnumerable<BridgeUpstreamEndpoint> endpoints, IEnumerable<BridgeReadbackCopper> candidates)
        {
            var expected = endpoints.OrderBy(e => e.Member).ToArray();
            var actual = candidates.OrderBy(c => c.Binding.Instance, StringComparer.Ordinal).ToArray();
            var problems = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            if (expected.GroupBy(e => e.Member).Any(g => g.Count() != 1))
                return new[] { expected[0].Phase + "相" + expected[0].Side + "柜端接口叠排成员编号重复。" };
            foreach (var endpoint in expected)
            {
                var label = endpoint.Phase + "相" + endpoint.Side + "柜端接口" + (endpoint.Member + 1);
                var native = endpoint.CopyNativePreview();
                if (native?.PointList == null || native.PointList.Count < 2 ||
                    native.StartBusbarVector == null || !(native.StartBusbarVector.Norm > .001) ||
                    native.WidthVector == null || !(native.WidthVector.Norm > .001) ||
                    !(native.BusBarWidth > 0) || !(native.BusBarThickness > 0))
                {
                    problems.Add(label + "缺少有效原生加工参数，无法统一接口基准。");
                    continue;
                }
                // 原驱动草图保存的是排厚侧边线；固定端快照保存中心线，必须经过原生成适配器转换。
                var point = BridgeCopperBusinessRecovery.Preview(native).First();
                var matches = actual.Where(c => TerminalDistance(c, point) < EndpointTolerance &&
                    Math.Abs(c.Width - native.BusBarWidth) < .001 && Math.Abs(c.Thickness - native.BusBarThickness) < .001).ToArray();
                if (matches.Length == 0)
                {
                    // 最近候选只用于诊断；没有唯一合格匹配时仍阻断，不据此推定组件归属。
                    var nearest = actual.OrderBy(c => TerminalDistance(c, point)).FirstOrDefault();
                    problems.Add(label + "与当前固定接口不符：" + (nearest == null ? "缺少柜端铜排实例。" :
                        string.Format(CultureInfo.InvariantCulture,
                            "最近候选组件={0}，端点偏差={1:0.###} mm（容差0.15 mm），实际规格={2:0.###}×{3:0.###}，要求={4:0.###}×{5:0.###}。",
                            nearest.Binding.Instance, TerminalDistance(nearest, point), nearest.Width, nearest.Thickness,
                            native.BusBarWidth, native.BusBarThickness)));
                }
                else if (matches.Length != 1)
                    problems.Add(label + "对应多个铜排实例，不能唯一核验：" + string.Join("、", matches.Select(c => c.Binding.Instance)));
                else if (!used.Add(matches[0].Binding.Instance))
                    problems.Add(label + "重复使用其他叠排成员的铜排实例：" + matches[0].Binding.Instance);
            }
            return problems.ToArray();
        }

        private double TerminalDistance(BridgeReadbackCopper copper, double[] point) => copper.Path == null || copper.Path.Length < 2
            ? double.PositiveInfinity : Math.Min(BridgeBusinessGeometry.Distance(copper.Path.First(), point),
                BridgeBusinessGeometry.Distance(copper.Path.Last(), point));
    }
}
