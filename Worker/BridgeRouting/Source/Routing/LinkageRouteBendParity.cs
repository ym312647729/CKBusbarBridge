using GeometRi;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>按 XYZ 折弯平面校验始末端排厚方向的奇偶关系。</summary>
    public static class LinkageRouteBendParity
    {
        private const double DirectionTolerance =
            LinkageRouteGeometryTolerances.Direction;

        public static bool IsAllowed(
            IList<Point3d> centerlinePoints,
            Vector3d startThicknessDirection,
            Vector3d startLengthDirection,
            Vector3d endThicknessDirection,
            out string reason)
        {
            reason = null;
            if (!TryGetAxis(startThicknessDirection, out var startThicknessAxis) ||
                !TryGetAxis(startLengthDirection, out var startLengthAxis) ||
                !TryGetAxis(endThicknessDirection, out var endThicknessAxis) ||
                startThicknessAxis == startLengthAxis)
            {
                reason = "端部排厚和排长方向必须是互相正交的 XYZ 轴向。";
                return false;
            }

            return IsAllowed(
                CountBends(centerlinePoints),
                startThicknessAxis,
                startLengthAxis,
                endThicknessAxis,
                out reason);
        }

        public static bool IsAllowed(
            IList<PlanarBendBusbarSetting> planarParts,
            Vector3d startThicknessDirection,
            Vector3d startLengthDirection,
            Vector3d endThicknessDirection,
            out string reason)
        {
            reason = null;
            if (planarParts == null || planarParts.Count == 0)
            {
                reason = "实际平面铜排不能为空。";
                return false;
            }

            var firstPart = planarParts.FirstOrDefault(part =>
                part?.PointList != null && part.PointList.Count >= 2);
            if (firstPart == null ||
                !TryGetLocalThickness(firstPart, true, out var firstLocalThicknessDirection) ||
                firstLocalThicknessDirection.Cross(startThicknessDirection.Normalized).Norm > DirectionTolerance)
            {
                reason = "实际首根平面铜排的局部排厚方向与始端固定排不平行。";
                return false;
            }

            foreach (var planarPart in planarParts.Where(part =>
                part?.PointList != null && part.PointList.Count >= 2))
            {
                if (HasNonAxisSegment(planarPart))
                {
                    continue;
                }

                if (!TryGetAxis(planarPart.StartBusbarVector, out var partStartThicknessAxis) ||
                    !TryGetAxis(
                        new Vector3d(planarPart.PointList[0], planarPart.PointList[1]),
                        out var partStartLengthAxis) ||
                    !TryGetLocalThickness(planarPart, false, out var partEndThicknessDirection) ||
                    !TryGetAxis(partEndThicknessDirection, out var partEndThicknessAxis) ||
                    !IsAllowed(
                        CountBends(planarPart.PointList),
                        partStartThicknessAxis,
                        partStartLengthAxis,
                        partEndThicknessAxis,
                        out reason))
                {
                    reason = "实际平面铜排内部折弯奇偶不满足要求：" + reason;
                    return false;
                }
            }

            return true;
        }

        private static bool IsAllowed(
            BendCounts bendCounts,
            AxisDirection startThicknessAxis,
            AxisDirection startLengthAxis,
            AxisDirection endThicknessAxis,
            out string reason)
        {
            reason = null;
            if (startThicknessAxis == endThicknessAxis)
            {
                if (bendCounts.XY % 2 == 0 &&
                    bendCounts.XZ % 2 == 0 &&
                    bendCounts.YZ % 2 == 0)
                {
                    return true;
                }

                reason = string.Format(
                    "始末排厚方向平行时各相关平面折弯次数必须为偶数；XY={0}，XZ={1}，YZ={2}。",
                    bendCounts.XY,
                    bendCounts.XZ,
                    bendCounts.YZ);
                return false;
            }

            var startWidthAxis = GetRemainingAxis(startThicknessAxis, startLengthAxis);
            var requiredPlane = endThicknessAxis == startLengthAxis
                ? GetPlane(startThicknessAxis, startLengthAxis)
                : endThicknessAxis == startWidthAxis
                    ? GetPlane(startThicknessAxis, startWidthAxis)
                    : AxisPlane.None;
            if (requiredPlane == AxisPlane.None || bendCounts.Get(requiredPlane) % 2 == 0)
            {
                reason = "末端排厚方向与始端排长或排宽方向平行时，对应法向平面折弯次数必须为奇数。";
                return false;
            }

            return true;
        }

        private static bool TryGetLocalThickness(
            PlanarBendBusbarSetting planarPart,
            bool useStart,
            out Vector3d localThicknessDirection)
        {
            localThicknessDirection = null;
            if (planarPart?.PointList == null || planarPart.PointList.Count < 2 ||
                planarPart.WidthVector == null || planarPart.WidthVector.Norm <= DirectionTolerance)
            {
                return false;
            }

            var segmentIndex = useStart ? 0 : planarPart.PointList.Count - 2;
            var segmentDirection = new Vector3d(
                planarPart.PointList[segmentIndex],
                planarPart.PointList[segmentIndex + 1]);
            if (segmentDirection.Norm <= DirectionTolerance)
            {
                return false;
            }

            localThicknessDirection = segmentDirection.Normalized
                .Cross(planarPart.WidthVector.Normalized).Normalized;
            return localThicknessDirection.Norm > DirectionTolerance;
        }

        private static bool HasNonAxisSegment(PlanarBendBusbarSetting planarPart)
        {
            if (planarPart?.PointList == null)
            {
                return false;
            }

            for (var pointIndex = 0; pointIndex < planarPart.PointList.Count - 1; pointIndex++)
            {
                if (!TryGetAxis(
                    new Vector3d(planarPart.PointList[pointIndex], planarPart.PointList[pointIndex + 1]),
                    out var ignoredAxis))
                {
                    return true;
                }
            }

            return false;
        }

        private static BendCounts CountBends(IList<Point3d> centerlinePoints)
        {
            var bendCounts = new BendCounts();
            var mergedAxes = new List<AxisDirection>();
            if (centerlinePoints == null)
            {
                return bendCounts;
            }

            for (var pointIndex = 0; pointIndex < centerlinePoints.Count - 1; pointIndex++)
            {
                if (!TryGetAxis(
                        new Vector3d(centerlinePoints[pointIndex], centerlinePoints[pointIndex + 1]),
                        out var segmentAxis))
                {
                    continue;
                }

                if (mergedAxes.Count == 0 || mergedAxes[mergedAxes.Count - 1] != segmentAxis)
                {
                    mergedAxes.Add(segmentAxis);
                }
            }

            for (var bendIndex = 0; bendIndex < mergedAxes.Count - 1; bendIndex++)
            {
                bendCounts.Increment(GetPlane(mergedAxes[bendIndex], mergedAxes[bendIndex + 1]));
            }

            return bendCounts;
        }

        private static bool TryGetAxis(Vector3d direction, out AxisDirection axisDirection)
        {
            axisDirection = AxisDirection.X;
            if (direction == null || direction.Norm <= DirectionTolerance)
            {
                return false;
            }

            var normalizedDirection = direction.Normalized;
            var absoluteX = Math.Abs(normalizedDirection.X);
            var absoluteY = Math.Abs(normalizedDirection.Y);
            var absoluteZ = Math.Abs(normalizedDirection.Z);
            if (absoluteX >= 1d - DirectionTolerance &&
                absoluteY <= DirectionTolerance && absoluteZ <= DirectionTolerance)
            {
                axisDirection = AxisDirection.X;
                return true;
            }

            if (absoluteY >= 1d - DirectionTolerance &&
                absoluteX <= DirectionTolerance && absoluteZ <= DirectionTolerance)
            {
                axisDirection = AxisDirection.Y;
                return true;
            }

            if (absoluteZ >= 1d - DirectionTolerance &&
                absoluteX <= DirectionTolerance && absoluteY <= DirectionTolerance)
            {
                axisDirection = AxisDirection.Z;
                return true;
            }

            return false;
        }

        private static AxisDirection GetRemainingAxis(
            AxisDirection firstAxis,
            AxisDirection secondAxis)
        {
            foreach (AxisDirection axisDirection in Enum.GetValues(typeof(AxisDirection)))
            {
                if (axisDirection != firstAxis && axisDirection != secondAxis)
                {
                    return axisDirection;
                }
            }

            return AxisDirection.X;
        }

        private static AxisPlane GetPlane(AxisDirection firstAxis, AxisDirection secondAxis)
        {
            if (firstAxis == secondAxis)
            {
                return AxisPlane.None;
            }

            if ((firstAxis == AxisDirection.X && secondAxis == AxisDirection.Y) ||
                (firstAxis == AxisDirection.Y && secondAxis == AxisDirection.X))
            {
                return AxisPlane.XY;
            }

            if ((firstAxis == AxisDirection.X && secondAxis == AxisDirection.Z) ||
                (firstAxis == AxisDirection.Z && secondAxis == AxisDirection.X))
            {
                return AxisPlane.XZ;
            }

            return AxisPlane.YZ;
        }

        private enum AxisDirection
        {
            X,
            Y,
            Z
        }

        private enum AxisPlane
        {
            None,
            XY,
            XZ,
            YZ
        }

        private sealed class BendCounts
        {
            public int XY { get; private set; }
            public int XZ { get; private set; }
            public int YZ { get; private set; }

            public void Increment(AxisPlane axisPlane)
            {
                if (axisPlane == AxisPlane.XY) XY++;
                if (axisPlane == AxisPlane.XZ) XZ++;
                if (axisPlane == AxisPlane.YZ) YZ++;
            }

            public int Get(AxisPlane axisPlane)
            {
                if (axisPlane == AxisPlane.XY) return XY;
                return axisPlane == AxisPlane.XZ ? XZ : YZ;
            }
        }
    }
}
