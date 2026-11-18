using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility
{
    /// <summary>平弯铜排的中心线设置，全部尺寸单位为毫米。</summary>
    public sealed class PlanarBendBusbarSetting
    {
        public bool IsCenter { get; set; } = true;
        public Vector3d StartBusbarVector { get; set; }
        public Vector3d WidthVector { get; set; }
        public List<Point3d> PointList { get; set; } = new List<Point3d>();
        public double BarBendingRadius { get; set; }
        public double BendAllowance { get; set; }
        public double BusBarThickness { get; set; }
        public double BusBarWidth { get; set; }
        public double StartLapWidth { get; set; }
        public double EndLapWidth { get; set; }
        public string GroupKey { get; set; }

        /// <summary>
        /// 算法进程内使用的物理分段索引。异规格方案在终端选项后处理时
        /// 依靠它区分始端搭接排、中间排和柜顶搭接排；不属于外部协议。
        /// </summary>
        public int PhysicalSectionIndex { get; internal set; } = -1;

        /// <summary>
        /// 输入规格段索引。一个 U 型规格段拆成两根 L 型实体后，两根实体拥有
        /// 不同 PhysicalSectionIndex，但继续共享同一个规格段索引。
        /// </summary>
        public int SpecificationSectionIndex { get; internal set; } = -1;

        public string BusBarSize => string.Format("{0}*{1}", BusBarWidth, BusBarThickness);

        public PlanarBendBusbarSetting Copy()
        {
            return new PlanarBendBusbarSetting
            {
                IsCenter = IsCenter,
                StartBusbarVector = CopyVector(StartBusbarVector),
                WidthVector = CopyVector(WidthVector),
                PointList = CopyPoints(PointList),
                BarBendingRadius = BarBendingRadius,
                BendAllowance = BendAllowance,
                BusBarThickness = BusBarThickness,
                BusBarWidth = BusBarWidth,
                StartLapWidth = StartLapWidth,
                EndLapWidth = EndLapWidth,
                GroupKey = GroupKey,
                PhysicalSectionIndex = PhysicalSectionIndex,
                SpecificationSectionIndex = SpecificationSectionIndex
            };
        }

        public List<Point3d> GetOffsetCenterlinePointList(double offsetDistance)
        {
            if (PointList == null || PointList.Count == 0 ||
                StartBusbarVector == null || StartBusbarVector.Norm < 1e-8 ||
                WidthVector == null || WidthVector.Norm < 1e-8 ||
                Math.Abs(offsetDistance) < 1e-9 || PointList.Count < 2)
            {
                return CopyPoints(PointList);
            }

            var normalizedWidthDirection = WidthVector.Normalized;
            var normalizedStartThicknessDirection = StartBusbarVector.Normalized;
            var offsetSegments = new List<OffsetSegment>();
            double? pathThicknessOrientationSign = null;
            for (var segmentIndex = 0; segmentIndex < PointList.Count - 1; segmentIndex++)
            {
                var segmentDirection = new Vector3d(
                    PointList[segmentIndex], PointList[segmentIndex + 1]);
                if (segmentDirection.Norm < 1e-8)
                {
                    continue;
                }

                segmentDirection = segmentDirection.Normalized;
                var segmentThicknessDirection = segmentDirection.Cross(normalizedWidthDirection);
                if (segmentThicknessDirection == null || segmentThicknessDirection.Norm < 1e-8)
                {
                    return CopyPoints(PointList);
                }

                segmentThicknessDirection = segmentThicknessDirection.Normalized;
                if (!pathThicknessOrientationSign.HasValue)
                {
                    pathThicknessOrientationSign =
                        segmentThicknessDirection.Dot(normalizedStartThicknessDirection) < 0d
                            ? -1d
                            : 1d;
                }

                if (pathThicknessOrientationSign.Value < 0d)
                {
                    segmentThicknessDirection = new Vector3d(
                        -segmentThicknessDirection.X,
                        -segmentThicknessDirection.Y,
                        -segmentThicknessDirection.Z);
                }

                offsetSegments.Add(new OffsetSegment
                {
                    Start = Translate(PointList[segmentIndex], segmentThicknessDirection, offsetDistance),
                    End = Translate(PointList[segmentIndex + 1], segmentThicknessDirection, offsetDistance),
                    Direction = segmentDirection
                });
            }

            if (offsetSegments.Count == 0)
            {
                return CopyPoints(PointList);
            }

            var offsetPoints = new List<Point3d> { CopyPoint(offsetSegments[0].Start) };
            for (var junctionIndex = 0; junctionIndex < offsetSegments.Count - 1; junctionIndex++)
            {
                var firstOffsetSegment = offsetSegments[junctionIndex];
                var secondOffsetSegment = offsetSegments[junctionIndex + 1];
                var denominator = normalizedWidthDirection.Dot(
                    firstOffsetSegment.Direction.Cross(secondOffsetSegment.Direction));
                if (Math.Abs(denominator) < 1e-8)
                {
                    offsetPoints.Add(new Point3d(
                        (firstOffsetSegment.End.X + secondOffsetSegment.Start.X) / 2d,
                        (firstOffsetSegment.End.Y + secondOffsetSegment.Start.Y) / 2d,
                        (firstOffsetSegment.End.Z + secondOffsetSegment.Start.Z) / 2d));
                    continue;
                }

                var lineStartDifference = new Vector3d(
                    firstOffsetSegment.Start, secondOffsetSegment.Start);
                var firstLineParameter = normalizedWidthDirection.Dot(
                    lineStartDifference.Cross(secondOffsetSegment.Direction)) / denominator;
                offsetPoints.Add(Translate(
                    firstOffsetSegment.Start,
                    firstOffsetSegment.Direction,
                    firstLineParameter));
            }

            offsetPoints.Add(CopyPoint(offsetSegments[offsetSegments.Count - 1].End));
            return offsetPoints;
        }

        public List<Point3d> GetPreviewPointList()
        {
            return !IsCenter || BusBarThickness <= 0d
                ? CopyPoints(PointList)
                : GetOffsetCenterlinePointList(-BusBarThickness / 2d);
        }

        public double GetDevelopedLength()
        {
            if (PointList == null || PointList.Count < 2)
            {
                return 0d;
            }

            var straightLength = 0d;
            for (var pointIndex = 0; pointIndex < PointList.Count - 1; pointIndex++)
            {
                straightLength += PointList[pointIndex].DistanceTo(PointList[pointIndex + 1]);
            }

            return straightLength + Math.Max(0, PointList.Count - 2) * Math.Max(0d, BendAllowance);
        }

        public void RemoveInvalidPoints(double tolerance = 1e-6)
        {
            if (PointList == null)
            {
                PointList = new List<Point3d>();
                return;
            }

            for (var pointIndex = PointList.Count - 2; pointIndex >= 0; pointIndex--)
            {
                if (PointList[pointIndex] == null || PointList[pointIndex + 1] == null ||
                    PointList[pointIndex].DistanceTo(PointList[pointIndex + 1]) <= tolerance)
                {
                    PointList.RemoveAt(pointIndex + 1);
                }
            }
        }

        private static List<Point3d> CopyPoints(IEnumerable<Point3d> sourcePoints)
        {
            return sourcePoints == null
                ? new List<Point3d>()
                : sourcePoints.Select(centerlinePoint => centerlinePoint == null
                    ? null
                    : new Point3d(centerlinePoint.X, centerlinePoint.Y, centerlinePoint.Z)).ToList();
        }

        private static Vector3d CopyVector(Vector3d sourceVector)
        {
            return sourceVector == null ? null : new Vector3d(sourceVector.X, sourceVector.Y, sourceVector.Z);
        }

        private static Point3d CopyPoint(Point3d sourcePoint)
        {
            return new Point3d(sourcePoint.X, sourcePoint.Y, sourcePoint.Z);
        }

        private static Point3d Translate(Point3d sourcePoint, Vector3d direction, double distance)
        {
            return new Point3d(
                sourcePoint.X + direction.X * distance,
                sourcePoint.Y + direction.Y * distance,
                sourcePoint.Z + direction.Z * distance);
        }

        private sealed class OffsetSegment
        {
            public Point3d Start { get; set; }
            public Point3d End { get; set; }
            public Vector3d Direction { get; set; }
        }
    }
}
