using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.DesignGeometry;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    /// <summary>单份草稿的侧对侧大偏移编辑。原柜端与加工结构保持不变，交互计算在未加密界面内执行。</summary>
    internal sealed class BridgeSideToSideCopperEditor
    {
        internal const string BreakpointIssueSource = "侧对侧三相断点";
        private const double MaximumDevelopedLengthMm = 3000d;
        private readonly BridgeDesignSample _baseline;
        private readonly Dictionary<string, BridgeDesignObject> _before;
        private readonly PlanarBendBusbarSetting[] _native;
        private readonly HashSet<int> _middle;
        private readonly double _clearance, _floor, _top, _halfWidth, _inletY, _outletY;
        private readonly int _offsetSection;
        private readonly bool _baselineSafe;
        private readonly BridgeCopperJointGeometryEditor _geometry = new BridgeCopperJointGeometryEditor();

        internal static double FixedJointX(IEnumerable<PlanarBendBusbarSetting> members, bool inlet, double overlap)
        {
            // 编辑坐标沿用搭接后截面；首段从B柜端实际排件起算，末段到A柜端实际排件止。
            return inlet ? members.Min(p => p.PointList.First().X) + overlap :
                members.Max(p => p.PointList.Last().X);
        }

        internal static string JointGroupProblem(IReadOnlyList<BridgeDesignObject> controls)
        {
            var reference = controls.Where(o => o.Phase == "B").OrderBy(o => o.X).ToArray();
            if (reference.Length < 2 || reference.First().Kind != DesignObjectKind.FixedJoint ||
                reference.Last().Kind != DesignObjectKind.FixedJoint ||
                controls.Count != reference.Length * 3 ||
                controls.Any(o => !o.IsJointGroup || double.IsNaN(o.X) || double.IsInfinity(o.X)) ||
                reference.Select(o => o.JointGroupId).Distinct().Count() != reference.Length)
                return "侧对侧缺少唯一的B相断点或柜端接口。";
            foreach (var phase in new[] { "A", "C" })
            {
                var members = controls.Where(o => o.Phase == phase).OrderBy(o => o.X).ToArray();
                if (members.Length != reference.Length || !members.Zip(reference, (node, b) =>
                    node.JointGroupId == b.JointGroupId && node.Kind == b.Kind &&
                    (node.Kind == DesignObjectKind.FixedJoint ||
                    Math.Abs(node.X - b.X) <= BridgeDesignPrecision.LengthToleranceMm)).All(match => match))
                    return phase + "相的断点组或桥向位置与B相不一致。";
            }
            return null;
        }

        internal static string PlanProblem(BridgeDesignSample sample)
        {
            var joints = sample.Objects.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
            var problem = JointGroupProblem(joints.Values.ToArray());
            if (problem != null) return problem;
            var paths = sample.CopperEditRoutes;
            if (paths.Count != 3 || paths.Select(r => r.Phase).Distinct().Count() != 3 ||
                paths.Any(r => r.PointIds.Count < 2 || r.PointIds.Any(id => !joints.ContainsKey(id))))
                return "侧对侧缺少与三相断点对应的段排路径。";
            var tolerance = BridgeDesignPrecision.LengthToleranceMm;
            var members = new List<(string Phase, string Section, double RearAtStart, double End)>();
            foreach (var route in sample.Routes.Where(r => r.GenerationGroup == "中间排"))
            {
                var shape = route.CopyNativePreview();
                if (shape?.PointList == null || shape.PointList.Count < 2)
                    return "侧对侧中间排缺少加工几何：" + route.Id;
                var length = BridgeCopperClearance.DevelopedLength(shape);
                if (double.IsNaN(length) || length > MaximumDevelopedLengthMm + tolerance)
                    return FormattableString.Invariant(
                        $"侧对侧 {shape.GroupKey} 展开长度为{length:0.0} mm，上限为{MaximumDevelopedLengthMm:0.0} mm。");
                var x = shape.PointList.Select(p => sample.Coordinates.ToDraft(new[] { p.X, p.Y, p.Z })[0]).ToArray();
                members.Add((route.Phase, route.CopperSectionId, x.Min() + shape.StartLapWidth, x.Max()));
            }
            if (members.Count == 0) return "侧对侧缺少可核验的中间排。";
            var sections = members.ToLookup(p => (p.Phase, p.Section));
            foreach (var route in paths)
            {
                var points = route.PointIds.Select(id => joints[id]).ToArray();
                for (var i = 1; i < points.Length; i++)
                    if (points[i].X - points[i - 1].X <= tolerance)
                        return "侧对侧断点不能越过相邻断点：" + route.Phase + "相。";
                var rootCount = sample.Routes.Count(r => r.Phase == route.Phase && r.GenerationGroup == "B柜连接段");
                for (var i = 1; i < points.Length - 1; i++)
                {
                    var before = BridgeCopperSection.IdFor(i);
                    var after = BridgeCopperSection.IdFor(i + 1);
                    if (sample.SpecialCopperSectionIds?.Contains(before) == true ||
                        sample.SpecialCopperSectionIds?.Contains(after) == true) continue;
                    var left = sections[(route.Phase, before)].ToArray();
                    var right = sections[(route.Phase, after)].ToArray();
                    var x = points[i].X;
                    if (rootCount == 0 || left.Length != rootCount || right.Length != rootCount ||
                        left.Any(p => Math.Abs(p.End - x) > tolerance) ||
                        right.Any(p => Math.Abs(p.RearAtStart - x) > tolerance))
                        return $"侧对侧第{i}个断点的{route.Phase}相叠排未形成完整的三相共用搭接。";
                }
            }
            return null;
        }

        internal static void RequireReadbackEditClearance(BridgeDesignSample baseline, BridgeDesignSample edited)
        {
            if (!baseline.IsSideToSide) return;
            var previous = baseline.Routes.ToDictionary(r => r.Id);
            var middle = new HashSet<int>(Enumerable.Range(0, edited.Routes.Count)
                .Where(i => edited.Routes[i].GenerationGroup == "中间排"));
            var changed = new HashSet<int>(middle.Where(i => !previous.TryGetValue(edited.Routes[i].Id, out var old) ||
                !ReferenceEquals(old, edited.Routes[i])));
            if (changed.Count == 0) return;
            var native = edited.Routes.Select(r => r.CopyNativePreview()).ToArray();
            // 历史配方缺少排件搭接参数时，由原实物恢复入口负责几何核验。
            if (changed.All(i => native[i]?.StartLapWidth == 0d)) return;
            var result = BridgeCopperClearance.EvaluateChanges(native, changed, middle, edited.Parameters.InsulationClearanceMm);
            if (!result.IsClear)
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"侧对侧断点调整后 {result.FirstGroupKey}/{result.SecondGroupKey} 净距为{result.MinimumDistance:0.0} mm。"));
            if (!SeparateMembers(native, middle))
                throw new InvalidOperationException("侧对侧断点调整后同相不同叠排实体相穿。");
        }

        internal static BridgeSideToSideCopperEditor Create(BridgeDesignSample sample, SideToSideCopperPlanningInput input)
        {
            if (sample.ScenarioKey != "SideToSideBase2" || input == null || input.FixedPhaseChange ||
                sample.FrameLengthBaseline.Count(f => f.CornerPreview != null) != 2 || sample.CopperEditRoutes.Count != 3) return null;
            if (sample.Routes.Any(r => r.HasRaisedLapBaseline))
            {
                var objects = sample.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
                var routes = sample.Routes.Select(r => r.ForRaisedLapEditing(sample.Coordinates)).ToArray();
                foreach (var route in routes.Where((r, i) => sample.Routes[i].HasRaisedLapBaseline))
                {
                    var local = sample.Coordinates.ToPlanning(route.CopyNativePreview());
                    for (var i = 0; i < route.PointIds.Count; i++)
                    {
                        var id = route.PointIds[i]; var point = local.PointList[i];
                        if (!objects.TryGetValue(id, out var node))
                            node = new BridgeDesignObject(id, route.Phase + "相原始加工点", DesignObjectKind.FixedRoutePoint,
                                new Dictionary<DesignField, double>(), Array.Empty<DesignField>(), "搭接抬高前加工路径", phase: route.Phase);
                        if (node.Kind != DesignObjectKind.FixedRoutePoint) return null;
                        var values = node.Values.ToDictionary(p => p.Key, p => p.Value);
                        values[DesignField.X] = point.X; values[DesignField.Y] = point.Z; values[DesignField.Z] = point.Y;
                        objects[id] = node.WithValues(values);
                    }
                }
                sample = sample.WithGeometry(objects.Values, routes);
            }
            var native = sample.Routes.Select(r => r.CopyUnraisedNativePreview()).ToArray();
            if (native.Any(s => s == null || s.IsMilledBar)) return null;
            return new BridgeSideToSideCopperEditor(sample, input, native);
        }

        private BridgeSideToSideCopperEditor(BridgeDesignSample baseline, SideToSideCopperPlanningInput input,
            PlanarBendBusbarSetting[] native)
        {
            _baseline = baseline; _before = baseline.Objects.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
            _native = native;
            _middle = new HashSet<int>(Enumerable.Range(0, baseline.Routes.Count)
                .Where(i => baseline.Routes[i].GenerationGroup == "中间排"));
            _clearance = input.Clearance;
            _floor = Math.Max(input.A.Opening.Y - input.A.OpeningHeight / 2d, input.B.Opening.Y - input.B.OpeningHeight / 2d) + 41.5d;
            _top = Math.Min(input.A.Opening.Y + input.A.OpeningHeight / 2d, input.B.Opening.Y + input.B.OpeningHeight / 2d);
            _halfWidth = Math.Min(input.A.BaseWidth, input.B.BaseWidth) / 2d - 40d;
            _inletY = baseline.FrameLengthBaseline.Single(f => f.CornerPreview?.ClosedEnd == 1).Y;
            _outletY = baseline.FrameLengthBaseline.Single(f => f.CornerPreview?.ClosedEnd == -1).Y;
            var path = baseline.CopperEditRoutes.Single(r => r.Phase == "B");
            _offsetSection = Enumerable.Range(1, path.PointIds.Count - 1).Single(i =>
                Math.Abs(_before[path.PointIds[i]].Y - _before[path.PointIds[i - 1]].Y) > .001d);
            _baselineSafe = BridgeCopperClearance.EvaluateChanges(_native,
                new HashSet<int>(Enumerable.Range(0, _native.Length)), _middle, _clearance).IsClear && SeparateMembers(_native, _middle);
        }

        internal bool TryReplan(IReadOnlyList<BridgeDesignObject> proposed, out BridgeDesignSample sample)
        {
            sample = null;
            var controls = proposed.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
            // 增删断点或固定接口改变需要重新规划拓扑，不套用原加工点列。
            if (!_before.Keys.OrderBy(id => id).SequenceEqual(controls.Keys.OrderBy(id => id)) ||
                _before.Values.Any(o => Math.Abs(o.Y - controls[o.Id].Y) > .001d || Math.Abs(o.Z - controls[o.Id].Z) > .001d ||
                    o.Kind == DesignObjectKind.FixedJoint && Math.Abs(o.X - controls[o.Id].X) > .001d)) return false;
            if (!_baselineSafe) throw new InvalidOperationException("原侧对侧铜排存在净距或同相叠排相穿问题，请先修复实际铜排。");
            var offset = BridgeOffsetCopperChange.Create(_baseline, proposed);
            if (offset == null) return false;
            var changes = controls.Values.Where(o => o.X != _before[o.Id].X).ToArray();
            if (changes.Length == 0)
            {
                sample = _baseline.WithGeometry(proposed, _baseline.Routes);
                return true;
            }
            var native = (PlanarBendBusbarSetting[])_native.Clone();
            var routes = _baseline.Routes.ToArray(); var objects = proposed.ToDictionary(o => o.Id);
            var changed = new HashSet<int>();
            foreach (var index in _middle)
            {
                var route = routes[index]; var shape = BridgeNativePreviewCopy.Copy(_native[index]);
                if (!_geometry.MoveRouteJointPoints(_baseline, route, shape, _before, changes, offset, true)) continue;
                if (shape.GroupKey.Contains("/") && !RigidTranslation(_native[index], shape)) return false;
                // 超长导致加工分段改变时才回到完整规划；常规长度编辑不重复搜索原分段。
                if (BridgeCopperClearance.DevelopedLength(shape) > MaximumDevelopedLengthMm + BridgeDesignPrecision.LengthToleranceMm)
                    return false;
                var local = _baseline.Coordinates.ToPlanning(shape);
                var solids = BridgeCopperClearance.CreateFixedInterfacePreviewSolids(local);
                ValidateBounds(route, solids);
                if (route.PointIds.Count != local.PointList.Count || route.PointIds.Any(id =>
                    !objects.ContainsKey(id) || objects[id].Kind != DesignObjectKind.FixedRoutePoint)) return false;
                for (var point = 0; point < local.PointList.Count; point++)
                {
                    var original = objects[route.PointIds[point]]; var p = local.PointList[point];
                    var values = original.Values.ToDictionary(v => v.Key, v => v.Value);
                    values[DesignField.X] = p.X; values[DesignField.Y] = p.Z; values[DesignField.Z] = p.Y;
                    objects[original.Id] = original.WithValues(values);
                }
                routes[index] = route.WithPhysicalGeometry(local.BusBarWidth, local.BusBarThickness,
                    solids.Select(s => s.Select(p => new[] { p[0], p[2], p[1] }).ToArray()))
                    .WithNativePreview(local, _baseline.Coordinates);
                native[index] = shape; changed.Add(index);
            }
            var result = BridgeCopperClearance.EvaluateChanges(native, changed, _middle, _clearance);
            if (!result.IsClear) throw new InvalidOperationException("侧对侧修改后跨相实体净距不足或柜端相穿：" +
                result.FirstGroupKey + "/" + result.SecondGroupKey + "，净距=" +
                result.MinimumDistance.ToString("0.###", CultureInfo.InvariantCulture) + " mm。");
            if (!SeparateMembers(native, _middle)) throw new InvalidOperationException("侧对侧修改后同相不同叠排实体相穿。");
            sample = _baseline.WithGeometry(proposed.Select(o => objects[o.Id]), routes)
                .WithFrameLengthEditing(offset.PreserveFrameBaseline(_baseline))
                .WithOffsetCopperSafetyShift(_baseline.OffsetCopperSafetyShift + offset.Shift);
            return true;
        }

        private bool RigidTranslation(PlanarBendBusbarSetting before, PlanarBendBusbarSetting after)
        {
            var a0 = before.PointList[0]; var b0 = after.PointList[0];
            return before.PointList.Zip(after.PointList, (a, b) =>
                Math.Abs(b.X - a.X - b0.X + a0.X) < .000001d &&
                Math.Abs(b.Y - a.Y - b0.Y + a0.Y) < .000001d &&
                Math.Abs(b.Z - a.Z - b0.Z + a0.Z) < .000001d).All(same => same);
        }

        private void ValidateBounds(BridgeDesignRoute route, IReadOnlyList<double[][]> solids)
        {
            var path = _baseline.CopperEditRoutes.Single(r => r.Phase == "B");
            var section = Enumerable.Range(1, path.PointIds.Count - 1)
                .Single(i => BridgeCopperSection.IdFor(i) == route.CopperSectionId);
            // 安全通道来自已核验的原始拐角几何，不能用已按0.1mm显示的断点舍入值扩大范围。
            var a = section <= _offsetSection ? _inletY : _outletY;
            var b = section < _offsetSection ? _inletY : _outletY;
            if (solids.SelectMany(s => s).Any(p => p[1] < _floor - .2d || p[1] > _top + .2d ||
                p[2] < Math.Min(a, b) - _halfWidth - .2d || p[2] > Math.Max(a, b) + _halfWidth + .2d))
                throw new InvalidOperationException("铜排实体截面超出固定接口的可用空间：" + route.Phase);
        }

        private static bool SeparateMembers(IReadOnlyList<PlanarBendBusbarSetting> native, IEnumerable<int> middle)
        {
            foreach (var phase in middle.Select(i => native[i]).GroupBy(s => s.GroupKey.Substring(0, 1)))
                if (!BridgeCopperClearance.EvaluateMemberStraightSegments(phase.ToArray(), s =>
                    string.Join("-", s.GroupKey.Split('/')[0].Split('-').Take(2))).IsClear) return false;
            return true;
        }
    }
}
