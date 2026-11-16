using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;
using GeometRi;
namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeCopperJointGeometryEditor
    {
        internal bool MoveRouteJointPoints(BridgeDesignSample baseline, BridgeDesignRoute route, PlanarBendBusbarSetting native,
            IReadOnlyDictionary<string, BridgeDesignObject> before, BridgeDesignObject[] changes, BridgeOffsetCopperChange offsetChange,
            bool preserveOffsetHeads = false)
        {
            var points = native.PointList.Select(p => baseline.Coordinates.ToDraft(new[] { p.X, p.Y, p.Z })).ToArray();
            var joints = before.Values.Where(o => o.Phase == route.Phase).ToArray();
            var anchors = points.Select(p => joints.FirstOrDefault(j =>
                p[0] >= j.X - baseline.Parameters.OverlapLengthMm - .15d && p[0] <= j.X + .15d)).ToArray();
            // 搭接后截面的物理入口按排宽定位，独立搭接参数不能使这个入口失去所属断点。
            foreach (var index in new[] { 0, points.Length - 1 })
                if (anchors[index] == null) anchors[index] = joints.FirstOrDefault(j => Math.Abs(points[index][0] - (j.X - native.BusBarWidth)) < .15d);
            var isOffset = offsetChange != null && route.CopperSectionId == offsetChange.SectionId;
            if (isOffset && preserveOffsetHeads)
            {
                // 大偏移三个物理方向共用段号。只有段排两端的实际加工端点绑定断点，
                // 内部转角即使落入某个搭接X区间，也必须随整组工艺中心移动。
                var path = baseline.CopperEditRoutes.Single(r => r.Phase == route.Phase);
                var section = Enumerable.Range(1, path.PointIds.Count - 1)
                    .Single(i => BridgeCopperSection.IdFor(i) == offsetChange.SectionId);
                var entry = before[path.PointIds[section - 1]]; var exit = before[path.PointIds[section]];
                Array.Clear(anchors, 0, anchors.Length);
                if (Math.Abs(points[0][0] - (entry.X - native.BusBarWidth)) < .15d) anchors[0] = entry;
                if (Math.Abs(points[points.Length - 1][0] - exit.X) < .15d) anchors[points.Length - 1] = exit;
            }
            var rigidHead = false;
            if (!isOffset || preserveOffsetHeads)
            {
                rigidHead = AnchorOrdinaryBridgeEnd(points, anchors, native.BusBarThickness, false);
                // 四点单桥头按B→A输出，尾部长直段不能反向解释成第二个桥头并覆盖入口归属。
                if ((!rigidHead && !isOffset) || points.Length >= 6)
                    rigidHead |= AnchorOrdinaryBridgeEnd(points, anchors, native.BusBarThickness, true);
            }
            var shifts = anchors.Select(anchor =>
            {
                if (anchor == null) return isOffset ? offsetChange.Shift : 0d;
                var changed = changes.FirstOrDefault(j => j.Id == anchor.Id);
                return changed == null ? 0d : changed.X - anchor.X;
            }).ToArray();
            if (shifts.All(shift => Math.Abs(shift) < .000001d)) return false;
            if (rigidHead)
            {
                var reserve = Math.Max(native.BarBendingRadius, native.VerticalBendingRadius);
                for (var index = 1; index < points.Length; index++)
                {
                    if (Math.Abs(shifts[index] - shifts[index - 1]) < .000001d) continue;
                    var oldSpan = points[index][0] - points[index - 1][0];
                    var newSpan = oldSpan + shifts[index] - shifts[index - 1];
                    if (Math.Abs(points[index][1] - points[index - 1][1]) > .001d ||
                        Math.Abs(points[index][2] - points[index - 1][2]) > .001d ||
                        Math.Sign(oldSpan) * newSpan <= reserve + .001d)
                        throw new InvalidOperationException("段排长度不足以保留原桥头和折弯余量，不能拉斜、折返或压缩桥头：" + native.GroupKey);
                }
            }
            // 一次从原路径计算，避免多个断点联动时重复移动同一个加工点。
            native.PointList = points.Select((point, index) =>
            {
                var world = baseline.Coordinates.ToAssembly(new[] { point[0] + shifts[index], point[1], point[2] });
                return new Point3d(world[0], world[1], world[2]);
            }).ToList();
            return true;
        }

        private bool AnchorOrdinaryBridgeEnd(double[][] points, BridgeDesignObject[] anchors, double thickness, bool reverse)
        {
            if (points.Length < 4) return false;
            var indices = reverse ? new[] { points.Length - 1, points.Length - 2, points.Length - 3 } : new[] { 0, 1, 2 };
            var a = points[indices[0]]; var b = points[indices[1]]; var c = points[indices[2]];
            if (anchors[indices[0]] == null || (b[0] - a[0]) * (c[0] - b[0]) <= 0d ||
                Math.Abs(Math.Abs(c[0] - b[0]) - 50d) > .15d ||
                Math.Abs(a[1] - b[1]) > .001d || Math.Abs(b[1] - c[1]) > .001d || Math.Abs(a[2] - b[2]) > .001d ||
                Math.Abs(Math.Abs(c[2] - b[2]) - thickness) > .15d) return false;
            // 正式单桥头有入口直头、50mm过渡和主体连接三点；外层叠排会改变第一直段的投影长度。
            anchors[indices[1]] = anchors[indices[2]] = anchors[indices[0]];
            return true;
        }

    }
}
