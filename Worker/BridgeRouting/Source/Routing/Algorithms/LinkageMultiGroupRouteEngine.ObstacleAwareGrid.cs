using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing.Algorithms
{
    internal static partial class LinkageMultiGroupRouteEngine
    {
        // 避让网格 A*：在已落位组、其他相固定排和障碍物按“半截面 + 绝缘距离”膨胀后的
        // 轴对齐盒之间，用 Hanan 网格（各盒边界、端点、容纳范围及中点坐标）做带折弯惩罚
        // 和最短段约束的正交 A*。两端复用本组既有候选骨架的搭接点与伸出点，只替换中间段；
        // 生成的骨架仍由原候选工厂完整实体化和校验，网格只负责提出可能避开的走向。
        // 同一组障碍只建一次网格：盒按坐标区间栅格化成“边被阻挡”数组，A* 每步只查表。
        private const double GridCoordinateMerge = 0.5d;
        private const double GridBoxMargin = 1d;
        private const int GridMaximumExpandedStates = 400000;
        private const int GridStatesPerNode = 72; // 方向6 × 最短段2 × 平弯片排宽轴3 × 当前片仅一段2

        private sealed class GridBox
        {
            public double[] Min = new double[3];
            public double[] Max = new double[3];
        }

        private struct GridStub
        {
            public Point3d Lap;
            public Point3d Exit;
            public IList<Point3d> Fixed; // 从搭接点到伸出点（含两端）保留的模板骨架点
            public int Direction; // 0..5: +X,-X,+Y,-Y,+Z,-Z；起点为离开方向，终点为进入搭接点方向
        }

        private sealed class GridFrame
        {
            public double[][] Coordinates;
            public int SizeX;
            public int SizeY;
            public int SizeZ;
            public bool[][] Blocked; // [轴][节点]：从该节点沿正向到相邻节点的边被阻挡

            public int NodeId(int x, int y, int z) => (x * SizeY + y) * SizeZ + z;
        }

        private sealed class GridSearchSettings
        {
            public double MinimumSegmentLength;
            public double MinimumRun;
            public double BendPenalty;
            public double TransitionReserve;
            public double UShapeMiddle;
            public int StartThicknessAxis;
            public int EndThicknessAxis;
            public double TransitionPenalty;
        }

        // 每次平弯片间过渡都会让叠排沿过渡厚向偏移一层，过渡数超过模板会使柜顶叠排基准与连续夹层错位。
        private static int CountPlanarTransitions(IList<Point3d> path, int startThicknessAxis)
        {
            if (path == null || path.Count < 2 || startThicknessAxis < 0) return -1;
            var previous = AxisDirectionIndex(path[0], path[1]);
            if (previous < 0 || previous / 2 == startThicknessAxis) return -1;
            var width = 3 - startThicknessAxis - previous / 2;
            var single = 0;
            var transitions = 0;
            for (var index = 1; index < path.Count - 1; index++)
            {
                var next = AxisDirectionIndex(path[index], path[index + 1]);
                if (next < 0) return -1;
                var before = width;
                if (!AdvancePlanarPart(ref width, ref single, previous / 2, next / 2)) return -1;
                if (width != before) transitions++;
                previous = next;
            }

            return transitions;
        }

        private static LinkageRouteCandidatePool BuildObstacleAwareGridPool(
            SearchContext context,
            LinkageRouteGroupRequest group,
            LinkageRouteCandidatePool seedPool,
            IReadOnlyList<LinkageRouteSolid> obstacles,
            CancellationToken token, IReadOnlyList<LinkageRouteSolid> gridAvoidance = null)
        {
            var request = context.RouteRequest;
            var pool = new LinkageRouteCandidatePool { GroupRequest = group };
            pool.TraversalDirections.Add(LinkageRouteTraversalDirection.Forward);
            if (request.ContainmentMinimum == null || request.ContainmentMaximum == null) return pool;

            var templates = GetGridTemplates(seedPool);
            // 端部保留模板的前/后两段（已被转换器证明可贴合固定排），A* 只替换中间；段数不足时退为一段。
            var startStubs = DistinctStubs(templates.SelectMany(candidate => new[] { 2, 1 }
                .Where(depth => candidate.AxisSkeleton.Count >= depth + 3 || depth == 1)
                .Select(depth => MakeStub(candidate.AxisSkeleton.Take(depth + 1).ToList(), false))));
            var endStubs = DistinctStubs(templates.SelectMany(candidate => new[] { 2, 1 }
                .Where(depth => candidate.AxisSkeleton.Count >= depth + 3 || depth == 1)
                .Select(depth => MakeStub(candidate.AxisSkeleton.Reverse().Take(depth + 1).ToList(), true))));
            if (startStubs.Count == 0 || endStubs.Count == 0)
            {
                pool.Diagnostics.Add("网格：无可复用端部骨架");
                return pool;
            }

            var width = group.GetBusbarWidth(request);
            var thickness = group.GetBusbarThickness(request);
            var count = group.GetStackCount(request);
            var stackThickness = LinkageRouteStackGeometry.GetStackEnvelopeThickness(thickness, count, group.StackMiddleSpace);
            var bendReserve = LinkageRouteStackGeometry.GetStackBendAllowance(thickness, count, group.StackMiddleSpace,
                request.GetEffectiveBendAllowance());
            var minimumRun = request.MinimumSegmentLength + bendReserve;
            var clearance = group.GetRequiredInsulationClearance(request);
            var clearanceKey = group.GetClearanceGroupKey();
            var settings = new GridSearchSettings
            {
                MinimumSegmentLength = request.MinimumSegmentLength,
                MinimumRun = minimumRun,
                TransitionReserve = width / 2d,
                UShapeMiddle = Math.Max(2d * request.MinimumSegmentLength,
                    Math.Max(width + request.BendAllowance, width + 2d * bendReserve)),
                StartThicknessAxis = CorridorAxis(group.StartTerminal?.ThicknessDirection),
                EndThicknessAxis = CorridorAxis(group.EndTerminal?.ThicknessDirection)
            };
            // 每次片间过渡都会让叠排沿过渡厚向偏移一层，过渡另计重惩罚，优先少过渡的走向。
            settings.TransitionPenalty = 4d * minimumRun;
            var evaluated = new Dictionary<string, LinkageRouteCandidate>(StringComparer.Ordinal);
            var tried = new HashSet<string>(StringComparer.Ordinal);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var searches = 0;
            long searchMilliseconds = 0;
            long buildMilliseconds = 0;
            var exitPoints = startStubs.Concat(endStubs).Select(stub => ToArray(stub.Exit)).ToList();

            // 粗截面（按排宽/叠厚较大者）先试；只有粗截面一条走向都求不出时才用细截面（较小者）。
            foreach (var halfSection in new[] { Math.Max(width, stackThickness) / 2d, Math.Min(width, stackThickness) / 2d }
                .Distinct())
            {
                if (tried.Count > 0) break;
                var boxes = BuildGridBoxes(gridAvoidance ?? obstacles, group, clearanceKey, halfSection, clearance,
                    Math.Max(clearance, request.LapInsulationClearance));
                var frames = new Dictionary<string, GridFrame>(StringComparer.Ordinal);
                foreach (var bendPenalty in new[] { 2d * minimumRun, 0.5d * minimumRun })
                foreach (var startStub in startStubs)
                foreach (var endStub in endStubs)
                {
                    if (token.IsCancellationRequested || pool.ValidCandidates.Count >= 2) goto Done;
                    var searchStart = watch.ElapsedMilliseconds;
                    searches++;
                    // 包含端部伸出点的盒（通常是本端相邻固定排）不参与网格阻挡，端部真实净距由候选工厂判定。
                    var startPoint = ToArray(startStub.Exit);
                    var endPoint = ToArray(endStub.Exit);
                    var exempt = Enumerable.Range(0, boxes.Count)
                        .Where(index => Contains(boxes[index], startPoint) || Contains(boxes[index], endPoint)).ToList();
                    var frameKey = string.Join(",", exempt);
                    if (!frames.TryGetValue(frameKey, out var frame))
                    {
                        frame = BuildGridFrame(request, boxes.Where((box, index) => !exempt.Contains(index)).ToList(),
                            exitPoints, halfSection, minimumRun);
                        frames[frameKey] = frame;
                    }

                    settings.BendPenalty = bendPenalty;
                    var path = frame == null ? null : FindGridPath(frame, startStub, endStub, settings, token);
                    searchMilliseconds += watch.ElapsedMilliseconds - searchStart;
                    if (path == null) continue;
                    var key = string.Join(";", path.Select(point =>
                        Math.Round(point.X, 1) + "," + Math.Round(point.Y, 1) + "," + Math.Round(point.Z, 1)));
                    if (!tried.Add(key)) continue;
                    var buildStart = watch.ElapsedMilliseconds;
                    LinkageRouteCandidateFactory.AddObstacleAwareGridSeed(pool, evaluated, request, group,
                        obstacles, path, token);
                    buildMilliseconds += watch.ElapsedMilliseconds - buildStart;
                }
            }

            Done:
            pool.Diagnostics.Add("网格：端部" + startStubs.Count + "×" + endStubs.Count + "，A*" + searches + "次/" +
                searchMilliseconds + "ms，实体化" + buildMilliseconds + "ms，骨架" + tried.Count +
                "，有效" + pool.ValidCandidates.Count + "，失败" + string.Join(",", pool.FailureStageCounts
                    .OrderByDescending(entry => entry.Value).Select(entry => entry.Key + "=" + entry.Value)) +
                "，样例" + string.Join(" || ", pool.FailureDiagnosticSamples
                    .Where(entry => entry.Key == LinkageRouteFailureStage.PlanarConversion)
                    .SelectMany(entry => entry.Value.Take(3))
                    .Select(message => message.Length > 220 ? message.Substring(0, 220) : message)));
            return pool;
        }

        private static List<LinkageRouteCandidate> GetGridTemplates(LinkageRouteCandidatePool seedPool)
        {
            // 端部只复用已通过候选门禁的骨架；没有时才退用失败代表候选。
            var templates = (seedPool?.ValidCandidates ?? new List<LinkageRouteCandidate>())
                .Where(candidate => candidate?.AxisSkeleton != null && candidate.AxisSkeleton.Count >= 3)
                .ToList();
            if (templates.Count == 0)
                templates = (seedPool?.RepresentativeFailedCandidates?.Values ?? Enumerable.Empty<LinkageRouteCandidate>())
                    .Concat(seedPool?.BestFailedCandidate == null ? Enumerable.Empty<LinkageRouteCandidate>()
                        : new[] { seedPool.BestFailedCandidate })
                    .Where(candidate => candidate?.AxisSkeleton != null && candidate.AxisSkeleton.Count >= 3)
                    .ToList();
            return templates;
        }

        private static bool HasGridTemplates(LinkageRouteCandidatePool seedPool) => GetGridTemplates(seedPool).Count > 0;

        private static GridStub MakeStub(IList<Point3d> fixedFromLap, bool isEnd)
        {
            var exit = fixedFromLap[fixedFromLap.Count - 1];
            var inner = fixedFromLap[fixedFromLap.Count - 2];
            var direction = isEnd ? AxisDirectionIndex(exit, inner) : AxisDirectionIndex(inner, exit);
            return new GridStub { Lap = fixedFromLap[0], Exit = exit, Direction = direction, Fixed = fixedFromLap };
        }

        private static List<GridStub> DistinctStubs(IEnumerable<GridStub> stubs)
        {
            var result = new List<GridStub>();
            foreach (var stub in stubs)
            {
                if (stub.Direction < 0 || stub.Lap == null || stub.Exit == null) continue;
                if (result.Any(existing => existing.Fixed.Count == stub.Fixed.Count &&
                    existing.Fixed.Zip(stub.Fixed, (first, second) => first.DistanceTo(second)).All(distance =>
                        distance < GridCoordinateMerge))) continue;
                result.Add(stub);
                if (result.Count >= 4) break;
            }

            return result;
        }

        private static int AxisDirectionIndex(Point3d from, Point3d to)
        {
            var delta = new[] { to.X - from.X, to.Y - from.Y, to.Z - from.Z };
            var axis = -1;
            for (var index = 0; index < 3; index++)
            {
                if (Math.Abs(delta[index]) <= GridCoordinateMerge) continue;
                if (axis >= 0) return -1;
                axis = index;
            }

            return axis < 0 ? -1 : axis * 2 + (delta[axis] > 0 ? 0 : 1);
        }

        private static List<GridBox> BuildGridBoxes(IReadOnlyList<LinkageRouteSolid> obstacles,
            LinkageRouteGroupRequest group, string clearanceKey, double halfSection, double clearance, double lapClearance)
        {
            var boxes = new List<GridBox>();
            foreach (var solid in obstacles)
            {
                if (solid == null ||
                    string.Equals(solid.GroupKey, group.GroupKey, StringComparison.OrdinalIgnoreCase)) continue;
                var sameClearance = !string.IsNullOrWhiteSpace(clearanceKey) &&
                    string.Equals(solid.ClearanceGroupKey, clearanceKey, StringComparison.OrdinalIgnoreCase);
                // 其他相固定排附近按搭接面绝缘距离（取通用与搭接较大者）膨胀；搭接包络本身已含距离。
                var inflation = halfSection + (sameClearance || solid.IsForbiddenTerminalRegion || solid.IsLapClearanceEnvelope ||
                    solid.SolidRole == LinkageRouteSolidRole.InputObstacle ? 0d
                    : solid.SolidRole == LinkageRouteSolidRole.FixedTerminalBusbar ? lapClearance : clearance);
                var box = new GridBox();
                for (var axis = 0; axis < 3; axis++)
                {
                    box.Min[axis] = double.PositiveInfinity;
                    box.Max[axis] = double.NegativeInfinity;
                }

                foreach (var vertex in solid.GetVertices())
                {
                    var values = new[] { vertex.X, vertex.Y, vertex.Z };
                    for (var axis = 0; axis < 3; axis++)
                    {
                        box.Min[axis] = Math.Min(box.Min[axis], values[axis]);
                        box.Max[axis] = Math.Max(box.Max[axis], values[axis]);
                    }
                }

                for (var axis = 0; axis < 3; axis++)
                {
                    box.Min[axis] -= inflation;
                    box.Max[axis] += inflation;
                }

                boxes.Add(box);
            }

            return boxes;
        }

        private static GridFrame BuildGridFrame(LinkageMultiGroupRouteRequest request, List<GridBox> boxes,
            IList<double[]> exactPoints, double halfSection, double minimumRun)
        {
            var lower = ToArray(request.ContainmentMinimum);
            var upper = ToArray(request.ContainmentMaximum);
            var frame = new GridFrame { Coordinates = new double[3][] };
            for (var axis = 0; axis < 3; axis++)
            {
                var low = Math.Min(lower[axis], upper[axis]) + halfSection;
                var high = Math.Max(lower[axis], upper[axis]) - halfSection;
                var exact = new HashSet<double>(exactPoints.Select(point => point[axis]));
                var values = new List<double>(exact) { low, high };
                foreach (var box in boxes)
                {
                    values.Add(box.Min[axis] - GridBoxMargin);
                    values.Add(box.Max[axis] + GridBoxMargin);
                }

                // 端部伸出点坐标必须精确保留：合并只吸收其他坐标，不能把端点挪成非轴向段。
                var merged = new List<double>();
                foreach (var value in values.Where(value => (value >= low && value <= high) || exact.Contains(value))
                    .OrderBy(value => value))
                {
                    if (merged.Count > 0 && value - merged[merged.Count - 1] <= GridCoordinateMerge)
                    {
                        if (exact.Contains(value) && !exact.Contains(merged[merged.Count - 1]))
                            merged[merged.Count - 1] = value;
                        else if (exact.Contains(value) && value != merged[merged.Count - 1])
                            merged.Add(value);
                        continue;
                    }

                    merged.Add(value);
                }

                var withMidpoints = new List<double>();
                for (var index = 0; index < merged.Count; index++)
                {
                    if (index > 0 && merged[index] - merged[index - 1] > 2d * minimumRun)
                        withMidpoints.Add((merged[index] + merged[index - 1]) / 2d);
                    withMidpoints.Add(merged[index]);
                }

                frame.Coordinates[axis] = withMidpoints.ToArray();
            }

            frame.SizeX = frame.Coordinates[0].Length;
            frame.SizeY = frame.Coordinates[1].Length;
            frame.SizeZ = frame.Coordinates[2].Length;
            var nodeCount = (long)frame.SizeX * frame.SizeY * frame.SizeZ;
            if (nodeCount == 0 || nodeCount > 8000000) return null;
            frame.Blocked = new[] { new bool[nodeCount], new bool[nodeCount], new bool[nodeCount] };
            foreach (var box in boxes)
            {
                for (var axis = 0; axis < 3; axis++)
                {
                    // 沿 axis 的边 [c_i, c_i+1] 与盒内部重叠，且另两轴坐标严格落在盒内时被阻挡。
                    var ranges = new int[3, 2];
                    var empty = false;
                    for (var other = 0; other < 3 && !empty; other++)
                    {
                        var values = frame.Coordinates[other];
                        int first;
                        int last;
                        if (other == axis)
                        {
                            first = 0;
                            while (first < values.Length - 1 && values[first + 1] <= box.Min[other]) first++;
                            last = values.Length - 2;
                            while (last >= 0 && values[last] >= box.Max[other]) last--;
                        }
                        else
                        {
                            first = 0;
                            while (first < values.Length && values[first] <= box.Min[other]) first++;
                            last = values.Length - 1;
                            while (last >= 0 && values[last] >= box.Max[other]) last--;
                        }

                        if (first > last) empty = true;
                        ranges[other, 0] = first;
                        ranges[other, 1] = last;
                    }

                    if (empty) continue;
                    var blocked = frame.Blocked[axis];
                    for (var x = ranges[0, 0]; x <= ranges[0, 1]; x++)
                    for (var y = ranges[1, 0]; y <= ranges[1, 1]; y++)
                    for (var z = ranges[2, 0]; z <= ranges[2, 1]; z++)
                        blocked[frame.NodeId(x, y, z)] = true;
                }
            }

            return frame;
        }

        // 按 LinkageRoutePlanarConverter 的拆片规则推进：段沿当前片排宽轴时开新片，新排宽轴取前一段轴；
        // 非首片只有一段就再次转片会导致相邻搭接段厚向不平行，视为无效。返回 false 表示该走向不可转换。
        private static bool AdvancePlanarPart(ref int widthAxis, ref int singleSegmentPart, int previousAxis, int nextAxis)
        {
            if (nextAxis == previousAxis || nextAxis != widthAxis)
            {
                if (nextAxis != previousAxis) singleSegmentPart = 0;
                return true;
            }

            if (singleSegmentPart == 1) return false;
            widthAxis = previousAxis;
            singleSegmentPart = 1;
            return true;
        }

        private static IList<Point3d> FindGridPath(GridFrame frame, GridStub start, GridStub end,
            GridSearchSettings settings, CancellationToken token)
        {
            if (settings.StartThicknessAxis < 0 || settings.EndThicknessAxis < 0) return null;
            var startAxes = new List<int>();
            for (var index = 0; index < start.Fixed.Count - 1; index++)
            {
                var direction = AxisDirectionIndex(start.Fixed[index], start.Fixed[index + 1]);
                if (direction < 0) return null;
                startAxes.Add(direction / 2);
            }

            var endAxes = new List<int>(); // 从伸出点走向搭接点
            for (var index = end.Fixed.Count - 1; index > 0; index--)
            {
                var direction = AxisDirectionIndex(end.Fixed[index], end.Fixed[index - 1]);
                if (direction < 0) return null;
                endAxes.Add(direction / 2);
            }

            if (startAxes[0] == settings.StartThicknessAxis ||
                endAxes[endAxes.Count - 1] == settings.EndThicknessAxis) return null;
            var initialWidth = 3 - settings.StartThicknessAxis - startAxes[0];
            var initialSingle = 0;
            for (var index = 1; index < startAxes.Count; index++)
                if (!AdvancePlanarPart(ref initialWidth, ref initialSingle, startAxes[index - 1], startAxes[index])) return null;
            var requiredEndWidth = 3 - settings.EndThicknessAxis - endAxes[endAxes.Count - 1];

            var coordinates = frame.Coordinates;
            var startPoint = ToArray(start.Exit);
            var endPoint = ToArray(end.Exit);
            var startIndex = new int[3];
            var endIndex = new int[3];
            for (var axis = 0; axis < 3; axis++)
            {
                startIndex[axis] = Array.IndexOf(coordinates[axis], startPoint[axis]);
                endIndex[axis] = Array.IndexOf(coordinates[axis], endPoint[axis]);
                if (startIndex[axis] < 0 || endIndex[axis] < 0) return null;
            }

            var sizeY = frame.SizeY;
            var sizeZ = frame.SizeZ;
            long StateId(int node, int direction, int satisfied, int width, int single) =>
                (((long)node * 6 + direction) * 2 + satisfied) * 6 + width * 2 + single;
            var bestCost = new Dictionary<long, double>();
            var parent = new Dictionary<long, long>();
            var runLength = new Dictionary<long, double>();
            var heap = new List<KeyValuePair<double, long>>();
            double Heuristic(int x, int y, int z) =>
                Math.Abs(coordinates[0][x] - endPoint[0]) + Math.Abs(coordinates[1][y] - endPoint[1]) +
                Math.Abs(coordinates[2][z] - endPoint[2]);

            var startState = StateId(frame.NodeId(startIndex[0], startIndex[1], startIndex[2]), start.Direction, 1,
                initialWidth, initialSingle);
            bestCost[startState] = 0d;
            runLength[startState] = double.PositiveInfinity;
            HeapPush(heap, Heuristic(startIndex[0], startIndex[1], startIndex[2]), startState);
            var goalNode = frame.NodeId(endIndex[0], endIndex[1], endIndex[2]);
            var expanded = 0;
            long goalState = -1;
            var position = new int[3];
            while (heap.Count > 0)
            {
                if ((++expanded & 1023) == 0 && token.IsCancellationRequested) return null;
                if (expanded > GridMaximumExpandedStates) return null;
                var current = HeapPop(heap);
                var state = current.Value;
                var single = (int)(state % 2);
                var width = (int)(state / 2 % 3);
                var satisfied = (int)(state / 6 % 2);
                var direction = (int)(state / 12 % 6);
                var node = (int)(state / GridStatesPerNode);
                var cost = bestCost[state];
                var z0 = node % sizeZ;
                var y0 = node / sizeZ % sizeY;
                var x0 = node / sizeZ / sizeY;
                if (current.Key > cost + Heuristic(x0, y0, z0) + 1e-6) continue;
                if (node == goalNode && direction != (end.Direction ^ 1) &&
                    (direction == end.Direction || satisfied == 1))
                {
                    // 接上终端模板段后，最后一片排宽轴必须使排厚与柜顶固定排平行，且最后一片不少于两段。
                    var finalWidth = width;
                    var finalSingle = single;
                    var valid = direction == end.Direction ||
                        AdvancePlanarPart(ref finalWidth, ref finalSingle, direction / 2, endAxes[0]);
                    for (var index = 1; valid && index < endAxes.Count; index++)
                        valid = AdvancePlanarPart(ref finalWidth, ref finalSingle, endAxes[index - 1], endAxes[index]);
                    if (valid && finalWidth == requiredEndWidth && finalSingle == 0)
                    {
                        goalState = state;
                        break;
                    }
                }

                for (var next = 0; next < 6; next++)
                {
                    if (next == (direction ^ 1)) continue;
                    var turning = next != direction;
                    if (turning && satisfied == 0) continue;
                    var nextWidth = width;
                    var nextSingle = single;
                    if (turning && !AdvancePlanarPart(ref nextWidth, ref nextSingle, direction / 2, next / 2)) continue;
                    if (turning)
                    {
                        var currentRun = runLength[state];
                        // 片间过渡两侧各让出半个排宽，过渡前后段都需额外半宽余量。
                        if ((nextSingle == 1 || single == 1) &&
                            currentRun < settings.MinimumRun + settings.TransitionReserve - GridCoordinateMerge)
                            continue;
                        // U 型（d → e → -d）中间段须能拆成双 L：满搭接加两侧折弯余量。
                        if (currentRun < settings.UShapeMiddle - GridCoordinateMerge &&
                            PreviousRunDirection(parent, state, startState, start.Direction) == (next ^ 1))
                            continue;
                    }

                    var axis = next / 2;
                    var forward = next % 2 == 0;
                    position[0] = x0;
                    position[1] = y0;
                    position[2] = z0;
                    position[axis] += forward ? 1 : -1;
                    if (position[axis] < 0 || position[axis] >= coordinates[axis].Length) continue;
                    var nextNode = frame.NodeId(position[0], position[1], position[2]);
                    if (frame.Blocked[axis][forward ? node : nextNode]) continue;
                    var length = Math.Abs(coordinates[axis][position[axis]] - coordinates[axis][axis == 0 ? x0 : axis == 1 ? y0 : z0]);
                    var newRun = (turning ? 0d : runLength[state]) + length;
                    var newSatisfied = newRun >= settings.MinimumRun - GridCoordinateMerge ? 1 : 0;
                    var nextState = StateId(nextNode, next, newSatisfied, nextWidth, nextSingle);
                    var nextCost = cost + length + (turning ? settings.BendPenalty : 0d) +
                        (turning && nextWidth != width ? settings.TransitionPenalty : 0d);
                    if (bestCost.TryGetValue(nextState, out var known) && known <= nextCost + 1e-9) continue;
                    bestCost[nextState] = nextCost;
                    parent[nextState] = state;
                    runLength[nextState] = newRun;
                    HeapPush(heap, nextCost + Heuristic(position[0], position[1], position[2]), nextState);
                }
            }

            if (goalState < 0) return null;
            var path = AssembleGridPath(parent, goalState, startState, start, end, coordinates, sizeY, sizeZ);
            for (var index = 0; index < path.Count - 1; index++)
            {
                if (path[index].DistanceTo(path[index + 1]) < settings.MinimumSegmentLength - GridCoordinateMerge) return null;
            }

            // 过渡数只作代价偏好（叠排基准偏移由候选工厂实测判定）；硬上限实测会误杀需要一次扭转的绕行。
            if (CountPlanarTransitions(path, settings.StartThicknessAxis) < 0) return null;
            return path;
        }

        private static List<Point3d> AssembleGridPath(Dictionary<long, long> parent, long goalState, long startState,
            GridStub start, GridStub end, double[][] coordinates, int sizeY, int sizeZ)
        {
            var nodes = new List<Point3d>();
            for (var state = goalState; ; state = parent[state])
            {
                var node = (int)(state / GridStatesPerNode);
                nodes.Add(new Point3d(coordinates[0][node / sizeZ / sizeY], coordinates[1][node / sizeZ % sizeY],
                    coordinates[2][node % sizeZ]));
                if (state == startState || !parent.ContainsKey(state)) break;
            }

            nodes.Reverse();
            var path = new List<Point3d> { start.Lap };
            foreach (var point in start.Fixed.Skip(1).Concat(nodes.Skip(1)).Concat(end.Fixed.Reverse().Skip(1)))
                AppendSimplified(path, point);
            return path;
        }

        private static int PreviousRunDirection(Dictionary<long, long> parent, long state, long startState, int startDirection)
        {
            var direction = (int)(state / 12 % 6);
            while (state != startState && parent.TryGetValue(state, out var previous))
            {
                var previousDirection = (int)(previous / 12 % 6);
                if (previousDirection != direction) return previousDirection;
                state = previous;
            }

            return startDirection == direction ? -1 : startDirection;
        }

        private static void AppendSimplified(List<Point3d> path, Point3d point)
        {
            if (path[path.Count - 1].DistanceTo(point) <= GridCoordinateMerge) return;
            if (path.Count >= 2)
            {
                var previous = AxisDirectionIndex(path[path.Count - 2], path[path.Count - 1]);
                var next = AxisDirectionIndex(path[path.Count - 1], point);
                if (previous >= 0 && previous == next)
                {
                    path[path.Count - 1] = point;
                    return;
                }
            }

            path.Add(point);
        }

        private static bool Contains(GridBox box, double[] point)
        {
            for (var axis = 0; axis < 3; axis++)
                if (point[axis] <= box.Min[axis] || point[axis] >= box.Max[axis]) return false;
            return true;
        }

        private static double[] ToArray(Point3d point) => new[] { point.X, point.Y, point.Z };

        private static void HeapPush(List<KeyValuePair<double, long>> heap, double key, long value)
        {
            heap.Add(new KeyValuePair<double, long>(key, value));
            var child = heap.Count - 1;
            while (child > 0)
            {
                var parentIndex = (child - 1) / 2;
                if (heap[parentIndex].Key <= heap[child].Key) break;
                var swap = heap[parentIndex];
                heap[parentIndex] = heap[child];
                heap[child] = swap;
                child = parentIndex;
            }
        }

        private static KeyValuePair<double, long> HeapPop(List<KeyValuePair<double, long>> heap)
        {
            var top = heap[0];
            var last = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0) return top;
            heap[0] = last;
            var index = 0;
            while (true)
            {
                var left = index * 2 + 1;
                var right = left + 1;
                var smallest = index;
                if (left < heap.Count && heap[left].Key < heap[smallest].Key) smallest = left;
                if (right < heap.Count && heap[right].Key < heap[smallest].Key) smallest = right;
                if (smallest == index) break;
                var swap = heap[smallest];
                heap[smallest] = heap[index];
                heap[index] = swap;
                index = smallest;
            }

            return top;
        }
    }
}
