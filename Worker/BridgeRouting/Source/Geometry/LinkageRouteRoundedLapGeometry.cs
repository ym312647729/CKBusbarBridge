using System;
using System.Collections.Generic;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Geometry
{
    public sealed class LinkageRouteFlatBoundary
    {
        public Point3d Start { get; internal set; }
        public Point3d End { get; internal set; }
        public Vector3d Direction { get; internal set; }
        public double OriginalLength { get; internal set; }
        public double StartTrim { get; internal set; }
        public double EndTrim { get; internal set; }
    }

    /// <summary>对实际叠排成员（不是未展开包络）计算圆弧以外的有限平直搭接区。</summary>
    public static class LinkageRouteRoundedLapGeometry
    {
        public static double GetSurfaceTangentDistance(double innerRadius, double thickness,
            double turnAngleRadians, bool isInnerSurface)
        {
            if (!IsFinite(innerRadius) || innerRadius < 0d || !IsFinite(thickness) || thickness <= 0d ||
                !IsFinite(turnAngleRadians) || turnAngleRadians < 0d ||
                turnAngleRadians >= Math.PI - LinkageRouteGeometryTolerances.ExactDirection)
                throw new ArgumentOutOfRangeException(nameof(turnAngleRadians), "圆弧半径、排厚或转角无效。");
            return (innerRadius + (isInnerSurface ? 0d : thickness)) * Math.Tan(turnAngleRadians / 2d);
        }

        public static bool TryGetFlatBoundary(PlanarBendBusbarSetting actualMember, bool useStart,
            out LinkageRouteFlatBoundary boundary, out string failureReason)
            => TryGetFlatSegment(actualMember, useStart ? 0 : (actualMember?.PointList?.Count ?? 1) - 2,
                out boundary, out failureReason);

        public static bool TryGetFlatSegment(PlanarBendBusbarSetting actualMember, int segmentIndex,
            out LinkageRouteFlatBoundary boundary, out string failureReason)
        {
            boundary = null;
            failureReason = null;
            if (actualMember?.PointList == null || actualMember.PointList.Count < 2 ||
                actualMember.WidthVector == null || actualMember.WidthVector.Norm <= 1e-8d ||
                actualMember.StartBusbarVector == null || actualMember.StartBusbarVector.Norm <= 1e-8d ||
                !IsFinite(actualMember.BusBarThickness) || actualMember.BusBarThickness <= 0d ||
                !IsFinite(actualMember.BarBendingRadius) || actualMember.BarBendingRadius < 0d ||
                segmentIndex < 0 || segmentIndex >= actualMember.PointList.Count - 1)
            {
                failureReason = "实际铜排点集、厚度、方向或折弯半径无效。";
                return false;
            }
            // 单侧基准先还原到实际成员中心面；切点不能直接从表面虚拟角减R+t。
            var centerPoints = actualMember.IsCenter ? actualMember.PointList :
                actualMember.GetOffsetCenterlinePointList(actualMember.BusBarThickness / 2d);
            var segmentVector = new Vector3d(centerPoints[segmentIndex], centerPoints[segmentIndex + 1]);
            if (!IsFinite(segmentVector.Norm) || segmentVector.Norm <= 1e-8d ||
                Math.Abs(segmentVector.Normalized.Dot(actualMember.WidthVector.Normalized)) > 1e-6d)
            {
                failureReason = "实际搭接直段长度为零或与排宽方向不正交。";
                return false;
            }
            if (!TryGetCenterTrim(centerPoints, segmentIndex, actualMember, out var startTrim) ||
                !TryGetCenterTrim(centerPoints, segmentIndex + 1, actualMember, out var endTrim))
            {
                failureReason = "实际折弯点退化或折返，无法建立真实平直搭接区。";
                return false;
            }
            if (startTrim + endTrim > segmentVector.Norm + LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
            {
                failureReason = "相邻圆弧切点越过直段，已无可制造平直区。";
                return false;
            }
            boundary = new LinkageRouteFlatBoundary
            {
                Start = centerPoints[segmentIndex], End = centerPoints[segmentIndex + 1],
                Direction = segmentVector.Normalized, OriginalLength = segmentVector.Norm,
                StartTrim = startTrim, EndTrim = endTrim
            };
            return true;
        }

        public static bool HasFlatLapCoverage(PlanarBendBusbarSetting actualMember, bool useStart,
            IReadOnlyList<Point3d> actualLapCorners, out double missingLength, out string failureReason)
            => HasFlatSegmentLapCoverage(actualMember, useStart ? 0 : (actualMember?.PointList?.Count ?? 1) - 2,
                actualLapCorners, out missingLength, out failureReason);

        public static bool HasFlatSegmentLapCoverage(PlanarBendBusbarSetting actualMember, int segmentIndex,
            IReadOnlyList<Point3d> actualLapCorners, out double missingLength, out string failureReason)
        {
            missingLength = 0d;
            if (!TryGetFlatSegment(actualMember, segmentIndex, out var flat, out failureReason)) return false;
            if (actualLapCorners == null || actualLapCorners.Count == 0)
            {
                failureReason = "缺少实际搭接矩形，不能跳过圆弧搭接校验。";
                return false;
            }
            foreach (var lapCorner in actualLapCorners)
            {
                if (lapCorner == null) { failureReason = "实际搭接矩形存在无效端点。"; return false; }
                var position = new Vector3d(flat.Start, lapCorner).Dot(flat.Direction);
                if (!IsFinite(position)) { failureReason = "实际搭接矩形存在非有限坐标。"; return false; }
                missingLength = Math.Max(missingLength,
                    Math.Max(flat.StartTrim - position, position - (flat.OriginalLength - flat.EndTrim)));
            }
            if (missingLength <= LinkageRouteGeometryTolerances.FinalAcceptanceComparison) return true;
            failureReason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "真实折弯圆弧侵入搭接平直区 {0:F3} mm（半径{1:F3}，排厚{2:F3}）。",
                missingLength, actualMember.BarBendingRadius, actualMember.BusBarThickness);
            return false;
        }

        /// <summary>实际成员调整后验收；设置余量只作为优先落位目标，允许局部减小时仍强制真实圆弧下界。</summary>
        internal static double GetActualMemberReserve(LinkageMultiGroupRouteRequest request,
            PlanarBendBusbarSetting member, bool useStart)
        {
            if (!TryGetFlatBoundary(member, useStart, out var boundary, out _)) return double.PositiveInfinity;
            var physical = useStart ? boundary.EndTrim : boundary.StartTrim;
            if (request.AllowBendGeometricLimit || request.AllowConstrainedBendAllowanceReduction) return physical;
            return Math.Max(physical, member.BusBarThickness / 2d + request.BendAllowance);
        }

        /// <summary>先验证设置余量；只有本成员的可用预留不足才允许局部减少，不改请求或其它成员。</summary>
        public static bool TryResolveBendAllowance(LinkageMultiGroupRouteRequest request,
            PlanarBendBusbarSetting actualMember, bool useStart, double availableReserve,
            out double appliedAllowance, out string failureReason)
        {
            appliedAllowance = 0d;
            if (request == null || !IsFinite(availableReserve) ||
                !TryGetFlatBoundary(actualMember, useStart, out var flat, out failureReason))
            { failureReason = "可用折弯预留或实际铜排无效。"; return false; }
            failureReason = null;
            var physicalReserve = useStart ? flat.EndTrim : flat.StartTrim;
            if (availableReserve + LinkageRouteGeometryTolerances.FinalAcceptanceComparison < physicalReserve)
            { failureReason = "可用预留不足真实圆弧切距，不能通过减小设置余量放行。"; return false; }
            if (request.AllowBendGeometricLimit || actualMember.PointList.Count < 3) return true;
            var configuredAllowance = Math.Max(0d, request.BendAllowance);
            var configuredReserve = Math.Max(physicalReserve, actualMember.BusBarThickness / 2d + configuredAllowance);
            if (availableReserve + LinkageRouteGeometryTolerances.FinalAcceptanceComparison >= configuredReserve)
            { appliedAllowance = configuredAllowance; return true; }
            if (!request.AllowConstrainedBendAllowanceReduction)
            { failureReason = "当前排受限且未允许减小折弯余量。"; return false; }
            appliedAllowance = Math.Min(configuredAllowance,
                Math.Max(0d, availableReserve - actualMember.BusBarThickness / 2d));
            return true;
        }

        private static bool TryGetCenterTrim(IReadOnlyList<Point3d> points, int cornerIndex,
            PlanarBendBusbarSetting member, out double trim)
        {
            trim = 0d;
            if (cornerIndex == 0 || cornerIndex == points.Count - 1) return true;
            var incoming = new Vector3d(points[cornerIndex - 1], points[cornerIndex]);
            var outgoing = new Vector3d(points[cornerIndex], points[cornerIndex + 1]);
            if (incoming.Norm <= 1e-8d || outgoing.Norm <= 1e-8d) return false;
            var cosine = Math.Max(-1d, Math.Min(1d, incoming.Normalized.Dot(outgoing.Normalized)));
            if (cosine <= -1d + 1e-8d) return false;
            var angle = Math.Acos(cosine);
            // 内外表面虚拟角分别偏移t/2*tan(theta/2)，投回中心面后切点相同。
            trim = (member.BarBendingRadius + member.BusBarThickness / 2d) * Math.Tan(angle / 2d);
            return IsFinite(trim);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
