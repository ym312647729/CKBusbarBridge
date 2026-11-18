using GeometRi;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using static RouteLinkBar.Algorithm.Geometry.GeometRiMethod;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// 提供可制造中心线的折弯角度判定，以及保持两端搭接锚点不动的锐角修复。
    /// </summary>
    public static class LinkageRouteBendGeometry
    {
        public const double MinimumAllowedCornerAngleDegrees = 89.95d;
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.PointCoincidence;

        public static bool HasNoAcuteAngle(
            IList<Point3d> centerlinePoints,
            out int acuteCornerPointIndex,
            out double acuteCornerAngleDegrees)
        {
            acuteCornerPointIndex = -1;
            acuteCornerAngleDegrees = 180d;
            if (centerlinePoints == null || centerlinePoints.Count < 3)
            {
                return true;
            }

            for (var centerlineCornerIndex = 1;
                centerlineCornerIndex < centerlinePoints.Count - 1;
                centerlineCornerIndex++)
            {
                var directionTowardPreviousPoint = new Vector3d(
                    centerlinePoints[centerlineCornerIndex],
                    centerlinePoints[centerlineCornerIndex - 1]);
                var directionTowardNextPoint = new Vector3d(
                    centerlinePoints[centerlineCornerIndex],
                    centerlinePoints[centerlineCornerIndex + 1]);
                if (directionTowardPreviousPoint.Norm < GeometryTolerance ||
                    directionTowardNextPoint.Norm < GeometryTolerance)
                {
                    acuteCornerPointIndex = centerlineCornerIndex;
                    acuteCornerAngleDegrees = 0d;
                    return false;
                }

                var measuredCornerAngleDegrees = directionTowardPreviousPoint.Normalized
                    .AngleToDeg(directionTowardNextPoint.Normalized);
                if (measuredCornerAngleDegrees + GeometryTolerance >=
                    MinimumAllowedCornerAngleDegrees)
                {
                    continue;
                }

                acuteCornerPointIndex = centerlineCornerIndex;
                acuteCornerAngleDegrees = measuredCornerAngleDegrees;
                return false;
            }

            return true;
        }

        public static bool HasNoSelfIntersection(
            IList<Point3d> centerlinePoints,
            out int firstIntersectingSegmentIndex,
            out int secondIntersectingSegmentIndex)
        {
            firstIntersectingSegmentIndex = -1;
            secondIntersectingSegmentIndex = -1;
            if (centerlinePoints == null || centerlinePoints.Count < 4)
            {
                return true;
            }

            for (var firstSegmentIndex = 0;
                firstSegmentIndex < centerlinePoints.Count - 1;
                firstSegmentIndex++)
            {
                for (var secondSegmentIndex = firstSegmentIndex + 2;
                    secondSegmentIndex < centerlinePoints.Count - 1;
                    secondSegmentIndex++)
                {
                    var squaredSegmentDistance = GetSquaredSegmentDistance(
                        centerlinePoints[firstSegmentIndex],
                        centerlinePoints[firstSegmentIndex + 1],
                        centerlinePoints[secondSegmentIndex],
                        centerlinePoints[secondSegmentIndex + 1]);
                    if (squaredSegmentDistance > GeometryTolerance * GeometryTolerance)
                    {
                        continue;
                    }

                    firstIntersectingSegmentIndex = firstSegmentIndex;
                    secondIntersectingSegmentIndex = secondSegmentIndex;
                    return false;
                }
            }

            return true;
        }

        public static string ClassifyFourPointShape(IList<Point3d> fourPointCenterline)
        {
            if (fourPointCenterline == null || fourPointCenterline.Count != 4)
            {
                return "Converted";
            }

            var firstTravelDirection = new Vector3d(
                fourPointCenterline[0], fourPointCenterline[1]);
            var lastTravelDirection = new Vector3d(
                fourPointCenterline[2], fourPointCenterline[3]);
            if (firstTravelDirection.Norm < GeometryTolerance ||
                lastTravelDirection.Norm < GeometryTolerance)
            {
                return "Converted";
            }

            var normalizedFirstTravelDirection = firstTravelDirection.Normalized;
            var normalizedLastTravelDirection = lastTravelDirection.Normalized;
            if (normalizedFirstTravelDirection.Cross(normalizedLastTravelDirection).Norm <
                GeometryTolerance)
            {
                return normalizedFirstTravelDirection.Dot(normalizedLastTravelDirection) >= 0d
                    ? "N"
                    : "U";
            }

            return Math.Abs(normalizedFirstTravelDirection.Dot(normalizedLastTravelDirection)) <
                GeometryTolerance
                ? "M"
                : "Converted";
        }

        public static bool TryCreateRightAngleU(
            Point3d startLapAnchor,
            Point3d endLapAnchor,
            Vector3d firstTravelDirection,
            Vector3d lastTravelDirection,
            double requiredFirstLegLength,
            double requiredLastLegLength,
            double minimumSegmentLength,
            out IList<Point3d> rightAngleUCenterline)
        {
            rightAngleUCenterline = null;
            if (startLapAnchor == null || endLapAnchor == null ||
                firstTravelDirection == null || lastTravelDirection == null ||
                firstTravelDirection.Norm < GeometryTolerance ||
                lastTravelDirection.Norm < GeometryTolerance)
            {
                return false;
            }

            var normalizedFirstTravelDirection = firstTravelDirection.Normalized;
            var normalizedLastTravelDirection = lastTravelDirection.Normalized;
            if (normalizedFirstTravelDirection.Cross(normalizedLastTravelDirection).Norm >=
                    GeometryTolerance ||
                normalizedFirstTravelDirection.Dot(normalizedLastTravelDirection) >
                    -1d + GeometryTolerance)
            {
                return false;
            }

            var anchorDisplacement = new Vector3d(startLapAnchor, endLapAnchor);
            var longitudinalAnchorDisplacement = anchorDisplacement.Dot(
                normalizedFirstTravelDirection);
            var transverseAnchorDisplacement = Subtract(
                anchorDisplacement,
                Scale(normalizedFirstTravelDirection, longitudinalAnchorDisplacement));
            var requiredMiddleSegmentLength = Math.Max(0d, minimumSegmentLength);
            if (transverseAnchorDisplacement.Norm + GeometryTolerance <
                requiredMiddleSegmentLength)
            {
                return false;
            }

            var normalizedRequiredFirstLegLength = Math.Max(
                Math.Max(0d, requiredFirstLegLength), requiredMiddleSegmentLength);
            var normalizedRequiredLastLegLength = Math.Max(
                Math.Max(0d, requiredLastLegLength), requiredMiddleSegmentLength);
            var solvedFirstLegLength = Math.Max(
                normalizedRequiredFirstLegLength,
                normalizedRequiredLastLegLength + longitudinalAnchorDisplacement);
            var solvedLastLegLength = solvedFirstLegLength - longitudinalAnchorDisplacement;
            if (solvedFirstLegLength + GeometryTolerance < normalizedRequiredFirstLegLength ||
                solvedLastLegLength + GeometryTolerance < normalizedRequiredLastLegLength)
            {
                return false;
            }

            var firstRightAngleCorner = Translate(
                startLapAnchor,
                normalizedFirstTravelDirection,
                solvedFirstLegLength);
            var secondRightAngleCorner = Translate(
                firstRightAngleCorner,
                transverseAnchorDisplacement,
                1d);
            var expectedSecondRightAngleCorner = Translate(
                endLapAnchor,
                normalizedLastTravelDirection,
                -solvedLastLegLength);
            if (secondRightAngleCorner.DistanceTo(expectedSecondRightAngleCorner) >
                GeometryTolerance)
            {
                return false;
            }

            var repairedUCenterline = new List<Point3d>
            {
                Copy(startLapAnchor),
                firstRightAngleCorner,
                secondRightAngleCorner,
                Copy(endLapAnchor)
            };
            if (!HasNoAcuteAngle(
                    repairedUCenterline,
                    out _,
                    out _))
            {
                return false;
            }

            rightAngleUCenterline = repairedUCenterline;
            return true;
        }

        public static bool TryCreateRightAngleL(
            Point3d startLapAnchor,
            Point3d endLapAnchor,
            Vector3d firstTravelDirection,
            Vector3d lastTravelDirection,
            double requiredFirstLegLength,
            double requiredLastLegLength,
            double minimumSegmentLength,
            out IList<Point3d> rightAngleLCenterline)
        {
            rightAngleLCenterline = null;
            if (startLapAnchor == null || endLapAnchor == null ||
                firstTravelDirection == null || lastTravelDirection == null ||
                firstTravelDirection.Norm < GeometryTolerance ||
                lastTravelDirection.Norm < GeometryTolerance)
            {
                return false;
            }

            var normalizedFirstTravelDirection = firstTravelDirection.Normalized;
            var normalizedLastTravelDirection = lastTravelDirection.Normalized;
            if (Math.Abs(normalizedFirstTravelDirection.Dot(normalizedLastTravelDirection)) >=
                GeometryTolerance)
            {
                return false;
            }

            var startToEndVector = new Vector3d(
                startLapAnchor,
                endLapAnchor);
            var firstTravelDistance = startToEndVector.Dot(
                normalizedFirstTravelDirection);
            var lastTravelDistance = startToEndVector.Dot(
                normalizedLastTravelDirection);
            var offPlaneResidual = new Vector3d(
                startToEndVector.X -
                    normalizedFirstTravelDirection.X * firstTravelDistance -
                    normalizedLastTravelDirection.X * lastTravelDistance,
                startToEndVector.Y -
                    normalizedFirstTravelDirection.Y * firstTravelDistance -
                    normalizedLastTravelDirection.Y * lastTravelDistance,
                startToEndVector.Z -
                    normalizedFirstTravelDirection.Z * firstTravelDistance -
                    normalizedLastTravelDirection.Z * lastTravelDistance);
            if (offPlaneResidual.Norm > GeometryTolerance)
            {
                return false;
            }

            var theoreticalCorner = new Point3d(
                startLapAnchor.X +
                    normalizedFirstTravelDirection.X * firstTravelDistance,
                startLapAnchor.Y +
                    normalizedFirstTravelDirection.Y * firstTravelDistance,
                startLapAnchor.Z +
                    normalizedFirstTravelDirection.Z * firstTravelDistance);

            var firstLegDirection = new Vector3d(startLapAnchor, theoreticalCorner);
            var lastLegDirection = new Vector3d(theoreticalCorner, endLapAnchor);
            var normalizedMinimumSegmentLength = Math.Max(0d, minimumSegmentLength);
            var minimumFirstLegLength = Math.Max(
                Math.Max(0d, requiredFirstLegLength), normalizedMinimumSegmentLength);
            var minimumLastLegLength = Math.Max(
                Math.Max(0d, requiredLastLegLength), normalizedMinimumSegmentLength);
            if (firstLegDirection.Norm + GeometryTolerance < minimumFirstLegLength ||
                lastLegDirection.Norm + GeometryTolerance < minimumLastLegLength ||
                firstLegDirection.Dot(normalizedFirstTravelDirection) <= GeometryTolerance ||
                lastLegDirection.Dot(normalizedLastTravelDirection) <= GeometryTolerance)
            {
                return false;
            }

            var repairedLCenterline = new List<Point3d>
            {
                Copy(startLapAnchor),
                Copy(theoreticalCorner),
                Copy(endLapAnchor)
            };
            if (!HasNoAcuteAngle(
                    repairedLCenterline,
                    out _,
                    out _))
            {
                return false;
            }

            rightAngleLCenterline = repairedLCenterline;
            return true;
        }

        private static double GetSquaredSegmentDistance(
            Point3d firstSegmentStart,
            Point3d firstSegmentEnd,
            Point3d secondSegmentStart,
            Point3d secondSegmentEnd)
        {
            var firstSegmentDirection = new Vector3d(firstSegmentStart, firstSegmentEnd);
            var secondSegmentDirection = new Vector3d(secondSegmentStart, secondSegmentEnd);
            var segmentStartDisplacement = new Vector3d(secondSegmentStart, firstSegmentStart);
            var firstSquaredLength = firstSegmentDirection.Dot(firstSegmentDirection);
            var secondSquaredLength = secondSegmentDirection.Dot(secondSegmentDirection);
            var secondDirectionDisplacementProjection = secondSegmentDirection.Dot(
                segmentStartDisplacement);
            double firstSegmentParameter;
            double secondSegmentParameter;

            if (firstSquaredLength <= GeometryTolerance * GeometryTolerance &&
                secondSquaredLength <= GeometryTolerance * GeometryTolerance)
            {
                return GetSquaredPointDistance(firstSegmentStart, secondSegmentStart);
            }

            if (firstSquaredLength <= GeometryTolerance * GeometryTolerance)
            {
                firstSegmentParameter = 0d;
                secondSegmentParameter = ClampToUnitInterval(
                    secondDirectionDisplacementProjection / secondSquaredLength);
            }
            else
            {
                var firstDirectionDisplacementProjection = firstSegmentDirection.Dot(
                    segmentStartDisplacement);
                if (secondSquaredLength <= GeometryTolerance * GeometryTolerance)
                {
                    secondSegmentParameter = 0d;
                    firstSegmentParameter = ClampToUnitInterval(
                        -firstDirectionDisplacementProjection / firstSquaredLength);
                }
                else
                {
                    var directionDotProduct = firstSegmentDirection.Dot(secondSegmentDirection);
                    var segmentDenominator =
                        firstSquaredLength * secondSquaredLength -
                        directionDotProduct * directionDotProduct;
                    firstSegmentParameter = Math.Abs(segmentDenominator) >
                        GeometryTolerance * GeometryTolerance
                        ? ClampToUnitInterval(
                            (directionDotProduct * secondDirectionDisplacementProjection -
                             firstDirectionDisplacementProjection * secondSquaredLength) /
                            segmentDenominator)
                        : 0d;
                    secondSegmentParameter =
                        (directionDotProduct * firstSegmentParameter +
                         secondDirectionDisplacementProjection) /
                        secondSquaredLength;
                    if (secondSegmentParameter < 0d)
                    {
                        secondSegmentParameter = 0d;
                        firstSegmentParameter = ClampToUnitInterval(
                            -firstDirectionDisplacementProjection / firstSquaredLength);
                    }
                    else if (secondSegmentParameter > 1d)
                    {
                        secondSegmentParameter = 1d;
                        firstSegmentParameter = ClampToUnitInterval(
                            (directionDotProduct - firstDirectionDisplacementProjection) /
                            firstSquaredLength);
                    }
                }
            }

            var firstClosestPoint = Translate(
                firstSegmentStart,
                firstSegmentDirection,
                firstSegmentParameter);
            var secondClosestPoint = Translate(
                secondSegmentStart,
                secondSegmentDirection,
                secondSegmentParameter);
            return GetSquaredPointDistance(firstClosestPoint, secondClosestPoint);
        }

        private static double GetSquaredPointDistance(Point3d firstPoint, Point3d secondPoint)
        {
            var xDifference = firstPoint.X - secondPoint.X;
            var yDifference = firstPoint.Y - secondPoint.Y;
            var zDifference = firstPoint.Z - secondPoint.Z;
            return xDifference * xDifference +
                yDifference * yDifference +
                zDifference * zDifference;
        }

        private static double ClampToUnitInterval(double sourceValue)
        {
            return Math.Max(0d, Math.Min(1d, sourceValue));
        }
    }
}
