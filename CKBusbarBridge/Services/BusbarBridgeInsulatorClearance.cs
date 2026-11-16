using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BusbarBridgeFrameJointClearance
    {
        private const double Tolerance = .1;
        internal const double MinimumLapDistanceMm = 200d;
        private const double ClearanceTolerance = .000001;
        private bool Ordinary(BridgeDesignObject frame) => frame.FrameTemplateKey == "Regular" ||
            frame.FrameTemplateKey == "SideToSidePhaseChange029" || BridgeOffsetCopperChange.IsWidenedTerminal(frame);

        private bool SmallOffsetLayout(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> frames)
        {
            // 删除过渡端头后仍沿用原规划的结构身份，不能把剩余常规架重新套入特殊接缝门禁。
            var templates = frames.Concat(sample.FrameLengthBaseline).Select(frame => frame.FrameTemplateKey);
            return templates.Contains("SmallOffset") && !templates.Contains("LargeOffsetCorner");
        }

        private bool RequiresLapClearance(bool smallOffset, BridgeDesignObject left, BridgeDesignObject right) =>
            !smallOffset || BridgeOffsetCopperChange.IsMember(left) || BridgeOffsetCopperChange.IsMember(right);

        private double Project(BridgeDesignObject frame, double x, double y) =>
            x * Math.Cos(frame.Direction * Math.PI / 180) + y * Math.Sin(frame.Direction * Math.PI / 180);

        private double[][] Intervals(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, BridgeDesignObject frame)
        {
            var angle = frame.Direction * Math.PI / 180; var c = Math.Cos(angle); var s = Math.Sin(angle);
            var across = -frame.X * s + frame.Y * c;
            return Intervals(sample, objects, c, s, across, frame.Width);
        }

        private double[][] Intervals(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects,
            double c, double s, double across, double width)
        {
            // 接缝到实际搭接边界至少200mm；桥头/折弯包络仅供绝缘子使用。
            return LapEnvelopes(sample, objects)
                .Where(h => h.Length > 0 && h.Max(p => -p[0] * s + p[1] * c) >= across - width / 2 &&
                    h.Min(p => -p[0] * s + p[1] * c) <= across + width / 2)
                .Select(h => new[] { h.Min(p => p[0] * c + p[1] * s) - MinimumLapDistanceMm,
                    h.Max(p => p[0] * c + p[1] * s) + MinimumLapDistanceMm }).OrderBy(p => p[0]).ToArray();
        }

        internal double EarlierLapClearSeam(BridgeDesignSample copper, double seam, double across, double width)
        {
            var intervals = Intervals(copper, copper.Objects, 1.0, 0.0, across, width);
            // 依次退出重叠的搭接禁区，选取最接近原接缝的合法柜侧位置。
            for (var index = intervals.Length - 1; index >= 0; index--)
                if (Inside(seam, intervals[index])) seam = intervals[index][0];
            return seam;
        }

        internal IReadOnlyList<double[][]> LapEnvelopes(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> objects)
        {
            var geometry = new BusbarBridgeDraftPreviewSceneFactory();
            var result = new List<double[][]>();
            var routes = sample.Routes.Where(r => r.IsPhysicalGeometry).ToArray();
            var nodes = objects.ToLookup(o => o.Id, StringComparer.Ordinal);
            var paths = routes.Select(r => r.PointIds.Select(id =>
            {
                var matches = nodes[id];
                var node = matches.FirstOrDefault();
                if (node == null) throw new InvalidOperationException("铜排路径缺少节点：" + id + "；路径：" + r.Id);
                if (matches.Skip(1).Any()) throw new InvalidOperationException("铜排路径节点重复：" + id + "；路径：" + r.Id);
                return geometry.WorldPosition(node, objects);
            }).ToArray()).ToArray();
            for (var first = 0; first < routes.Length; first++)
            for (var second = first + 1; second < routes.Length; second++)
            {
                if (routes[first].Phase != routes[second].Phase) continue;
                foreach (var firstEnd in new[] { 0, paths[first].Length - 1 })
                foreach (var secondEnd in new[] { 0, paths[second].Length - 1 })
                {
                    if (paths[first].Length < 2 || paths[second].Length < 2) continue;
                    var a = paths[first][firstEnd];
                    var b = paths[second][secondEnd];
                    var ai = paths[first][firstEnd == 0 ? 1 : firstEnd - 1];
                    var bi = paths[second][secondEnd == 0 ? 1 : secondEnd - 1];
                    var ax = ai[0] - a[0]; var ay = ai[1] - a[1];
                    var bx = bi[0] - b[0]; var by = bi[1] - b[1];
                    var al = Math.Sqrt(ax * ax + ay * ay); var bl = Math.Sqrt(bx * bx + by * by);
                    if (al <= .001 || bl <= .001) continue;
                    ax /= al; ay /= al; bx /= bl; by /= bl;
                    // 对接的两根端部直段朝各自排内的方向相反；叠排同端不构成搭接。
                    if (ax * bx + ay * by > -1d + .000001) continue;
                    var across = -(b[0] - a[0]) * ay + (b[1] - a[1]) * ax;
                    if (Math.Abs(across) > .01) continue;
                    var along = (b[0] - a[0]) * ax + (b[1] - a[1]) * ay;
                    var start = Math.Max(0d, along - bl); var end = Math.Min(al, along);
                    if (end - start <= .001) continue;
                    var lapLength = sample.Parameters.OverlapLengthMm;
                    if (Math.Abs(end - start - lapLength) > .01) continue;
                    var halfWidth = Math.Max(routes[first].SectionWidth, routes[second].SectionWidth) / 2d;
                    result.Add(new[] {
                        new[] { a[0] + ax * start - ay * halfWidth, a[1] + ay * start + ax * halfWidth, a[2] },
                        new[] { a[0] + ax * start + ay * halfWidth, a[1] + ay * start - ax * halfWidth, a[2] },
                        new[] { a[0] + ax * end - ay * halfWidth, a[1] + ay * end + ax * halfWidth, a[2] },
                        new[] { a[0] + ax * end + ay * halfWidth, a[1] + ay * end - ax * halfWidth, a[2] } });
                }
            }
            // 逻辑路径的接缝有显式搭接中心，按独立搭接长度生成禁区。
            if (routes.Length == 0)
            foreach (var node in objects.Where(o => o.Kind == DesignObjectKind.Lap ||
                o.Kind == DesignObjectKind.Breakpoint || o.Kind == DesignObjectKind.FixedJoint))
            {
                var p = geometry.WorldPosition(node, objects);
                var halfLap = sample.Parameters.OverlapLengthMm / 2d;
                var halfWidth = sample.Source.BusbarWidth / 2d;
                result.Add(new[] { new[] { p[0] - halfLap, p[1] - halfWidth, p[2] },
                    new[] { p[0] + halfLap, p[1] + halfWidth, p[2] } });
            }
            return result;
        }

        internal BridgeInsulatorPlacement Plan(BridgeDesignSample sample, ISet<string> fixedFrames = null)
        {
            var whole = PlanCore(sample, fixedFrames, true);
            // 整毫米是制造优先项；不能因此放弃本来满足安全约束的小数方案。
            return whole.Issues.Count == 0 ? whole : PlanCore(sample, fixedFrames, false);
        }

        private BridgeInsulatorPlacement PlanCore(BridgeDesignSample sample, ISet<string> fixedFrames, bool preferWhole)
        {
            var objects = sample.Objects.ToArray();
            ExtendLargeOffsetGroups(sample, objects, fixedFrames, preferWhole);
            objects = ExtendSpecialFrames(sample, objects, fixedFrames, preferWhole);
            var frames = BusbarBridgeDraftEditor.FramesInInstallationOrder(objects.Where(o => o.Kind == DesignObjectKind.Frame));
            var smallOffset = SmallOffsetLayout(sample, frames);
            for (var first = 0; first < frames.Length; first++)
            {
                if (!Ordinary(frames[first])) continue;
                var last = first;
                while (last + 1 < frames.Length && Ordinary(frames[last + 1]) &&
                    Math.Abs(frames[last + 1].Direction - frames[first].Direction) < .01 &&
                    Math.Abs(-(frames[last + 1].X - frames[first].X) * Math.Sin(frames[first].Direction * Math.PI / 180) +
                        (frames[last + 1].Y - frames[first].Y) * Math.Cos(frames[first].Direction * Math.PI / 180)) < Tolerance &&
                    Math.Abs(Project(frames[first], frames[last + 1].X, frames[last + 1].Y) - frames[last + 1].Length / 2 -
                        Project(frames[first], frames[last].X, frames[last].Y) - frames[last].Length / 2) < Tolerance)
                    last++;
                if (last > first)
                {
                    var run = frames.Skip(first).Take(last - first + 1).ToArray();
                    // 小偏移的常规包络架及左右过渡架不承担特殊搭接安全区间。
                    // 真正包络偏移排的小偏移架仍由上方ExtendSpecialFrames保持两侧净距。
                    var forbidden = smallOffset ? Array.Empty<double[]>() : Intervals(sample, objects, run[0]);
                    double[] boundaries = null;
                    if (preferWhole)
                    {
                        // 尾差集中在一节可调架；换补偿节再试，避免被锁长或人工固定边界挡住。
                        foreach (var tail in Enumerable.Range(0, run.Length).Reverse().Where(i => !LengthFixed(sample, run[i], fixedFrames)))
                        {
                            boundaries = PlanBoundaries(sample, run, forbidden, fixedFrames, true, tail);
                            if (boundaries != null) break;
                        }
                    }
                    boundaries = boundaries ?? PlanBoundaries(sample, run, forbidden, fixedFrames, false, -1);
                    if (boundaries != null)
                        for (var i = 0; i < run.Length; i++)
                            ResizeFrame(objects, run[i], boundaries[i + 1] - boundaries[i],
                                (boundaries[i] + boundaries[i + 1]) / 2 - Project(run[i], run[i].X, run[i].Y));
                }
                first = last;
            }
            return new BridgeInsulatorPlacement(objects, Validate(sample, objects));
        }

        private double[] PlanBoundaries(BridgeDesignSample sample, BridgeDesignObject[] run, double[][] forbidden,
            ISet<string> fixedFrames, bool whole, int tail)
        {
            var start = Project(run[0], run[0].X, run[0].Y) - run[0].Length / 2;
            var end = Project(run[0], run.Last().X, run.Last().Y) + run.Last().Length / 2;
            var fixedLengths = run.Select(f => LengthFixed(sample, f, fixedFrames)).ToArray();
            var minimum = run.Select((f, i) => fixedLengths[i] ? f.Length : 200d).ToArray();
            var maximum = run.Select((f, i) => fixedLengths[i] ? f.Length : 1400d).ToArray();
            var available = end - start - run.Where((f, i) => fixedLengths[i]).Sum(f => f.Length);
            var fraction = available - Math.Floor(available + 1e-8);
            if (Math.Abs(fraction) < 1e-8 || Math.Abs(1 - fraction) < 1e-8) fraction = 0;
            var phase = Enumerable.Repeat(start, run.Length + 1).ToArray();
            var step = whole ? 1d : .2d;
            for (var i = 0; i < run.Length; i++)
            {
                if (!whole) continue;
                var remainder = fixedLengths[i] ? run[i].Length : i == tail ? fraction : 0d;
                phase[i + 1] = phase[i] + remainder;
                if (fixedLengths[i]) continue;
                minimum[i] = remainder + Math.Ceiling(minimum[i] - remainder - 1e-8);
                maximum[i] = remainder + Math.Floor(maximum[i] - remainder + 1e-8);
            }
            var prefix = new double[run.Length + 1];
            for (var i = 0; i < run.Length; i++) prefix[i + 1] = prefix[i] + minimum[i];
            var reachable = new List<double[]>[run.Length + 1];
            reachable[run.Length] = new List<double[]> { new[] { end, end } };
            for (var i = run.Length - 1; i > 0; i--)
            {
                var ranges = Subtract(reachable[i + 1].Select(p => new[] {
                    Math.Max(start + prefix[i], p[0] - maximum[i]),
                    Math.Min(end - (prefix[run.Length] - prefix[i]), p[1] - minimum[i]) }), forbidden);
                reachable[i] = whole ? OnGrid(ranges, phase[i], step).ToList() : ranges;
                if (fixedFrames != null && (fixedFrames.Contains(run[i - 1].Id) || fixedFrames.Contains(run[i].Id)))
                {
                    var boundary = Project(run[0], run[i - 1].X, run[i - 1].Y) + run[i - 1].Length / 2;
                    reachable[i] = reachable[i].Where(p => boundary >= p[0] - 1e-8 && boundary <= p[1] + 1e-8 &&
                        (!whole || Math.Abs(boundary - phase[i] - Math.Round(boundary - phase[i])) < 1e-8))
                        .Take(1).Select(p => new[] { boundary, boundary }).ToList();
                }
            }
            var boundaries = new List<double> { start };
            for (var i = 1; i < run.Length; i++)
            {
                var previous = boundaries.Last();
                var target = Project(run[0], run[i - 1].X, run[i - 1].Y) + run[i - 1].Length / 2;
                var candidates = OnGrid(reachable[i].Select(p => new[] { Math.Max(p[0], previous + minimum[i - 1]),
                    Math.Min(p[1], previous + maximum[i - 1]) }), phase[i], step)
                    .Select(p => Math.Max(p[0], Math.Min(p[1], phase[i] + Math.Round((target - phase[i]) / step) * step)))
                    .OrderBy(p => Math.Abs(p - target)).ThenBy(p => p).ToArray();
                if (candidates.Length == 0) return null;
                // 中心坐标保留几何精度；只量化长度，不能把绝对接缝坐标再取一位小数。
                boundaries.Add(candidates[0]);
            }
            var lastLength = end - boundaries.Last();
            if (lastLength > maximum[run.Length - 1] + .000001 || lastLength < minimum[run.Length - 1] - .000001) return null;
            boundaries.Add(end);
            return boundaries.ToArray();
        }

        private IEnumerable<double[]> OnGrid(IEnumerable<double[]> ranges, double origin, double step) =>
            ranges.Where(p => p[0] <= p[1] + 1e-8).Select(p => new[] {
                origin + Math.Ceiling((p[0] - origin) / step - 1e-8) * step,
                origin + Math.Floor((p[1] - origin) / step + 1e-8) * step }).Where(p => p[0] <= p[1] + 1e-8);

        internal BridgeDesignSample PreferWholeCopperLengths(BridgeDesignSample sample)
        {
            var route = sample.CopperEditRoutes.OrderBy(r => r.Phase == "B" ? 0 : 1).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
            if (route == null || sample.ReplanCopper == null) return sample;
            var count = FractionalCopperSections(sample, route, sample.Objects);
            if (count == 0) return sample;
            var prefix = FractionalCopperSections(sample, route, sample.Objects, true);
            var minimum = MinimumFractionalCopperSections(sample, route);
            if (count <= minimum && prefix == 0) return sample;
            var best = sample;
            foreach (var proposed in WholeCopperCandidates(sample, route))
            {
                var proposedCount = FractionalCopperSections(sample, route, proposed);
                if (proposedCount > count || proposedCount == count &&
                    FractionalCopperSections(sample, route, proposed, true) >= prefix) continue;
                BridgeDesignSample geometry;
                try
                {
                    BridgeOffsetCopperChange.Create(sample, proposed);
                    geometry = sample.ReplanCopper(proposed);
                    if (geometry == null) continue;
                    if (geometry.Issues.Any(i => i.BlocksGeneration && i.Source != "基架连接避让")) continue;
                    geometry = geometry.WithFrameLengthEditing(sample.SavedFrameLengthEditing).WithSavedLocks(sample.SavedLockedObjectIds)
                        .WithCopperLengthBaseline(sample.CopperLengthBaseline);
                    if (sample.Coordinates != null) geometry = geometry.WithCoordinates(sample.Coordinates, sample.ScenarioKey);
                    var frames = Plan(geometry);
                    if (frames.Issues.Count > 0) continue;
                    geometry = geometry.WithGeometry(frames.Objects, geometry.Routes);
                    if (!PreservesFrameConstraints(sample, geometry, null) || !PreservesOffsetCoverage(sample, geometry) ||
                        new FaceToWallSupportValidation().Validate(geometry, geometry.Objects).Any(i => i.BlocksGeneration)) continue;
                }
                // 取整是可选优化；非法候选由既有工艺规划器拒绝，继续尝试另一侧或原小数值。
                catch (ArgumentException) { continue; }
                catch (InvalidOperationException) { continue; }
                var candidateCount = FractionalCopperSections(geometry, route, geometry.Objects);
                var candidatePrefix = FractionalCopperSections(geometry, route, geometry.Objects, true);
                if (candidateCount > count || candidateCount == count && candidatePrefix >= prefix) continue;
                best = geometry; count = candidateCount; prefix = candidatePrefix;
                if (count <= minimum && prefix == 0) break;
            }
            return best;
        }

        private bool AdjustableCopperGroup(BridgeDesignSample sample, BridgeDesignObject node) => node.IsJointGroup &&
            sample.Objects.Where(o => o.JointGroupId == node.JointGroupId).All(o =>
                (o.Kind == DesignObjectKind.Breakpoint || o.Kind == DesignObjectKind.Lap) &&
                o.EditableFields.Contains(DesignField.X) && !sample.SavedLockedObjectIds.Contains(o.Id));

        private int FractionalCopperSections(BridgeDesignSample sample, BridgeDesignRoute route,
            IReadOnlyList<BridgeDesignObject> objects, bool excludeLast = false)
        {
            var nodes = objects.ToDictionary(o => o.Id);
            return Enumerable.Range(1, Math.Max(0, route.PointIds.Count - (excludeLast ? 2 : 1))).Count(i =>
            {
                var length = Math.Abs(nodes[route.PointIds[i]].X - nodes[route.PointIds[i - 1]].X) + sample.Parameters.OverlapLengthMm;
                return Math.Abs(length - Math.Round(length)) > .000001;
            });
        }

        private int MinimumFractionalCopperSections(BridgeDesignSample sample, BridgeDesignRoute route)
        {
            var nodes = route.PointIds.Select(id => sample.Objects.Single(o => o.Id == id)).ToArray();
            var first = 0; var minimum = 0;
            for (var i = 1; i < nodes.Length; i++)
            {
                if (i < nodes.Length - 1 && AdjustableCopperGroup(sample, nodes[i])) continue;
                var sum = nodes[i].X - nodes[first].X + (i - first) * sample.Parameters.OverlapLengthMm;
                if (Math.Abs(sum - Math.Round(sum)) > .000001) minimum++;
                first = i;
            }
            return minimum;
        }

        private IEnumerable<BridgeDesignObject[]> WholeCopperCandidates(BridgeDesignSample sample, BridgeDesignRoute route)
        {
            var nodes = route.PointIds.Select(id => sample.Objects.Single(o => o.Id == id)).ToArray();
            var candidates = new List<BridgeDesignObject[]> { sample.Objects.ToArray() };
            var anchor = nodes[0].X; var first = 0;
            for (var i = 1; i < nodes.Length - 1; i++)
            {
                var node = nodes[i];
                if (!AdjustableCopperGroup(sample, node)) { anchor = node.X; first = i; continue; }
                var origin = anchor - (i - first) * sample.Parameters.OverlapLengthMm;
                var values = new[] { origin + Math.Floor(node.X - origin + 1e-8), origin + Math.Ceiling(node.X - origin - 1e-8), node.X }.Distinct();
                candidates = candidates.SelectMany(objects => values.Select(x => objects.Select(o => o.JointGroupId == node.JointGroupId
                    ? o.WithValues(o.Values.ToDictionary(p => p.Key, p => p.Key == DesignField.X ? p.Value + x - node.X : p.Value)) : o).ToArray()))
                    .OrderBy(objects => FractionalCopperSections(sample, route, objects))
                    .ThenBy(objects => FractionalCopperSections(sample, route, objects, true))
                    .ThenBy(objects => objects.Where(o => o.IsJointGroup).Sum(o => Math.Abs(o.X - sample.Objects.Single(old => old.Id == o.Id).X)))
                    .Take(64).ToList();
            }
            return candidates;
        }

        internal BridgeDesignSample PlanWithCopper(BridgeDesignSample sample, ISet<string> fixedFrames = null,
            ISet<string> fixedJoints = null, Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample> replan = null,
            bool retainSupports = true)
        {
            var first = Plan(sample, fixedFrames);
            var initial = sample.WithGeometry(first.Objects, sample.Routes);
            replan = replan ?? sample.ReplanCopper;
            if (first.Issues.Count == 0 || replan == null) return initial;
            var frontier = new[] { initial };
            var visited = new HashSet<string> { CopperKey(initial) };
            // 每轮移动一组三相断点；保留候选分支，允许两侧断点共同让开同一组特殊基架。
            var maximumPasses = Math.Min(12, sample.CopperEditRoutes.SelectMany(r => r.PointIds).Distinct().Count() * 2 + 2);
            for (var pass = 0; pass < maximumPasses; pass++)
            {
                var next = new List<BridgeDesignSample>();
                foreach (var current in frontier)
                foreach (var proposed in CopperCandidates(current, fixedJoints))
                {
                    var key = string.Join(";", proposed.Where(o => o.IsJointGroup).OrderBy(o => o.Id, StringComparer.Ordinal)
                        .Select(o => o.Id + ":" + o.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                    if (!visited.Add(key)) continue;
                    BridgeDesignSample geometry;
                    try
                    {
                        BridgeOffsetCopperChange.Create(sample, proposed);
                        geometry = replan(proposed);
                    }
                    // 工艺规划器以异常拒绝非法长度或顺序；候选未提交，继续搜索另一侧。
                    catch (ArgumentException) { continue; }
                    catch (InvalidOperationException) { continue; }
                    if (geometry == null) continue;
                    // 铜排重规划器可能同时生成一套初始支撑；避让候选必须从当前安装链继续，
                    // 不能借重新铺排绕过锁长、人工分节或原有特殊架安全范围。
                    if (retainSupports)
                        geometry = geometry.WithGeometry(geometry.Objects.Where(o => o.Kind != DesignObjectKind.Frame && o.Kind != DesignObjectKind.Insulator)
                            .Concat(sample.Objects.Where(o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator)), geometry.Routes);
                    var planned = Plan(geometry.WithSavedLocks(sample.SavedLockedObjectIds), fixedFrames);
                    geometry = geometry.WithGeometry(planned.Objects, geometry.Routes)
                        .WithFrameLengthEditing(sample.SavedFrameLengthEditing)
                        .WithSavedLocks(sample.SavedLockedObjectIds).WithCopperLengthBaseline(sample.CopperLengthBaseline);
                    if (sample.Coordinates != null) geometry = geometry.WithCoordinates(sample.Coordinates, sample.ScenarioKey);
                    if (!PreservesFrameConstraints(sample, geometry, fixedFrames)) continue;
                    if (planned.Issues.Count == 0 && PreservesOffsetCoverage(sample, geometry)) return geometry;
                    next.Add(geometry);
                }
                if (next.Count == 0) break;
                frontier = next.OrderBy(s => Validate(s, s.Objects).Count)
                    .ThenBy(s => CopperMovement(sample, s)).ThenBy(CopperKey, StringComparer.Ordinal).Take(16).ToArray();
            }
            return initial;
        }

        private bool PreservesOffsetCoverage(BridgeDesignSample before, BridgeDesignSample after)
        {
            var change = BridgeOffsetCopperChange.Create(before, after.Objects);
            if (change == null) return true;
            foreach (var frame in after.Objects.Where(o => change.FrameIds.Contains(o.Id)))
            {
                var span = frame.Value(frame.CornerPreview != null || frame.FrameTemplateKey == "SmallOffset" ? DesignField.Length : DesignField.Width);
                if (frame.X - span / 2 > change.StartX + .1 || frame.X + span / 2 < change.EndX - .1) return false;
            }
            return true;
        }

        private string CopperKey(BridgeDesignSample sample) => string.Join(";", sample.Objects.Where(o => o.IsJointGroup)
            .OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id + ":" + o.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        private double CopperMovement(BridgeDesignSample before, BridgeDesignSample after)
        {
            var positions = before.Objects.Where(o => o.IsJointGroup).ToDictionary(o => o.Id, o => o.X);
            return after.Objects.Where(o => positions.ContainsKey(o.Id)).Sum(o => Math.Abs(o.X - positions[o.Id]));
        }

        private bool PreservesFrameConstraints(BridgeDesignSample before, BridgeDesignSample after, ISet<string> fixedFrames)
        {
            var frames = after.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id);
            foreach (var old in before.Objects.Where(o => o.Kind == DesignObjectKind.Frame))
            {
                if (!frames.TryGetValue(old.Id, out var frame)) return false;
                if (LengthFixed(before, old, fixedFrames) && Math.Abs(frame.Length - old.Length) > .000001) return false;
                if (fixedFrames?.Contains(old.Id) == true && new[] { DesignField.X, DesignField.Y, DesignField.Z, DesignField.Direction }
                    .Any(f => Math.Abs(old.Value(f) - frame.Value(f)) > .000001)) return false;
                if (!frame.FrameTemplateKey.StartsWith("FaceToWallLarge", StringComparison.Ordinal) &&
                    (frame.Length < 200 - .01 || frame.Length > 1400 + .01)) return false;
            }
            return true;
        }

        private IEnumerable<BridgeDesignObject[]> CopperCandidates(BridgeDesignSample sample, ISet<string> fixedJoints)
        {
            var objects = sample.Objects;
            var groups = objects.Where(o => o.IsJointGroup).GroupBy(o => o.JointGroupId)
                .Where(g => g.All(o => (o.Kind == DesignObjectKind.Breakpoint || o.Kind == DesignObjectKind.Lap) &&
                    o.EditableFields.Contains(DesignField.X) && !sample.SavedLockedObjectIds.Contains(o.Id) &&
                    fixedJoints?.Contains(o.Id) != true)).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            var envelopes = LapEnvelopes(sample, objects);
            var frames = objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            var smallOffset = SmallOffsetLayout(sample, frames);
            foreach (var frame in frames)
            {
                var c = Math.Cos(frame.Direction * Math.PI / 180); var s = Math.Sin(frame.Direction * Math.PI / 180);
                if (Math.Abs(c) < .000001) continue;
                var end = Project(frame, frame.X, frame.Y) + frame.Length / 2;
                if (!frames.Any(other => other.Id != frame.Id && RequiresLapClearance(smallOffset, frame, other) &&
                    Math.Abs(other.Direction - frame.Direction) < .01 &&
                    Math.Abs(-(other.X - frame.X) * s + (other.Y - frame.Y) * c) <= (frame.Width + other.Width) / 2 &&
                    Math.Abs(Project(frame, other.X, other.Y) - other.Length / 2 - end) < Tolerance)) continue;
                foreach (var envelope in envelopes)
                {
                    var across = -frame.X * s + frame.Y * c;
                    if (envelope.Max(p => -p[0] * s + p[1] * c) < across - frame.Width / 2 ||
                        envelope.Min(p => -p[0] * s + p[1] * c) > across + frame.Width / 2) continue;
                    var interval = new[] { envelope.Min(p => p[0] * c + p[1] * s) - MinimumLapDistanceMm,
                        envelope.Max(p => p[0] * c + p[1] * s) + MinimumLapDistanceMm };
                    if (!Inside(end, interval)) continue;
                    foreach (var group in groups.Where(g => g.Any(o =>
                        o.X >= envelope.Min(p => p[0]) - .01 && o.X <= envelope.Max(p => p[0]) + .01 &&
                        o.Y >= envelope.Min(p => p[1]) - .01 && o.Y <= envelope.Max(p => p[1]) + .01)))
                    foreach (var boundary in interval)
                    {
                        var delta = (end - boundary) / c;
                        delta = Math.Sign(delta) * Math.Ceiling(Math.Abs(delta) * 10 - .000001) / 10;
                        var ids = new HashSet<string>(group.Select(o => o.Id));
                        foreach (var move in new[] { WholeCopperEscapeDelta(sample, group.ToArray(), delta), delta }.Distinct())
                            yield return objects.Select(o => ids.Contains(o.Id) ? o.WithValues(o.Values.ToDictionary(p => p.Key,
                                p => p.Key == DesignField.X ? p.Value + move : p.Value)) : o).ToArray();
                    }
                }
            }
        }

        private double WholeCopperEscapeDelta(BridgeDesignSample sample, BridgeDesignObject[] group, double delta)
        {
            var node = group.OrderBy(o => o.Phase == "B" ? 0 : 1).ThenBy(o => o.Id, StringComparer.Ordinal).First();
            var route = sample.CopperEditRoutes.FirstOrDefault(r => r.PointIds.Contains(node.Id));
            var index = route?.PointIds.ToList().IndexOf(node.Id) ?? -1;
            if (index <= 0) return delta;
            var previous = sample.Objects.Single(o => o.Id == route.PointIds[index - 1]);
            var length = node.X + delta - previous.X + sample.Parameters.OverlapLengthMm;
            // 朝退出禁区的方向取整，不能把200mm边界向内舍入。
            var whole = delta > 0 ? Math.Ceiling(length - 1e-8) : Math.Floor(length + 1e-8);
            return previous.X + whole - sample.Parameters.OverlapLengthMm - node.X;
        }

        private void ExtendLargeOffsetGroups(BridgeDesignSample sample, BridgeDesignObject[] objects, ISet<string> fixedFrames, bool preferWhole)
        {
            var frames = BusbarBridgeDraftEditor.FramesInInstallationOrder(objects.Where(o => o.Kind == DesignObjectKind.Frame));
            foreach (var inlet in frames.Where(o => o.CornerPreview?.ClosedEnd == 1))
            {
                var matches = frames.Where(o => o.CornerPreview?.ClosedEnd == -1 &&
                    Math.Abs(o.X - inlet.X) < Tolerance && Math.Abs(o.Length - inlet.Length) < Tolerance).ToArray();
                if (matches.Length != 1) continue;
                var outlet = matches[0];
                if (LengthFixed(sample, inlet, fixedFrames) || LengthFixed(sample, outlet, fixedFrames)) continue;
                var leftIndex = Array.FindIndex(frames, o => o.Id == inlet.Id);
                var rightIndex = Array.FindIndex(frames, o => o.Id == outlet.Id);
                if (leftIndex >= rightIndex) continue;
                var left = CompensationChain(frames, leftIndex, -1, fixedFrames);
                var right = CompensationChain(frames, rightIndex, 1, fixedFrames);
                if (left.Length == 0 || right.Length == 0) continue;
                var start = Project(inlet, inlet.X, inlet.Y) - inlet.Length / 2;
                var end = Project(outlet, outlet.X, outlet.Y) + outlet.Length / 2;
                var leftForbidden = Intervals(sample, objects, inlet);
                var rightForbidden = Intervals(sample, objects, outlet);
                var growth = Math.Max(RequiredGrowth(start, -1, leftForbidden), RequiredGrowth(end, 1, rightForbidden));
                if (preferWhole) growth = WholeGrowth(inlet.Length, growth, 2);
                for (var pass = 0; pass <= leftForbidden.Length + rightForbidden.Length; pass++)
                {
                    var extra = Math.Max(RequiredGrowth(start - growth, -1, leftForbidden), RequiredGrowth(end + growth, 1, rightForbidden));
                    if (extra <= ClearanceTolerance) break;
                    growth += extra;
                    if (preferWhole) growth = WholeGrowth(inlet.Length, growth, 2);
                }
                if (growth <= ClearanceTolerance || inlet.Length + 2 * growth > 1400 + .01 ||
                    growth > CompensationCapacity(sample, left) + .01 || growth > CompensationCapacity(sample, right) + .01) continue;
                // 两个028共享桥向跨度；连接架宽度随跨度变化，两侧普通链各补偿半个增量。
                var length = inlet.Length + 2 * growth;
                ResizeFrame(objects, inlet, length, 0); ResizeFrame(objects, outlet, length, 0);
                foreach (var member in frames.Skip(leftIndex + 1).Take(rightIndex - leftIndex - 1)
                    .Where(o => o.CornerPreview == null && o.Role.Contains("大偏移")))
                {
                    var values = member.Values.ToDictionary(p => p.Key, p => p.Value); values[DesignField.Width] = length;
                    objects[Array.FindIndex(objects, o => o.Id == member.Id)] = member.WithValues(values);
                }
                Compensate(sample, objects, left, growth, -1); Compensate(sample, objects, right, growth, 1);
            }
        }

        private BridgeDesignObject[] ExtendSpecialFrames(BridgeDesignSample sample, BridgeDesignObject[] objects, ISet<string> fixedFrames, bool preferWhole)
        {
            var frames = BusbarBridgeDraftEditor.FramesInInstallationOrder(objects.Where(o => o.Kind == DesignObjectKind.Frame));
            for (var i = 0; i < frames.Length; i++)
            {
                var special = frames[i];
                if (Ordinary(special) || special.CornerPreview != null || LengthFixed(sample, special, fixedFrames)) continue;
                var left = i > 0 ? frames[i - 1] : null;
                var right = i + 1 < frames.Length ? frames[i + 1] : null;
                var start = Project(special, special.X, special.Y) - special.Length / 2;
                var end = start + special.Length;
                var leftChain = CompensationChain(frames, i, -1, fixedFrames);
                var rightChain = CompensationChain(frames, i, 1, fixedFrames);
                var leftConnected = leftChain.Length > 0;
                var rightConnected = rightChain.Length > 0;
                var forbidden = Intervals(sample, objects, special)
                    .Concat(leftConnected ? Intervals(sample, objects, left) : Array.Empty<double[]>())
                    .Concat(rightConnected ? Intervals(sample, objects, right) : Array.Empty<double[]>()).ToArray();
                var leftGrowth = leftConnected ? RequiredGrowth(start, -1, forbidden) : 0;
                var rightGrowth = rightConnected ? RequiredGrowth(end, 1, forbidden) : 0;
                var centered = left != null && right != null;
                if (preferWhole)
                {
                    if (centered && leftConnected && rightConnected)
                        leftGrowth = rightGrowth = WholeGrowth(special.Length, Math.Max(leftGrowth, rightGrowth), 2);
                    else if (!centered && leftConnected) leftGrowth = WholeGrowth(special.Length, leftGrowth, 1);
                    else if (!centered && rightConnected) rightGrowth = WholeGrowth(special.Length, rightGrowth, 1);
                }
                if (leftGrowth < .01 && rightGrowth < .01) continue;
                // 中间特殊基架不平移中心；桥端特殊基架保留外端接口，只延长朝向普通基架的一端。
                if (centered)
                {
                    if (!leftConnected || !rightConnected) continue;
                    leftGrowth = rightGrowth = Math.Max(leftGrowth, rightGrowth);
                }
                for (var pass = 0; pass <= forbidden.Length; pass++)
                {
                    var leftExtra = leftConnected ? RequiredGrowth(start - leftGrowth, -1, forbidden) : 0;
                    var rightExtra = rightConnected ? RequiredGrowth(end + rightGrowth, 1, forbidden) : 0;
                    if (Math.Max(leftExtra, rightExtra) <= ClearanceTolerance) break;
                    leftGrowth += centered ? Math.Max(leftExtra, rightExtra) : leftExtra;
                    rightGrowth += centered ? Math.Max(leftExtra, rightExtra) : rightExtra;
                    if (!preferWhole) continue;
                    if (centered) leftGrowth = rightGrowth = WholeGrowth(special.Length, leftGrowth, 2);
                    else if (leftConnected) leftGrowth = WholeGrowth(special.Length, leftGrowth, 1);
                    else if (rightConnected) rightGrowth = WholeGrowth(special.Length, rightGrowth, 1);
                }
                if (leftGrowth > CompensationCapacity(sample, leftChain) + .01 ||
                    rightGrowth > CompensationCapacity(sample, rightChain) + .01) continue;
                var length = special.Length + leftGrowth + rightGrowth;
                // 面对墙大基架按实际特殊铜排包络驱动，没有普通基架的1400mm上限。
                if (!special.FrameTemplateKey.StartsWith("FaceToWallLarge", StringComparison.Ordinal) && length > 1400 + .01) continue;
                ResizeFrame(objects, special, length, (rightGrowth - leftGrowth) / 2);
                Compensate(sample, objects, leftChain, leftGrowth, -1);
                Compensate(sample, objects, rightChain, rightGrowth, 1);
                frames = frames.Select(f => objects.Single(o => o.Id == f.Id)).ToArray();
            }
            return objects;
        }

        private double WholeGrowth(double length, double growth, int ends)
        {
            var candidate = Math.Max(0, (Math.Ceiling(length + ends * growth - 1e-8) - length) / ends);
            // 对称加长的半增量也必须可按0.1mm补偿，否则会在固定中心与相邻架之间引入缺口。
            return Math.Abs(candidate - BridgeDesignPrecision.OneDecimal(candidate)) < 1e-8 ? candidate : growth;
        }

        private BridgeDesignObject[] CompensationChain(BridgeDesignObject[] frames, int index, int direction, ISet<string> fixedFrames)
        {
            var chain = new List<BridgeDesignObject>();
            var previous = frames[index];
            for (var i = index + direction; i >= 0 && i < frames.Length; i += direction)
            {
                var joint = Project(previous, previous.X, previous.Y) + direction * previous.Length / 2;
                if (fixedFrames?.Contains(frames[i].Id) == true || !CanCompensate(previous, frames[i], joint, direction < 0)) break;
                chain.Add(frames[i]); previous = frames[i];
            }
            return chain.ToArray();
        }

        private double CompensationCapacity(BridgeDesignSample sample, IEnumerable<BridgeDesignObject> frames) =>
            frames.Where(f => !LengthFixed(sample, f, null)).Sum(f => Math.Max(0, f.Length - 200));

        private bool LengthFixed(BridgeDesignSample sample, BridgeDesignObject frame, ISet<string> fixedFrames) =>
            !frame.EditableFields.Contains(DesignField.Length) || sample.SavedLockedObjectIds.Contains(frame.Id) ||
            fixedFrames?.Contains(frame.Id) == true;

        private void Compensate(BridgeDesignSample sample, BridgeDesignObject[] objects,
            IEnumerable<BridgeDesignObject> frames, double growth, int direction)
        {
            foreach (var frame in frames)
            {
                if (growth <= ClearanceTolerance) break;
                var reduction = LengthFixed(sample, frame, null) ? 0 : Math.Min(growth, Math.Max(0, frame.Length - 200));
                ResizeFrame(objects, frame, frame.Length - reduction, direction * (growth - reduction / 2));
                growth -= reduction;
            }
        }

        private bool CanCompensate(BridgeDesignObject special, BridgeDesignObject neighbor, double joint, bool left)
        {
            if (neighbor == null || !Ordinary(neighbor) || Math.Abs(neighbor.Direction - special.Direction) > .01) return false;
            var across = -(neighbor.X - special.X) * Math.Sin(special.Direction * Math.PI / 180) +
                (neighbor.Y - special.Y) * Math.Cos(special.Direction * Math.PI / 180);
            return Math.Abs(across) <= (special.Width + neighbor.Width) / 2 &&
                Math.Abs(Project(special, neighbor.X, neighbor.Y) + (left ? 1 : -1) * neighbor.Length / 2 - joint) < Tolerance;
        }

        private double RequiredGrowth(double joint, int direction, double[][] forbidden)
        {
            var target = joint;
            for (var pass = 0; pass <= forbidden.Length; pass++)
            {
                var collision = forbidden.FirstOrDefault(p => Inside(target, p));
                if (collision == null) break;
                target = collision[direction < 0 ? 0 : 1];
            }
            // 0.2mm步长保证尺寸链中心和长度均可按0.1mm保存。
            return Math.Ceiling(Math.Abs(target - joint) * 5 - 1e-8) / 5;
        }

        private void ResizeFrame(BridgeDesignObject[] objects, BridgeDesignObject frame, double length, double shift)
        {
            var values = frame.Values.ToDictionary(p => p.Key, p => p.Value);
            values[DesignField.X] += shift * Math.Cos(frame.Direction * Math.PI / 180);
            values[DesignField.Y] += shift * Math.Sin(frame.Direction * Math.PI / 180);
            values[DesignField.Length] = length;
            objects[Array.FindIndex(objects, o => o.Id == frame.Id)] = frame.WithValues(values);
            foreach (var child in objects.Where(o => o.ParentId == frame.Id).ToArray())
            {
                var childValues = child.Values.ToDictionary(p => p.Key, p => p.Value);
                childValues[DesignField.X] -= (length - frame.Length) / 2;
                objects[Array.FindIndex(objects, o => o.Id == child.Id)] = child.WithValues(childValues);
            }
        }

        private List<double[]> Subtract(IEnumerable<double[]> ranges, double[][] forbidden)
        {
            var result = ranges.Where(p => p[0] <= p[1]).ToList();
            foreach (var obstacle in forbidden)
            {
                var next = new List<double[]>();
                foreach (var range in result)
                {
                    if (range[1] <= obstacle[0] || range[0] >= obstacle[1]) next.Add(range);
                    else
                    {
                        if (range[0] < obstacle[0]) next.Add(new[] { range[0], obstacle[0] });
                        if (range[1] > obstacle[1]) next.Add(new[] { obstacle[1], range[1] });
                    }
                }
                result = next;
            }
            var merged = new List<double[]>();
            foreach (var range in result.OrderBy(p => p[0]))
            {
                if (merged.Count > 0 && range[0] <= merged.Last()[1]) merged.Last()[1] = Math.Max(merged.Last()[1], range[1]);
                else merged.Add(range);
            }
            return merged;
        }

        internal IReadOnlyList<BridgeDesignIssue> Validate(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            // 重读前清空草稿会传入空方案和空对象；此时没有接缝需要校验。
            // 有对象却缺少方案上下文仍属于读取错误，不能当作空草稿放行。
            if (sample == null)
            {
                if (objects.Count == 0) return Array.Empty<BridgeDesignIssue>();
                throw new ArgumentNullException(nameof(sample), "基架接缝校验缺少当前方案上下文。");
            }
            var frames = objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            var smallOffset = SmallOffsetLayout(sample, frames);
            var issues = ValidateBaseJoints(sample, objects).ToList();
            foreach (var frame in frames)
            {
                var end = Project(frame, frame.X, frame.Y) + frame.Length / 2;
                if (!frames.Any(other => other.Id != frame.Id && RequiresLapClearance(smallOffset, frame, other) &&
                    Math.Abs(Math.Sin((other.Direction - frame.Direction) * Math.PI / 180)) < .000001 &&
                    Math.Abs(-(other.X - frame.X) * Math.Sin(frame.Direction * Math.PI / 180) +
                        (other.Y - frame.Y) * Math.Cos(frame.Direction * Math.PI / 180)) <= (frame.Width + other.Width) / 2 &&
                    Math.Abs(Project(frame, other.X, other.Y) - other.Length / 2 - end) < Tolerance)) continue;
                if (Intervals(sample, objects, frame).Any(p => Inside(end, p)))
                    issues.Add(new BridgeDesignIssue(smallOffset
                        ? "特殊包络基架连接点距铜排实际搭接区域边界不足200.0 mm，请调整基架分节或铜排断点。"
                        : "基架连接点距铜排实际搭接区域边界不足200 mm，请调整基架分节或铜排断点；特殊基架不能换位。",
                        frame.Id, "基架连接避让"));
            }
            return issues.AsReadOnly();
        }

        internal IReadOnlyList<BridgeDesignIssue> ValidateBaseJoints(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> objects)
        {
            if (sample?.Source.IsModelDerived != true) return Array.Empty<BridgeDesignIssue>();
            var issues = new List<BridgeDesignIssue>();
            foreach (var constraint in sample.BaseJointConstraints)
            {
                // 断点取前段排的后截面；正向端须额外让出一段搭接长度，反向端直接量至搭接近边。
                var minimum = MinimumLapDistanceMm + (constraint.OutwardSign > 0 ? sample.Parameters.OverlapLengthMm : 0d);
                var joints = objects.Where(o => o.JointGroupId == constraint.JointGroupId).ToArray();
                if (joints.Any(o => (o.X - constraint.BoundaryX) * constraint.OutwardSign < minimum - ClearanceTolerance))
                    issues.Add(new BridgeDesignIssue(FormattableString.Invariant(
                        $"基座L排断点距基座边不得小于{minimum:0.0} mm，实际搭接区须避让固定接缝至少200mm；请移动铜排断点。"),
                        joints.First().Id, "基座接缝避让"));
            }
            return issues.AsReadOnly();
        }

        private bool Inside(double position, double[] interval) => position > interval[0] + ClearanceTolerance &&
            position < interval[1] - ClearanceTolerance;
    }

    public sealed class BridgeInsulatorPlacement
    {
        public IReadOnlyList<BridgeDesignObject> Objects { get; }
        public IReadOnlyList<BridgeDesignIssue> Issues { get; }
        public BridgeInsulatorPlacement(IEnumerable<BridgeDesignObject> objects, IEnumerable<BridgeDesignIssue> issues)
        { Objects = Array.AsReadOnly(objects.ToArray()); Issues = Array.AsReadOnly(issues.ToArray()); }
    }

    /// <summary>预览级平面包络避让：沿基架局部纵向整组平移，不改变基架、排路径或落底高度。</summary>
    public sealed class BusbarBridgeInsulatorClearance
    {
        public const double MarginMm = 10;
        public const double JointCenterClearanceMm = 200;
        public const double PreferredGroupSpacingMm = 1000;
        private const double Tolerance = .000001;
        private readonly BusbarBridgeDraftPreviewSceneFactory _geometry = new BusbarBridgeDraftPreviewSceneFactory();
        private BridgeDesignSample _footprintSample;
        private readonly Dictionary<string, InsulatorFootprint> _footprints = new Dictionary<string, InsulatorFootprint>();
        private sealed class InsulatorFootprint
        {
            internal BridgeDesignObject Member { get; }
            internal BridgeDesignObject Owner { get; }
            internal BridgeDesignObject[] Group { get; }
            internal double[] Bounds { get; }
            internal InsulatorFootprint(BridgeDesignObject member, BridgeDesignObject owner, BridgeDesignObject[] group, double[] bounds)
            { Member = member; Owner = owner; Group = group; Bounds = bounds; }
        }

        // 两个入口都按实际安装顺序规划；模型同源关系在最终安装位置确定后归并。
        public BridgeInsulatorPlacement Place(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, Func<string, bool> locked)
            => PlaceCore(sample, objects, locked);

        // 编辑中沿用同一顺序目标，人工及锁定位置保留；同源身份由草稿在提交时独立。
        public BridgeInsulatorPlacement PlaceIndependently(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, Func<string, bool> locked)
            => PlaceCore(sample, objects, locked);

        // 兼容旧入口；人工覆盖与自动候选必须分别处理，不能用锁定状态替代编辑来源。
        public BridgeInsulatorPlacement PlaceIndependently(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> objects, Func<string, bool> locked,
            Func<string, bool> manuallyPositioned)
            => PlaceCore(sample, objects, locked, manuallyPositioned);

        internal BridgeInsulatorPlacement PlaceIndependently(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> objects, Func<string, bool> locked,
            Func<string, bool> manuallyPositioned, ISet<string> hangingTargets, bool preferSharedWidenedEnds = false)
            => PlaceCore(sample, objects, locked, manuallyPositioned, hangingTargets, preferSharedWidenedEnds);

        private sealed class PlacementContext
        {
            internal BridgeDesignSample Sample;
            internal IReadOnlyList<BridgeDesignObject> Objects;
            internal Dictionary<string, BridgeDesignObject> Frames;
            internal Dictionary<string, BridgeDesignObject[]> Children;
            internal Func<BridgeDesignObject, double[]> Footprint;
            internal Func<BridgeDesignObject, HeadConstraint[]> Heads;
            internal Func<string, bool> Locked, Manual;
            internal FaceToWallInternalSupportPlanner WallSupport;
            internal OffsetInsulatorCopperAlignment CopperAlignment;
            internal BridgePhaseChangeSupportPlanner PhaseSupport;
            internal readonly Dictionary<string, BridgeDesignObject> Replacements = new Dictionary<string, BridgeDesignObject>();
            internal readonly HashSet<string> Removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<BridgeDesignIssue> Issues = new List<BridgeDesignIssue>();
            internal bool Fixed(BridgeDesignObject member) => Locked != null && (Locked(member.ParentId) || Locked(member.Id)) ||
                Manual != null && Manual(member.Id);
        }

        private sealed class GroupConstraints
        {
            internal double Minimum = double.NegativeInfinity, Maximum = double.PositiveInfinity;
            internal readonly List<double[]> Forbidden = new List<double[]>();
            internal readonly List<double[]> HeadForbidden = new List<double[]>();
            internal Func<double, bool> Fits;
        }

        private sealed class HeadEnvelope
        {
            internal double[][] Corners;
            internal bool ActualLapOnly;
        }

        private sealed class HeadConstraint
        {
            internal double[] Bounds;
            internal bool ActualLapOnly;
        }

        private BridgeInsulatorPlacement PlaceCore(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects,
            Func<string, bool> locked, Func<string, bool> manuallyPositioned = null, ISet<string> hangingTargets = null,
            bool preferSharedWidenedEnds = false)
        {
            if (sample == null) return new BridgeInsulatorPlacement(objects, Array.Empty<BridgeDesignIssue>());
            var heads = CollectHeadEnvelopes(sample, objects);
            var context = new PlacementContext { Sample = sample, Objects = objects, Locked = locked, Manual = manuallyPositioned,
                Frames = objects.Where(o => o.Kind == DesignObjectKind.Frame).ToDictionary(o => o.Id),
                Children = objects.Where(o => o.Kind == DesignObjectKind.Insulator && o.ParentId != null)
                    .GroupBy(o => o.ParentId).ToDictionary(g => g.Key, g => g.ToArray()),
                WallSupport = sample.ScenarioKey == "FaceToWall" && sample.Routes.Any(r => r.PhysicalSolids.Count > 0)
                    ? new FaceToWallInternalSupportPlanner() : null,
                CopperAlignment = new OffsetInsulatorCopperAlignment(sample, objects, _geometry),
                PhaseSupport = new BridgePhaseChangeSupportPlanner(sample, objects) };
            if (!ReferenceEquals(_footprintSample, sample)) { _footprints.Clear(); _footprintSample = sample; }
            var activeIds = new HashSet<string>(objects.Where(o => o.Kind == DesignObjectKind.Insulator).Select(o => o.Id));
            foreach (var id in _footprints.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _footprints.Remove(id);
            var footprints = new Dictionary<BridgeDesignObject, double[]>();
            context.Footprint = member =>
            {
                if (!footprints.TryGetValue(member, out var bounds))
                    footprints[member] = bounds = FootprintFor(member, context.Frames[member.ParentId], objects);
                return bounds;
            };
            var frameHeads = new Dictionary<BridgeDesignObject, HeadConstraint[]>();
            context.Heads = frame =>
            {
                if (!frameHeads.TryGetValue(frame, out var bounds))
                    frameHeads[frame] = bounds = heads.Select(head => new HeadConstraint {
                        Bounds = Bounds(head.Corners, frame), ActualLapOnly = head.ActualLapOnly }).ToArray();
                return bounds;
            };
            PlanGroups(context, preferSharedWidenedEnds);
            var mountPlanner = context.PhaseSupport;
            var movedFrames = new HashSet<string>(context.Replacements.Values.Select(o => o.ParentId));
            var retained = objects.Where(o => !context.Removed.Contains(o.Id))
                .Select(o => context.Replacements.TryGetValue(o.Id, out var moved) ? moved :
                    o.Kind == DesignObjectKind.Frame && movedFrames.Contains(o.Id) ? o.WithAutomaticInsulatorPlacement() : o).ToArray();
            var skippedFrames = new HashSet<string>(objects.Where(o => context.Removed.Contains(o.Id)).Select(o => o.ParentId));
            var finalObjects = retained.Select(o =>
            {
                if (o.Kind != DesignObjectKind.Frame || !skippedFrames.Contains(o.Id)) return o;
                var values = o.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
                values[DesignField.InsulatorCount] = retained.Where(child => child.Kind == DesignObjectKind.Insulator && child.ParentId == o.Id)
                    .Select(child => child.InsulatorGroupId ?? child.Id).Distinct().Count();
                return o.WithValues(values);
            }).ToArray();
            foreach (var group in finalObjects.Where(o => o.Kind == DesignObjectKind.Insulator).GroupBy(o => o.InsulatorGroupId ?? o.Id))
            {
                if (hangingTargets != null && !group.Any(o => hangingTargets.Contains(o.Id))) continue;
                var member = group.First();
                if (context.Issues.Any(issue => issue.Source == BridgePhaseChangeSupportPlanner.IssueSource &&
                    group.Any(o => o.Id == issue.TargetId))) continue;
                var frame = context.Frames[member.ParentId];
                mountPlanner.At(frame, member.X, out var problem);
                if (problem != null) AddHangingIssue(context, frame, member, problem);
            }
            return new BridgeInsulatorPlacement(mountPlanner.Apply(finalObjects, hangingTargets), context.Issues);
        }

        private void PlanGroups(PlacementContext context, bool preferSharedWidenedEnds)
        {
            if (context.Children.Count == 0) return;
            double? previous = null;
            var station = 0d;
            double[] previousOutlet = null;
            var frames = BusbarBridgeDraftEditor.FramesInInstallationOrder(context.Frames.Values);
            var first = frames.FirstOrDefault();
            var last = frames.LastOrDefault();
            var shareEnds = preferSharedWidenedEnds && context.Sample.ScenarioKey == "FaceToFace" &&
                BridgeOffsetCopperChange.IsWidenedTerminal(first) && BridgeOffsetCopperChange.IsWidenedTerminal(last) &&
                !first.IsReversed && last.IsReversed && first.UseOffsetInsulators && last.UseOffsetInsulators &&
                first.Value(DesignField.InsulatorCount) == 1d &&
                new PrototypeBusbarBridgeDesignProvider().SameFrameModel(first, last, Array.Empty<BridgeDesignObject>());
            BridgeDesignObject firstEndSupport = null;
            foreach (var frame in frames)
            {
                // 使用已核验的模板接口和桥向轴累计里程；反装只改变局部安装尺寸的正负。
                var ports = _geometry.FrameInterfacePoints(frame);
                var center = new[] { frame.X, frame.Y, frame.Z };
                if (previousOutlet != null) station += Distance(previousOutlet, ports[0]);
                var centerStation = station + Distance(ports[0], center);
                station = centerStation + Distance(center, ports[1]);
                previousOutlet = ports[1];
                if (!context.Children.TryGetValue(frame.Id, out var children)) continue;
                // 中间搭接排的支承中心是工艺约束，不参与普通支架的桥向节距搜索或自动删除。
                if (children.Any(BridgeSmallOffsetSupportPlanner.IsSupport))
                {
                    foreach (var member in children) context.Processed.Add(member.Id);
                    previous = centerStation + children.Max(o => o.X);
                    continue;
                }
                var sign = frame.IsReversed ? -1d : 1d;
                // 套号对应模板安装位；按临时候选坐标排序会让新增套越过人工套，或在重复规划时交换身份。
                var groups = children.GroupBy(o => o.InsulatorGroupId ?? o.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
                for (var index = 0; index < groups.Length; index++)
                {
                    var group = groups[index];
                    var members = group.OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
                    var currentX = members.Average(o => o.X);
                    var constraints = ConstraintsFor(context, frame, members);
                    if (members.Any(context.Fixed))
                    {
                        context.PhaseSupport.At(frame, currentX, out var mountProblem);
                        if (mountProblem == null && !constraints.Fits(0))
                        {
                            var manual = context.Manual != null && members.Any(o => context.Manual(o.Id));
                            context.Issues.Add(new BridgeDesignIssue(frame.Name + (manual
                                ? "绝缘子人工位置与搭接区域、桥头避让或安装边界冲突，已保留人工覆盖，请调整位置或恢复自动布局。"
                                : "绝缘子组已锁定，无法满足搭接区域、桥头避让或安装约束，请调整方案。"),
                                members[members.Length / 2].Id, manual ? "人工覆盖冲突" : "预览避让"));
                        }
                        previous = centerStation + sign * currentX;
                    }
                    else
                    {
                        // 每节连接架的首套优先居中，避免跨拐角的间距目标将其挤向端口。
                        var centerFirstGroup = index == 0 && frame.FrameTemplateKey == "Regular" &&
                            frame.CornerPreview == null && frame.Role.Contains("大偏移");
                        var target = previous.HasValue && !centerFirstGroup
                            ? sign * (previous.Value + PreferredGroupSpacingMm - centerStation) - currentX : -currentX;
                        var upper = ReserveFollowingGroups(context, frame, groups, index, sign);
                        var preferredX = currentX + Math.Max(constraints.Minimum, Math.Min(constraints.Maximum, target));
                        var requiresHanging = context.PhaseSupport.RequiresHangingAt(frame, currentX) ||
                            context.PhaseSupport.RequiresHangingAt(frame, preferredX);
                        Func<double, bool> fits = delta => constraints.Fits(delta) &&
                            (!requiresHanging || context.PhaseSupport.RequiresHangingAt(frame, currentX + delta)) &&
                            (!previous.HasValue || centerStation + sign * (currentX + delta) > previous.Value + Tolerance) &&
                            sign * (currentX + delta) <= upper + Tolerance;
                        // 首次规划优先复用首节的一套局部安装位，尾节仍按本节真实铜排与避让区校验。
                        if (shareEnds && frame == last && firstEndSupport != null && groups.Length == 1)
                        {
                            var sharedDelta = firstEndSupport.X - currentX;
                            var firstCenter = context.Children[first.Id].Average(member =>
                                context.Replacements.TryGetValue(member.Id, out var moved) ? moved.Y : member.Y);
                            if (fits(sharedDelta) && context.CopperAlignment.TryCenter(frame, members[0], sharedDelta, out var tailCenter) &&
                                Math.Abs(firstCenter - tailCenter) <= BridgeDesignPrecision.LengthComparisonToleranceMm)
                                target = sharedDelta;
                        }
                        var candidates = CandidateDeltas(context, frame, members, constraints, target, sign * upper - currentX)
                            .Where(fits).OrderBy(d => Math.Abs(d - target)).ThenBy(d => sign * d).ToArray();
                        if (candidates.Length == 0)
                        {
                            if (requiresHanging)
                            {
                                AddHangingIssue(context, frame, members[0], "同一基架内找不到具有可靠支撑面且满足避让和安装边界的吊装位置。");
                                previous = centerStation + sign * currentX;
                                foreach (var member in members) context.Processed.Add(member.Id);
                                continue;
                            }
                            // 空架是合法结果；不更新前组锚点，也不产生缺少绝缘子的提示。
                            foreach (var member in members) context.Removed.Add(member.Id);
                            continue;
                        }
                        ApplyPlacement(context, frame, members, candidates[0]);
                        if (shareEnds && frame == first && groups.Length == 1)
                            firstEndSupport = context.Replacements.TryGetValue(members[0].Id, out var support) ? support : members[0];
                        previous = centerStation + sign * (currentX + candidates[0]);
                    }
                    foreach (var member in members) context.Processed.Add(member.Id);
                }
            }
        }

        private void AddHangingIssue(PlacementContext context, BridgeDesignObject frame, BridgeDesignObject member, string problem)
        {
            var groupId = member.InsulatorGroupId ?? member.Id;
            var groupIndex = context.Children[frame.Id].Select(o => o.InsulatorGroupId ?? o.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList().FindIndex(id => StringComparer.OrdinalIgnoreCase.Equals(id, groupId));
            var position = member.X + frame.DriveValue(DesignField.Length) / 2d;
            var message = FormattableString.Invariant($"{frame.DisplayName}第{groupIndex + 1}套绝缘子，定位 {position:0.0} mm。{problem}");
            context.Issues.Add(new BridgeDesignIssue(message, member.Id, BridgePhaseChangeSupportPlanner.IssueSource));
        }

        private IEnumerable<double> CandidateDeltas(PlacementContext context, BridgeDesignObject frame,
            BridgeDesignObject[] members, GroupConstraints constraints, params double[] targets) =>
            targets.Concat(new[] { 0d, constraints.Minimum, constraints.Maximum }).Concat(constraints.Forbidden.SelectMany(i => i))
                .Concat(constraints.HeadForbidden.SelectMany(i => i.Take(2)))
                .Concat(context.WallSupport == null ? Enumerable.Empty<double>() :
                    context.WallSupport.CandidateDeltas(context.Sample, frame, members[0]))
                .Concat(frame.UseOffsetInsulators ? context.CopperAlignment.CandidateDeltas(frame, members[0]) : Enumerable.Empty<double>())
                .Concat(context.PhaseSupport.CandidateDeltas(frame, members.Average(o => o.X)))
                .Where(d => !double.IsInfinity(d) && !double.IsNaN(d))
                // 边界可能落在相邻0.1mm网格之间，须向两侧尝试，不能舍入回禁区。
                .SelectMany(d => new[] { BridgeDesignPrecision.OneDecimal(d), Math.Floor(d * 10) / 10, Math.Ceiling(d * 10) / 10 }).Distinct();

        private double ReserveFollowingGroups(PlacementContext context, BridgeDesignObject frame,
            IGrouping<string, BridgeDesignObject>[] groups, int current, double sign)
        {
            var upperEdge = double.PositiveInfinity;
            // 先从本架末端为后续套保留实体空间，避免前套贪心靠边而不必要地挤掉后套。
            for (var index = groups.Length - 1; index > current; index--)
            {
                var members = groups[index].OrderBy(o => o.Id, StringComparer.Ordinal).ToArray();
                var center = sign * members.Average(o => o.X);
                var edges = GroupEdges(context, members, sign);
                if (!members.Any(context.Fixed))
                {
                    var constraints = ConstraintsFor(context, frame, members);
                    var localX = members.Average(o => o.X);
                    var requiresHanging = context.PhaseSupport.RequiresHangingAt(frame, localX);
                    var candidates = CandidateDeltas(context, frame, members, constraints, sign * (upperEdge - edges[1]) - localX)
                        .Where(delta => constraints.Fits(delta) &&
                            (!requiresHanging || context.PhaseSupport.RequiresHangingAt(frame, localX + delta)) &&
                            center + sign * delta + edges[1] <= upperEdge + Tolerance)
                        .OrderByDescending(delta => center + sign * delta).ToArray();
                    if (candidates.Length == 0) continue;
                    center += sign * candidates[0];
                }
                upperEdge = center + edges[0] - MarginMm;
            }
            return upperEdge - GroupEdges(context, groups[current].ToArray(), sign)[1];
        }

        private double[] GroupEdges(PlacementContext context, BridgeDesignObject[] members, double sign)
        {
            var center = sign * members.Average(o => o.X);
            var positions = members.SelectMany(member => context.Footprint(member).Take(2)).Select(x => sign * x - center).ToArray();
            return new[] { positions.Min(), positions.Max() };
        }

        private GroupConstraints ConstraintsFor(PlacementContext context, BridgeDesignObject frame, BridgeDesignObject[] members)
        {
            var result = new GroupConstraints();
            var lateralFits = true;
            var groupX = members.Average(o => o.X);
            foreach (var member in members)
            {
                var footprint = context.Footprint(member);
                result.Minimum = Math.Max(result.Minimum, -frame.Length / 2 + 24 - footprint[0]);
                result.Maximum = Math.Min(result.Maximum, frame.Length / 2 - 24 - footprint[1]);
                var span = frame.FrameTemplateKey == "SideToSidePhaseChange029" ? frame.Height : frame.Width;
                lateralFits &= footprint[2] >= -span / 2 + 20 - Tolerance && footprint[3] <= span / 2 - 20 + Tolerance;
                foreach (var constraint in context.Heads(frame))
                {
                    var head = constraint.Bounds;
                    if (!frame.UseOffsetInsulators && (footprint[3] <= head[2] || footprint[2] >= head[3])) continue;
                    // 换相段只避开实际搭接实体，不追加200mm或10mm；其他段仍按安装中心避让。
                    result.HeadForbidden.Add(constraint.ActualLapOnly
                        ? new[] { head[0] - footprint[1], head[1] - footprint[0], head[4], head[5] }
                        : new[] { head[0] - JointCenterClearanceMm - groupX,
                            head[1] + JointCenterClearanceMm - groupX, head[4], head[5] });
                }
                // 后续自动候选尚未落位，不能用它们的临时位置挡住前组；人工/锁定位始终作为障碍。
                foreach (var other in context.Children[frame.Id].Where(o => (o.InsulatorGroupId ?? o.Id) != (member.InsulatorGroupId ?? member.Id) &&
                    !context.Removed.Contains(o.Id) && (context.Processed.Contains(o.Id) || context.Fixed(o))))
                {
                    var obstacle = context.Footprint(context.Replacements.TryGetValue(other.Id, out var moved) ? moved : other);
                    if (footprint[3] <= obstacle[2] - MarginMm || footprint[2] >= obstacle[3] + MarginMm) continue;
                    result.Forbidden.Add(new[] { obstacle[0] - MarginMm - footprint[1], obstacle[1] + MarginMm - footprint[0] });
                }
            }
            result.Fits = delta =>
            {
                if (delta < result.Minimum - Tolerance || delta > result.Maximum + Tolerance ||
                    result.Forbidden.Any(i => delta > i[0] + Tolerance && delta < i[1] - Tolerance)) return false;
                var mount = context.PhaseSupport.At(frame, groupX + delta, out var problem);
                if (problem != null || !HeadFits(frame, mount, delta, result.HeadForbidden)) return false;
                if (mount != null) return true;
                var floorFits = frame.UseOffsetInsulators
                    ? context.CopperAlignment.TryCenter(frame, members[0], delta, out var center) &&
                        members.All(m => Math.Abs(center + m.Y - members.Average(n => n.Y)) + m.Width / 2 <= frame.Width / 2 - 20 + .01)
                    : lateralFits;
                return floorFits && (context.WallSupport == null || context.WallSupport.Supports(context.Sample, context.Objects, members, delta));
            };
            return result;
        }

        private bool HeadFits(BridgeDesignObject frame, BridgeInsulatorMountPlan mount, double delta, IEnumerable<double[]> heads)
        {
            var lower = mount == null ? double.NegativeInfinity : frame.DriveValue(DesignField.Height) / 2 - BridgePhaseChangeSupportPlanner.CeilingInsetMm -
                InsulatorPreviewProfile.BracketHeight - mount.BodyHeights.Max() - (2 * frame.InsulatorPadCount + 1) * frame.InsulatorPadThickness;
            return heads.All(h => h[3] < lower - BridgeDesignPrecision.LengthComparisonToleranceMm ||
                delta <= h[0] + Tolerance || delta >= h[1] - Tolerance);
        }

        private void ApplyPlacement(PlacementContext context, BridgeDesignObject frame, BridgeDesignObject[] members, double delta)
        {
            var originalCenter = members.Average(m => m.Y);
            var center = originalCenter;
            if (frame.UseOffsetInsulators && !context.PhaseSupport.RequiresHangingAt(frame, members.Average(m => m.X) + delta) &&
                !context.CopperAlignment.TryCenter(frame, members[0], delta, out center))
                throw new InvalidOperationException("绝缘子安装位缺少可对准的三相铜排直段：" + frame.Name);
            foreach (var member in members)
            {
                var values = member.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = member.X + delta;
                values[DesignField.Y] = member.Y + center - originalCenter;
                if (Math.Abs(delta) > Tolerance || Math.Abs(values[DesignField.Y] - member.Y) > Tolerance)
                    context.Replacements[member.Id] = member.WithValues(values);
            }
        }

        private double Distance(double[] a, double[] b) => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i])));

        private double[] FootprintFor(BridgeDesignObject member, BridgeDesignObject owner, IReadOnlyList<BridgeDesignObject> objects)
        {
            // 029共用支架取决于整组成员；普通安装位姿只依赖本体与父基架的不可变快照。
            var group = owner.FrameTemplateKey == "SideToSidePhaseChange029"
                ? objects.Where(o => o.ParentId == owner.Id && o.InsulatorGroupId == member.InsulatorGroupId)
                    .OrderBy(o => o.Id, StringComparer.Ordinal).ToArray() : Array.Empty<BridgeDesignObject>();
            if (_footprints.TryGetValue(member.Id, out var cached) && ReferenceEquals(cached.Member, member) &&
                ReferenceEquals(cached.Owner, owner) && cached.Group.SequenceEqual(group)) return cached.Bounds;
            // 上轮无接触面的诊断会隐藏预览实体；候选搜索仍需原安装轮廓，之后重新核验当前接触面。
            var footprintMember = member.MountPlan == null && member.InsulatorMountProblem == null
                ? member : member.WithMountPlan(null, member.MountGroupIndex);
            var bounds = Bounds(_geometry.Solids(footprintMember, objects).SelectMany(s => s.Corners), owner);
            _footprints[member.Id] = new InsulatorFootprint(member, owner, group, bounds);
            return bounds;
        }

        internal IReadOnlyList<double[][]> HeadEnvelopes(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
            => CollectHeadEnvelopes(sample, objects).Select(head => head.Corners).ToArray();

        private IReadOnlyList<HeadEnvelope> CollectHeadEnvelopes(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            var phaseChange = new BridgePhaseChangeSupportPlanner(sample, objects);
            var laps = new BusbarBridgeFrameJointClearance().LapEnvelopes(sample, objects);
            var result = laps.Select(corners => new HeadEnvelope {
                Corners = corners, ActualLapOnly = phaseChange.IsPhaseChangeRegion(corners) }).ToList();
            Action<double[][]> addHead = corners => {
                if (!phaseChange.IsPhaseChangeRegion(corners)) result.Add(new HeadEnvelope { Corners = corners });
            };
            var frameAngles = objects.Where(o => o.Kind == DesignObjectKind.Frame)
                .Select(o => o.Direction * Math.PI / 180).Distinct().ToArray();
            foreach (var route in sample.Routes)
            foreach (var segment in _geometry.CopperSegments(route, objects))
            {
                var path = _geometry.CopperSegmentPath(segment, objects, sample.Source.BusbarWidth, sample.Source.BusbarThickness, sample.Parameters.OverlapLengthMm);
                if (segment.IsPhysicalGeometry)
                {
                    // 物理排件已经含桥头；不能将两点直排整段作为桥头禁区。
                    // 端部搭接及真实折弯均按局部包络避让，不占用中间可支撑直段。
                    var halfWidth = segment.SectionWidth / 2d;
                    var vertical = sample.ScenarioKey == "SideToSideBase1";
                    for (var i = 0; i + 1 < path.Length; i++)
                    {
                        var a = path[i]; var b = path[i + 1];
                        if (phaseChange.IsAction(route.CopperSectionId, a, b)) continue;
                        // 柜后/柜前均在草稿X方向展开。桥头入口直段也属于桥头，
                        // 不能只用端点包络留下斜段和入口中间的可安装假空隙。
                        var entryHead = sample.ScenarioKey == "FaceToWall" && route.IsBridge && path.Length >= 3 && i < 2;
                        var dx = b[0] - a[0]; var dy = b[1] - a[1];
                        var straight = Math.Abs(a[2] - b[2]) <= Tolerance && frameAngles.Any(angle =>
                            Math.Abs(dx * Math.Sin(angle) - dy * Math.Cos(angle)) <= Tolerance);
                        if (!entryHead && straight) continue;
                        addHead(new[] {
                            new[] { Math.Min(a[0], b[0]) - halfWidth, Math.Min(a[1], b[1]) - halfWidth, Math.Min(a[2], b[2]) },
                            new[] { Math.Max(a[0], b[0]) + halfWidth, Math.Max(a[1], b[1]) + halfWidth, Math.Max(a[2], b[2]) } });
                    }
                    foreach (var point in path.Where((p, i) => (i == 0 || i == path.Length - 1 ||
                        !Collinear(path[i - 1], p, path[i + 1])) && !phaseChange.IsAction(route.CopperSectionId, p, p)))
                        addHead(new[]
                        {
                            new[] { point[0] - halfWidth, point[1] - (vertical ? segment.SectionThickness / 2d : halfWidth), point[2] - (vertical ? halfWidth : 0d) },
                            new[] { point[0] + halfWidth, point[1] + (vertical ? segment.SectionThickness / 2d : halfWidth), point[2] + (vertical ? halfWidth : 0d) }
                        });
                    continue;
                }
                double[][] occupied;
                if (segment.StartHead == BridgeCopperHeadKind.Bridge) occupied = path.Take(3).ToArray();
                else if (segment.StraightLapAtStart) occupied = new[] { path[0], _geometry.WorldPosition(segment.Points[0], objects) };
                else continue;
                var mesh = _geometry.RouteMesh(occupied, sample.Source.BusbarWidth, sample.Source.BusbarThickness);
                addHead(Enumerable.Range(0, mesh.Vertices.Length / 3).Select(i => new[] { mesh.Vertices[i * 3], mesh.Vertices[i * 3 + 1], mesh.Vertices[i * 3 + 2] }).ToArray());
            }
            // 接缝本身也属于禁区，覆盖无桥头的直直搭接和固定端部接缝。
            foreach (var node in objects.Where(o => o.Kind == DesignObjectKind.Lap || o.Kind == DesignObjectKind.Breakpoint || o.Kind == DesignObjectKind.FixedJoint))
            {
                var p = _geometry.WorldPosition(node, objects); var half = sample.Source.BusbarWidth / 2;
                var phaseLap = phaseChange.IsPhaseChangeRegion(new[] { p });
                // 已有实际排件搭接时以其真实边界为准，逻辑断点不能再添加一个扩展禁区。
                if (phaseLap && laps.Any(lap => p[0] >= lap.Min(q => q[0]) - .01 && p[0] <= lap.Max(q => q[0]) + .01 &&
                    p[1] >= lap.Min(q => q[1]) - .01 && p[1] <= lap.Max(q => q[1]) + .01)) continue;
                var halfLap = phaseLap ? sample.Parameters.OverlapLengthMm / 2d : 0d;
                result.Add(new HeadEnvelope { ActualLapOnly = phaseLap,
                    Corners = new[] { new[] { p[0] - halfLap, p[1] - half, p[2] }, new[] { p[0] + halfLap, p[1] + half, p[2] } } });
            }
            return result;
        }

        private bool Collinear(double[] a, double[] b, double[] c)
        {
            var u = new GeometRi.Vector3d(b[0] - a[0], b[1] - a[1], b[2] - a[2]);
            var v = new GeometRi.Vector3d(c[0] - b[0], c[1] - b[1], c[2] - b[2]);
            return u.Norm > Tolerance && v.Norm > Tolerance && u.Normalized.Dot(v.Normalized) > 1 - Tolerance;
        }

        private double[] Bounds(IEnumerable<double[]> corners, BridgeDesignObject frame)
        {
            var angle = frame.ModelDirection * Math.PI / 180; var c = Math.Cos(angle); var s = Math.Sin(angle);
            var minX = double.PositiveInfinity; var maxX = double.NegativeInfinity;
            var minY = double.PositiveInfinity; var maxY = double.NegativeInfinity;
            var minZ = double.PositiveInfinity; var maxZ = double.NegativeInfinity;
            var vertical = frame.FrameTemplateKey == "SideToSidePhaseChange029";
            foreach (var p in corners)
            {
                var x = (p[0] - frame.X) * c + (p[1] - frame.Y) * s;
                var y = vertical ? p[2] - frame.Z : -(p[0] - frame.X) * s + (p[1] - frame.Y) * c;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                minZ = Math.Min(minZ, p[2] - frame.Z); maxZ = Math.Max(maxZ, p[2] - frame.Z);
            }
            if (double.IsPositiveInfinity(minX)) throw new InvalidOperationException("绝缘子避让几何没有有效轮廓。");
            return new[] { minX, maxX, minY, maxY, minZ, maxZ };
        }
    }

    // 使用预览与正式产出共用的铜排加工点列；只在真实三相直段上建立支撑，不能从外形包络猜中心。
    internal sealed class OffsetInsulatorCopperAlignment
    {
        private readonly List<Tuple<string, double[], double[]>> _lines = new List<Tuple<string, double[], double[]>>();
        internal OffsetInsulatorCopperAlignment(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects,
            BusbarBridgeDraftPreviewSceneFactory geometry)
        {
            if (!objects.Any(o => o.Kind == DesignObjectKind.Frame && o.UseOffsetInsulators)) return;
            foreach (var route in sample.Routes)
            foreach (var segment in geometry.CopperSegments(route, objects))
            {
                var path = geometry.CopperSegmentPath(segment, objects, sample.Source.BusbarWidth,
                    sample.Source.BusbarThickness, sample.Parameters.OverlapLengthMm);
                for (var i = 1; i < path.Length; i++)
                    _lines.Add(Tuple.Create(route.Phase, path[i - 1], path[i]));
            }
        }

        internal IEnumerable<double> CandidateDeltas(BridgeDesignObject frame, BridgeDesignObject member)
        {
            var angle = frame.ModelDirection * Math.PI / 180;
            // 三相合法直段的端点也是可行区边界，不能只尝试桥头边界而误判无位置。
            return _lines.SelectMany(line => new[] { line.Item2, line.Item3 })
                .Select(p => (p[0] - frame.X) * Math.Cos(angle) + (p[1] - frame.Y) * Math.Sin(angle) - member.X);
        }

        internal bool TryCenter(BridgeDesignObject frame, BridgeDesignObject member, double delta, out double center)
        {
            center = 0;
            var a = frame.ModelDirection * Math.PI / 180; var c = Math.Cos(a); var s = Math.Sin(a);
            var x = member.X + delta;
            var centers = new List<double>();
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var candidates = new List<double>();
                foreach (var line in _lines.Where(l => l.Item1 == phase))
                {
                    var p = line.Item2; var q = line.Item3;
                    var px = (p[0] - frame.X) * c + (p[1] - frame.Y) * s;
                    var qx = (q[0] - frame.X) * c + (q[1] - frame.Y) * s;
                    if (Math.Abs(qx - px) < .01 || x < Math.Min(px, qx) - .01 || x > Math.Max(px, qx) + .01) continue;
                    var py = -(p[0] - frame.X) * s + (p[1] - frame.Y) * c;
                    var qy = -(q[0] - frame.X) * s + (q[1] - frame.Y) * c;
                    if (Math.Abs(py - qy) > .01 || Math.Abs(p[2] - q[2]) > .01 ||
                        p[2] < frame.Z - frame.Height / 2 || p[2] > frame.Z + frame.Height / 2) continue;
                    if (!candidates.Any(v => Math.Abs(v - py) <= .01)) candidates.Add(py);
                }
                if (candidates.Count != 1) return false;
                centers.Add(candidates[0]);
            }
            centers.Sort();
            if (Math.Abs(centers[1] - centers[0] - frame.InsulatorPhaseSpacing) > .11 ||
                Math.Abs(centers[2] - centers[1] - frame.InsulatorPhaseSpacing) > .11) return false;
            center = BridgeDesignPrecision.OneDecimal(centers.Average());
            return true;
        }
    }
}
