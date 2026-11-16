using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CKBusbarBridge.Prototype
{
    // 仅用于二维显示；工程净距和支撑校验继续使用原加工几何。
    public sealed class BridgeCopperRoundedProjection
    {
        public Geometry Create(BridgeDesignRoute route, BridgeCoordinateFrame coordinates, bool sideView)
        {
            var native = route.CopyNativePreview();
            if (native?.PointList == null || native.PointList.Count < 3 || native.WidthVector == null || native.StartBusbarVector == null ||
                native.BarBendingRadius <= 0 || native.BusBarThickness <= 0 || coordinates == null)
                return null;
            var width = coordinates.ToDraftDirection(new[] { native.WidthVector.X, native.WidthVector.Y, native.WidthVector.Z });
            var normalAxis = sideView ? 1 : 2;
            if (Math.Abs(Math.Abs(width[normalAxis]) - 1) > 1e-6) return null;
            var draft = native.PointList.Select(p => coordinates.ToDraft(new[] { p.X, p.Y, p.Z })).ToArray();
            if (draft.Any(p => Math.Abs(p[normalAxis] - draft[0][normalAxis]) > .001)) return null;
            var points = draft.Select(p => new Point(p[0], sideView ? -p[2] : p[1])).ToArray();
            var directions = new Vector[points.Length - 1];
            var offsets = new Vector[directions.Length];
            for (var i = 0; i < directions.Length; i++)
            {
                directions[i] = points[i + 1] - points[i];
                if (directions[i].Length < .001) return null;
                directions[i].Normalize();
                var along = new GeometRi.Vector3d(native.PointList[i], native.PointList[i + 1]).Normalized;
                var thickness = along.Cross(native.WidthVector).Normalized;
                if (thickness.Dot(native.StartBusbarVector) < 0)
                    thickness = new GeometRi.Vector3d(-thickness.X, -thickness.Y, -thickness.Z);
                var axis = coordinates.ToDraftDirection(new[] { thickness.X, thickness.Y, thickness.Z });
                offsets[i] = new Vector(axis[0], sideView ? -axis[2] : axis[1]) * (native.IsCenter ? 0 : native.BusBarThickness / 2);
            }
            // 非中面路径先恢复实体中面，转角取两条偏移直线的交点。
            var centers = (Point[])points.Clone();
            centers[0] += offsets[0]; centers[centers.Length - 1] += offsets.Last();
            for (var i = 1; i < centers.Length - 1; i++)
            {
                var cross = Vector.CrossProduct(directions[i - 1], directions[i]);
                centers[i] += offsets[i - 1];
                if (Math.Abs(cross) > 1e-6)
                    centers[i] += directions[i - 1] * (Vector.CrossProduct(offsets[i] - offsets[i - 1], directions[i]) / cross);
            }
            var radius = native.BarBendingRadius + native.BusBarThickness / 2;
            var trims = new double[centers.Length];
            for (var i = 1; i < centers.Length - 1; i++)
            {
                var dot = Math.Max(-1, Math.Min(1, Vector.Multiply(directions[i - 1], directions[i])));
                if (dot < -1 + 1e-6) return null;
                trims[i] = radius * Math.Tan(Math.Acos(dot) / 2);
            }
            for (var i = 0; i < directions.Length; i++)
                if (trims[i] + trims[i + 1] >= (centers[i + 1] - centers[i]).Length - .001) return null;
            var path = new StreamGeometry();
            using (var context = path.Open())
            {
                context.BeginFigure(centers[0], false, false);
                for (var i = 1; i < centers.Length - 1; i++)
                {
                    var start = centers[i] - directions[i - 1] * trims[i];
                    var end = centers[i] + directions[i] * trims[i];
                    context.LineTo(start, true, false);
                    if (trims[i] > .001)
                        context.ArcTo(end, new Size(radius, radius), 0, false,
                            Vector.CrossProduct(directions[i - 1], directions[i]) > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
                    else context.LineTo(end, true, false);
                }
                context.LineTo(centers.Last(), true, false);
            }
            return path.GetWidenedPathGeometry(new Pen(Brushes.Black, native.BusBarThickness), .01, ToleranceType.Absolute);
        }
    }
}
