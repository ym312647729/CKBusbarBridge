using GeometRi;
using System;

namespace RouteLinkBar.Algorithm.Geometry
{
    public sealed class LinkageRouteLapSegment
    {
        public LinkageRouteLapSegment(
            Point3d segmentStart,
            Point3d segmentEnd,
            Vector3d widthDirection,
            Vector3d thicknessDirection)
            : this(segmentStart, segmentEnd, widthDirection, thicknessDirection, 0d, 0d)
        {
        }

        public LinkageRouteLapSegment(
            Point3d segmentStart,
            Point3d segmentEnd,
            Vector3d widthDirection,
            Vector3d thicknessDirection,
            double singleBusbarThickness)
            : this(
                segmentStart,
                segmentEnd,
                widthDirection,
                thicknessDirection,
                singleBusbarThickness,
                0d)
        {
        }

        public LinkageRouteLapSegment(
            Point3d segmentStart,
            Point3d segmentEnd,
            Vector3d widthDirection,
            Vector3d thicknessDirection,
            double singleBusbarThickness,
            double busbarWidth)
        {
            Start = segmentStart ?? throw new ArgumentNullException(nameof(segmentStart));
            End = segmentEnd ?? throw new ArgumentNullException(nameof(segmentEnd));
            WidthDirection = widthDirection ?? throw new ArgumentNullException(nameof(widthDirection));
            ThicknessDirection = thicknessDirection ?? throw new ArgumentNullException(nameof(thicknessDirection));
            SingleBusbarThickness = singleBusbarThickness;
            BusbarWidth = busbarWidth;
        }

        public Point3d Start { get; }
        public Point3d End { get; }
        public Vector3d WidthDirection { get; }
        public Vector3d ThicknessDirection { get; }
        public double SingleBusbarThickness { get; }
        public double BusbarWidth { get; }
    }

    public sealed class LinkageRouteLapEvaluation
    {
        public bool IsValid { get; internal set; }
        public bool IsPerpendicular { get; internal set; }
        public double ThicknessDistance { get; internal set; }
        public double RequiredThicknessDistance { get; internal set; }
        public double OverlapLength { get; internal set; }
        public double WidthOverlapLength { get; internal set; }
        public double FirstRequiredEndDistance { get; internal set; }
        public double SecondRequiredEndDistance { get; internal set; }
        public Point3d ProjectedIntersection { get; internal set; }
        public string Message { get; internal set; }
    }

    public static class LinkageRouteLapValidator
    {
        private const double DirectionTolerance =
            LinkageRouteGeometryTolerances.Direction;

        public static LinkageRouteLapEvaluation Evaluate(
            LinkageRouteLapSegment firstSegment,
            LinkageRouteLapSegment secondSegment,
            double requiredBusbarWidth)
        {
            var requiredThicknessDistance = firstSegment == null || secondSegment == null
                ? 0d
                : (firstSegment.SingleBusbarThickness + secondSegment.SingleBusbarThickness) / 2d;
            return EvaluateCore(
                firstSegment,
                secondSegment,
                requiredBusbarWidth,
                requiredThicknessDistance,
                1e-4,
                false,
                false);
        }

        /// <summary>
        /// 校验搭接，并仅在垂直接口上允许指定线段采用单侧、一个较窄排宽的
        /// 实际重合。未指定的一侧仍必须跨过对方完整排宽。
        /// </summary>
        public static LinkageRouteLapEvaluation EvaluateAllowingOneSidedPerpendicularLap(
            LinkageRouteLapSegment firstSegment,
            LinkageRouteLapSegment secondSegment,
            double requiredBusbarWidth,
            bool allowFirstSegmentOneSided,
            bool allowSecondSegmentOneSided)
        {
            var requiredThicknessDistance = firstSegment == null || secondSegment == null
                ? 0d
                : (firstSegment.SingleBusbarThickness +
                    secondSegment.SingleBusbarThickness) / 2d;
            return EvaluateCore(
                firstSegment,
                secondSegment,
                requiredBusbarWidth,
                requiredThicknessDistance,
                1e-4,
                allowFirstSegmentOneSided,
                allowSecondSegmentOneSided);
        }

        public static LinkageRouteLapEvaluation Evaluate(
            LinkageRouteLapSegment firstSegment,
            LinkageRouteLapSegment secondSegment,
            double requiredBusbarWidth,
            double singleBusbarThickness,
            double tolerance = 1e-4)
        {
            return EvaluateCore(
                firstSegment,
                secondSegment,
                requiredBusbarWidth,
                singleBusbarThickness,
                tolerance,
                false,
                false);
        }

        public static LinkageRouteLapEvaluation EvaluateAllowingOneSidedPerpendicularLap(
            LinkageRouteLapSegment firstSegment,
            LinkageRouteLapSegment secondSegment,
            double requiredBusbarWidth,
            double requiredThicknessDistance,
            bool allowFirstSegmentOneSided,
            bool allowSecondSegmentOneSided,
            double tolerance = 1e-4)
        {
            return EvaluateCore(
                firstSegment,
                secondSegment,
                requiredBusbarWidth,
                requiredThicknessDistance,
                tolerance,
                allowFirstSegmentOneSided,
                allowSecondSegmentOneSided);
        }

        private static LinkageRouteLapEvaluation EvaluateCore(
            LinkageRouteLapSegment firstSegment,
            LinkageRouteLapSegment secondSegment,
            double requiredBusbarWidth,
            double requiredThicknessDistance,
            double tolerance,
            bool allowFirstSegmentOneSidedPerpendicularLap,
            bool allowSecondSegmentOneSidedPerpendicularLap)
        {
            var lapEvaluation = new LinkageRouteLapEvaluation
            {
                RequiredThicknessDistance = requiredThicknessDistance
            };
            if (firstSegment == null || secondSegment == null ||
                requiredBusbarWidth <= 0d || requiredThicknessDistance <= 0d)
            {
                lapEvaluation.Message = "搭接输入无效。";
                return lapEvaluation;
            }

            var firstThicknessDirection = firstSegment.ThicknessDirection.Normalized;
            var secondThicknessDirection = secondSegment.ThicknessDirection.Normalized;
            if (firstThicknessDirection.Cross(secondThicknessDirection).Norm > DirectionTolerance)
            {
                lapEvaluation.Message = "相邻搭接段的排厚方向不平行。";
                return lapEvaluation;
            }

            var firstToSecond = new Vector3d(firstSegment.Start, secondSegment.Start);
            lapEvaluation.ThicknessDistance = Math.Abs(firstToSecond.Dot(firstThicknessDirection));
            if (Math.Abs(lapEvaluation.ThicknessDistance - requiredThicknessDistance) > tolerance)
            {
                lapEvaluation.Message = "中心线沿排厚方向的距离不等于两根单排厚度之和的一半。";
                return lapEvaluation;
            }

            var projectedSecondStart = ProjectToPlane(
                secondSegment.Start, firstSegment.Start, firstThicknessDirection);
            var projectedSecondEnd = ProjectToPlane(
                secondSegment.End, firstSegment.Start, firstThicknessDirection);
            var firstDirection = new Vector3d(firstSegment.Start, firstSegment.End);
            var secondDirection = new Vector3d(projectedSecondStart, projectedSecondEnd);
            if (firstDirection.Norm <= DirectionTolerance || secondDirection.Norm <= DirectionTolerance)
            {
                lapEvaluation.Message = "搭接线段长度为零。";
                return lapEvaluation;
            }

            var normalizedFirstDirection = firstDirection.Normalized;
            var normalizedSecondDirection = secondDirection.Normalized;
            var firstBusbarWidth = ResolveBusbarWidth(firstSegment, requiredBusbarWidth);
            var secondBusbarWidth = ResolveBusbarWidth(secondSegment, requiredBusbarWidth);
            if (normalizedFirstDirection.Cross(normalizedSecondDirection).Norm <= DirectionTolerance)
            {
                return EvaluateParallel(
                    lapEvaluation,
                    firstSegment.Start,
                    firstSegment.End,
                    projectedSecondStart,
                    projectedSecondEnd,
                    normalizedFirstDirection,
                    firstSegment.WidthDirection,
                    requiredBusbarWidth,
                    firstBusbarWidth,
                    secondBusbarWidth,
                    tolerance);
            }

            if (Math.Abs(normalizedFirstDirection.Dot(normalizedSecondDirection)) <= DirectionTolerance)
            {
                return EvaluatePerpendicular(
                    lapEvaluation,
                    firstSegment.Start,
                    firstSegment.End,
                    projectedSecondStart,
                    projectedSecondEnd,
                    firstThicknessDirection,
                    firstBusbarWidth,
                    secondBusbarWidth,
                    tolerance,
                    allowFirstSegmentOneSidedPerpendicularLap,
                    allowSecondSegmentOneSidedPerpendicularLap);
            }

            lapEvaluation.Message = "搭接线段既不平行也不垂直。";
            return lapEvaluation;
        }

        private static LinkageRouteLapEvaluation EvaluateParallel(
            LinkageRouteLapEvaluation lapEvaluation,
            Point3d firstStart,
            Point3d firstEnd,
            Point3d secondStart,
            Point3d secondEnd,
            Vector3d firstDirection,
            Vector3d firstWidthDirection,
            double requiredBusbarWidth,
            double firstBusbarWidth,
            double secondBusbarWidth,
            double tolerance)
        {
            var lateralDifference = new Vector3d(firstStart, secondStart);
            var lateralProjection = lateralDifference.Dot(firstDirection);
            var perpendicularDifference = new Vector3d(
                lateralDifference.X - firstDirection.X * lateralProjection,
                lateralDifference.Y - firstDirection.Y * lateralProjection,
                lateralDifference.Z - firstDirection.Z * lateralProjection);
            var normalizedFirstWidthDirection = firstWidthDirection.Normalized;
            if (perpendicularDifference.Norm > tolerance &&
                perpendicularDifference.Normalized
                    .Cross(normalizedFirstWidthDirection).Norm > DirectionTolerance)
            {
                lapEvaluation.Message = "平行搭接的横向偏移不沿排宽方向。";
                return lapEvaluation;
            }

            var signedWidthOffset = lateralDifference.Dot(normalizedFirstWidthDirection);
            var firstWidthMinimum = -firstBusbarWidth / 2d;
            var firstWidthMaximum = firstBusbarWidth / 2d;
            var secondWidthMinimum = signedWidthOffset - secondBusbarWidth / 2d;
            var secondWidthMaximum = signedWidthOffset + secondBusbarWidth / 2d;
            lapEvaluation.WidthOverlapLength = Math.Max(
                0d,
                Math.Min(firstWidthMaximum, secondWidthMaximum) -
                Math.Max(firstWidthMinimum, secondWidthMinimum));
            var requiredWidthOverlap = Math.Min(firstBusbarWidth, secondBusbarWidth);
            if (lapEvaluation.WidthOverlapLength + tolerance < requiredWidthOverlap)
            {
                lapEvaluation.Message = "平行搭接的较窄铜排未完整落入较宽铜排的排宽范围。";
                return lapEvaluation;
            }

            var firstLength = firstStart.DistanceTo(firstEnd);
            var secondStartCoordinate = new Vector3d(firstStart, secondStart).Dot(firstDirection);
            var secondEndCoordinate = new Vector3d(firstStart, secondEnd).Dot(firstDirection);
            var secondMinimum = Math.Min(secondStartCoordinate, secondEndCoordinate);
            var secondMaximum = Math.Max(secondStartCoordinate, secondEndCoordinate);
            lapEvaluation.OverlapLength = Math.Max(0d,
                Math.Min(firstLength, secondMaximum) - Math.Max(0d, secondMinimum));
            lapEvaluation.IsValid = lapEvaluation.OverlapLength + tolerance >= requiredBusbarWidth;
            lapEvaluation.Message = lapEvaluation.IsValid
                ? "平行满搭接通过。"
                : "平行搭接投影重合长度不足一个排宽。";
            return lapEvaluation;
        }

        private static LinkageRouteLapEvaluation EvaluatePerpendicular(
            LinkageRouteLapEvaluation lapEvaluation,
            Point3d firstStart,
            Point3d firstEnd,
            Point3d secondStart,
            Point3d secondEnd,
            Vector3d planeNormal,
            double firstBusbarWidth,
            double secondBusbarWidth,
            double tolerance,
            bool allowFirstSegmentOneSided,
            bool allowSecondSegmentOneSided)
        {
            lapEvaluation.IsPerpendicular = true;
            var firstVector = new Vector3d(firstStart, firstEnd);
            var secondVector = new Vector3d(secondStart, secondEnd);
            var startDifference = new Vector3d(firstStart, secondStart);
            var denominator = planeNormal.Dot(firstVector.Cross(secondVector));
            if (Math.Abs(denominator) <= DirectionTolerance)
            {
                lapEvaluation.Message = "垂直搭接投影没有实际交点。";
                return lapEvaluation;
            }

            var firstParameter = planeNormal.Dot(startDifference.Cross(secondVector)) / denominator;
            var secondParameter = planeNormal.Dot(startDifference.Cross(firstVector)) / denominator;
            // tolerance是毫米尺寸误差；参数是无量纲，不能直接共用同一数值。
            // 交点计算与方向精度不变，越界误差须换回各自实际线段长度。
            var firstParameterTolerance = tolerance / firstVector.Norm;
            var secondParameterTolerance = tolerance / secondVector.Norm;
            if (firstParameter < -firstParameterTolerance || firstParameter > 1d + firstParameterTolerance ||
                secondParameter < -secondParameterTolerance || secondParameter > 1d + secondParameterTolerance)
            {
                lapEvaluation.Message = "只有延长线相交，实际线段没有交点。";
                return lapEvaluation;
            }

            var intersectionPoint = LinkageRouteSolidGeometry.Translate(firstStart, firstVector, firstParameter);
            var firstNearestEndDistance = Math.Min(
                intersectionPoint.DistanceTo(firstStart), intersectionPoint.DistanceTo(firstEnd));
            var secondNearestEndDistance = Math.Min(
                intersectionPoint.DistanceTo(secondStart), intersectionPoint.DistanceTo(secondEnd));
            var firstRequiredEndDistance = secondBusbarWidth / 2d;
            var secondRequiredEndDistance = firstBusbarWidth / 2d;
            var requiredOneSidedOverlapLength = Math.Min(
                firstBusbarWidth,
                secondBusbarWidth);
            var firstActualOverlapLength = GetCenteredSpanOverlapLength(
                firstVector.Norm,
                intersectionPoint.DistanceTo(firstStart),
                secondBusbarWidth);
            var secondActualOverlapLength = GetCenteredSpanOverlapLength(
                secondVector.Norm,
                intersectionPoint.DistanceTo(secondStart),
                firstBusbarWidth);
            var firstSegmentCoverageIsValid = allowFirstSegmentOneSided
                ? firstActualOverlapLength + tolerance >=
                    requiredOneSidedOverlapLength
                : firstNearestEndDistance + tolerance >=
                    firstRequiredEndDistance;
            var secondSegmentCoverageIsValid = allowSecondSegmentOneSided
                ? secondActualOverlapLength + tolerance >=
                    requiredOneSidedOverlapLength
                : secondNearestEndDistance + tolerance >=
                    secondRequiredEndDistance;
            lapEvaluation.ProjectedIntersection = intersectionPoint;
            lapEvaluation.OverlapLength = Math.Min(
                firstActualOverlapLength,
                secondActualOverlapLength);
            lapEvaluation.FirstRequiredEndDistance = firstRequiredEndDistance;
            lapEvaluation.SecondRequiredEndDistance = secondRequiredEndDistance;
            lapEvaluation.IsValid = firstSegmentCoverageIsValid &&
                secondSegmentCoverageIsValid;
            lapEvaluation.Message = lapEvaluation.IsValid
                ? allowFirstSegmentOneSided || allowSecondSegmentOneSided
                    ? "垂直单侧排宽搭接通过。"
                    : "垂直满搭接通过。"
                : allowFirstSegmentOneSided || allowSecondSegmentOneSided
                    ? "垂直搭接未保留一个较窄排宽，或另一侧未完整跨过对方排宽。"
                    : "垂直搭接交点到某一侧最近端头不足对方铜排的半个排宽。";
            return lapEvaluation;
        }

        private static double GetCenteredSpanOverlapLength(
            double segmentLength,
            double intersectionDistanceFromStart,
            double opposingBusbarWidth)
        {
            var opposingHalfWidth = opposingBusbarWidth / 2d;
            var opposingMinimum = intersectionDistanceFromStart -
                opposingHalfWidth;
            var opposingMaximum = intersectionDistanceFromStart +
                opposingHalfWidth;
            return Math.Max(
                0d,
                Math.Min(segmentLength, opposingMaximum) -
                Math.Max(0d, opposingMinimum));
        }

        private static double ResolveBusbarWidth(
            LinkageRouteLapSegment lapSegment,
            double fallbackBusbarWidth)
        {
            return lapSegment?.BusbarWidth > 0d
                ? lapSegment.BusbarWidth
                : fallbackBusbarWidth;
        }

        private static Point3d ProjectToPlane(Point3d sourcePoint, Point3d planePoint, Vector3d planeNormal)
        {
            var planeOffset = new Vector3d(planePoint, sourcePoint).Dot(planeNormal);
            return LinkageRouteSolidGeometry.Translate(sourcePoint, planeNormal, -planeOffset);
        }
    }
}
