using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Services
{
    // 只移动已存在搭接的两个水平直头，不改变Worker拓扑、折弯、铣削或端部相别。
    internal sealed class FaceToWallCopperDraft
    {
        private sealed class Pair
        {
            internal int LeftGroup, LeftIndex, RightGroup, RightIndex;
        }
        private sealed class Joint
        {
            internal string Id;
            internal Pair[] Pairs;
            internal double MinimumDelta, MaximumDelta;
            internal Dictionary<string, Point3d> Points;
            internal bool RequiresPlanning;
        }
        private readonly IReadOnlyList<AlgorithmBusbarGenerationGroup> _groups;
        private readonly Joint[] _joints;
        private readonly LinkageRouteWorkerRequestDto _request;
        private readonly LinkageRouteWorkerSolutionDto _solution;
        private readonly Dictionary<int, int> _generatedPredecessors = new Dictionary<int, int>();
        internal IReadOnlyList<AlgorithmBusbarGenerationGroup> CopyBaseline() => CopyGroups(_groups);
        internal double? SpecialStartX { get; set; }
        internal string[] JointIds => _joints.Select(joint => joint.Id).ToArray();
        internal FaceToWallCopperDraft WithReplannedJointIds(FaceToWallCopperDraft previous)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var oldBase = previous._joints.SingleOrDefault(previous.IsBaseJoint);
            var retained = previous._joints.Where(j => !previous.IsBaseJoint(j) && previous.IsOrdinaryJoint(j))
                .ToDictionary(j => previous.JointConnectionKey(j), j => j.Id, StringComparer.Ordinal);
            for (var i = 0; i < _joints.Length; i++)
            {
                var joint = _joints[i];
                joint.Id = IsBaseJoint(joint) && oldBase != null ? oldBase.Id :
                    IsOrdinaryJoint(joint) && retained.TryGetValue(JointConnectionKey(joint), out var id) ? id : "wall-special-" + suffix + "-" + i;
            }
            return this;
        }
        private bool IsOrdinaryJoint(Joint joint) => joint.Pairs.All(p =>
            _groups[p.RightGroup].DisplayName != "算法中间段" && _groups[p.RightGroup].DisplayName != "特殊段入口桥头");
        private string JointConnectionKey(Joint joint) => string.Join("|", joint.Pairs.Select(p =>
            _groups[p.LeftGroup].DisplayName + ":" + _groups[p.LeftGroup].Busbars[p.LeftIndex].GroupKey + ">" +
            _groups[p.RightGroup].DisplayName + ":" + _groups[p.RightGroup].Busbars[p.RightIndex].GroupKey).OrderBy(key => key, StringComparer.Ordinal));
        internal FaceToWallCopperDraft WithJointIds(string[] ids)
        {
            if (ids == null) return this; // 旧记录沿用原始顺序身份。
            if (ids.Length != _joints.Length || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct().Count() != ids.Length)
                throw new InvalidOperationException("面对墙保存的断点身份缺失或重复。");
            for (var i = 0; i < ids.Length; i++) _joints[i].Id = ids[i];
            return this;
        }
        internal FaceToWallCopperDraft Snapshot(IReadOnlyList<BridgeDesignObject> objects)
        {
            var current = Repartition(objects);
            return new FaceToWallCopperDraft(current.Apply(objects), _request, _solution)
                { SpecialStartX = SpecialStartX }.WithJointIds(current.JointIds);
        }

        internal IReadOnlyList<double> OrdinaryBreakpoints(double interfaceX)
        {
            if (!SpecialStartX.HasValue) throw new InvalidOperationException("普通段缺少特殊段入口。");
            // 分节归属来自原生铜排组连接关系；不能按特殊排折回后的X位置猜普通断点。
            return new[] { 0d }.Concat(_joints.Where(j => !IsBaseJoint(j) && j.Pairs.Length > 0 &&
                j.Pairs.All(p => _groups[p.LeftGroup].DisplayName != "算法中间段" &&
                    _groups[p.RightGroup].DisplayName != "算法中间段"))
                .OrderBy(j => j.Points["B"].X).Select(j => j.Points["B"].X - interfaceX))
                .Concat(new[] { SpecialStartX.Value - interfaceX }).ToArray();
        }

        internal FaceToWallCopperDraft ReplaceSpecial(IReadOnlyList<AlgorithmBusbarGenerationGroup> groups,
            LinkageRouteWorkerRequestDto request, LinkageRouteWorkerSolutionDto solution)
        {
            var result = new FaceToWallCopperDraft(groups, request, solution) { SpecialStartX = SpecialStartX };
            var entry = Array.FindIndex(_joints, joint => joint.Pairs.Any(pair =>
                _groups[pair.RightGroup].DisplayName == "算法中间段"));
            var nextEntry = Array.FindIndex(result._joints, joint => joint.Pairs.Any(pair =>
                result._groups[pair.RightGroup].DisplayName == "算法中间段"));
            if (entry < 0 || nextEntry != entry)
                throw new InvalidOperationException("替换方案的普通段拓扑或特殊段边界不一致。");
            for (var i = 0; i <= entry; i++)
            {
                if (new[] { "A", "B", "C" }.Any(phase =>
                    _joints[i].Points[phase].DistanceTo(result._joints[i].Points[phase]) > .01d))
                    throw new InvalidOperationException("替换方案改变了普通段断点或特殊段固定入口。");
                result._joints[i].Id = _joints[i].Id;
            }
            // 特殊段拓扑可以变化，不能让旧覆盖或锁定误认同序号的新加工断点。
            var suffix = Guid.NewGuid().ToString("N");
            for (var i = entry + 1; i < result._joints.Length; i++)
                result._joints[i].Id = "wall-special-" + suffix + "-" + i;
            return result;
        }

        internal FaceToWallCopperDraft ReplanForSupportHeight(IReadOnlyList<AlgorithmBusbarGenerationGroup> groups,
            LinkageRouteWorkerRequestDto request, LinkageRouteWorkerSolutionDto solution)
        {
            var next = new FaceToWallCopperDraft(groups, request, solution) { SpecialStartX = SpecialStartX };
            var entry = Array.FindIndex(_joints, j => j.Pairs.Any(p => _groups[p.RightGroup].DisplayName == "算法中间段"));
            var nextEntry = Array.FindIndex(next._joints, j => j.Pairs.Any(p => next._groups[p.RightGroup].DisplayName == "算法中间段"));
            if (entry < 0 || entry != nextEntry)
                throw new InvalidOperationException("重规划改变了普通段分节，原草稿保留。");
            var ids = next.JointIds; var suffix = Guid.NewGuid().ToString("N");
            for (var i = 0; i < ids.Length; i++) ids[i] = i <= entry ? _joints[i].Id : "wall-special-" + suffix + "-" + i;
            next.WithJointIds(ids);
            var controls = new List<BridgeDesignObject>();
            next.AddControls(controls, new List<BridgeDesignRoute>(), null);
            var retained = _joints.Take(entry + 1).ToDictionary(j => j.Id);
            var positions = controls.Select(o =>
            {
                if (o.JointGroupId == null || !retained.TryGetValue(o.JointGroupId, out var joint)) return o;
                var values = o.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = joint.Points[o.Phase].X;
                return o.WithValues(values);
            }).ToArray();
            return new FaceToWallCopperDraft(next.Apply(positions), request, solution)
                { SpecialStartX = SpecialStartX }.WithJointIds(ids);
        }

        internal FaceToWallCopperDraft Repartition(IReadOnlyList<BridgeDesignObject> edits)
        {
            var expectedIds = new HashSet<string>(_joints.SelectMany(j => new[] { "A", "B", "C" }.Select(p => j.Id + "-" + p)));
            var controls = edits.Where(o => expectedIds.Contains(o.Id) ||
                o.JointGroupId?.StartsWith("user-joint-", StringComparison.Ordinal) == true).ToArray();
            if (expectedIds.All(id => controls.Any(o => o.Id == id)) && controls.Length == expectedIds.Count) return this;
            var baseline = new List<BridgeDesignObject>();
            AddControls(baseline, new List<BridgeDesignRoute>(), null);
            var backfilled = baseline.Select(o => edits.FirstOrDefault(e => e.Id == o.Id) ?? o).ToArray();
            var moved = Apply(backfilled);
            var entry = Array.FindIndex(_joints, j => j.RequiresPlanning ||
                j.Pairs.Any(p => _groups[p.RightGroup].DisplayName == "算法中间段"));
            if (entry < 1) throw new InvalidOperationException("未找到普通段与特殊段的明确边界，不能重新分节。");
            var boundary = _joints[entry];
            var prefixEnd = boundary.Pairs[0].LeftGroup;
            var ordered = controls.Where(o => o.Phase == "B" && o.X <= backfilled.Single(b => b.Id == boundary.Id + "-B").X + .001)
                .OrderBy(o => o.X).ToArray();
            if (ordered.Length < 2 || ordered[0].JointGroupId != _joints[0].Id || ordered.Last().JointGroupId != boundary.Id)
                throw new InvalidOperationException("固定接口和特殊段排边界必须保留。");
            if (_joints.Skip(entry).Any(j => !controls.Any(o => o.JointGroupId == j.Id)))
                throw new InvalidOperationException("特殊段排断点不允许移除。");
            var groups = moved.Take(1).ToList();
            for (var i = 1; i < ordered.Length; i++)
            {
                var start = ordered[i - 1].X; var end = ordered[i].X;
                var sourceIndex = Enumerable.Range(1, prefixEnd).First(g =>
                {
                    var bar = moved[g].Busbars.First(b => Phase(b.GroupKey) == "B");
                    return start < bar.PointList.Last().X - .01;
                });
                var source = moved[sourceIndex];
                var originalStart = _joints.First(j => j.Pairs.Any(p => p.RightGroup == sourceIndex)).Id;
                var originalEnd = _joints.First(j => j.Pairs.Any(p => p.LeftGroup == sourceIndex)).Id;
                var dxStart = start - backfilled.Single(o => o.Id == originalStart + "-B").X;
                var dxEnd = end - backfilled.Single(o => o.Id == originalEnd + "-B").X;
                var bars = source.Busbars.Select(b =>
                {
                    var shape = BridgeNativePreviewCopy.Copy(b);
                    if (shape.IsMilledBar || shape.IsVerticalBend) throw new InvalidOperationException("特殊段排不允许重新分节。");
                    for (var p = 0; p < shape.PointList.Count; p++)
                        shape.PointList[p].X += p < shape.PointList.Count / 2 ? dxStart : dxEnd;
                    if (ordered[i - 1].JointGroupId.StartsWith("user-joint-", StringComparison.Ordinal) && shape.PointList.Count == 2)
                        shape.PointList = FaceToWallModelBusbarPlanner.CreateBridgeReferenceCenterlinePoints(
                            shape.PointList.First(), shape.PointList.Last(), new Vector3d(1, 0, 0), new Vector3d(0, 1, 0),
                            shape.BusBarWidth, shape.BusBarThickness, shape.BusBarThickness, 0d).ToList();
                    if (shape.PointList.Zip(shape.PointList.Skip(1), (a, c) => c.X - a.X).Any(dx => dx < -.001) ||
                        shape.GetDevelopedLength() > 3000d + .001)
                        throw new InvalidOperationException("重新分节后的普通排直头不足或展开长度超过3000 mm，未提交修改。");
                    return shape;
                }).ToArray();
                groups.Add(new AlgorithmBusbarGenerationGroup { DisplayName = source.DisplayName == "算法始端普通段" && i == ordered.Length - 1
                    ? "算法始端普通段" : "普通段" + i, IsBridge = source.IsBridge,
                    RestrictSourceReuseToCurrentGroup = source.RestrictSourceReuseToCurrentGroup, Busbars = Array.AsReadOnly(bars) });
            }
            groups.AddRange(moved.Skip(prefixEnd + 1));
            var result = new FaceToWallCopperDraft(groups.AsReadOnly(), _request, _solution) { SpecialStartX = SpecialStartX };
            foreach (var joint in result._joints)
            {
                var matches = controls.Where(o => o.Phase == "B" && Math.Abs(o.X - joint.Points["B"].X) < .01).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("重新分节的断点身份未唯一对应。");
                joint.Id = matches[0].JointGroupId;
            }
            return result;
        }
        internal FaceToWallCopperDraft(IReadOnlyList<AlgorithmBusbarGenerationGroup> groups,
            LinkageRouteWorkerRequestDto request, LinkageRouteWorkerSolutionDto solution)
        {
            _groups = CopyGroups(groups); _request = request; _solution = solution;
            var joints = new List<Joint>();
            var startGroup = groups.ToList().FindIndex(g => g.DisplayName == "特殊段入口桥头");
            if (startGroup < 0) startGroup = groups.ToList().FindIndex(g => g.DisplayName == "算法始端普通段");
            var generatedGroup = groups.ToList().FindIndex(g => g.DisplayName == "算法中间段");
            if (startGroup < 0 && generatedGroup > 0) startGroup = generatedGroup - 1;
            if (startGroup < 0 || generatedGroup < 0) { _joints = joints.ToArray(); return; }
            // 柜端连接排及普通段在Worker之外保留，已有普通搭接同样允许ABC联动编辑。
            for (var g = 1; g <= startGroup; g++)
            {
                var pairs = new List<Pair>();
                foreach (var phase in new[] { "A", "B", "C" })
                {
                    var left = Enumerable.Range(0, groups[g - 1].Busbars.Count).Where(i => Phase(groups[g - 1].Busbars[i].GroupKey) == phase).ToArray();
                    var right = Enumerable.Range(0, groups[g].Busbars.Count).Where(i => Phase(groups[g].Busbars[i].GroupKey) == phase).ToArray();
                    if (left.Length != right.Length) throw new InvalidOperationException("普通搭接两侧叠排根数不一致。");
                    foreach (var index in left)
                    {
                        var bar = groups[g - 1].Busbars[index];
                        // 前缀保留生成成员身份；Worker排序改变后仍按身份唯一关联。
                        var selected = right.Where(i => string.Equals(groups[g].Busbars[i].GroupKey,
                            bar.GroupKey, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (selected.Length != 1) throw new InvalidOperationException("普通搭接叠排成员身份未唯一对应。");
                        var match = selected[0]; right = right.Where(i => i != match).ToArray();
                        pairs.Add(new Pair { LeftGroup = g - 1, LeftIndex = index, RightGroup = g, RightIndex = match });
                    }
                }
                AddJoint(joints, pairs.ToArray());
            }
            var sequences = new Dictionary<string, List<List<int>>>();
            var generated = groups[generatedGroup].Busbars;
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var nativeIndices = Enumerable.Range(0, generated.Count).Where(i => Phase(generated[i].GroupKey) == phase).ToArray();
                var group = solution.GroupSolutions.Single(g => Phase(g.GroupKey) == phase);
                var lengths = group.StackMembers.OrderBy(m => m.MemberIndex).Select(m => m.PlanarParts.Count).ToArray();
                if (lengths.Sum() != nativeIndices.Length) throw new InvalidOperationException("Worker叠排成员与实体序号不一致。");
                var offset = 0; var members = new List<List<int>>();
                foreach (var length in lengths)
                {
                    var member = nativeIndices.Skip(offset).Take(length).ToList();
                    for (var i = 1; i < member.Count; i++) _generatedPredecessors.Add(member[i], member[i - 1]);
                    members.Add(member); offset += length;
                }
                sequences.Add(phase, members);
            }
            var entry = new List<Pair>();
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var indices = Enumerable.Range(0, groups[startGroup].Busbars.Count).Where(i => Phase(groups[startGroup].Busbars[i].GroupKey) == phase).ToArray();
                if (indices.Length != sequences[phase].Count) throw new InvalidOperationException("柜端及Worker叠排根数不一致。");
                for (var m = 0; m < indices.Length; m++) entry.Add(new Pair { LeftGroup = startGroup, LeftIndex = indices[m],
                    RightGroup = generatedGroup, RightIndex = sequences[phase][m][0] });
            }
            var entryJointCount = joints.Count;
            AddJoint(joints, entry.ToArray());
            if (joints.Count > entryJointCount && groups[startGroup].DisplayName != "算法始端普通段")
                joints[joints.Count - 1].RequiresPlanning = true;
            if (joints.Count == entryJointCount)
            {
                // 特殊排入口未必是可直接拖动的水平搭接；仍保留真实Worker边界供选择及跨度重算。
                var points = request.Groups.ToDictionary(g => g.Phase, g => new Point3d(
                    g.StartTerminal.RouteAnchor.X, g.StartTerminal.RouteAnchor.Y, g.StartTerminal.RouteAnchor.Z));
                joints.Add(new Joint { Id = "wall-joint-" + joints.Count, Pairs = entry.ToArray(),
                    Points = points, RequiresPlanning = true });
            }
            var commonPartCount = sequences.Values.SelectMany(v => v).Min(m => m.Count);
            for (var i = 1; i < commonPartCount; i++)
            {
                var pairs = sequences.Values.SelectMany(members => members.Select(m => new Pair { LeftGroup = generatedGroup,
                    LeftIndex = m[i - 1], RightGroup = generatedGroup, RightIndex = m[i] })).ToArray();
                AddJoint(joints, pairs);
            }
            _joints = joints.OrderBy(j => j.Points["B"].X).ToArray();
        }
        private void AddJoint(List<Joint> target, Pair[] pairs)
        {
            var minimum = double.NegativeInfinity; var maximum = double.PositiveInfinity;
            var centers = new Dictionary<string, List<Point3d>>();
            foreach (var pair in pairs)
            {
                var left = _groups[pair.LeftGroup].Busbars[pair.LeftIndex];
                var right = _groups[pair.RightGroup].Busbars[pair.RightIndex];
                if (!HorizontalHead(left, false) || !HorizontalHead(right, true)) return;
                var end = left.PointList.Last(); var start = right.PointList.First();
                var leftInner = left.PointList[left.PointList.Count - 2]; var rightInner = right.PointList[1];
                if (leftInner.X >= end.X || rightInner.X <= start.X ||
                    Math.Abs(end.Y - start.Y) > left.BusBarThickness + right.BusBarThickness + .01 ||
                    Math.Abs(end.Z - start.Z) > .01 || Math.Abs(left.BusBarWidth - right.BusBarWidth) > .01) return;
                // 保持已有搭接关系及厚度错层；不扩展到最近折弯的半径/搭接保留区。
                var margin = Math.Max(_request.MinimumSegmentLength,
                    Math.Max(left.BarBendingRadius, right.BarBendingRadius) + Math.Max(left.EndLapWidth, right.StartLapWidth));
                minimum = Math.Max(minimum, leftInner.X + margin - end.X);
                maximum = Math.Min(maximum, rightInner.X - margin - start.X);
                var phase = Phase(left.GroupKey);
                if (!centers.ContainsKey(phase)) centers.Add(phase, new List<Point3d>());
                centers[phase].Add(end.Copy());
            }
            if (minimum > -.1 || maximum < .1 || centers.Count != 3) return;
            var points = centers.ToDictionary(p => p.Key, p => new Point3d(p.Value.Average(v => v.X),
                p.Value.Average(v => v.Y), p.Value.Average(v => v.Z)));
            // 三相及各叠排必须有共同制造截面；不同拓扑的转角只读。
            if (points.Values.Max(p => p.X) - points.Values.Min(p => p.X) > .01) return;
            target.Add(new Joint { Id = "wall-joint-" + target.Count, Pairs = pairs, Points = points,
                MinimumDelta = minimum, MaximumDelta = maximum });
        }
        private bool HorizontalHead(PlanarBendBusbarSetting shape, bool first)
        {
            if (shape.IsMilledBar || shape.PointList.Count < 2 || shape.WidthVector == null ||
                Math.Abs(shape.WidthVector.Normalized.Z) < .999999) return false;
            var a = shape.PointList[first ? 0 : shape.PointList.Count - 2];
            var b = shape.PointList[first ? 1 : shape.PointList.Count - 1];
            return Math.Abs(a.X - b.X) > .01 && Math.Abs(a.Y - b.Y) < .01 && Math.Abs(a.Z - b.Z) < .01;
        }
        internal IReadOnlyList<AlgorithmBusbarGenerationGroup> Apply(IReadOnlyList<BridgeDesignObject> edits)
        {
            var result = CopyGroups(_groups);
            if (edits == null) return result;
            foreach (var joint in _joints)
            {
                var values = new[] { "A", "B", "C" }.Select(p => edits.Single(o => o.Id == joint.Id + "-" + p).X - joint.Points[p].X).ToArray();
                var delta = values[1];
                if (joint.RequiresPlanning)
                {
                    if (values.Any(v => Math.Abs(v) > .01))
                        throw new InvalidOperationException("特殊段入口须通过修改特殊段桥向跨度刷新方案。");
                    continue;
                }
                if (values.Any(v => Math.Abs(v - delta) > .01) || delta < joint.MinimumDelta - .001 || delta > joint.MaximumDelta + .001)
                    throw new InvalidOperationException("制造断点只能三相联动移动，并须保留原搭接及折弯直头长度。");
                foreach (var pair in joint.Pairs)
                {
                    var left = result[pair.LeftGroup].Busbars[pair.LeftIndex];
                    var right = result[pair.RightGroup].Busbars[pair.RightIndex];
                    left.PointList.Last().X += delta; right.PointList.First().X += delta;
                }
            }
            // 相邻断点可同时移动，不能只按各自基线范围判断而把中间直排压短或翻转。
            for (var groupIndex = 0; groupIndex < result.Count; groupIndex++)
                for (var barIndex = 0; barIndex < result[groupIndex].Busbars.Count; barIndex++)
                {
                    var original = _groups[groupIndex].Busbars[barIndex];
                    var current = result[groupIndex].Busbars[barIndex];
                    for (var i = 1; i < current.PointList.Count; i++)
                    {
                        var before = new Vector3d(original.PointList[i - 1], original.PointList[i]);
                        var after = new Vector3d(current.PointList[i - 1], current.PointList[i]);
                        if (current.PointList[i].DistanceTo(original.PointList[i]) < .001 &&
                            current.PointList[i - 1].DistanceTo(original.PointList[i - 1]) < .001) continue;
                        var minimum = Math.Min(before.Norm, Math.Max(_request.MinimumSegmentLength,
                            current.BarBendingRadius + Math.Max(current.StartLapWidth, current.EndLapWidth)));
                        if (after.Norm < minimum - .001 || before.Dot(after) <= 0)
                            throw new InvalidOperationException("相邻制造断点之间的直排不足或方向反转，请增大间距。");
                    }
                }
            return result;
        }
        internal int? CopperSectionIndex(int groupIndex, int barIndex)
        {
            if (_groups[groupIndex].DisplayName == "柜端连接段") return null;
            var offset = _groups.Any(g => g.DisplayName == "柜端连接段") ? 1 : 0;
            var end = Array.FindIndex(_joints, j => j.Pairs.Any(p => p.LeftGroup == groupIndex && p.LeftIndex == barIndex));
            var start = Array.FindIndex(_joints, j => j.Pairs.Any(p => p.RightGroup == groupIndex && p.RightIndex == barIndex));
            var index = end >= 0 ? end + 1 - offset : start >= 0 ? start + 2 - offset : 0;
            // 未形成可编辑搭接的Worker分件仍属于同一特殊段，不能从选择对象中消失。
            if (index <= 0 && _groups[groupIndex].DisplayName == "算法中间段" &&
                _generatedPredecessors.TryGetValue(barIndex, out var previous))
                return CopperSectionIndex(groupIndex, previous);
            return index > 0 ? (int?)index : null;
        }

        internal void AddControls(List<BridgeDesignObject> objects, List<BridgeDesignRoute> routes, IReadOnlyList<BridgeDesignObject> edits)
        {
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var ids = new List<string>();
                var group = _request.Groups.Single(g => g.Phase == phase);
                // 柜端L排保留实际实体和编辑断点，但不作为桥内段排；第一道搭接即桥内起点。
                if (!_groups.Any(g => g.DisplayName == "柜端连接段"))
                    AddControl(objects, ids, "wall-edit-start-" + phase, phase, group.StartTerminal.RouteAnchor, null, false);
                var number = 0;
                foreach (var joint in _joints)
                {
                    var p = joint.Points[phase]; var id = joint.Id + "-" + phase;
                    var old = edits?.Single(o => o.Id == id);
                    var baseJoint = IsBaseJoint(joint);
                    var name = baseJoint
                        ? "基座L排断点" : "断点" + (++number);
                    AddControl(objects, ids, id, phase, new LinkageRouteWorkerCoordinateDto { X = old?.X ?? p.X, Y = p.Y, Z = p.Z },
                        joint.Id, true, joint.RequiresPlanning, name, baseJoint);
                }
                AddControl(objects, ids, "wall-edit-end-" + phase, phase, group.EndTerminal.RouteAnchor, null, false);
                routes.Add(new BridgeDesignRoute("wall-edit-" + phase, phase, ids));
            }
        }
        private void AddControl(List<BridgeDesignObject> objects, List<string> ids, string id, string phase,
            LinkageRouteWorkerCoordinateDto point, string jointId, bool editable, bool requiresPlanning = false, string name = null, bool baseJoint = false)
        {
            var node = new BridgeDesignObject(id, name ?? (editable ? "制造断点" : "固定接口"), editable && !baseJoint ? DesignObjectKind.Breakpoint : DesignObjectKind.FixedJoint,
                new Dictionary<DesignField, double> { [DesignField.X] = point.X, [DesignField.Y] = point.Z, [DesignField.Z] = point.Y },
                editable ? new[] { DesignField.X } : Array.Empty<DesignField>(),
                baseJoint ? "基座L排断点；距基座边距离不得小于搭接长度+200mm" :
                requiresPlanning ? "特殊段重算入口" : "Worker已有搭接截面；ABC联动", phase: phase);
            objects.Add(node.WithJointGroup(jointId ?? id.Replace("-" + phase, ""))); ids.Add(id);
        }
        private bool IsBaseJoint(Joint joint) =>
            joint.Pairs.Any(pair => _groups[pair.LeftGroup].DisplayName == "柜端连接段");

        internal FaceToWallCopperDraft InitializeBaseJointsAtDefaultDistance(
            IReadOnlyList<BridgeDesignObject> fixedObjects, double overlapLength, double installationSeamX)
        {
            if (double.IsNaN(overlapLength) || double.IsInfinity(overlapLength) || overlapLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(overlapLength), "搭接长度必须为有限正数。");
            var constraints = BaseJointConstraints(fixedObjects, overlapLength, installationSeamX);
            if (constraints.Count == 0) return this;
            var controls = new List<BridgeDesignObject>();
            AddControls(controls, new List<BridgeDesignRoute>(), null);
            var aligned = controls.Select(node =>
            {
                var constraint = constraints.SingleOrDefault(c => c.JointGroupId == node.JointGroupId);
                if (constraint == null) return node;
                var values = node.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = constraint.BoundaryX + constraint.OutwardSign *
                    (overlapLength + BusbarBridgeFrameJointClearance.MinimumLapDistanceMm);
                return node.WithValues(values);
            }).ToArray();
            // 断点表示搭接后截面，额外让出搭接长度以保留200mm净距；保存恢复不重新初始化。
            return new FaceToWallCopperDraft(Apply(aligned), _request, _solution) { SpecialStartX = SpecialStartX };
        }

        internal IReadOnlyList<BridgeBaseJointConstraint> BaseJointConstraints(
            IReadOnlyList<BridgeDesignObject> fixedObjects, double overlapLength, double installationSeamX)
        {
            if (double.IsNaN(installationSeamX) || double.IsInfinity(installationSeamX))
                throw new ArgumentOutOfRangeException(nameof(installationSeamX), "基座实际安装接缝坐标必须为有限值。");
            var constraints = new List<BridgeBaseJointConstraint>();
            foreach (var joint in _joints.Where(IsBaseJoint))
            {
                // wall-base 只核验归属；安装接缝统一取实际安装坐标系，不从名义中心和半长反算。
                var owners = fixedObjects.Where(o => o.Id == "wall-base" && o.Kind == DesignObjectKind.Base).ToArray();
                if (owners.Length != 1) throw new InvalidOperationException("面对墙L排断点缺少唯一的普通基座归属，不能确定距基座边距离。");
                var owner = owners[0];
                var x = joint.Points["B"].X;
                var boundary = installationSeamX;
                // 基座与首节基架的接缝固定，必须由铜排断点保留整段搭接和200mm净距。
                var minimum = boundary + overlapLength + BusbarBridgeFrameJointClearance.MinimumLapDistanceMm;
                if (joint.RequiresPlanning)
                {
                    // 直连特殊段的L排断点须整案重规划；范围来自固定接缝、真实直尾及展开长度，
                    // 不能沿用“只拉伸缓存实体”的零位移区间。
                    var bars = joint.Pairs.Select(p => _groups[p.LeftGroup].Busbars[p.LeftIndex]).ToArray();
                    var planner = new FaceToWallPreviewPlanning();
                    minimum = Math.Max(minimum, bars.Max(b => planner.LastStraightStart(b).X +
                        Math.Max(b.EndLapWidth, b.BusBarWidth) + b.BendAllowance + b.BarBendingRadius));
                    var maximum = Math.Min(_request.Groups.Min(g => g.EndTerminal.RouteAnchor.X) - .00001d,
                        bars.Min(b => b.PointList.Last().X + 3000d - b.GetDevelopedLength()));
                    constraints.Add(new BridgeBaseJointConstraint(joint.Id, owner.Id, boundary, 1d, minimum, maximum));
                    continue;
                }
                constraints.Add(new BridgeBaseJointConstraint(joint.Id, owner.Id,
                    boundary, 1d, Math.Max(x + joint.MinimumDelta, minimum), x + joint.MaximumDelta));
            }
            return constraints.AsReadOnly();
        }
        private IReadOnlyList<AlgorithmBusbarGenerationGroup> CopyGroups(IReadOnlyList<AlgorithmBusbarGenerationGroup> source) =>
            Array.AsReadOnly(source.Select(g => new AlgorithmBusbarGenerationGroup { DisplayName = g.DisplayName, IsBridge = g.IsBridge,
                RestrictSourceReuseToCurrentGroup = g.RestrictSourceReuseToCurrentGroup,
                Busbars = Array.AsReadOnly(g.Busbars.Select(b => BridgeNativePreviewCopy.Copy(b)).ToArray()) }).ToArray());
        internal static string Phase(string key) => new[] { "A", "B", "C" }.FirstOrDefault(p =>
            string.Equals(key, p, StringComparison.OrdinalIgnoreCase) || key?.StartsWith(p + "-", StringComparison.OrdinalIgnoreCase) == true ||
            key?.EndsWith("-" + p, StringComparison.OrdinalIgnoreCase) == true) ?? throw new InvalidOperationException("加工排相别身份无效。");
    }
}
