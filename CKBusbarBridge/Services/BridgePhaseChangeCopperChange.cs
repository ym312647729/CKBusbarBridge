using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Prototype
{
    /// <summary>保留反读换相工艺，端头随断点、动作组随段中心；不重新铺排铜排。</summary>
    internal sealed class BridgePhaseChangeCopperChange
    {
        private sealed class Member
        {
            internal PlanarBendBusbarSetting Native;
            internal double[][] Points;
            internal int[] Anchors;
        }

        private readonly Dictionary<string, Member> _members = new Dictionary<string, Member>();
        private BridgeDesignSample _sample;
        private double _startShift, _endShift, _coreShift;
        internal string SectionId { get; private set; }

        internal static IReadOnlyList<BridgePhaseChangeCopperChange> Create(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> proposed)
        {
            var result = new List<BridgePhaseChangeCopperChange>();
            if (sample.ScenarioKey != "FaceToFace") return result;
            var before = sample.Objects.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
            var after = proposed.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
            var a = sample.CopperEditRoutes.SingleOrDefault(r => r.Phase == "A");
            var c = sample.CopperEditRoutes.SingleOrDefault(r => r.Phase == "C");
            if (a == null || c == null) return result;
            if (a.PointIds.Count != c.PointIds.Count)
                throw new InvalidOperationException("换相三相断点数量不一致，无法核验段排。");
            for (var i = 1; i < a.PointIds.Count; i++)
            {
                // 用电气相别的接口顺序识别换相，柜间平移不会颠倒A/C相的顺序。
                var start = before[a.PointIds[i - 1]]; var end = before[a.PointIds[i]];
                if ((start.Y - before[c.PointIds[i - 1]].Y) * (end.Y - before[c.PointIds[i]].Y) >= 0d) continue;
                if (!after.ContainsKey(start.Id) || !after.ContainsKey(end.Id)) continue; // 拓扑增删由原规划器处理。
                var startShift = after[start.Id].X - start.X; var endShift = after[end.Id].X - end.X;
                if (Math.Abs(startShift) < .001d && Math.Abs(endShift) < .001d) continue;
                var change = new BridgePhaseChangeCopperChange { _sample = sample, SectionId = BridgeCopperSection.IdFor(i),
                    _startShift = startShift, _endShift = endShift };
                change.Initialize(start, end, after[start.Id], after[end.Id]);
                result.Add(change);
            }
            return result;
        }

        private void Initialize(BridgeDesignObject start, BridgeDesignObject end,
            BridgeDesignObject newStart, BridgeDesignObject newEnd)
        {
            var routes = _sample.Routes.Where(r => r.CopperSectionId == SectionId).ToArray();
            if (routes.Select(r => r.Phase).Distinct().Count() != 3)
                throw new InvalidOperationException("换相段缺少完整三相实际铜排，无法核验工艺长度。");
            var width = routes.Max(r => r.SectionWidth);
            foreach (var route in routes)
            {
                var native = route.CopyUnraisedNativePreview();
                if (native?.PointList == null || native.PointList.Count < 2)
                    throw new InvalidOperationException("换相段缺少原始工艺路径，无法调整长度。");
                var points = native.PointList.Select(p => _sample.Coordinates.ToDraft(new[] { p.X, p.Y, p.Z })).ToArray();
                var anchors = points.Select((p, index) => index != 0 && index != points.Length - 1 ? 0 :
                    Math.Abs(p[0] - (start.X - width)) < .15d ? -1 : Math.Abs(p[0] - end.X) < .15d ? 1 : 0).ToArray();
                // 原模板前桥头为“排宽+40 mm”直段和50 mm斜段，三点必须整体随端头移动。
                AnchorBridge(native, points, anchors, false);
                AnchorBridge(native, points, anchors, true);
                _members.Add(route.Id, new Member { Native = native, Points = points, Anchors = anchors });
            }

            var cMembers = routes.Where(r => r.Phase == "C").Select(r => _members[r.Id]).ToArray();
            var actions = cMembers.SelectMany(member => Enumerable.Range(1, member.Points.Length - 1)
                .Where(i => Math.Abs(member.Points[i][2] - member.Points[i - 1][2]) > member.Native.BusBarThickness + .001d &&
                    Math.Abs(member.Points[i][0] - member.Points[i - 1][0]) < .001d &&
                    Math.Abs(member.Points[i][1] - member.Points[i - 1][1]) < .001d)
                .SelectMany(i => new[] { member.Points[i][0], member.Points[i - 1][0] })).ToArray();
            if (actions.Length < 4)
                throw new InvalidOperationException("换相段缺少可核验的C相升降工艺，无法调整长度。");
            var coreCenter = (actions.Min() + actions.Max()) / 2d;
            // 用户断点为搭接后截面，原换相规划坐标相对它偏置半排宽；独立搭接长度不改变基准。
            var center = (newStart.X + newEnd.X - width) / 2d;
            _coreShift = center - coreCenter;

            // 保留实际动作跨度（含原绝缘距离、叠排包络和折弯余量），不因手改段长压缩工艺。
            var envelope = routes.GroupBy(r => r.Phase).Max(group =>
                StackEnvelope(group.Select(r => _members[r.Id]).ToArray(), start.X - width, end.X));
            var reserve = FaceToFaceModelBusbarPlanner.CalculatePhaseChangePerpendicularBendReserve(width, envelope,
                _members.Values.Max(m => m.Native.BendAllowance));
            var halfRequired = (actions.Max() - actions.Min()) / 2d + reserve;
            foreach (var member in _members.Values)
            for (var i = 1; i < member.Points.Length; i++)
            {
                var first = member.Anchors[i - 1]; var second = member.Anchors[i];
                if (first == second || first != 0 && second != 0) continue;
                var anchorIndex = first == 0 ? i : i - 1; var coreIndex = first == 0 ? i - 1 : i;
                var anchor = member.Anchors[anchorIndex];
                var radius = Math.Max(member.Native.BarBendingRadius, member.Native.VerticalBendingRadius);
                var boundary = anchor < 0 ? start.X - width : end.X;
                // 刚性端头之后仍须容纳原折弯半径；使用两侧较严约束，保证动作中心不偏移。
                halfRequired = Math.Max(halfRequired, anchor < 0
                    ? coreCenter - member.Points[coreIndex][0] + member.Points[anchorIndex][0] - boundary + radius + .001d
                    : member.Points[coreIndex][0] - coreCenter + boundary - member.Points[anchorIndex][0] + radius + .001d);
            }
            var minimum = Math.Ceiling((2d * halfRequired - width + _sample.Parameters.OverlapLengthMm) * 10d - .000001d) / 10d;
            var length = newEnd.X - newStart.X + _sample.Parameters.OverlapLengthMm;
            if (length < minimum - .001d)
                throw new InvalidOperationException("换相段排端部最小长度为" + minimum.ToString("0.0", CultureInfo.InvariantCulture) +
                    " mm（含搭接及原换相工艺、端头和折弯余量），本次联动会使换相段过短，未提交修改。");
        }

        private static double StackEnvelope(Member[] members, double startX, double endX)
        {
            var minimum = double.PositiveInfinity; var maximum = double.NegativeInfinity;
            foreach (var member in members)
            foreach (var point in member.Points)
            {
                if (Math.Abs(point[0] - startX) >= .15d && Math.Abs(point[0] - endX) >= .15d) continue;
                minimum = Math.Min(minimum, point[2] - member.Native.BusBarThickness / 2d);
                maximum = Math.Max(maximum, point[2] + member.Native.BusBarThickness / 2d);
            }
            if (double.IsInfinity(minimum))
                throw new InvalidOperationException("换相段缺少实际叠排端部接口，无法核验工艺长度。");
            return maximum - minimum;
        }

        private static void AnchorBridge(PlanarBendBusbarSetting native, double[][] points, int[] anchors, bool reverse)
        {
            if (points.Length < 4) return;
            var indices = reverse ? new[] { points.Length - 1, points.Length - 2, points.Length - 3 } : new[] { 0, 1, 2 };
            var a = points[indices[0]]; var b = points[indices[1]]; var c = points[indices[2]];
            if (anchors[indices[0]] == 0 || Math.Abs(Math.Abs(b[0] - a[0]) - native.BusBarWidth - 40d) > .15d ||
                Math.Abs(Math.Abs(c[0] - b[0]) - 50d) > .15d || Math.Abs(a[1] - b[1]) > .001d ||
                Math.Abs(b[1] - c[1]) > .001d || Math.Abs(a[2] - b[2]) > .001d ||
                Math.Abs(Math.Abs(c[2] - b[2]) - native.BusBarThickness) > .15d) return;
            anchors[indices[1]] = anchors[indices[2]] = anchors[indices[0]];
        }

        internal PlanarBendBusbarSetting Move(BridgeDesignRoute route)
        {
            var member = _members[route.Id]; var native = route.CopyUnraisedNativePreview();
            native.PointList = member.Points.Select((point, i) =>
            {
                var draft = (double[])point.Clone();
                draft[0] += member.Anchors[i] < 0 ? _startShift : member.Anchors[i] > 0 ? _endShift : _coreShift;
                var world = _sample.Coordinates.ToAssembly(draft); return new Point3d(world[0], world[1], world[2]);
            }).ToList();
            return native;
        }
    }
}
