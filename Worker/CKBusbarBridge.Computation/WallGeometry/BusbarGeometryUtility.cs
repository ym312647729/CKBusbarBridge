using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Computation.WallGeometry
{
    internal static class BusbarGeometryUtility
    {
        internal const double PointTolerance = 0.2d;
        internal const double DirectionTolerance = 0.999390827d; // cos(2°)

        internal static double Project(Point3d point, Vector3d direction)
        {
            return point.X * direction.X + point.Y * direction.Y + point.Z * direction.Z;
        }

        internal static Point3d WithProjection(Point3d sourcePoint, Vector3d direction, double projection)
        {
            return PlanarBendBusbarSetting.Translate(
                sourcePoint,
                direction,
                projection - Project(sourcePoint, direction));
        }

        internal static Point3d Average(IEnumerable<Point3d> points)
        {
            var actualPoints = points?.Where(point => point != null).ToList() ?? new List<Point3d>();
            if (actualPoints.Count == 0)
            {
                return null;
            }

            return new Point3d(
                actualPoints.Average(point => point.X),
                actualPoints.Average(point => point.Y),
                actualPoints.Average(point => point.Z));
        }

        internal static Vector3d Align(Vector3d source, Vector3d reference)
        {
            if (source == null || source.Norm < 1e-9)
            {
                return null;
            }

            var normalized = source.Normalized;
            if (reference != null && reference.Norm > 1e-9 && normalized.Dot(reference) < 0d)
            {
                return normalized.Negate();
            }

            return normalized;
        }

        internal static bool AreParallel(Vector3d first, Vector3d second)
        {
            return first != null && second != null && first.Norm > 1e-9 && second.Norm > 1e-9 &&
                   Math.Abs(first.Normalized.Dot(second.Normalized)) >= DirectionTolerance;
        }

        internal static Vector3d Canonicalize(Vector3d direction)
        {
            if (direction == null || direction.Norm < 1e-9)
            {
                return null;
            }

            var normalized = direction.Normalized;
            if (Math.Abs(normalized.X) > 1e-8)
            {
                return normalized.X < 0d ? normalized.Negate() : normalized;
            }

            if (Math.Abs(normalized.Y) > 1e-8)
            {
                return normalized.Y < 0d ? normalized.Negate() : normalized;
            }

            return normalized.Z < 0d ? normalized.Negate() : normalized;
        }

        internal static bool SamePoint(Point3d first, Point3d second, double tolerance = PointTolerance)
        {
            return first != null && second != null && first.DistanceTo(second) <= tolerance;
        }

        internal static List<Segment3d> DistinctSegments(IEnumerable<Segment3d> sourceSegments)
        {
            var result = new List<Segment3d>();
            foreach (var sourceSegment in sourceSegments ?? Enumerable.Empty<Segment3d>())
            {
                if (sourceSegment == null || sourceSegment.Length <= PointTolerance)
                {
                    continue;
                }

                if (result.Any(existingSegment =>
                        SamePoint(existingSegment.P1, sourceSegment.P1) &&
                        SamePoint(existingSegment.P2, sourceSegment.P2) ||
                        SamePoint(existingSegment.P1, sourceSegment.P2) &&
                        SamePoint(existingSegment.P2, sourceSegment.P1)))
                {
                    continue;
                }

                result.Add(sourceSegment);
            }

            return result;
        }

        internal static List<Point3d> DistinctPoints(IEnumerable<Point3d> sourcePoints)
        {
            var result = new List<Point3d>();
            foreach (var sourcePoint in sourcePoints ?? Enumerable.Empty<Point3d>())
            {
                if (sourcePoint != null && !result.Any(existingPoint => SamePoint(existingPoint, sourcePoint)))
                {
                    result.Add(sourcePoint);
                }
            }

            return result;
        }

    }
}
