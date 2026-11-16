using AutoOpeningHoleDataModel;
using GeometRi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 立弯铜排数据。分段规则移植自铜排大师
    /// BusbarTools.MathUtility/BusbarVerticalBend/VerticalBendPreview.cs。
    /// 路径会被拆成平弯段与立弯段，预览和正式生成共用同一分段结果。
    /// </summary>
    public sealed class VerticalBendSetting
    {
        public Vector3d StartBusbarVector { get; set; }
        public Vector3d WidthVector { get; set; }
        public Vector3d LastBarVector { get; private set; }
        public Vector3d VerticalThicknessVector3d { get; set; }
        public Point3d StartPoint { get; private set; }
        public List<Point3d> PointList { get; set; } = new List<Point3d>();
        public List<List<Point3d>> LastPointList { get; set; } =
            new List<List<Point3d>>();
        public List<Tuple<bool, List<Point3d>, Vector3d>> SegmentationPoints
            { get; private set; } =
            new List<Tuple<bool, List<Point3d>, Vector3d>>();

        public BusbarPhase_e? Phase { get; set; } = BusbarPhase_e.Unknow;
        public string GroupKey { get; set; }
        public double BarBendingRadius { get; set; }
        public double BusBarVerticalRadius { get; set; }
        public double BusBarThickness { get; set; }
        public double BusBarWidth { get; set; }
        public double StartLapWidth { get; set; }
        public double EndLapWidth { get; set; }
        public double K { get; set; } = 0.47d;
        public int FixedForm { get; set; } = 1;
        public string BusbarType { get; set; }
        public string BusbarSurFaceTreatment { get; set; }
        public bool IsStartBend { get; private set; }

        public string BusBarSize =>
            BusBarWidth + "*" + BusBarThickness;

        public Vector3d LastWidthVector
        {
            get
            {
                GetPointList();
                if (PointList == null || PointList.Count < 2 ||
                    LastBarVector == null)
                {
                    return null;
                }

                return LastBarVector.Cross(new Vector3d(
                    PointList[PointList.Count - 2],
                    PointList[PointList.Count - 1]));
            }
        }

        public VerticalBendSetting Copy()
        {
            var copiedSetting = new VerticalBendSetting
            {
                StartBusbarVector = StartBusbarVector?.Copy(),
                WidthVector = WidthVector?.Copy(),
                VerticalThicknessVector3d =
                    VerticalThicknessVector3d?.Copy(),
                PointList = PointList?
                    .Select(point => point.Copy())
                    .ToList() ?? new List<Point3d>(),
                LastPointList = LastPointList?
                    .Select(pointGroup => pointGroup
                        .Select(point => point.Copy())
                        .ToList())
                    .ToList() ?? new List<List<Point3d>>(),
                Phase = Phase,
                GroupKey = GroupKey,
                BarBendingRadius = BarBendingRadius,
                BusBarVerticalRadius = BusBarVerticalRadius,
                BusBarThickness = BusBarThickness,
                BusBarWidth = BusBarWidth,
                StartLapWidth = StartLapWidth,
                EndLapWidth = EndLapWidth,
                K = K,
                FixedForm = FixedForm,
                BusbarType = BusbarType,
                BusbarSurFaceTreatment = BusbarSurFaceTreatment
            };
            if (SegmentationPoints != null && SegmentationPoints.Count > 0)
            {
                copiedSetting.SegmentationPoints = SegmentationPoints
                    .Select(segment => Tuple.Create(
                        segment.Item1,
                        segment.Item2.Select(point => point.Copy()).ToList(),
                        segment.Item3?.Copy()))
                    .ToList();
                copiedSetting.LastBarVector = LastBarVector?.Copy();
                copiedSetting.StartPoint = StartPoint?.Copy();
                copiedSetting.IsStartBend = IsStartBend;
            }

            return copiedSetting;
        }

        public void GetWidthVector()
        {
            if (StartBusbarVector == null || PointList == null ||
                PointList.Count < 2)
            {
                return;
            }

            WidthVector = StartBusbarVector
                .Cross(new Vector3d(PointList[0], PointList[1]))
                .Negate()
                .Normalized;
        }

        public void SetSegmentPoints(bool record = true)
        {
            ValidateCoreGeometry();
            RemoveInvalidPoints();
            ValidateCoreGeometry();
            StartPoint = PointList[0].Copy();
            if (record)
            {
                LastPointList.Add(PointList
                    .Select(point => point.Copy())
                    .ToList());
            }

            Segmentation();
        }

        public void GetPointList()
        {
            if (SegmentationPoints == null ||
                SegmentationPoints.Count == 0)
            {
                return;
            }

            PointList = SegmentationPoints
                .SelectMany(segment => segment.Item2)
                .Select(point => point.Copy())
                .ToList();
            RemoveInvalidPoints();
        }

        private void ValidateCoreGeometry()
        {
            if (PointList == null || PointList.Count < 2)
            {
                throw new InvalidOperationException(
                    "立弯铜排路径至少需要两个有效点。");
            }
            if (StartBusbarVector == null ||
                StartBusbarVector.Norm <= 0.000001d)
            {
                throw new InvalidOperationException(
                    "立弯铜排起始排厚方向无效。");
            }
            if (WidthVector == null || WidthVector.Norm <= 0.000001d)
            {
                GetWidthVector();
            }
            if (WidthVector == null || WidthVector.Norm <= 0.000001d)
            {
                throw new InvalidOperationException(
                    "立弯铜排排宽方向无效。");
            }
            if (BusBarWidth <= 0d || BusBarThickness <= 0d ||
                BarBendingRadius < 0d || BusBarVerticalRadius < 0d)
            {
                throw new InvalidOperationException(
                    "立弯铜排规格或折弯半径无效。");
            }
        }

        private void RemoveInvalidPoints()
        {
            for (var pointIndex = 0;
                 pointIndex < PointList.Count - 1;
                 pointIndex++)
            {
                if (PointList[pointIndex].DistanceTo(
                        PointList[pointIndex + 1]) < 0.1d)
                {
                    PointList.RemoveAt(pointIndex + 1);
                    pointIndex--;
                }
            }

            for (var pointIndex = 0;
                 pointIndex < PointList.Count - 2;
                 pointIndex++)
            {
                if (PointList[pointIndex].DistanceTo(new Line3d(
                        PointList[pointIndex + 1],
                        PointList[pointIndex + 2])) < 0.1d)
                {
                    PointList.RemoveAt(pointIndex + 1);
                    pointIndex--;
                }
            }
        }

        /// <summary>
        /// 铜排大师原始分段算法。Item1=true 为平弯段，false 为立弯段。
        /// </summary>
        private void Segmentation()
        {
            SegmentationPoints.Clear();
            StartPoint = PointList[0].Copy();
            var remainingPoints = PointList
                .Select(point => point.Copy())
                .ToList();
            var currentWidthVector = WidthVector;
            var startPlane = new Plane3d(remainingPoints[0], currentWidthVector);
            if (remainingPoints[1].DistanceTo(startPlane) > 0.1d)
            {
                throw new InvalidOperationException(
                    "立弯铜排起始排厚方向与第一条路径线不垂直。");
            }

            var lastBarVector = StartBusbarVector;
            var isPlanarBendSegment = true;
            IsStartBend = false;
            var startsWithVerticalBend = false;
            if (remainingPoints.Count > 2)
            {
                startsWithVerticalBend = !new Plane3d(
                        remainingPoints[0],
                        remainingPoints[1],
                        remainingPoints[2])
                    .Normal.Normalized.IsParallelToTol(
                        startPlane.Normal.Normalized,
                        0.1d);
                IsStartBend = startsWithVerticalBend;
            }
            else
            {
                SegmentationPoints.Add(Tuple.Create(
                    true,
                    remainingPoints,
                    lastBarVector.Copy()));
                LastBarVector = lastBarVector.Copy();
                return;
            }

            lastBarVector = WidthVector;
            var segmentIndex = 0;
            Point3d previousSecondPoint = null;
            while (remainingPoints.Count > 1)
            {
                var segmentPoints = new List<Point3d>();
                currentWidthVector = lastBarVector;
                if (segmentIndex == 0 && startsWithVerticalBend)
                {
                    var virtualPoints = CalculateVirtualPoints(
                        remainingPoints[0],
                        remainingPoints[1],
                        remainingPoints[2],
                        BusBarWidth / 2d + BusBarVerticalRadius);
                    segmentPoints.Add(remainingPoints[0].Copy());
                    remainingPoints.RemoveAt(0);
                    segmentPoints.Add(virtualPoints[0]);
                    remainingPoints.Insert(0, virtualPoints[1]);
                }
                else
                {
                    var segmentPlane = new Plane3d(
                        remainingPoints[0],
                        currentWidthVector);
                    previousSecondPoint = remainingPoints[1].Copy();
                    for (var requiredPointIndex = 0;
                         requiredPointIndex < 2;
                         requiredPointIndex++)
                    {
                        segmentPoints.Add(remainingPoints[0].Copy());
                        remainingPoints.RemoveAt(0);
                    }

                    Point3d lastCoplanarPoint = null;
                    for (var remainingPointIndex = 0;
                         remainingPointIndex < remainingPoints.Count;
                         remainingPointIndex++)
                    {
                        if (remainingPoints[remainingPointIndex]
                                .DistanceTo(segmentPlane) < 0.1d)
                        {
                            lastCoplanarPoint =
                                remainingPoints[remainingPointIndex].Copy();
                            segmentPoints.Add(lastCoplanarPoint.Copy());
                            remainingPoints.RemoveAt(remainingPointIndex);
                            remainingPointIndex--;
                        }
                        else
                        {
                            remainingPoints.Insert(
                                0,
                                lastCoplanarPoint ?? previousSecondPoint);
                            break;
                        }
                    }
                }

                if (segmentPoints.Count < 2)
                {
                    throw new InvalidOperationException(
                        "立弯铜排分段没有形成有效线段。");
                }

                lastBarVector = currentWidthVector.Cross(new Vector3d(
                    segmentPoints[segmentPoints.Count - 2],
                    segmentPoints[segmentPoints.Count - 1])).Normalized;
                if (!isPlanarBendSegment)
                {
                    lastBarVector = lastBarVector.Negate();
                }
                if (segmentIndex == 0 && startsWithVerticalBend)
                {
                    lastBarVector = StartBusbarVector;
                }

                SegmentationPoints.Add(Tuple.Create(
                    isPlanarBendSegment,
                    segmentPoints,
                    lastBarVector.Copy()));
                isPlanarBendSegment = !isPlanarBendSegment;
                segmentIndex++;
            }

            LastBarVector = lastBarVector.Copy();
            ApplyVirtualBendAllowancePoints(startsWithVerticalBend);
        }

        private void ApplyVirtualBendAllowancePoints(
            bool startsWithVerticalBend)
        {
            for (var segmentIndex = 0;
                 segmentIndex < SegmentationPoints.Count;
                 segmentIndex++)
            {
                if (SegmentationPoints[segmentIndex].Item1)
                {
                    continue;
                }

                var verticalSegmentPoints = SegmentationPoints[segmentIndex]
                    .Item2.Select(point => point.Copy()).ToList();
                if (segmentIndex != 0 &&
                    !(startsWithVerticalBend && segmentIndex < 2))
                {
                    var previousSegmentPoints =
                        SegmentationPoints[segmentIndex - 1]
                            .Item2.Select(point => point.Copy()).ToList();
                    var virtualPoints = CalculateVirtualPoints(
                        previousSegmentPoints[
                            previousSegmentPoints.Count - 2],
                        previousSegmentPoints[
                            previousSegmentPoints.Count - 1],
                        verticalSegmentPoints[1],
                        BusBarWidth / 2d + BusBarVerticalRadius);
                    previousSegmentPoints[previousSegmentPoints.Count - 1] =
                        virtualPoints[0];
                    verticalSegmentPoints.Insert(0, virtualPoints[1]);
                    SegmentationPoints[segmentIndex - 1] = Tuple.Create(
                        SegmentationPoints[segmentIndex - 1].Item1,
                        previousSegmentPoints,
                        SegmentationPoints[segmentIndex - 1].Item3);
                    SegmentationPoints[segmentIndex] = Tuple.Create(
                        false,
                        verticalSegmentPoints,
                        SegmentationPoints[segmentIndex].Item3);
                }

                if (segmentIndex == SegmentationPoints.Count - 1 ||
                    segmentIndex <= 0)
                {
                    continue;
                }

                var nextSegmentPoints = SegmentationPoints[segmentIndex + 1]
                    .Item2.Select(point => point.Copy()).ToList();
                var endVirtualPoints = CalculateVirtualPoints(
                    verticalSegmentPoints[verticalSegmentPoints.Count - 2],
                    verticalSegmentPoints[verticalSegmentPoints.Count - 1],
                    nextSegmentPoints[1],
                    BusBarThickness + BarBendingRadius);
                verticalSegmentPoints[verticalSegmentPoints.Count - 1] =
                    endVirtualPoints[0];
                nextSegmentPoints.Insert(0, endVirtualPoints[1]);
                SegmentationPoints[segmentIndex] = Tuple.Create(
                    false,
                    verticalSegmentPoints,
                    SegmentationPoints[segmentIndex].Item3);
                SegmentationPoints[segmentIndex + 1] = Tuple.Create(
                    SegmentationPoints[segmentIndex + 1].Item1,
                    nextSegmentPoints,
                    SegmentationPoints[segmentIndex + 1].Item3);
            }
        }

        private static List<Point3d> CalculateVirtualPoints(
            Point3d firstPoint,
            Point3d cornerPoint,
            Point3d thirdPoint,
            double offsetDistance)
        {
            var firstSegment = new MySegment(firstPoint, cornerPoint);
            var secondSegment = new MySegment(cornerPoint, thirdPoint);
            var tangentDistance = CalculateFilletTangentDistance(
                firstSegment,
                secondSegment,
                offsetDistance);
            var virtualPoint = cornerPoint.Move(
                new Vector3d(cornerPoint, firstPoint),
                tangentDistance + 1d);
            return new List<Point3d>
            {
                virtualPoint.Copy(),
                virtualPoint.Copy()
            };
        }

        private static double CalculateFilletTangentDistance(
            MySegment firstSegment,
            MySegment secondSegment,
            double radius)
        {
            if (firstSegment.EndPoint.DistanceTo(
                    secondSegment.StartPoint) > 0.001d)
            {
                Debug.Print("立弯相邻线段不在同一连接点。");
            }

            var cornerAngle = new Vector3d(
                    firstSegment.EndPoint,
                    firstSegment.StartPoint)
                .AngleTo(new Vector3d(
                    firstSegment.EndPoint,
                    secondSegment.EndPoint));
            if (cornerAngle <= 0.000001d ||
                Math.Abs(Math.PI - cornerAngle) <= 0.000001d)
            {
                throw new InvalidOperationException(
                    "立弯拐角角度无效，无法计算折弯余量。");
            }

            return radius / Math.Tan(cornerAngle / 2d);
        }
    }
}
