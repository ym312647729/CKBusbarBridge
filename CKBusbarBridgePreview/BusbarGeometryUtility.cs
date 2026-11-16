using GeometRi;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
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

        internal static Point3d GetComponentCenter(Component2 component)
        {
            if (component == null)
            {
                return null;
            }

            try
            {
                var box = component.GetBox(false, false) as double[];
                if (box != null && box.Length >= 6)
                {
                    return new Point3d(
                        (box[0] + box[3]) * 500d,
                        (box[1] + box[4]) * 500d,
                        (box[2] + box[5]) * 500d);
                }
            }
            catch
            {
            }

            try
            {
                var transformData = component.Transform2?.ArrayData as double[];
                if (transformData != null && transformData.Length >= 12)
                {
                    return new Point3d(
                        transformData[9] * 1000d,
                        transformData[10] * 1000d,
                        transformData[11] * 1000d);
                }
            }
            catch
            {
            }

            return null;
        }

        internal static bool TryGetComponentBoundingBox(
            Component2 component,
            out Point3d minimumPoint,
            out Point3d maximumPoint)
        {
            minimumPoint = null;
            maximumPoint = null;
            if (component == null)
            {
                return false;
            }

            try
            {
                var componentAssemblyBox = component.GetBox(false, false) as double[];
                if (componentAssemblyBox == null || componentAssemblyBox.Length < 6)
                {
                    return false;
                }

                minimumPoint = new Point3d(
                    componentAssemblyBox[0] * 1000d,
                    componentAssemblyBox[1] * 1000d,
                    componentAssemblyBox[2] * 1000d);
                maximumPoint = new Point3d(
                    componentAssemblyBox[3] * 1000d,
                    componentAssemblyBox[4] * 1000d,
                    componentAssemblyBox[5] * 1000d);
                return maximumPoint.X > minimumPoint.X &&
                       maximumPoint.Y > minimumPoint.Y &&
                       maximumPoint.Z > minimumPoint.Z;
            }
            catch
            {
                minimumPoint = null;
                maximumPoint = null;
                return false;
            }
        }

        internal static bool TryGetComponentProjectionRange(
            Component2 component,
            Vector3d projectionDirection,
            out double minimumProjectionCoordinate,
            out double maximumProjectionCoordinate)
        {
            minimumProjectionCoordinate = double.NaN;
            maximumProjectionCoordinate = double.NaN;
            if (component == null || projectionDirection == null ||
                projectionDirection.Norm < 1e-9)
            {
                return false;
            }

            try
            {
                var componentAssemblyBox = component.GetBox(false, false) as double[];
                if (componentAssemblyBox == null || componentAssemblyBox.Length < 6)
                {
                    return false;
                }

                var componentMinimumX = componentAssemblyBox[0] * 1000d;
                var componentMinimumY = componentAssemblyBox[1] * 1000d;
                var componentMinimumZ = componentAssemblyBox[2] * 1000d;
                var componentMaximumX = componentAssemblyBox[3] * 1000d;
                var componentMaximumY = componentAssemblyBox[4] * 1000d;
                var componentMaximumZ = componentAssemblyBox[5] * 1000d;
                var normalizedProjectionDirection = projectionDirection.Normalized;
                var componentBoundingCornerPoints = new[]
                {
                    new Point3d(componentMinimumX, componentMinimumY, componentMinimumZ),
                    new Point3d(componentMinimumX, componentMinimumY, componentMaximumZ),
                    new Point3d(componentMinimumX, componentMaximumY, componentMinimumZ),
                    new Point3d(componentMinimumX, componentMaximumY, componentMaximumZ),
                    new Point3d(componentMaximumX, componentMinimumY, componentMinimumZ),
                    new Point3d(componentMaximumX, componentMinimumY, componentMaximumZ),
                    new Point3d(componentMaximumX, componentMaximumY, componentMinimumZ),
                    new Point3d(componentMaximumX, componentMaximumY, componentMaximumZ)
                };
                var componentCornerProjectionCoordinates = componentBoundingCornerPoints
                    .Select(componentCornerPoint =>
                        Project(componentCornerPoint, normalizedProjectionDirection))
                    .ToList();
                minimumProjectionCoordinate = componentCornerProjectionCoordinates.Min();
                maximumProjectionCoordinate = componentCornerProjectionCoordinates.Max();
                return !double.IsNaN(minimumProjectionCoordinate) &&
                       !double.IsInfinity(minimumProjectionCoordinate) &&
                       !double.IsNaN(maximumProjectionCoordinate) &&
                       !double.IsInfinity(maximumProjectionCoordinate);
            }
            catch
            {
                minimumProjectionCoordinate = double.NaN;
                maximumProjectionCoordinate = double.NaN;
                return false;
            }
        }

        internal static double Distance(Point3d first, Point3d second)
        {
            return first == null || second == null ? double.MaxValue : first.DistanceTo(second);
        }

        internal static string SafeName(Component2 component)
        {
            try
            {
                return component?.Name2 ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
