using GeometRi;
using System;
using System.Collections.Generic;

namespace RouteLinkBar.Algorithm.Geometry
{
    public static class LinkageRouteSolidDistance
    {
        private const double Tolerance =
            LinkageRouteGeometryTolerances.ExactPointCoincidence;
        private static readonly int[,] SolidEdgeVertexIndexPairs =
        {
            { 0, 1 }, { 0, 2 }, { 0, 4 },
            { 1, 3 }, { 1, 5 }, { 2, 3 },
            { 2, 6 }, { 3, 7 }, { 4, 5 },
            { 4, 6 }, { 5, 7 }, { 6, 7 }
        };

        // 仅用于已确认正体积相交的实体。给出沿 XYZ 轴退出重叠的最小投影位移；
        // 搭接绝缘盒已包含绝缘距离，此处不能再加通用净距或再次膨胀。
        internal static double GetOverlapAxisExitDistance(
            LinkageRouteSolid movableSolid,
            LinkageRouteSolid fixedSolid,
            out Vector3d exitDirection)
        {
            var movableExtents = movableSolid.GetAxisAlignedExtents();
            var fixedExtents = fixedSolid.GetAxisAlignedExtents();
            var centerOffsets = new[]
            {
                movableSolid.Center.X - fixedSolid.Center.X,
                movableSolid.Center.Y - fixedSolid.Center.Y,
                movableSolid.Center.Z - fixedSolid.Center.Z
            };
            var minimumExitDistance = double.PositiveInfinity;
            var selectedAxis = 0;
            for (var axisIndex = 0; axisIndex < 3; axisIndex++)
            {
                var axisExitDistance = Math.Max(0d, movableExtents[axisIndex] +
                    fixedExtents[axisIndex] - Math.Abs(centerOffsets[axisIndex]));
                if (axisExitDistance < minimumExitDistance ||
                    (axisExitDistance == minimumExitDistance &&
                     Math.Abs(centerOffsets[axisIndex]) > Math.Abs(centerOffsets[selectedAxis])))
                {
                    selectedAxis = axisIndex;
                    minimumExitDistance = axisExitDistance;
                }
            }
            var exitSign = centerOffsets[selectedAxis] < 0d ? -1d : 1d;
            exitDirection = new Vector3d(selectedAxis == 0 ? exitSign : 0d,
                selectedAxis == 1 ? exitSign : 0d, selectedAxis == 2 ? exitSign : 0d);
            return minimumExitDistance;
        }

        public static double GetAxisAlignedBoundingBoxDistanceLowerBound(
            LinkageRouteSolid firstSolid,
            LinkageRouteSolid secondSolid)
        {
            if (firstSolid == null || secondSolid == null)
            {
                return double.PositiveInfinity;
            }

            var firstExtents = firstSolid.GetAxisAlignedExtents();
            var secondExtents = secondSolid.GetAxisAlignedExtents();
            var xGap = Math.Max(0d,
                Math.Abs(firstSolid.Center.X - secondSolid.Center.X) -
                firstExtents[0] - secondExtents[0]);
            var yGap = Math.Max(0d,
                Math.Abs(firstSolid.Center.Y - secondSolid.Center.Y) -
                firstExtents[1] - secondExtents[1]);
            var zGap = Math.Max(0d,
                Math.Abs(firstSolid.Center.Z - secondSolid.Center.Z) -
                firstExtents[2] - secondExtents[2]);
            return Math.Sqrt(xGap * xGap + yGap * yGap + zGap * zGap);
        }

        public static double GetSegmentAxisAlignedBoundingBoxDistanceLowerBound(
            Point3d segmentStart,
            Point3d segmentEnd,
            LinkageRouteSolid targetSolid)
        {
            if (segmentStart == null || segmentEnd == null || targetSolid == null)
            {
                return double.PositiveInfinity;
            }

            var targetExtents = targetSolid.GetAxisAlignedExtents();
            var xGap = GetIntervalGap(
                Math.Min(segmentStart.X, segmentEnd.X),
                Math.Max(segmentStart.X, segmentEnd.X),
                targetSolid.Center.X - targetExtents[0],
                targetSolid.Center.X + targetExtents[0]);
            var yGap = GetIntervalGap(
                Math.Min(segmentStart.Y, segmentEnd.Y),
                Math.Max(segmentStart.Y, segmentEnd.Y),
                targetSolid.Center.Y - targetExtents[1],
                targetSolid.Center.Y + targetExtents[1]);
            var zGap = GetIntervalGap(
                Math.Min(segmentStart.Z, segmentEnd.Z),
                Math.Max(segmentStart.Z, segmentEnd.Z),
                targetSolid.Center.Z - targetExtents[2],
                targetSolid.Center.Z + targetExtents[2]);
            return Math.Sqrt(xGap * xGap + yGap * yGap + zGap * zGap);
        }

        public static double GetMinimumDistance(LinkageRouteSolid firstSolid, LinkageRouteSolid secondSolid)
        {
            if (firstSolid == null || secondSolid == null)
            {
                return double.PositiveInfinity;
            }

            if (Intersects(firstSolid, secondSolid))
            {
                return 0d;
            }

            var minimumDistance = double.PositiveInfinity;
            var firstVertices = firstSolid.GetVertices();
            var secondVertices = secondSolid.GetVertices();
            foreach (var firstVertex in firstVertices)
            {
                minimumDistance = Math.Min(minimumDistance, GetPointDistance(firstVertex, secondSolid));
            }

            foreach (var secondVertex in secondVertices)
            {
                minimumDistance = Math.Min(minimumDistance, GetPointDistance(secondVertex, firstSolid));
            }

            for (var firstEdgeIndex = 0;
                firstEdgeIndex < SolidEdgeVertexIndexPairs.GetLength(0);
                firstEdgeIndex++)
            {
                var firstEdgeStart = firstVertices[
                    SolidEdgeVertexIndexPairs[firstEdgeIndex, 0]];
                var firstEdgeEnd = firstVertices[
                    SolidEdgeVertexIndexPairs[firstEdgeIndex, 1]];
                for (var secondEdgeIndex = 0;
                    secondEdgeIndex < SolidEdgeVertexIndexPairs.GetLength(0);
                    secondEdgeIndex++)
                {
                    var secondEdgeStart = secondVertices[
                        SolidEdgeVertexIndexPairs[secondEdgeIndex, 0]];
                    var secondEdgeEnd = secondVertices[
                        SolidEdgeVertexIndexPairs[secondEdgeIndex, 1]];
                    if (GetSegmentPairAxisAlignedBoundingBoxDistanceLowerBound(
                            firstEdgeStart,
                            firstEdgeEnd,
                            secondEdgeStart,
                            secondEdgeEnd) >= minimumDistance)
                    {
                        continue;
                    }

                    minimumDistance = Math.Min(minimumDistance,
                        GetSegmentMinimumDistance(
                            firstEdgeStart,
                            firstEdgeEnd,
                            secondEdgeStart,
                            secondEdgeEnd));
                }
            }

            return minimumDistance;
        }

        /// <summary>
        /// 计算中心线段到有向长方体的真实最小距离。路径骨架阶段只在该距离已经
        /// 小于绝缘要求时提前淘汰；因为中心线属于最终铜排实体，这个剪枝不会把
        /// 可能满足绝缘的铜排误删。最终铜排仍须执行完整实体距离校验。
        /// </summary>
        public static double GetSegmentMinimumDistance(
            Point3d segmentStart,
            Point3d segmentEnd,
            LinkageRouteSolid targetSolid)
        {
            if (segmentStart == null || segmentEnd == null || targetSolid == null)
            {
                return double.PositiveInfinity;
            }

            if (SegmentIntersectsSolid(segmentStart, segmentEnd, targetSolid))
            {
                return 0d;
            }

            var minimumDistance = Math.Min(
                GetPointDistance(segmentStart, targetSolid),
                GetPointDistance(segmentEnd, targetSolid));
            var solidVertices = targetSolid.GetVertices();
            for (var solidEdgeIndex = 0;
                solidEdgeIndex < SolidEdgeVertexIndexPairs.GetLength(0);
                solidEdgeIndex++)
            {
                minimumDistance = Math.Min(
                    minimumDistance,
                    GetSegmentMinimumDistance(
                        segmentStart,
                        segmentEnd,
                        solidVertices[SolidEdgeVertexIndexPairs[
                            solidEdgeIndex, 0]],
                        solidVertices[SolidEdgeVertexIndexPairs[
                            solidEdgeIndex, 1]]));
            }

            return minimumDistance;
        }

        /// <summary>
        /// 计算一个中心线点到有向长方体真实表面的最小距离。中心线点本身已经
        /// 小于绝缘距离时，任何不移动该点的后续路径转换都不可能修复该违规。
        /// </summary>
        public static double GetPointMinimumDistance(
            Point3d sourcePoint,
            LinkageRouteSolid targetSolid)
        {
            if (sourcePoint == null || targetSolid == null)
            {
                return double.PositiveInfinity;
            }

            return GetPointDistance(sourcePoint, targetSolid);
        }

        public static double GetSegmentMinimumDistance(
            Point3d firstStart,
            Point3d firstEnd,
            Point3d secondStart,
            Point3d secondEnd)
        {
            return GetSegmentDistanceCore(
                firstStart,
                firstEnd,
                secondStart,
                secondEnd);
        }

        public static bool Intersects(LinkageRouteSolid firstSolid, LinkageRouteSolid secondSolid)
        {
            if (firstSolid == null || secondSolid == null)
            {
                return false;
            }

            var centerDifference = new Vector3d(firstSolid.Center, secondSolid.Center);
            foreach (var separationAxis in BuildSeparationAxes(firstSolid, secondSolid))
            {
                var centerDistance = Math.Abs(centerDifference.Dot(separationAxis));
                var projectionRadius = GetProjectionRadius(firstSolid, separationAxis) +
                    GetProjectionRadius(secondSolid, separationAxis);
                if (centerDistance > projectionRadius + Tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 判断两个有向长方体是否存在正体积重叠。仅共面、共边或共点接触时返回 false，
        /// 用于同相路径的实体自交检查；该规则独立于相间绝缘距离。
        /// </summary>
        public static bool HasPositiveVolumeIntersection(
            LinkageRouteSolid firstSolid,
            LinkageRouteSolid secondSolid,
            double positiveVolumeTolerance = 1e-6d)
        {
            if (firstSolid == null || secondSolid == null)
            {
                return false;
            }

            var normalizedPositiveVolumeTolerance = Math.Max(
                Tolerance,
                positiveVolumeTolerance);
            var solidCenterDifference = new Vector3d(
                firstSolid.Center,
                secondSolid.Center);
            foreach (var candidateSeparationAxis in BuildSeparationAxes(
                firstSolid,
                secondSolid))
            {
                var projectedCenterDistance = Math.Abs(
                    solidCenterDifference.Dot(candidateSeparationAxis));
                var combinedProjectionRadius =
                    GetProjectionRadius(firstSolid, candidateSeparationAxis) +
                    GetProjectionRadius(secondSolid, candidateSeparationAxis);
                var projectedOverlapDepth = combinedProjectionRadius -
                    projectedCenterDistance;
                if (projectedOverlapDepth <= normalizedPositiveVolumeTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static double GetPointDistance(Point3d sourcePoint, LinkageRouteSolid targetSolid)
        {
            var centerToPoint = new Vector3d(targetSolid.Center, sourcePoint);
            var lengthCoordinate = Clamp(centerToPoint.Dot(targetSolid.LengthDirection),
                -targetSolid.HalfLength, targetSolid.HalfLength);
            var widthCoordinate = Clamp(centerToPoint.Dot(targetSolid.WidthDirection),
                -targetSolid.HalfWidth, targetSolid.HalfWidth);
            var thicknessCoordinate = Clamp(centerToPoint.Dot(targetSolid.ThicknessDirection),
                -targetSolid.HalfThickness, targetSolid.HalfThickness);
            var closestPoint = LinkageRouteSolidGeometry.Translate(
                targetSolid.Center,
                targetSolid.LengthDirection,
                lengthCoordinate,
                targetSolid.WidthDirection,
                widthCoordinate,
                targetSolid.ThicknessDirection,
                thicknessCoordinate);
            return sourcePoint.DistanceTo(closestPoint);
        }

        private static double GetSegmentDistanceCore(
            Point3d firstStart,
            Point3d firstEnd,
            Point3d secondStart,
            Point3d secondEnd)
        {
            var firstDirection = new Vector3d(firstStart, firstEnd);
            var secondDirection = new Vector3d(secondStart, secondEnd);
            var secondToFirst = new Vector3d(secondStart, firstStart);
            var firstSquaredLength = firstDirection.Dot(firstDirection);
            var firstSecondDot = firstDirection.Dot(secondDirection);
            var secondSquaredLength = secondDirection.Dot(secondDirection);
            var firstOffsetDot = firstDirection.Dot(secondToFirst);
            var secondOffsetDot = secondDirection.Dot(secondToFirst);
            var denominator = firstSquaredLength * secondSquaredLength - firstSecondDot * firstSecondDot;
            if (firstSquaredLength <= Tolerance * Tolerance &&
                secondSquaredLength <= Tolerance * Tolerance)
            {
                return firstStart.DistanceTo(secondStart);
            }

            if (firstSquaredLength <= Tolerance * Tolerance)
            {
                return GetPointToSegmentDistance(
                    firstStart,
                    secondStart,
                    secondEnd,
                    secondSquaredLength);
            }

            if (secondSquaredLength <= Tolerance * Tolerance)
            {
                return GetPointToSegmentDistance(
                    secondStart,
                    firstStart,
                    firstEnd,
                    firstSquaredLength);
            }

            double firstParameterNumerator;
            double firstParameterDenominator = denominator;
            double secondParameterNumerator;
            double secondParameterDenominator = denominator;

            var denominatorTolerance = Tolerance *
                firstSquaredLength * secondSquaredLength;
            if (denominator <= denominatorTolerance)
            {
                firstParameterNumerator = 0d;
                firstParameterDenominator = 1d;
                secondParameterNumerator = secondOffsetDot;
                secondParameterDenominator = secondSquaredLength;
            }
            else
            {
                firstParameterNumerator = firstSecondDot * secondOffsetDot - secondSquaredLength * firstOffsetDot;
                secondParameterNumerator = firstSquaredLength * secondOffsetDot - firstSecondDot * firstOffsetDot;
                if (firstParameterNumerator < 0d)
                {
                    firstParameterNumerator = 0d;
                    secondParameterNumerator = secondOffsetDot;
                    secondParameterDenominator = secondSquaredLength;
                }
                else if (firstParameterNumerator > firstParameterDenominator)
                {
                    firstParameterNumerator = firstParameterDenominator;
                    secondParameterNumerator = secondOffsetDot + firstSecondDot;
                    secondParameterDenominator = secondSquaredLength;
                }
            }

            if (secondParameterNumerator < 0d)
            {
                secondParameterNumerator = 0d;
                if (-firstOffsetDot < 0d)
                {
                    firstParameterNumerator = 0d;
                }
                else if (-firstOffsetDot > firstSquaredLength)
                {
                    firstParameterNumerator = firstParameterDenominator;
                }
                else
                {
                    firstParameterNumerator = -firstOffsetDot;
                    firstParameterDenominator = firstSquaredLength;
                }
            }
            else if (secondParameterNumerator > secondParameterDenominator)
            {
                secondParameterNumerator = secondParameterDenominator;
                var shiftedFirstOffset = -firstOffsetDot + firstSecondDot;
                if (shiftedFirstOffset < 0d)
                {
                    firstParameterNumerator = 0d;
                }
                else if (shiftedFirstOffset > firstSquaredLength)
                {
                    firstParameterNumerator = firstParameterDenominator;
                }
                else
                {
                    firstParameterNumerator = shiftedFirstOffset;
                    firstParameterDenominator = firstSquaredLength;
                }
            }

            var firstParameter = firstParameterNumerator /
                firstParameterDenominator;
            var secondParameter = secondParameterNumerator /
                secondParameterDenominator;
            var closestDifference = new Vector3d(
                secondToFirst.X + firstParameter * firstDirection.X - secondParameter * secondDirection.X,
                secondToFirst.Y + firstParameter * firstDirection.Y - secondParameter * secondDirection.Y,
                secondToFirst.Z + firstParameter * firstDirection.Z - secondParameter * secondDirection.Z);
            return closestDifference.Norm;
        }

        private static double GetPointToSegmentDistance(
            Point3d sourcePoint,
            Point3d segmentStart,
            Point3d segmentEnd,
            double segmentSquaredLength)
        {
            if (segmentSquaredLength <= Tolerance * Tolerance)
            {
                return sourcePoint.DistanceTo(segmentStart);
            }

            var segmentDirection = new Vector3d(segmentStart, segmentEnd);
            var segmentStartToPoint = new Vector3d(segmentStart, sourcePoint);
            var segmentParameter = Clamp(
                segmentStartToPoint.Dot(segmentDirection) /
                    segmentSquaredLength,
                0d,
                1d);
            var closestPoint = new Point3d(
                segmentStart.X + segmentDirection.X * segmentParameter,
                segmentStart.Y + segmentDirection.Y * segmentParameter,
                segmentStart.Z + segmentDirection.Z * segmentParameter);
            return sourcePoint.DistanceTo(closestPoint);
        }

        private static bool SegmentIntersectsSolid(
            Point3d segmentStart,
            Point3d segmentEnd,
            LinkageRouteSolid targetSolid)
        {
            var segmentDirection = new Vector3d(segmentStart, segmentEnd);
            var solidCenterToStart = new Vector3d(targetSolid.Center, segmentStart);
            var minimumParameter = 0d;
            var maximumParameter = 1d;
            var solidAxes = new[]
            {
                targetSolid.LengthDirection,
                targetSolid.WidthDirection,
                targetSolid.ThicknessDirection
            };
            var solidHalfExtents = new[]
            {
                targetSolid.HalfLength,
                targetSolid.HalfWidth,
                targetSolid.HalfThickness
            };
            for (var axisIndex = 0; axisIndex < solidAxes.Length; axisIndex++)
            {
                var startCoordinate = solidCenterToStart.Dot(solidAxes[axisIndex]);
                var directionCoordinate = segmentDirection.Dot(solidAxes[axisIndex]);
                var halfExtent = solidHalfExtents[axisIndex];
                if (Math.Abs(directionCoordinate) <= Tolerance)
                {
                    if (startCoordinate < -halfExtent - Tolerance ||
                        startCoordinate > halfExtent + Tolerance)
                    {
                        return false;
                    }

                    continue;
                }

                var firstIntersectionParameter =
                    (-halfExtent - startCoordinate) / directionCoordinate;
                var secondIntersectionParameter =
                    (halfExtent - startCoordinate) / directionCoordinate;
                if (firstIntersectionParameter > secondIntersectionParameter)
                {
                    var swappedParameter = firstIntersectionParameter;
                    firstIntersectionParameter = secondIntersectionParameter;
                    secondIntersectionParameter = swappedParameter;
                }

                minimumParameter = Math.Max(minimumParameter, firstIntersectionParameter);
                maximumParameter = Math.Min(maximumParameter, secondIntersectionParameter);
                if (minimumParameter > maximumParameter + Tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static IReadOnlyList<Vector3d> BuildSeparationAxes(
            LinkageRouteSolid firstSolid,
            LinkageRouteSolid secondSolid)
        {
            var firstSolidAxes = firstSolid.GetAxes();
            var secondSolidAxes = secondSolid.GetAxes();
            var candidateSeparationAxes = new List<Vector3d>();
            candidateSeparationAxes.AddRange(firstSolidAxes);
            candidateSeparationAxes.AddRange(secondSolidAxes);
            foreach (var firstSolidAxis in firstSolidAxes)
            {
                foreach (var secondSolidAxis in secondSolidAxes)
                {
                    var crossProductAxis = firstSolidAxis.Cross(secondSolidAxis);
                    if (crossProductAxis != null && crossProductAxis.Norm > Tolerance)
                    {
                        candidateSeparationAxes.Add(crossProductAxis.Normalized);
                    }
                }
            }

            return candidateSeparationAxes;
        }

        private static double GetIntervalGap(
            double firstMinimum,
            double firstMaximum,
            double secondMinimum,
            double secondMaximum)
        {
            if (firstMaximum < secondMinimum)
            {
                return secondMinimum - firstMaximum;
            }

            return secondMaximum < firstMinimum
                ? firstMinimum - secondMaximum
                : 0d;
        }

        private static double
            GetSegmentPairAxisAlignedBoundingBoxDistanceLowerBound(
            Point3d firstSegmentStart,
            Point3d firstSegmentEnd,
            Point3d secondSegmentStart,
            Point3d secondSegmentEnd)
        {
            var xGap = GetIntervalGap(
                Math.Min(firstSegmentStart.X, firstSegmentEnd.X),
                Math.Max(firstSegmentStart.X, firstSegmentEnd.X),
                Math.Min(secondSegmentStart.X, secondSegmentEnd.X),
                Math.Max(secondSegmentStart.X, secondSegmentEnd.X));
            var yGap = GetIntervalGap(
                Math.Min(firstSegmentStart.Y, firstSegmentEnd.Y),
                Math.Max(firstSegmentStart.Y, firstSegmentEnd.Y),
                Math.Min(secondSegmentStart.Y, secondSegmentEnd.Y),
                Math.Max(secondSegmentStart.Y, secondSegmentEnd.Y));
            var zGap = GetIntervalGap(
                Math.Min(firstSegmentStart.Z, firstSegmentEnd.Z),
                Math.Max(firstSegmentStart.Z, firstSegmentEnd.Z),
                Math.Min(secondSegmentStart.Z, secondSegmentEnd.Z),
                Math.Max(secondSegmentStart.Z, secondSegmentEnd.Z));
            return Math.Sqrt(xGap * xGap + yGap * yGap + zGap * zGap);
        }

        public static double GetPointAxisAlignedBoundingBoxDistanceLowerBound(
            Point3d sourcePoint,
            LinkageRouteSolid targetSolid)
        {
            if (sourcePoint == null || targetSolid == null)
            {
                return double.PositiveInfinity;
            }

            var targetExtents = targetSolid.GetAxisAlignedExtents();
            var xGap = GetIntervalGap(
                sourcePoint.X,
                sourcePoint.X,
                targetSolid.Center.X - targetExtents[0],
                targetSolid.Center.X + targetExtents[0]);
            var yGap = GetIntervalGap(
                sourcePoint.Y,
                sourcePoint.Y,
                targetSolid.Center.Y - targetExtents[1],
                targetSolid.Center.Y + targetExtents[1]);
            var zGap = GetIntervalGap(
                sourcePoint.Z,
                sourcePoint.Z,
                targetSolid.Center.Z - targetExtents[2],
                targetSolid.Center.Z + targetExtents[2]);
            return Math.Sqrt(xGap * xGap + yGap * yGap + zGap * zGap);
        }

        private static double GetProjectionRadius(LinkageRouteSolid routeSolid, Vector3d projectionAxis)
        {
            return Math.Abs(routeSolid.LengthDirection.Dot(projectionAxis)) * routeSolid.HalfLength +
                Math.Abs(routeSolid.WidthDirection.Dot(projectionAxis)) * routeSolid.HalfWidth +
                Math.Abs(routeSolid.ThicknessDirection.Dot(projectionAxis)) * routeSolid.HalfThickness;
        }

        private static double Clamp(double sourceValue, double minimumValue, double maximumValue)
        {
            return Math.Max(minimumValue, Math.Min(maximumValue, sourceValue));
        }

    }
}
