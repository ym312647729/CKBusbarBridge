using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.DesignGeometry
{
    internal sealed class BridgeRaisedLapClearance
    {
        internal string MemberKey { get; set; }
        internal double BottomClearanceMm { get; set; }
        internal double TopClearanceMm { get; set; }
    }

    internal sealed class BridgeRaisedLapLiftInterval
    {
        internal bool IsFeasible { get; set; }
        internal double MinimumLiftMm { get; set; }
        internal double MaximumLiftMm { get; set; }
        internal string FailureReason { get; set; }
    }

    internal sealed class BridgeRaisedLapEndResult
    {
        internal bool Success { get; set; }
        internal string FailureReason { get; set; }
        internal PlanarBendBusbarSetting Setting { get; set; }
        internal double TransitionProjectionMm { get; set; }
        internal double DevelopedLengthMm { get; set; }
    }

    /// <summary>草稿几何的局部搭接抬高；不接触模型，不累加修改输入设置。</summary>
    internal sealed class BridgeRaisedLapGeometry
    {
        internal const double FastenerDownwardProjectionMm = 35d;
        internal const double RequiredClearanceMm = 125d;
        private const double Tolerance = .000001d;
        private const double TransitionReferenceProjectionMm = 50d;
        private const double HeadStraightReserveMm = 40d;

        internal BridgeCopperClearanceEvaluation EvaluateActual(IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> selector, double required)
        {
            if (busbars == null || selector == null) throw new ArgumentNullException(busbars == null ? nameof(busbars) : nameof(selector));
            if (double.IsNaN(required) || double.IsInfinity(required) || required < 0d) throw new ArgumentOutOfRangeException(nameof(required));
            var records = busbars.SelectMany(bar => (CreateStructureSolids(bar, new Vector3d(0, 1, 0)) ?? BridgeCopperClearance.UiLongitudinalSolids(bar))
                .Select(solid => new { Solid = solid, Key = selector(bar),
                    X = AxisSpan(solid, 0), Y = AxisSpan(solid, 1), Z = AxisSpan(solid, 2) })).ToArray();
            var result = new BridgeCopperClearanceEvaluation { IsClear = true, MinimumDistance = double.PositiveInfinity };
            for (var i = 0; i < records.Length; i++)
            for (var j = i + 1; j < records.Length; j++)
            {
                var first = records[i]; var second = records[j];
                if (string.Equals(first.Key, second.Key, StringComparison.OrdinalIgnoreCase)) continue;
                // 仅用已构造实体的投影下界筛掉远距离对，避免圆角分片增加交互中的精确距离计算。
                var x = Math.Max(0, Math.Abs(first.Solid.Center.X - second.Solid.Center.X) - first.X - second.X);
                var y = Math.Max(0, Math.Abs(first.Solid.Center.Y - second.Solid.Center.Y) - first.Y - second.Y);
                var z = Math.Max(0, Math.Abs(first.Solid.Center.Z - second.Solid.Center.Z) - first.Z - second.Z);
                if (x * x + y * y + z * z >= result.MinimumDistance * result.MinimumDistance) continue;
                var distance = BridgeCopperClearance.UiPairDistance(first.Solid, second.Solid);
                if (distance >= result.MinimumDistance) continue;
                result.MinimumDistance = distance; result.FirstGroupKey = records[i].Key; result.SecondGroupKey = records[j].Key;
            }
            result.IsClear = result.MinimumDistance + Tolerance >= required;
            return result;
        }

        internal string FindRoundedMemberInterference(IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> phaseSelector)
        {
            const double penetrationTolerance = 1.0d;
            var records = busbars.Select((bar, index) =>
            {
                var solids = CreateStructureSolids(bar, new Vector3d(0, 1, 0), true);
                if (solids == null)
                    throw new InvalidOperationException("面对墙铜排缺少可核验的实际折弯截面：" + bar.GroupKey);
                return new { Bar = bar, Index = index + 1, Phase = phaseSelector(bar),
                    HasBend = solids.Any(solid => solid.SegmentIndex < 0),
                    Solids = solids.Select(solid => new { Solid = solid, X = AxisSpan(solid, 0),
                        Y = AxisSpan(solid, 1), Z = AxisSpan(solid, 2) }).ToArray() };
            }).ToArray();
            for (var i = 0; i < records.Length; i++)
            for (var j = i + 1; j < records.Length; j++)
            {
                var a = records[i]; var b = records[j];
                if (!(a.HasBend || b.HasBend) || !string.Equals(a.Phase, b.Phase, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var first in a.Solids)
                foreach (var second in b.Solids)
                {
                    if (first.Solid.SegmentIndex >= 0 && second.Solid.SegmentIndex >= 0 ||
                        first.X + second.X - Math.Abs(first.Solid.Center.X - second.Solid.Center.X) <= penetrationTolerance ||
                        first.Y + second.Y - Math.Abs(first.Solid.Center.Y - second.Solid.Center.Y) <= penetrationTolerance ||
                        first.Z + second.Z - Math.Abs(first.Solid.Center.Z - second.Solid.Center.Z) <= penetrationTolerance ||
                        !BridgeCopperClearance.UiPenetrates(first.Solid, second.Solid, penetrationTolerance)) continue;
                    return $"面对墙同相铜排实际圆角干涉：{a.Bar.GroupKey}第{a.Index}排{SolidLocation(first.Solid)}与" +
                        $"{b.Bar.GroupKey}第{b.Index}排{SolidLocation(second.Solid)}。";
                }
            }
            return null;
        }

        private static string SolidLocation(BridgeUiSolid solid) => solid.SegmentIndex < 0
            ? $"第{-solid.SegmentIndex}处折弯" : $"第{solid.SegmentIndex}直段";

        private double AxisSpan(BridgeUiSolid solid, int axis)
        {
            Func<Vector3d, double> component = v => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
            return Math.Abs(component(solid.LengthDirection)) * solid.HalfLength +
                Math.Abs(component(solid.WidthDirection)) * solid.HalfWidth + Math.Abs(component(solid.ThicknessDirection)) * solid.HalfThickness;
        }

        internal BridgeRaisedLapLiftInterval SolveLiftInterval(IEnumerable<BridgeRaisedLapClearance> members)
        {
            if (members == null) throw new ArgumentNullException(nameof(members));
            var minimum = 0d;
            var maximum = double.PositiveInfinity;
            var count = 0;
            foreach (var member in members)
            {
                if (member == null) throw new ArgumentException("搭接成员净距不能为空。", nameof(members));
                RequireNonNegative(member.BottomClearanceMm, nameof(member.BottomClearanceMm));
                RequireNonNegative(member.TopClearanceMm, nameof(member.TopClearanceMm));
                minimum = Math.Max(minimum, RequiredClearanceMm + FastenerDownwardProjectionMm - member.BottomClearanceMm);
                maximum = Math.Min(maximum, member.TopClearanceMm - RequiredClearanceMm);
                count++;
            }
            if (count == 0) throw new ArgumentException("至少需要一个搭接成员净距。", nameof(members));
            // 向上取0.1mm并再次检查上界，不能四舍五入后损失下方绝缘距离。
            minimum = Math.Ceiling(minimum * 10d - Tolerance) / 10d;
            var feasible = minimum <= maximum + Tolerance;
            return new BridgeRaisedLapLiftInterval
            {
                IsFeasible = feasible,
                MinimumLiftMm = minimum,
                MaximumLiftMm = maximum,
                FailureReason = feasible ? null : string.Format(CultureInfo.InvariantCulture,
                    "搭接需要抬高{0:0.###} mm，上方最多允许{1:0.###} mm，缺少{2:0.###} mm。",
                    minimum, maximum, minimum - maximum)
            };
        }

        internal BridgeRaisedLapEndResult RaiseEnd(PlanarBendBusbarSetting source, bool atStart,
            Vector3d up, double liftMm, double lapLengthMm, double minimumFixedStraightMm = 0d)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            RequireDirection(up, nameof(up));
            RequireNonNegative(liftMm, nameof(liftMm));
            RequirePositive(lapLengthMm, nameof(lapLengthMm));
            RequireNonNegative(minimumFixedStraightMm, nameof(minimumFixedStraightMm));
            ValidateSource(source);
            if (liftMm <= Tolerance) return Completed(BridgeNativePreviewCopy.Copy(source), 0d);
            if (source.IsMilledBar) return Failed(source, "铣排组合体不支持在端部增加抬高折弯。");
            if (source.PreviewBendPointKinds != null && source.PreviewBendPointKinds.Count > 0 &&
                source.PreviewBendPointKinds.Count != source.PointList.Count)
                return Failed(source, "逐点折弯类型与路径点数不一致，不能增加抬高桥头。");
            if (source.IsVerticalBend && (source.PreviewBendPointKinds == null ||
                source.PreviewBendPointKinds.Count != source.PointList.Count))
                return Failed(source, "立弯排缺少逐点折弯类型，不能增加抬高桥头。");

            var points = source.PointList.Select(Copy).ToList();
            var kinds = source.PreviewBendPointKinds != null && source.PreviewBendPointKinds.Count == points.Count
                ? new List<BusbarPreviewBendPointKind_e>(source.PreviewBendPointKinds) : null;
            if (!atStart)
            {
                points.Reverse();
                kinds?.Reverse();
            }
            var vertical = up.Normalized;
            var direction = new Vector3d(points[0], points[1]).Normalized;
            if (Math.Abs(direction.Dot(vertical)) > Tolerance ||
                Math.Abs(source.WidthVector.Normalized.Dot(vertical)) > Tolerance ||
                Math.Abs(direction.Dot(source.WidthVector.Normalized)) > Tolerance ||
                Math.Abs(source.StartBusbarVector.Normalized.Dot(vertical)) < 1d - Tolerance)
                return Failed(source, "搭接端不是具有水平排宽的水平直头，不能沿竖直方向抬高。");

            var head = ExistingHead(points, direction, vertical);
            double projection;
            string reason;
            if (head)
            {
                if (!RaiseExistingHead(source, points, kinds, direction, vertical, liftMm,
                    lapLengthMm, minimumFixedStraightMm, out projection, out reason))
                    return Failed(source, reason);
            }
            else if (!InsertHead(source, points, kinds, direction, vertical, liftMm,
                lapLengthMm, minimumFixedStraightMm, out projection, out reason))
                return Failed(source, reason);

            if (!atStart)
            {
                points.Reverse();
                kinds?.Reverse();
            }
            var result = BridgeNativePreviewCopy.Copy(source);
            result.PointList = points;
            result.PreviewBendPointKinds = kinds ?? new List<BusbarPreviewBendPointKind_e>();
            return Completed(result, projection);
        }

        /// <summary>整个实际搭接投影内，覆盖穿层杆身至最低铜排面下35mm的保守实体。</summary>
        internal BridgeUiSolid CreateFastenerSolid(Point3d overlapStartLowestFace,
            Point3d overlapEndLowestFace, Vector3d widthDirection, Vector3d up,
            double widthMm, string groupKey, double copperHeightMm = 0d)
        {
            RequirePoint(overlapStartLowestFace, nameof(overlapStartLowestFace));
            RequirePoint(overlapEndLowestFace, nameof(overlapEndLowestFace));
            RequireDirection(widthDirection, nameof(widthDirection));
            RequireDirection(up, nameof(up));
            RequirePositive(widthMm, nameof(widthMm));
            RequireNonNegative(copperHeightMm, nameof(copperHeightMm));
            var length = new Vector3d(overlapStartLowestFace, overlapEndLowestFace);
            RequireDirection(length, nameof(overlapEndLowestFace));
            if (Math.Abs(length.Normalized.Dot(up.Normalized)) > Tolerance ||
                Math.Abs(length.Normalized.Dot(widthDirection.Normalized)) > Tolerance ||
                Math.Abs(widthDirection.Normalized.Dot(up.Normalized)) > Tolerance)
                throw new ArgumentException("紧固件搭接投影的长度、排宽、竖直方向必须正交。");
            var centerOffset = (copperHeightMm - FastenerDownwardProjectionMm) / 2d;
            return BridgeCopperClearance.CreateUiSegmentSolid(groupKey ?? "搭接紧固件",
                Translate(overlapStartLowestFace, up.Normalized, centerOffset),
                Translate(overlapEndLowestFace, up.Normalized, centerOffset), widthDirection.Normalized,
                up.Normalized, widthMm, copperHeightMm + FastenerDownwardProjectionMm, 1,
                groupKey ?? "搭接紧固件");
        }

        /// <summary>
        /// 竖向平弯的结构检查实体。每个圆环扇区用其在水平/高度轴上的精确极值包围，
        /// 不把加工展开余量当作真实外形；不支持的排面返回null，由调用方保留保守检查。
        /// </summary>
        internal IReadOnlyList<BridgeUiSolid> CreateStructureSolids(PlanarBendBusbarSetting source, Vector3d up)
            => CreateStructureSolids(source, up, false);

        private IReadOnlyList<BridgeUiSolid> CreateStructureSolids(PlanarBendBusbarSetting source, Vector3d up, bool radialBoxes)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            RequireDirection(up, nameof(up));
            ValidateSource(source);
            if (source.IsMilledBar || source.PreviewBendPointKinds != null &&
                source.PreviewBendPointKinds.Any(k => k != BusbarPreviewBendPointKind_e.Planar)) return null;
            var width = source.WidthVector.Normalized;
            var verticalDot = up.Normalized.Dot(width);
            var reference = up.Normalized - width * verticalDot;
            if (reference.Norm <= Tolerance)
                reference = source.StartBusbarVector - width * source.StartBusbarVector.Dot(width);
            if (reference.Norm <= Tolerance) return null;
            var vertical = reference.Normalized;
            var along = width.Cross(vertical).Normalized;
            var directions = source.PointList.Zip(source.PointList.Skip(1), (a, b) => new Vector3d(a, b).Normalized).ToArray();
            if (directions.Any(d => Math.Abs(d.Dot(width)) > Tolerance)) return null;
            var firstNormal = directions[0].Cross(width).Normalized;
            var sign = firstNormal.Dot(source.StartBusbarVector.Normalized) >= 0d ? 1d : -1d;
            if (Math.Abs(firstNormal.Dot(source.StartBusbarVector.Normalized)) < 1d - Tolerance) return null;
            var points = source.PointList.Select(Copy).ToList();
            if (!source.IsCenter && !MoveSurfacePathToCenter(points, directions, width, sign, source.BusBarThickness / 2d)) return null;

            var corners = new RoundedCorner[points.Count];
            var centerRadius = source.BarBendingRadius + source.BusBarThickness / 2d;
            for (var i = 1; i < points.Count - 1; i++)
            {
                var incoming = directions[i - 1];
                var outgoing = directions[i];
                var dot = Math.Max(-1d, Math.Min(1d, incoming.Dot(outgoing)));
                var cross = incoming.Dot(along) * outgoing.Dot(vertical) - incoming.Dot(vertical) * outgoing.Dot(along);
                var turn = Math.Atan2(cross, dot);
                if (Math.Abs(turn) <= Tolerance) continue;
                if (Math.Abs(turn) >= Math.PI - Tolerance) return null;
                var trim = centerRadius * Math.Tan(Math.Abs(turn) / 2d);
                var first = Translate(points[i], incoming, -trim);
                var inwardNormal = new Vector3d(
                    -along.X * incoming.Dot(vertical) + vertical.X * incoming.Dot(along),
                    -along.Y * incoming.Dot(vertical) + vertical.Y * incoming.Dot(along),
                    -along.Z * incoming.Dot(vertical) + vertical.Z * incoming.Dot(along));
                var center = Translate(first, inwardNormal, Math.Sign(turn) * centerRadius);
                var radial = new Vector3d(center, first);
                corners[i] = new RoundedCorner { Index = i, Trim = trim, Center = center,
                    FirstAngle = Math.Atan2(radial.Dot(vertical), radial.Dot(along)), Turn = turn };
            }
            var solids = new List<BridgeUiSolid>();
            for (var i = 0; i < points.Count - 1; i++)
            {
                var startTrim = corners[i]?.Trim ?? 0d;
                var endTrim = corners[i + 1]?.Trim ?? 0d;
                var straightLength = points[i].DistanceTo(points[i + 1]) - startTrim - endTrim;
                if (straightLength < -Tolerance)
                    throw new InvalidOperationException("铜排圆角切点重叠，不能以不完整外形校核结构净距：" + source.GroupKey);
                if (straightLength <= Tolerance) continue;
                solids.Add(BridgeCopperClearance.CreateUiSegmentSolid(source.GroupKey ?? "铜排",
                    Translate(points[i], directions[i], startTrim), Translate(points[i + 1], directions[i], -endTrim),
                    width, source.StartBusbarVector, source.BusBarWidth, source.BusBarThickness, i + 1, source.GroupKey));
            }
            foreach (var corner in corners.Where(c => c != null))
                AddCornerSolids(solids, corner, source, along, vertical, width, radialBoxes);
            return solids.AsReadOnly();
        }

        private sealed class RoundedCorner
        {
            internal int Index;
            internal double Trim, FirstAngle, Turn;
            internal Point3d Center;
        }

        private bool MoveSurfacePathToCenter(List<Point3d> points, Vector3d[] directions,
            Vector3d width, double sign, double offset)
        {
            var normals = directions.Select(d => d.Cross(width).Normalized).ToArray();
            for (var i = 0; i < points.Count; i++)
            {
                var first = normals[Math.Max(0, i - 1)];
                var second = normals[Math.Min(normals.Length - 1, i)];
                var divisor = 1d + first.Dot(second);
                if (divisor <= Tolerance) return false;
                var distance = sign * offset / divisor;
                points[i] = new Point3d(points[i].X + (first.X + second.X) * distance,
                    points[i].Y + (first.Y + second.Y) * distance,
                    points[i].Z + (first.Z + second.Z) * distance);
            }
            return true;
        }

        private void AddCornerSolids(List<BridgeUiSolid> solids, RoundedCorner corner,
            PlanarBendBusbarSetting source, Vector3d along, Vector3d vertical, Vector3d width, bool radialBoxes)
        {
            // 对每段圆环扇区求两个正交轴的精确投影极值；并集严格覆盖圆角，
            // 同时不会像对称扫掠盒一样低于真实圆角的最低表面。
            var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(corner.Turn) / (Math.PI / 36d)));
            for (var i = 0; i < count; i++)
            {
                var first = corner.FirstAngle + corner.Turn * i / count;
                var last = corner.FirstAngle + corner.Turn * (i + 1) / count;
                var basisAngle = radialBoxes ? (first + last) / 2d : 0d;
                var horizontalAxis = radialBoxes ? along * Math.Cos(basisAngle) + vertical * Math.Sin(basisAngle) : along;
                var verticalAxis = radialBoxes ? vertical * Math.Cos(basisAngle) - along * Math.Sin(basisAngle) : vertical;
                double xLow, xHigh, zLow, zHigh;
                RingSectorRange(first - basisAngle, last - basisAngle, source.BarBendingRadius,
                    source.BarBendingRadius + source.BusBarThickness, out xLow, out xHigh, out zLow, out zHigh);
                var center = Translate(Translate(corner.Center, horizontalAxis, (xLow + xHigh) / 2d), verticalAxis, (zLow + zHigh) / 2d);
                // 极值来自实际半径，不额外加BendAllowance。微小数值外扩低于净距比较容差。
                const double outward = 1e-9d;
                solids.Add(BridgeCopperClearance.CreateUiSegmentSolid(source.GroupKey ?? "铜排圆角",
                    Translate(center, horizontalAxis, -(xHigh - xLow) / 2d - outward),
                    Translate(center, horizontalAxis, (xHigh - xLow) / 2d + outward), width, verticalAxis,
                    source.BusBarWidth, zHigh - zLow + 2d * outward, -corner.Index, source.GroupKey));
            }
        }

        private void RingSectorRange(double first, double last, double inner, double outer,
            out double xLow, out double xHigh, out double zLow, out double zHigh)
        {
            var low = Math.Min(first, last);
            var high = Math.Max(first, last);
            var angles = new List<double> { first, last };
            var step = Math.PI / 2d;
            for (var quadrant = (int)Math.Ceiling(low / step); quadrant <= (int)Math.Floor(high / step); quadrant++)
                angles.Add(quadrant * step);
            xLow = zLow = double.PositiveInfinity;
            xHigh = zHigh = double.NegativeInfinity;
            foreach (var angle in angles)
            foreach (var radius in new[] { inner, outer })
            {
                var x = radius * Math.Cos(angle);
                var z = radius * Math.Sin(angle);
                xLow = Math.Min(xLow, x); xHigh = Math.Max(xHigh, x);
                zLow = Math.Min(zLow, z); zHigh = Math.Max(zHigh, z);
            }
        }

        private bool ExistingHead(IReadOnlyList<Point3d> points, Vector3d direction, Vector3d up)
        {
            if (points.Count < 4) return false;
            var transition = new Vector3d(points[1], points[2]);
            var projection = transition.Dot(direction);
            var rise = transition.Dot(up);
            var sideways = transition.Dot(direction.Cross(up));
            var following = new Vector3d(points[2], points[3]);
            // 仅接管已知50mm桥头；其他内部工艺保持原样，在端部直段增加独立过渡。
            return Math.Abs(projection - TransitionReferenceProjectionMm) <= .15d &&
                Math.Abs(rise) > Tolerance && Math.Abs(sideways) <= Tolerance &&
                following.Normalized.Dot(direction) >= 1d - Tolerance;
        }

        private bool RaiseExistingHead(PlanarBendBusbarSetting source, List<Point3d> points,
            List<BusbarPreviewBendPointKind_e> kinds, Vector3d direction, Vector3d up,
            double lift, double lap, double fixedStraight, out double projection, out string reason)
        {
            var oldTransition = new Vector3d(points[1], points[2]);
            var oldProjection = oldTransition.Dot(direction);
            var oldRise = oldTransition.Dot(up);
            var rise = oldRise - lift;
            projection = Math.Max(oldProjection, Math.Abs(rise) * oldProjection / Math.Abs(oldRise));
            var raisedStart = Translate(points[0], up, lift);
            if (Math.Abs(rise) <= Tolerance)
            {
                // 原桥头的高度差被抬高恰好抵消时，去掉共线伪折弯。
                if (raisedStart.DistanceTo(points[3]) < lap + fixedStraight + EndTrim(source, points, 3) - Tolerance)
                { reason = "桥头回平后不足以保留搭接和固定直段。"; return false; }
                points[0] = raisedStart;
                points.RemoveRange(1, 2);
                kinds?.RemoveRange(1, 2);
                reason = null;
                projection = 0d;
                return true;
            }
            var raisedCorner = Translate(points[1], up, lift);
            var baseCorner = Translate(Translate(raisedCorner, direction, projection), up, rise);
            var trim = BendTrim(source, Math.Atan2(Math.Abs(rise), projection));
            var plateau = raisedStart.DistanceTo(raisedCorner);
            var remaining = new Vector3d(baseCorner, points[3]).Dot(direction);
            if (plateau < lap + trim - Tolerance)
            { reason = "抬高桥头的圆角切点侵入搭接直段。"; return false; }
            if (Math.Sqrt(projection * projection + rise * rise) < 2d * trim + Tolerance ||
                remaining < fixedStraight + trim + EndTrim(source, points, 3) + Tolerance)
            { reason = "段排长度不足以加长抬高过渡并保留圆角和固定直段。"; return false; }
            points[0] = raisedStart;
            points[1] = raisedCorner;
            points[2] = baseCorner;
            if (kinds != null) kinds[1] = kinds[2] = BusbarPreviewBendPointKind_e.Planar;
            reason = null;
            return true;
        }

        private bool InsertHead(PlanarBendBusbarSetting source, List<Point3d> points,
            List<BusbarPreviewBendPointKind_e> kinds, Vector3d direction, Vector3d up,
            double lift, double lap, double fixedStraight, out double projection, out string reason)
        {
            // 新桥头同样沿用“单根排厚/50mm”的既有斜率，不引入新的折弯角工艺。
            projection = Math.Max(TransitionReferenceProjectionMm,
                lift * TransitionReferenceProjectionMm / source.BusBarThickness);
            var trim = BendTrim(source, Math.Atan2(lift, projection));
            var plateau = lap + Math.Max(HeadStraightReserveMm, trim);
            var required = plateau + projection + trim + fixedStraight + EndTrim(source, points, 1);
            if (points[0].DistanceTo(points[1]) < required + Tolerance)
            { reason = "端部直段长度不足以放置搭接平台、抬高过渡、圆角和固定直段。"; return false; }
            if (Math.Sqrt(projection * projection + lift * lift) < 2d * trim + Tolerance)
            { reason = "抬高过渡长度不足以容纳两端圆角。"; return false; }
            var raisedStart = Translate(points[0], up, lift);
            var raisedCorner = Translate(raisedStart, direction, plateau);
            var baseCorner = Translate(points[0], direction, plateau + projection);
            points[0] = raisedStart;
            points.Insert(1, raisedCorner);
            points.Insert(2, baseCorner);
            if (kinds != null) kinds.InsertRange(1, new[]
                { BusbarPreviewBendPointKind_e.Planar, BusbarPreviewBendPointKind_e.Planar });
            reason = null;
            return true;
        }

        private double EndTrim(PlanarBendBusbarSetting source, IReadOnlyList<Point3d> points, int index)
        {
            if (index >= points.Count - 1) return 0d;
            var incoming = new Vector3d(points[index - 1], points[index]).Normalized;
            var outgoing = new Vector3d(points[index], points[index + 1]).Normalized;
            return BendTrim(source, Math.Acos(Math.Max(-1d, Math.Min(1d, incoming.Dot(outgoing)))));
        }

        private double BendTrim(PlanarBendBusbarSetting source, double turn)
        {
            if (turn >= Math.PI - Tolerance) return double.PositiveInfinity;
            // 保守采用较大内半径加半排厚，避免将圆角切点当作可安装直段。
            var radius = Math.Max(source.BarBendingRadius, source.VerticalBendingRadius) + source.BusBarThickness / 2d;
            return radius * Math.Tan(turn / 2d);
        }

        private BridgeRaisedLapEndResult Completed(PlanarBendBusbarSetting setting, double projection)
        {
            return new BridgeRaisedLapEndResult { Success = true, Setting = setting,
                TransitionProjectionMm = projection, DevelopedLengthMm = BridgeCopperClearance.DevelopedLength(setting) };
        }

        private BridgeRaisedLapEndResult Failed(PlanarBendBusbarSetting source, string reason)
        {
            return new BridgeRaisedLapEndResult { Success = false,
                FailureReason = "铜排“" + (source.GroupKey ?? "未知") + "”：" + reason };
        }

        private void ValidateSource(PlanarBendBusbarSetting source)
        {
            if (source.PointList == null || source.PointList.Count < 2)
                throw new ArgumentException("铜排至少需要两个有效路径点。", nameof(source));
            for (var index = 0; index < source.PointList.Count; index++)
            {
                RequirePoint(source.PointList[index], nameof(source.PointList));
                if (index > 0 && source.PointList[index].DistanceTo(source.PointList[index - 1]) <= Tolerance)
                    throw new ArgumentException("铜排存在重复路径点。", nameof(source));
            }
            RequireDirection(source.WidthVector, nameof(source.WidthVector));
            RequireDirection(source.StartBusbarVector, nameof(source.StartBusbarVector));
            RequirePositive(source.BusBarWidth, nameof(source.BusBarWidth));
            RequirePositive(source.BusBarThickness, nameof(source.BusBarThickness));
            RequireNonNegative(source.BarBendingRadius, nameof(source.BarBendingRadius));
            RequireNonNegative(source.VerticalBendingRadius, nameof(source.VerticalBendingRadius));
            RequireNonNegative(source.BendAllowance, nameof(source.BendAllowance));
        }

        private void RequirePoint(Point3d point, string parameter)
        {
            if (point == null || !Finite(point.X) || !Finite(point.Y) || !Finite(point.Z))
                throw new ArgumentException("几何坐标必须是有限数值。", parameter);
        }

        private void RequireDirection(Vector3d direction, string parameter)
        {
            if (direction == null || !Finite(direction.X) || !Finite(direction.Y) ||
                !Finite(direction.Z) || direction.Norm <= Tolerance)
                throw new ArgumentException("方向必须是有限非零向量。", parameter);
        }

        private void RequirePositive(double value, string parameter)
        {
            if (!Finite(value) || value <= Tolerance)
                throw new ArgumentOutOfRangeException(parameter, "尺寸必须是有限正数。");
        }

        private void RequireNonNegative(double value, string parameter)
        {
            if (!Finite(value) || value < 0d)
                throw new ArgumentOutOfRangeException(parameter, "尺寸必须是有限非负数。");
        }

        private bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private Point3d Copy(Point3d point) => new Point3d(point.X, point.Y, point.Z);
        private Point3d Translate(Point3d point, Vector3d direction, double distance) => new Point3d(
            point.X + direction.X * distance, point.Y + direction.Y * distance, point.Z + direction.Z * distance);
    }
}
