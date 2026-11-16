using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GeometRi;

namespace CKBusbarBridgePreview
{
    /// <summary>只调整中间排柜端搭接层，不移动柜端连接排或中间工艺区。</summary>
    internal static class SideToSideLapAdapter
    {
        internal static void AdaptHorizontal(PlanarBendBusbarSetting[] pieces,
            IReadOnlyList<IReadOnlyList<Point3d>> b, IReadOnlyList<IReadOnlyList<Point3d>> a,
            bool first, bool last)
        {
            foreach (var group in pieces.GroupBy(MemberKey))
            {
                var parts = group.Key.Split('-');
                var phase = "ABC".IndexOf(parts[0], StringComparison.Ordinal);
                if (parts.Length != 2 || phase < 0 || !int.TryParse(parts[1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number) || number < 1 || number > b[phase].Count)
                    throw new InvalidOperationException("侧对侧搭接排缺少有效相别或叠排序号。");
                var members = group.ToArray();
                var entry = members.First(); var exit = members.Last();
                if (first) SetTerminal(entry, true, b[phase].OrderBy(p => p.Y).ElementAt(number - 1).Y, false, true);
                if (last) SetTerminal(exit, false, a[phase].OrderBy(p => p.Y).ElementAt(number - 1).Y, false, false);
                foreach (var member in members) RemoveCollinearPlanarPoints(member);
                var milled = members.Where(p => p.IsMilledBar).ToArray();
                if (milled.Length != 0)
                {
                    if (milled.Length != 3) throw new InvalidOperationException("铣排组合体必须保留三块实体。");
                    foreach (var piece in milled)
                    {
                        piece.MilledBarStartLength = milled.First().GetDevelopedLength();
                        piece.MilledBarEndLength = milled.Last().GetDevelopedLength();
                    }
                    if (milled.Sum(p => p.GetDevelopedLength()) > 3000d + .001d)
                        throw new InvalidOperationException("柜端变厚搭接后铣排组合体展开长度超过3000 mm。");
                }
            }
        }

        internal static void SetTerminal(PlanarBendBusbarSetting shape, bool entry, double contact,
            bool vertical, bool existingBridge)
        {
            if (double.IsNaN(contact) || double.IsInfinity(contact))
                throw new ArgumentException("柜端接触层必须为有限值。", nameof(contact));
            EnsureKinds(shape);
            Func<Point3d, double> coordinate = p => vertical ? p.Z : p.Y;
            Action<Point3d, double> set = (p, value) => { if (vertical) p.Z = value; else p.Y = value; };
            var points = shape.PointList;
            var terminal = entry ? 0 : points.Count - 1;
            if (Math.Abs(coordinate(points[terminal]) - contact) <= .001d) return;
            if (existingBridge)
            {
                if (points.Count < 4) throw new InvalidOperationException("柜端桥头缺少完整直段和过渡段。");
                set(points[terminal], contact);
                set(points[entry ? 1 : points.Count - 2], contact);
            }
            else
            {
                var end = points[terminal].Copy();
                var neighbor = points[entry ? 1 : points.Count - 2];
                var sign = entry ? 1d : -1d;
                var length = sign * (neighbor.X - end.X);
                var straight = shape.BusBarWidth + 40d;
                var transition = 50d;
                if (length <= straight + transition + Math.Max(shape.BarBendingRadius, shape.BusBarThickness) ||
                    Math.Abs(end.Y - neighbor.Y) > .001d || Math.Abs(end.Z - neighbor.Z) > .001d)
                    throw new InvalidOperationException("柜端直段不足以放置变厚搭接过渡：" + shape.GroupKey);
                var outer = end.Copy(); outer.X += sign * straight; set(outer, contact);
                var inner = end.Copy(); inner.X += sign * (straight + transition);
                set(points[terminal], contact);
                var insert = entry ? 1 : points.Count - 1;
                points.InsertRange(insert, entry ? new[] { outer, inner } : new[] { inner, outer });
                shape.PreviewBendPointKinds.InsertRange(insert, new[] {
                    BusbarPreviewBendPointKind_e.Planar, BusbarPreviewBendPointKind_e.Planar });
            }
            // 柜端局部路径沿柜间方向前进；过渡不能侵入已有折弯或反向。
            var start = entry ? 0 : points.Count - 3;
            for (var i = start; i < start + 2; i++)
                if (points[i + 1].X - points[i].X <= .001d)
                    throw new InvalidOperationException("柜端搭接过渡发生重合或反向：" + shape.GroupKey);
        }

        internal static void RemoveCollinearPlanarPoints(PlanarBendBusbarSetting shape)
        {
            EnsureKinds(shape);
            for (var i = shape.PointList.Count - 2; i > 0; i--)
            {
                if (shape.PreviewBendPointKinds[i] != BusbarPreviewBendPointKind_e.Planar) continue;
                var before = new Vector3d(shape.PointList[i - 1], shape.PointList[i]);
                var after = new Vector3d(shape.PointList[i], shape.PointList[i + 1]);
                if (before.Norm > .001d && after.Norm > .001d &&
                    before.Normalized.Dot(after.Normalized) > 1d - 1e-10d)
                {
                    shape.PointList.RemoveAt(i);
                    shape.PreviewBendPointKinds.RemoveAt(i);
                }
            }
        }

        private static void EnsureKinds(PlanarBendBusbarSetting shape)
        {
            if (shape.PreviewBendPointKinds == null || shape.PreviewBendPointKinds.Count == 0)
                shape.PreviewBendPointKinds = shape.PointList.Select(p => shape.IsVerticalBend
                    ? BusbarPreviewBendPointKind_e.Vertical : BusbarPreviewBendPointKind_e.Planar).ToList();
            else if (shape.PreviewBendPointKinds.Count != shape.PointList.Count)
                throw new InvalidOperationException("铜排折弯类型数量与路径点不一致：" + shape.GroupKey);
        }

        internal static string MemberKey(PlanarBendBusbarSetting shape) =>
            string.Join("-", shape.GroupKey.Split('/')[0].Split('-').Take(2));

        internal static void ValidateSeparateMembers(IEnumerable<PlanarBendBusbarSetting> pieces)
        {
            foreach (var phase in pieces.GroupBy(p => p.GroupKey.Substring(0, 1)))
            {
                var result = PlanarBusbarClearanceValidator.EvaluateMemberStraightSegments(phase.ToArray(), MemberKey);
                if (!result.IsClear)
                    throw new InvalidOperationException("柜端变厚搭接或自动分段导致同相不同叠排实体相穿：" +
                        result.FirstGroupKey + "/" + result.SecondGroupKey);
            }
        }
    }
}
