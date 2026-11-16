using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridge.DesignGeometry;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;
using GeometRi;
using Newtonsoft.Json;

namespace CKBusbarBridge.Services
{
    // 求已找到的轴向布局在既定动作顺序内的最小值；不把有限次Worker搜索冒充全局最优证明。
    internal sealed class FaceToWallMinimumSpan
    {
        internal const string Scope = "Worker候选布局及桥向动作顺序内";
        private const double Epsilon = .000001d;
        private readonly Graph _graph = new Graph();
        private readonly LinkageRouteWorkerRequestDto _request;
        private readonly LinkageRouteWorkerSolutionDto _solution;
        private readonly double _overlap, _end;
        private readonly int _startNode, _endNode;
        private readonly CancellationToken _cancellation;
        internal double MinimumLength { get; }
        internal IReadOnlyList<string> ControllingConstraints { get; }

        internal FaceToWallMinimumSpan(LinkageRouteWorkerRequestDto request,
            LinkageRouteWorkerSolutionDto solution, double overlap, CancellationToken cancellation)
        {
            if (request == null || solution == null || !Finite(overlap) || overlap <= 0 ||
                solution.IsRelaxedClearanceSolution || !new FaceToWallWorkerSolutions().HasStraightWallLap(request, solution))
                throw new InvalidOperationException("特殊段最小长度计算需要严格绝缘、墙端同向夹接的完整Worker候选。");
            if (request.Obstacles != null && request.Obstacles.Count != 0)
                throw new InvalidOperationException("特殊段最小长度暂不支持移动布局中的额外障碍物，不能给出最小值。");
            _request = request; _solution = solution; _overlap = overlap; _cancellation = cancellation;
            var starts = request.Groups.Select(g => g.StartTerminal.RouteAnchor.X).ToArray();
            var ends = request.Groups.Select(g => g.EndTerminal.RouteAnchor.X +
                g.EndTerminal.LengthDirection.X * g.EndTerminal.Members[0].FixedBusbar.EndLapWidth).ToArray();
            if (starts.Max() - starts.Min() > .01d || ends.Max() - ends.Min() > .01d ||
                request.Groups.Any(g => Math.Abs(g.StartTerminal.LengthDirection.X - 1d) > Epsilon ||
                    Math.Abs(g.EndTerminal.LengthDirection.X - 1d) > Epsilon))
                throw new InvalidOperationException("特殊段最小长度要求三组具有共同的桥向始端及墙端截面。");
            _end = ends.Average();
            _startNode = _graph.Node(starts.Average()); _endNode = _graph.Node(_end);
            var solids = BuildGeometry();
            _graph.OrderPlanes();
            AddClearances(solids);
            // 既有Worker终端要求虚拟近端在普通排末端前方，保持相同的接口定义。
            _graph.Require(_startNode, _endNode,
                request.Groups.Max(g => g.EndTerminal.Members[0].FixedBusbar.EndLapWidth) + .00001d, "墙端虚拟接口前置空间");
            var distances = _graph.Solve(_startNode, cancellation);
            MinimumLength = distances[_endNode] + overlap;
            ControllingConstraints = _graph.CriticalConstraints(_endNode);
            if (!Finite(MinimumLength) || MinimumLength <= overlap)
                throw new InvalidOperationException("特殊段最小长度未形成有效的有限正跨度。");
        }

        internal static double RoundUpToTen(double minimum)
        {
            if (!Finite(minimum) || minimum <= 0)
                throw new InvalidOperationException("特殊段最小长度必须为有限正数。");
            // 只消除计算噪声，不将实际超过十位边界的小数向下截断。
            var nearest = Math.Round(minimum / 10d) * 10d;
            return Math.Abs(minimum - nearest) <= 1e-8d ? nearest : Math.Ceiling(minimum / 10d) * 10d;
        }

        internal LinkageRouteWorkerSolutionDto CreateSolution(double adoptedLength)
        {
            if (!Finite(adoptedLength) || adoptedLength < MinimumLength - Epsilon)
                throw new InvalidOperationException("采用长度小于候选布局所需最小长度。");
            var distances = _graph.Solve(_startNode, _cancellation,
                new Constraint(_startNode, _endNode, adoptedLength - _overlap));
            if (Math.Abs(distances[_endNode] + _overlap - adoptedLength) > .01d)
                throw new InvalidOperationException("取整后的特殊段长度与布局约束不一致。");
            var copy = Copy(_solution);
            var origin = _end - distances[_endNode];
            foreach (var group in copy.GroupSolutions)
            {
                Map(group.AxisSkeleton, distances, origin);
                Map(group.ManufacturableCenterline, distances, origin);
                foreach (var part in group.PlanarParts ?? Array.Empty<LinkageRouteWorkerPlanarSettingDto>())
                    Map(part.PointList, distances, origin);
                foreach (var member in group.StackMembers)
                    foreach (var part in member.PlanarParts) Map(part.PointList, distances, origin);
                foreach (var accessory in group.Accessories ?? Array.Empty<LinkageRouteWorkerAccessoryDto>())
                    if (accessory.Setting != null) Map(accessory.Setting.PointList, distances, origin);
                group.ActualCopperLength = group.StackMembers.Sum(m => m.PlanarParts.Sum(p => Native(p).GetDevelopedLength()));
                // 原拓扑签名含旧几何信息，压缩后不得继续作为当前实体的身份依据。
                group.TopologySignature = null;
            }
            copy.ActualCopperLength = copy.GroupSolutions.Sum(g => g.ActualCopperLength);
            var copper = copy.GroupSolutions.SelectMany(g => g.StackMembers.SelectMany(m => m.PlanarParts)
                .Select(p => Native(p, g.GroupKey))).ToArray();
            var geometry = new BridgeRaisedLapGeometry();
            var interfaceCopper = _request.Groups.SelectMany(group =>
                group.StartTerminal.Members.Select(member => TranslatedTerminal(member.FixedBusbar, group.GroupKey,
                    origin - group.StartTerminal.RouteAnchor.X)).Concat(group.EndTerminal.Members.Select(member =>
                        Native(member.FixedBusbar, group.GroupKey))));
            var bendInterference = geometry.FindRoundedMemberInterference(copper.Concat(interfaceCopper).ToArray(), b => b.GroupKey);
            if (bendInterference != null) throw new InvalidOperationException(bendInterference);
            var clearance = geometry.EvaluateActual(copper, b => b.GroupKey, _request.InsulationClearance);
            if (!clearance.IsClear)
                throw new InvalidOperationException("特殊段最小布局未通过原实体净距终检。");
            copy.MinimumClearance = clearance.MinimumDistance;
            return copy;
        }

        private List<Primitive> BuildGeometry()
        {
            var solids = new List<Primitive>();
            foreach (var group in _request.Groups)
            {
                _cancellation.ThrowIfCancellationRequested();
                var matching = _solution.GroupSolutions.Single(g => g.GroupKey == group.GroupKey);
                BindStack(matching.StackMembers);
                AddTerminal(solids, group.StartTerminal, _startNode, group.GroupKey, true, -1);
                AddTerminal(solids, group.EndTerminal, _endNode, group.GroupKey, false, matching.StackMembers[0].PlanarParts.Count);
                foreach (var member in matching.StackMembers)
                {
                    if (matching.Accessories != null && matching.Accessories.Count != 0)
                        throw new InvalidOperationException("附带接头附件的候选尚不支持最小长度压缩。");
                    var owner = group.GroupKey + "-" + member.MemberIndex;
                    LinkageRouteWorkerPlanarSettingDto previous = null;
                    for (var index = 0; index < member.PlanarParts.Count; index++)
                    {
                        var part = member.PlanarParts[index];
                        if (!part.IsCenter) throw new InvalidOperationException("最小长度计算要求铜排使用实际中心线。");
                        AddPart(solids, part, group.GroupKey, owner, false, index);
                        AddStraightConstraints(part, index == 0, index == member.PlanarParts.Count - 1);
                        if (previous != null)
                        {
                            var left = previous.PointList.Last(); var right = part.PointList.First();
                            _graph.Bind(_graph.Node(left.X), _graph.Node(right.X), right.X - left.X);
                        }
                        previous = part;
                    }
                    var first = member.PlanarParts.First().PointList.First();
                    var last = member.PlanarParts.Last().PointList.Last();
                    _graph.Bind(_startNode, _graph.Node(first.X), first.X - group.StartTerminal.RouteAnchor.X);
                    _graph.Bind(_endNode, _graph.Node(last.X), last.X - _end);
                }
            }
            var copper = _solution.GroupSolutions.SelectMany(group => group.StackMembers.SelectMany(member => member.PlanarParts)
                .Select(part => Native(part, group.GroupKey))).ToArray();
            var number = 0;
            foreach (var envelope in new BridgeRaisedLapPlanner().CreateWorkerFasteners(copper))
            {
                var solid = envelope.Solid;
                var anchor = _graph.Node(envelope.AnchorX);
                var x = HalfSpan(solid, 0); var y = HalfSpan(solid, 2); var z = HalfSpan(solid, 1);
                solids.Add(new Primitive { Phase = envelope.Phase, Owner = "hardware-" + number++, Terminal = true,
                    Name = envelope.Phase + "搭接紧固件", Left = anchor, Right = anchor,
                    LeftOffset = solid.Center.X - x - envelope.AnchorX, RightOffset = solid.Center.X + x - envelope.AnchorX,
                    X0 = solid.Center.X - x, X1 = solid.Center.X + x,
                    Y0 = solid.Center.Z - y, Y1 = solid.Center.Z + y, Z0 = solid.Center.Y - z, Z1 = solid.Center.Y + z });
            }
            return solids;
        }

        private void BindStack(IList<LinkageRouteWorkerStackedMemberDto> members)
        {
            var reference = members[0];
            foreach (var member in members.Skip(1))
            {
                if (member.PlanarParts.Count != reference.PlanarParts.Count)
                    throw new InvalidOperationException("叠排成员分件拓扑不同，无法保持逐层折弯错位计算最小长度。");
                for (var part = 0; part < reference.PlanarParts.Count; part++)
                {
                    var first = reference.PlanarParts[part].PointList; var next = member.PlanarParts[part].PointList;
                    if (first.Count != next.Count)
                        throw new InvalidOperationException("叠排成员折弯点数不同，无法保持逐层折弯错位计算最小长度。");
                    for (var point = 0; point < first.Count; point++)
                        _graph.Bind(_graph.Node(first[point].X), _graph.Node(next[point].X), next[point].X - first[point].X);
                }
            }
        }

        private void AddTerminal(List<Primitive> solids, LinkageRouteWorkerTerminalAssemblyDto terminal,
            int anchor, string phase, bool start, int section)
        {
            foreach (var member in terminal.Members)
            {
                var part = member.FixedBusbar;
                foreach (var point in part.PointList)
                    _graph.Bind(anchor, _graph.Node(point.X), point.X - (start ? terminal.RouteAnchor.X : _end));
                AddPart(solids, part, phase, phase + (start ? "-始端" : "-墙端"), true, section);
            }
        }

        private void AddPart(List<Primitive> solids, LinkageRouteWorkerPlanarSettingDto part,
            string phase, string owner, bool terminal, int section)
        {
            if (part?.PointList == null || part.PointList.Count < 2 || !part.IsCenter ||
                part.WidthVector == null || part.StartBusbarVector == null ||
                !Finite(part.BusBarWidth) || !Finite(part.BusBarThickness) || part.BusBarWidth <= 0 || part.BusBarThickness <= 0 ||
                part.PointList.Any(p => p == null || !Finite(p.X) || !Finite(p.Y) || !Finite(p.Z)))
                throw new InvalidOperationException(owner + "缺少有效的实际中心线或铜排截面。");
            var bar = Native(part, phase);
            foreach (var point in part.PointList)
            {
                var node = _graph.Node(point.X);
                if (!terminal) _graph.Require(node, _endNode, 0);
            }
            var actual = new BridgeRaisedLapGeometry().CreateStructureSolids(bar, new Vector3d(0, 1, 0));
            if (actual != null)
            {
                foreach (var solid in actual)
                {
                    var bend = solid.SegmentIndex < 0;
                    var a = part.PointList[bend ? -solid.SegmentIndex : solid.SegmentIndex - 1];
                    var b = part.PointList[bend ? -solid.SegmentIndex : solid.SegmentIndex];
                    var first = Math.Min(a.X, b.X); var last = Math.Max(a.X, b.X);
                    var x = HalfSpan(solid, 0); var y = HalfSpan(solid, 1); var z = HalfSpan(solid, 2);
                    var primitive = new Primitive { Phase = phase, Owner = owner, Part = part, Terminal = terminal, Bend = bend,
                        Section = section, Segment = solid.SegmentIndex,
                        ContactNormal = bend ? null : solid.ThicknessDirection,
                        TrimmedAlongX = !bend && last - first > Epsilon &&
                            (CornerTrim(part, solid.SegmentIndex - 1) > Epsilon || CornerTrim(part, solid.SegmentIndex) > Epsilon),
                        Name = owner + (bend ? "-折弯" : "-直段") + Math.Abs(solid.SegmentIndex),
                        Left = _graph.Node(first), Right = _graph.Node(last),
                        LeftOffset = solid.Center.X - x - first, RightOffset = solid.Center.X + x - last,
                        X0 = solid.Center.X - x, X1 = solid.Center.X + x,
                        Y0 = solid.Center.Y - y, Y1 = solid.Center.Y + y, Z0 = solid.Center.Z - z, Z1 = solid.Center.Z + z };
                    solids.Add(primitive);
                    if (!terminal)
                    {
                        _graph.Require(_startNode, primitive.Left, _request.ContainmentMinimum.X -
                            _request.Groups[0].StartTerminal.RouteAnchor.X - primitive.LeftOffset);
                        _graph.Require(primitive.Right, _endNode, primitive.RightOffset - (_request.ContainmentMaximum.X - _end));
                    }
                }
                return;
            }
            foreach (var item in BridgeCopperClearance.LongitudinalSolids(bar))
            {
                if (!Axis(item.Solid.LengthDirection) || !Axis(item.Solid.WidthDirection) || !Axis(item.Solid.ThicknessDirection))
                    throw new InvalidOperationException("特殊段最小长度仅支持XYZ轴向直段和正交折弯。");
                var points = item.Solid.GetVertices();
                var first = Math.Min(item.First.X, item.Last.X); var last = Math.Max(item.First.X, item.Last.X);
                solids.Add(new Primitive { Phase = phase, Owner = owner, Part = part, Terminal = terminal,
                    Name = owner + (item.First.DistanceTo(item.Last) <= Epsilon ? "-折弯" : "-直段") + item.Solid.SegmentIndex,
                    Bend = item.First.DistanceTo(item.Last) <= Epsilon,
                    Left = _graph.Node(first), Right = _graph.Node(last),
                    LeftOffset = points.Min(p => p.X) - first, RightOffset = points.Max(p => p.X) - last,
                    X0 = points.Min(p => p.X), X1 = points.Max(p => p.X),
                    Y0 = points.Min(p => p.Y), Y1 = points.Max(p => p.Y),
                    Z0 = points.Min(p => p.Z), Z1 = points.Max(p => p.Z) });
                if (!terminal)
                {
                    var solid = solids.Last();
                    _graph.Require(_startNode, solid.Left, _request.ContainmentMinimum.X -
                        _request.Groups[0].StartTerminal.RouteAnchor.X - solid.LeftOffset);
                    _graph.Require(solid.Right, _endNode, solid.RightOffset - (_request.ContainmentMaximum.X - _end));
                }
            }
        }

        private void AddStraightConstraints(LinkageRouteWorkerPlanarSettingDto part, bool first, bool last)
        {
            for (var index = 0; index < part.PointList.Count - 1; index++)
            {
                var a = part.PointList[index]; var b = part.PointList[index + 1];
                var delta = new Vector3d(a.X, a.Y, a.Z) - new Vector3d(b.X, b.Y, b.Z);
                if (!Axis(delta)) throw new InvalidOperationException("特殊段候选包含非轴向或零长度直段。");
                var minimum = _request.MinimumSegmentLength;
                var trim = CornerTrim(part, index) + CornerTrim(part, index + 1);
                minimum = Math.Max(minimum, trim);
                if (first && index == 0) minimum = Math.Max(minimum, part.StartLapWidth);
                if (last && index == part.PointList.Count - 2) minimum = Math.Max(minimum, part.EndLapWidth);
                if (Math.Abs(delta.X) > Epsilon)
                    _graph.Require(_graph.Node(Math.Min(a.X, b.X)), _graph.Node(Math.Max(a.X, b.X)), minimum,
                        FormattableString.Invariant($"{part.GroupKey}直段{index + 1}最小{minimum:0.###}mm"));
                else if (delta.Norm < minimum - .01d)
                    throw new InvalidOperationException("候选的横向或高度直段不足，移动普通段分界不能修复该制造约束。");
            }
        }

        private static double CornerTrim(LinkageRouteWorkerPlanarSettingDto part, int corner)
        {
            if (corner <= 0 || corner + 1 >= part.PointList.Count) return 0d;
            var incoming = Direction(part.PointList[corner - 1], part.PointList[corner]).Normalized;
            var outgoing = Direction(part.PointList[corner], part.PointList[corner + 1]).Normalized;
            var dot = Math.Max(-1d, Math.Min(1d, incoming.Dot(outgoing)));
            if (dot <= -1d + Epsilon) throw new InvalidOperationException("特殊段候选包含无法制造的回头折弯。");
            return (part.BarBendingRadius + part.BusBarThickness / 2d) * Math.Tan(Math.Acos(dot) / 2d);
        }

        private static Vector3d Direction(LinkageRouteWorkerCoordinateDto from, LinkageRouteWorkerCoordinateDto to)
            => new Vector3d(to.X - from.X, to.Y - from.Y, to.Z - from.Z);

        private void AddClearances(IReadOnlyList<Primitive> solids)
        {
            for (var i = 0; i < solids.Count; i++)
            {
                _cancellation.ThrowIfCancellationRequested();
                for (var j = i + 1; j < solids.Count; j++)
                {
                    var a = solids[i]; var b = solids[j];
                    if (a.Phase == b.Phase && (a.Bend || b.Bend))
                    {
                        if (a.Part == null || b.Part == null || ReferenceEquals(a.Part, b.Part) ||
                            Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0) <= Epsilon ||
                            Math.Min(a.Z1, b.Z1) - Math.Max(a.Z0, b.Z0) <= Epsilon) continue;
                        // 沿X已分开的圆角和另一物理排压缩后也要保持分开。原包络相交时不
                        // 强行决定先后，嵌套折弯的实际相穿由更细的扇区终检判断。
                        if (a.X1 <= b.X0 + Epsilon)
                            _graph.Require(a.Right, b.Left, a.RightOffset - b.LeftOffset, a.Name + "/" + b.Name + "同相圆角避让");
                        else if (b.X1 <= a.X0 + Epsilon)
                            _graph.Require(b.Right, a.Left, b.RightOffset - a.LeftOffset, b.Name + "/" + a.Name + "同相圆角避让");
                        continue;
                    }
                    if (a.Phase == b.Phase) PreserveFlatLap(a, b);
                    if ((a.Phase == b.Phase && (a.Terminal || b.Terminal)) ||
                        (a.Owner == b.Owner && a.X1 >= b.X0 - .01d && b.X1 >= a.X0 - .01d)) continue;
                    var clearance = a.Phase == b.Phase ? 0d : _request.InsulationClearance;
                    if (clearance == 0d && (Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0) <= .01d ||
                        Math.Min(a.Z1, b.Z1) - Math.Max(a.Z0, b.Z0) <= .01d)) continue;
                    var y = Gap(a.Y0, a.Y1, b.Y0, b.Y1); var z = Gap(a.Z0, a.Z1, b.Z0, b.Z1);
                    var squared = clearance * clearance - y * y - z * z;
                    if (squared < -Epsilon || (clearance == 0d && (y > Epsilon || z > Epsilon))) continue;
                    var required = Math.Sqrt(Math.Max(0d, squared));
                    if (a.X1 <= b.X0 + .01d)
                        _graph.Require(a.Right, b.Left, required + a.RightOffset - b.LeftOffset, a.Name + "/" + b.Name + "净距");
                    else if (b.X1 <= a.X0 + .01d)
                        _graph.Require(b.Right, a.Left, required + b.RightOffset - a.LeftOffset, b.Name + "/" + a.Name + "净距");
                    else if (required > Epsilon || (y < Epsilon && z < Epsilon))
                        throw new InvalidOperationException(FormattableString.Invariant(
                            $"候选固定横截面存在干涉：{a.Name}与{b.Name}，桥向范围[{a.X0:0.###},{a.X1:0.###}]/[{b.X0:0.###},{b.X1:0.###}]，横截面距离={Math.Sqrt(y * y + z * z):0.###}mm，要求={clearance:0.###}mm。"));
                }
            }
        }

        private void PreserveFlatLap(Primitive first, Primitive second)
        {
            if (first.ContactNormal == null || second.ContactNormal == null || !(first.TrimmedAlongX || second.TrimmedAlongX)) return;
            var left = first.Section < second.Section ? first : second;
            var right = ReferenceEquals(left, first) ? second : first;
            if (right.Section != left.Section + 1 ||
                (!left.Terminal && left.Segment != left.Part.PointList.Count - 1) || (!right.Terminal && right.Segment != 1)) return;
            var normal = left.ContactNormal;
            if (Math.Abs(normal.X) > Epsilon || Math.Abs(normal.Dot(right.ContactNormal)) < 1d - Epsilon) return;
            var facesAlongY = Math.Abs(normal.Y) > .5d;
            var faceGap = facesAlongY ? Gap(left.Y0, left.Y1, right.Y0, right.Y1) : Gap(left.Z0, left.Z1, right.Z0, right.Z1);
            var faceOverlap = facesAlongY ? Overlap(left.Y0, left.Y1, right.Y0, right.Y1) : Overlap(left.Z0, left.Z1, right.Z0, right.Z1);
            var transverseOverlap = facesAlongY ? Overlap(left.Z0, left.Z1, right.Z0, right.Z1) : Overlap(left.Y0, left.Y1, right.Y0, right.Y1);
            if (faceGap > Epsilon || faceOverlap > Epsilon || transverseOverlap <= Epsilon) return;
            var lapLength = Math.Min(left.Part.EndLapWidth, right.Part.StartLapWidth);
            var originalOverlap = Overlap(left.X0, left.X1, right.X0, right.X1);
            if (lapLength <= Epsilon || originalOverlap < lapLength - Epsilon) return;
            lapLength = Math.Min(lapLength, originalOverlap);
            // 保留原已满足的直面搭接。其他同相投影（如叠排间的局部贴面）不作为接口锁住。
            var context = left.Name + "/" + right.Name + "实际直面搭接";
            foreach (var start in new[] { left, right })
            foreach (var finish in new[] { left, right })
                _graph.Require(start.Left, finish.Right, lapLength + start.LeftOffset - finish.RightOffset, context);
        }

        private void Map(IEnumerable<LinkageRouteWorkerCoordinateDto> points, double[] distances, double origin)
        {
            foreach (var point in points ?? Array.Empty<LinkageRouteWorkerCoordinateDto>())
                point.X = origin + _graph.Interpolate(point.X, distances);
        }

        private static bool Axis(Vector3d value)
        {
            if (value == null || !Finite(value.Norm) || value.Norm <= Epsilon) return false;
            var v = value.Normalized;
            return new[] { Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z) }.Count(x => x > Epsilon) == 1;
        }
        private double HalfSpan(BridgeUiSolid solid, int axis)
        {
            Func<Vector3d, double> component = v => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
            return Math.Abs(component(solid.LengthDirection)) * solid.HalfLength +
                Math.Abs(component(solid.WidthDirection)) * solid.HalfWidth + Math.Abs(component(solid.ThicknessDirection)) * solid.HalfThickness;
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static double Gap(double a0, double a1, double b0, double b1) => Math.Max(0d, Math.Max(a0 - b1, b0 - a1));
        private static double Overlap(double a0, double a1, double b0, double b1) => Math.Min(a1, b1) - Math.Max(a0, b0);
        private static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value),
            new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
        private static PlanarBendBusbarSetting Native(LinkageRouteWorkerPlanarSettingDto p, string key = null)
            => new PlanarBendBusbarSetting { IsCenter = p.IsCenter, GroupKey = key ?? p.GroupKey,
                BusBarWidth = p.BusBarWidth, BusBarThickness = p.BusBarThickness, BarBendingRadius = p.BarBendingRadius,
                BendAllowance = p.BendAllowance, StartLapWidth = p.StartLapWidth, EndLapWidth = p.EndLapWidth,
                WidthVector = new Vector3d(p.WidthVector.X, p.WidthVector.Y, p.WidthVector.Z),
                StartBusbarVector = new Vector3d(p.StartBusbarVector.X, p.StartBusbarVector.Y, p.StartBusbarVector.Z),
                PointList = p.PointList.Select(x => new Point3d(x.X, x.Y, x.Z)).ToList() };

        private static PlanarBendBusbarSetting TranslatedTerminal(LinkageRouteWorkerPlanarSettingDto part, string phase, double offset)
        {
            var translated = Native(part, phase);
            translated.PointList = translated.PointList.Select(p => new Point3d(p.X + offset, p.Y, p.Z)).ToList();
            return translated;
        }

        private sealed class Primitive
        {
            internal int Left, Right;
            internal double LeftOffset, RightOffset, X0, X1, Y0, Y1, Z0, Z1;
            internal string Phase, Owner, Name;
            internal bool Terminal, Bend, TrimmedAlongX;
            internal LinkageRouteWorkerPlanarSettingDto Part;
            internal int Section, Segment;
            internal Vector3d ContactNormal;
        }
        private sealed class Constraint
        {
            internal readonly int From, To;
            internal readonly double Distance;
            internal readonly string Context;
            internal Constraint(int from, int to, double distance, string context = null)
            { From = from; To = to; Distance = distance; Context = context; }
        }
        private sealed class Graph
        {
            private readonly List<double> _planes = new List<double>();
            private readonly Dictionary<double, int> _nodes = new Dictionary<double, int>();
            private readonly List<Constraint> _edges = new List<Constraint>();
            private Constraint[] _parents;
            internal int Node(double x)
            {
                if (!Finite(x)) throw new InvalidOperationException("最小长度输入包含非有限桥向坐标。");
                var key = Math.Round(x, 6);
                if (_nodes.TryGetValue(key, out var index)) return index;
                index = _planes.Count; _nodes.Add(key, index); _planes.Add(key); return index;
            }
            internal void Require(int from, int to, double distance, string context = null)
                => _edges.Add(new Constraint(from, to, distance, context));
            internal void Bind(int from, int to, double distance)
            { Require(from, to, distance); Require(to, from, -distance); }
            internal void OrderPlanes()
            {
                var nodes = Enumerable.Range(0, _planes.Count).OrderBy(i => _planes[i]).ToArray();
                for (var i = 1; i < nodes.Length; i++) Require(nodes[i - 1], nodes[i], 0);
            }
            internal double[] Solve(int start, CancellationToken cancellation, Constraint extra = null)
            {
                var values = Enumerable.Repeat(double.NegativeInfinity, _planes.Count).ToArray(); values[start] = 0;
                _parents = new Constraint[values.Length];
                var edges = extra == null ? _edges : _edges.Concat(new[] { extra });
                for (var pass = 0; pass < values.Length; pass++)
                {
                    cancellation.ThrowIfCancellationRequested(); var changed = false;
                    foreach (var edge in edges)
                    {
                        var next = values[edge.From] + edge.Distance;
                        if (next <= values[edge.To] + Epsilon) continue;
                        values[edge.To] = next; _parents[edge.To] = edge; changed = true;
                    }
                    if (!changed)
                    {
                        if (values.Any(v => !Finite(v))) throw new InvalidOperationException("最小布局存在未连接到固定始端的动作。");
                        return values;
                    }
                }
                throw new InvalidOperationException("候选的搭接、折弯和净距约束相互冲突，不能确定最小长度。");
            }
            internal IReadOnlyList<string> CriticalConstraints(int end)
            {
                var result = new List<string>(); var visited = new HashSet<int>();
                while (visited.Add(end) && _parents[end] != null)
                {
                    var edge = _parents[end];
                    if (!string.IsNullOrWhiteSpace(edge.Context)) result.Add(edge.Context);
                    end = edge.From;
                }
                result.Reverse(); return result.Distinct().ToArray();
            }
            internal double Interpolate(double x, double[] values)
            {
                if (_nodes.TryGetValue(Math.Round(x, 6), out var exact)) return values[exact];
                var order = Enumerable.Range(0, _planes.Count).OrderBy(i => _planes[i]).ToArray();
                for (var i = 1; i < order.Length; i++)
                    if (x >= _planes[order[i - 1]] && x <= _planes[order[i]])
                    {
                        var left = order[i - 1]; var right = order[i];
                        return values[left] + (values[right] - values[left]) * (x - _planes[left]) / (_planes[right] - _planes[left]);
                    }
                throw new InvalidOperationException("候选辅助中心线超出已核验的最小布局范围。");
            }
        }
    }
}
