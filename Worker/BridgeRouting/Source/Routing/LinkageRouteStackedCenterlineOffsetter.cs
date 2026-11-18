using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using static RouteLinkBar.Algorithm.Geometry.GeometRiMethod;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// 参考 DQBusbarBridge 的叠排分段偏移：首段、中间段和末段可以使用不同
    /// 偏移量，转角由相邻偏移线求交，并对求交后产生的锐角进行回退修复。
    /// </summary>
    internal static class LinkageRouteStackedCenterlineOffsetter
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.ExactPointCoincidence;
        private const double MinimumStableOffsetCornerAngleDegrees = 89.95d;

        public static bool TryOffset(
            PlanarBendBusbarSetting sourceSetting,
            IReadOnlyList<double> segmentOffsetDistances,
            out List<Point3d> offsetPointList,
            out string failureDiagnostic)
        {
            return TryOffset(
                sourceSetting,
                segmentOffsetDistances,
                false,
                out offsetPointList,
                out failureDiagnostic);
        }

        public static bool TryOffset(
            PlanarBendBusbarSetting sourceSetting,
            IReadOnlyList<double> segmentOffsetDistances,
            bool repairOnlyWhenRequired,
            out List<Point3d> offsetPointList,
            out string failureDiagnostic)
        {
            offsetPointList = new List<Point3d>();
            failureDiagnostic = null;
            if (sourceSetting?.PointList == null || sourceSetting.PointList.Count < 2 ||
                sourceSetting.StartBusbarVector == null ||
                sourceSetting.StartBusbarVector.Norm <= GeometryTolerance ||
                sourceSetting.WidthVector == null ||
                sourceSetting.WidthVector.Norm <= GeometryTolerance)
            {
                failureDiagnostic = "叠排偏移缺少有效平弯铜排中心线或方向。";
                return false;
            }

            var sourceSegmentCount = sourceSetting.PointList.Count - 1;
            if (segmentOffsetDistances == null ||
                segmentOffsetDistances.Count != sourceSegmentCount)
            {
                failureDiagnostic = "叠排分段偏移数量与中心线段数不一致。";
                return false;
            }

            var normalizedWidthDirection = sourceSetting.WidthVector.Normalized;
            var normalizedStartThicknessDirection =
                sourceSetting.StartBusbarVector.Normalized;
            var rawOffsetSegments = new List<OffsetSegment>(sourceSegmentCount);
            double? pathThicknessOrientationSign = null;
            for (var sourceSegmentIndex = 0;
                sourceSegmentIndex < sourceSegmentCount;
                sourceSegmentIndex++)
            {
                var sourceSegmentDirection = new Vector3d(
                    sourceSetting.PointList[sourceSegmentIndex],
                    sourceSetting.PointList[sourceSegmentIndex + 1]);
                if (sourceSegmentDirection.Norm <= GeometryTolerance)
                {
                    failureDiagnostic = "叠排偏移输入包含零长度线段。";
                    return false;
                }

                sourceSegmentDirection = sourceSegmentDirection.Normalized;
                var localThicknessDirection = sourceSegmentDirection
                    .Cross(normalizedWidthDirection);
                if (localThicknessDirection == null ||
                    localThicknessDirection.Norm <= GeometryTolerance)
                {
                    failureDiagnostic = "叠排偏移无法建立局部排厚方向。";
                    return false;
                }

                localThicknessDirection = localThicknessDirection.Normalized;
                if (!pathThicknessOrientationSign.HasValue)
                {
                    pathThicknessOrientationSign = localThicknessDirection.Dot(
                            normalizedStartThicknessDirection) < 0d
                        ? -1d
                        : 1d;
                }

                if (pathThicknessOrientationSign.Value < 0d)
                {
                    localThicknessDirection = Scale(localThicknessDirection, -1d);
                }

                rawOffsetSegments.Add(new OffsetSegment
                {
                    StartPoint = Translate(
                        sourceSetting.PointList[sourceSegmentIndex],
                        localThicknessDirection,
                        segmentOffsetDistances[sourceSegmentIndex]),
                    EndPoint = Translate(
                        sourceSetting.PointList[sourceSegmentIndex + 1],
                        localThicknessDirection,
                        segmentOffsetDistances[sourceSegmentIndex]),
                    Direction = sourceSegmentDirection
                });
            }

            var rebuiltOffsetSegments = BuildIntersectedSegments(
                rawOffsetSegments,
                normalizedWidthDirection);
            var exactOffsetPointList = BuildPointList(rebuiltOffsetSegments);
            var exactOffsetHasNoAcuteAngle =
                LinkageRouteBendGeometry.HasNoAcuteAngle(
                    exactOffsetPointList,
                    out _,
                    out _);
            var exactOffsetHasNoSelfIntersection =
                LinkageRouteBendGeometry.HasNoSelfIntersection(
                    exactOffsetPointList,
                    out _,
                    out _);
            var exactOffsetIsManufacturable = exactOffsetHasNoAcuteAngle &&
                exactOffsetHasNoSelfIntersection;
            if ((!repairOnlyWhenRequired || !exactOffsetIsManufacturable) &&
                !TryRepairAcuteOffsetSegments(
                    rebuiltOffsetSegments,
                    rawOffsetSegments,
                    out failureDiagnostic))
            {
                return false;
            }

            if (rebuiltOffsetSegments.Count == 0 ||
                rebuiltOffsetSegments.Exists(offsetSegment =>
                    offsetSegment.StartPoint.DistanceTo(offsetSegment.EndPoint) <=
                    GeometryTolerance))
            {
                failureDiagnostic = "叠排首中末分段偏移后产生零长度线段。";
                return false;
            }

            offsetPointList.AddRange(BuildPointList(rebuiltOffsetSegments));

            return true;
        }

        private static List<Point3d> BuildPointList(
            IReadOnlyList<OffsetSegment> offsetSegments)
        {
            var centerlinePoints = new List<Point3d>();
            if (offsetSegments == null || offsetSegments.Count == 0)
            {
                return centerlinePoints;
            }

            centerlinePoints.Add(Copy(offsetSegments[0].StartPoint));
            foreach (var offsetSegment in offsetSegments)
            {
                centerlinePoints.Add(Copy(offsetSegment.EndPoint));
            }

            return centerlinePoints;
        }

        private static List<OffsetSegment> BuildIntersectedSegments(
            IReadOnlyList<OffsetSegment> rawOffsetSegments,
            Vector3d normalizedWidthDirection)
        {
            var rebuiltOffsetPoints = new List<Point3d>
            {
                Copy(rawOffsetSegments[0].StartPoint)
            };
            for (var junctionIndex = 0;
                junctionIndex + 1 < rawOffsetSegments.Count;
                junctionIndex++)
            {
                var precedingOffsetSegment = rawOffsetSegments[junctionIndex];
                var followingOffsetSegment = rawOffsetSegments[junctionIndex + 1];
                var intersectionDenominator = normalizedWidthDirection.Dot(
                    precedingOffsetSegment.Direction.Cross(
                        followingOffsetSegment.Direction));
                if (Math.Abs(intersectionDenominator) <= GeometryTolerance)
                {
                    rebuiltOffsetPoints.Add(new Point3d(
                        (precedingOffsetSegment.EndPoint.X +
                            followingOffsetSegment.StartPoint.X) / 2d,
                        (precedingOffsetSegment.EndPoint.Y +
                            followingOffsetSegment.StartPoint.Y) / 2d,
                        (precedingOffsetSegment.EndPoint.Z +
                            followingOffsetSegment.StartPoint.Z) / 2d));
                    continue;
                }

                var offsetLineStartDifference = new Vector3d(
                    precedingOffsetSegment.StartPoint,
                    followingOffsetSegment.StartPoint);
                var precedingLineParameter = normalizedWidthDirection.Dot(
                        offsetLineStartDifference.Cross(
                            followingOffsetSegment.Direction)) /
                    intersectionDenominator;
                rebuiltOffsetPoints.Add(Translate(
                    precedingOffsetSegment.StartPoint,
                    precedingOffsetSegment.Direction,
                    precedingLineParameter));
            }

            rebuiltOffsetPoints.Add(Copy(
                rawOffsetSegments[rawOffsetSegments.Count - 1].EndPoint));
            var rebuiltOffsetSegments = new List<OffsetSegment>(
                rebuiltOffsetPoints.Count - 1);
            for (var rebuiltSegmentIndex = 0;
                rebuiltSegmentIndex + 1 < rebuiltOffsetPoints.Count;
                rebuiltSegmentIndex++)
            {
                rebuiltOffsetSegments.Add(new OffsetSegment
                {
                    StartPoint = Copy(rebuiltOffsetPoints[rebuiltSegmentIndex]),
                    EndPoint = Copy(rebuiltOffsetPoints[rebuiltSegmentIndex + 1]),
                    Direction = rawOffsetSegments[rebuiltSegmentIndex].Direction
                });
            }

            return rebuiltOffsetSegments;
        }

        private static bool TryRepairAcuteOffsetSegments(
            IList<OffsetSegment> rebuiltOffsetSegments,
            IReadOnlyList<OffsetSegment> rawOffsetSegments,
            out string failureDiagnostic)
        {
            failureDiagnostic = null;
            var rebuiltOffsetSnapshot = new List<OffsetSegment>(
                rebuiltOffsetSegments.Count);
            foreach (var rebuiltOffsetSegment in rebuiltOffsetSegments)
            {
                rebuiltOffsetSnapshot.Add(new OffsetSegment
                {
                    StartPoint = Copy(rebuiltOffsetSegment.StartPoint),
                    EndPoint = Copy(rebuiltOffsetSegment.EndPoint),
                    Direction = new Vector3d(
                        rebuiltOffsetSegment.Direction.X,
                        rebuiltOffsetSegment.Direction.Y,
                        rebuiltOffsetSegment.Direction.Z)
                });
            }

            var requestedJunctionPoints = new Dictionary<int, Point3d>();
            for (var middleSegmentIndex = 1;
                middleSegmentIndex + 1 < rebuiltOffsetSnapshot.Count;
                middleSegmentIndex++)
            {
                var precedingRebuiltSegment =
                    rebuiltOffsetSnapshot[middleSegmentIndex - 1];
                var middleRebuiltSegment = rebuiltOffsetSnapshot[middleSegmentIndex];
                var followingRebuiltSegment =
                    rebuiltOffsetSnapshot[middleSegmentIndex + 1];
                var middleSegmentVector = new Vector3d(
                    middleRebuiltSegment.StartPoint,
                    middleRebuiltSegment.EndPoint);
                var precedingReverseVector = new Vector3d(
                    precedingRebuiltSegment.EndPoint,
                    precedingRebuiltSegment.StartPoint);
                var followingReverseVector = new Vector3d(
                    followingRebuiltSegment.EndPoint,
                    followingRebuiltSegment.StartPoint);
                if (middleSegmentVector.Norm <= GeometryTolerance ||
                    precedingReverseVector.Norm <= GeometryTolerance ||
                    followingReverseVector.Norm <= GeometryTolerance)
                {
                    continue;
                }

                var precedingCornerAngle = middleSegmentVector.AngleToDeg(
                    precedingReverseVector);
                var followingCornerAngle = middleSegmentVector.AngleToDeg(
                    followingReverseVector);
                if (precedingCornerAngle >=
                        MinimumStableOffsetCornerAngleDegrees &&
                    followingCornerAngle >=
                        MinimumStableOffsetCornerAngleDegrees)
                {
                    continue;
                }

                if (precedingCornerAngle <
                        MinimumStableOffsetCornerAngleDegrees &&
                    !TryRegisterRepairJunction(
                        requestedJunctionPoints,
                        middleSegmentIndex,
                        Copy(rawOffsetSegments[
                            middleSegmentIndex - 1].EndPoint),
                        out var startJunctionConflictDistance))
                {
                    failureDiagnostic = CreateJunctionConflictDiagnostic(
                        middleSegmentIndex,
                        startJunctionConflictDistance);
                    return false;
                }

                if (followingCornerAngle <
                        MinimumStableOffsetCornerAngleDegrees &&
                    !TryRegisterRepairJunction(
                        requestedJunctionPoints,
                        middleSegmentIndex + 1,
                        Copy(rawOffsetSegments[
                            middleSegmentIndex + 1].StartPoint),
                        out var endJunctionConflictDistance))
                {
                    failureDiagnostic = CreateJunctionConflictDiagnostic(
                        middleSegmentIndex + 1,
                        endJunctionConflictDistance);
                    return false;
                }
            }

            foreach (var requestedJunctionPoint in requestedJunctionPoints)
            {
                var junctionIndex = requestedJunctionPoint.Key;
                var repairedJunctionPoint = requestedJunctionPoint.Value;
                rebuiltOffsetSegments[junctionIndex - 1].EndPoint =
                    Copy(repairedJunctionPoint);
                rebuiltOffsetSegments[junctionIndex].StartPoint =
                    Copy(repairedJunctionPoint);
            }

            return true;
        }

        private static bool TryRegisterRepairJunction(
            IDictionary<int, Point3d> requestedJunctionPoints,
            int junctionIndex,
            Point3d requestedJunctionPoint,
            out double conflictingJunctionDistance)
        {
            conflictingJunctionDistance = 0d;
            if (!requestedJunctionPoints.TryGetValue(
                    junctionIndex,
                    out var existingJunctionPoint))
            {
                requestedJunctionPoints.Add(
                    junctionIndex,
                    Copy(requestedJunctionPoint));
                return true;
            }

            conflictingJunctionDistance = existingJunctionPoint.DistanceTo(
                requestedJunctionPoint);
            return conflictingJunctionDistance <= GeometryTolerance;
        }

        private static string CreateJunctionConflictDiagnostic(
            int junctionIndex,
            double conflictingJunctionDistance)
        {
            return
                $"叠排连续锐角偏移回退在第 {junctionIndex} 个接缝产生互斥位置，" +
                $"共享接缝相差 {conflictingJunctionDistance:0.######} mm。";
        }

        private sealed class OffsetSegment
        {
            public Point3d StartPoint { get; set; }
            public Point3d EndPoint { get; set; }
            public Vector3d Direction { get; set; }
        }
    }
}
