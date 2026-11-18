using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteLinkBar.Algorithm.Geometry
{
    internal sealed class LinkageRouteLapFootprint
    {
        public IReadOnlyList<Point3d> Corners { get; set; }
        public Vector3d LengthDirection { get; set; }
        public Vector3d WidthDirection { get; set; }
        public Vector3d PlaneNormal { get; set; }
    }

    public static class LinkageRouteTerminalLapRegionValidator
    {
        private const double DirectionTolerance =
            LinkageRouteGeometryTolerances.Direction;

        public static bool HasNoPositiveAreaOverlap(
            PlanarBendBusbarSetting busbarSetting,
            out string failureReason,
            double tolerance = 1e-6d)
        {
            failureReason = null;
            if (busbarSetting?.PointList == null || busbarSetting.PointList.Count < 2 ||
                busbarSetting.WidthVector == null || busbarSetting.WidthVector.Norm <= DirectionTolerance ||
                busbarSetting.BusBarWidth <= 0d)
            {
                failureReason = "铜排中心线、排宽方向或排宽无效，无法校验首末搭接区域。";
                return false;
            }

            if (busbarSetting.StartLapWidth <= tolerance ||
                busbarSetting.EndLapWidth <= tolerance)
            {
                return true;
            }

            var firstSegmentDirection = new Vector3d(
                busbarSetting.PointList[0],
                busbarSetting.PointList[1]);
            var lastSegmentDirection = new Vector3d(
                busbarSetting.PointList[busbarSetting.PointList.Count - 2],
                busbarSetting.PointList[busbarSetting.PointList.Count - 1]);
            if (firstSegmentDirection.Norm <= DirectionTolerance ||
                lastSegmentDirection.Norm <= DirectionTolerance)
            {
                failureReason = "铜排首段或末段长度为零，无法校验首末搭接区域。";
                return false;
            }

            firstSegmentDirection = firstSegmentDirection.Normalized;
            lastSegmentDirection = lastSegmentDirection.Normalized;
            var normalizedWidthDirection = busbarSetting.WidthVector.Normalized;
            var firstLapPlaneNormalVector = firstSegmentDirection
                .Cross(normalizedWidthDirection);
            var lastLapPlaneNormalVector = lastSegmentDirection
                .Cross(normalizedWidthDirection);
            if (firstLapPlaneNormalVector.Norm <= DirectionTolerance ||
                lastLapPlaneNormalVector.Norm <= DirectionTolerance)
            {
                failureReason = "铜排首段或末段排向与排宽方向平行，无法建立搭接区域。";
                return false;
            }

            var firstLapPlaneNormal = firstLapPlaneNormalVector.Normalized;
            var lastLapPlaneNormal = lastLapPlaneNormalVector.Normalized;
            if (firstLapPlaneNormal.Cross(lastLapPlaneNormal).Norm > DirectionTolerance)
            {
                return true;
            }

            var firstLapStart = busbarSetting.PointList[0];
            var firstLapEnd = Translate(
                firstLapStart,
                firstSegmentDirection,
                busbarSetting.StartLapWidth);
            var lastLapEnd = busbarSetting.PointList[busbarSetting.PointList.Count - 1];
            var lastLapStart = Translate(
                lastLapEnd,
                lastSegmentDirection,
                -busbarSetting.EndLapWidth);
            var lapPlaneDistance = Math.Abs(new Vector3d(
                firstLapStart, lastLapStart).Dot(firstLapPlaneNormal));
            if (lapPlaneDistance > tolerance)
            {
                return true;
            }

            var firstLapCorners = BuildRectangleCorners(
                firstLapStart,
                firstLapEnd,
                normalizedWidthDirection,
                busbarSetting.BusBarWidth);
            var lastLapCorners = BuildRectangleCorners(
                lastLapStart,
                lastLapEnd,
                normalizedWidthDirection,
                busbarSetting.BusBarWidth);
            var lengthAxisOverlap = GetProjectionOverlap(
                firstLapCorners,
                lastLapCorners,
                firstLapStart,
                firstSegmentDirection);
            var widthAxisOverlap = GetProjectionOverlap(
                firstLapCorners,
                lastLapCorners,
                firstLapStart,
                normalizedWidthDirection);
            var lastLengthAxisOverlap = GetProjectionOverlap(
                firstLapCorners,
                lastLapCorners,
                firstLapStart,
                lastSegmentDirection);
            var lastWidthAxisOverlap = GetProjectionOverlap(
                firstLapCorners,
                lastLapCorners,
                firstLapStart,
                normalizedWidthDirection);
            if (lengthAxisOverlap <= tolerance ||
                widthAxisOverlap <= tolerance ||
                lastLengthAxisOverlap <= tolerance ||
                lastWidthAxisOverlap <= tolerance)
            {
                return true;
            }

            failureReason = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "同一铜排的始端与末端搭接区域存在正面积重叠：排长方向重叠 {0:F3} mm，排宽方向重叠 {1:F3} mm。",
                lengthAxisOverlap,
                widthAxisOverlap);
            return false;
        }

        internal static bool TryCreateActualLapFootprint(
            PlanarBendBusbarSetting targetSetting,
            bool targetUseStart,
            PlanarBendBusbarSetting neighborSetting,
            bool neighborUseStart,
            double requiredBusbarWidth,
            out LinkageRouteLapFootprint lapFootprint,
            bool allowTargetOneSidedPerpendicularLap = false,
            bool allowNeighborOneSidedPerpendicularLap = false,
            double tolerance = 1e-6d)
        {
            return TryCreateLapFootprintCore(
                targetSetting,
                targetUseStart,
                neighborSetting,
                neighborUseStart,
                requiredBusbarWidth,
                true,
                out lapFootprint,
                allowTargetOneSidedPerpendicularLap,
                allowNeighborOneSidedPerpendicularLap,
                tolerance);
        }

        /// <summary>
        /// 获取两根排在搭接平面上的真实投影交集，不要求两张厚面当前已经
        /// 接触。用于根据既有搭接区域生成填满厚向净空的垫块。
        /// </summary>
        internal static bool TryCreateProjectedLapFootprint(
            PlanarBendBusbarSetting targetSetting,
            bool targetUseStart,
            PlanarBendBusbarSetting neighborSetting,
            bool neighborUseStart,
            double requiredBusbarWidth,
            out LinkageRouteLapFootprint lapFootprint,
            double tolerance = 1e-6d)
        {
            return TryCreateLapFootprintCore(
                targetSetting,
                targetUseStart,
                neighborSetting,
                neighborUseStart,
                requiredBusbarWidth,
                false,
                out lapFootprint,
                false,
                false,
                tolerance);
        }

        private static bool TryCreateLapFootprintCore(
            PlanarBendBusbarSetting targetSetting,
            bool targetUseStart,
            PlanarBendBusbarSetting neighborSetting,
            bool neighborUseStart,
            double requiredBusbarWidth,
            bool requireThicknessContact,
            out LinkageRouteLapFootprint lapFootprint,
            bool allowTargetOneSidedPerpendicularLap,
            bool allowNeighborOneSidedPerpendicularLap,
            double tolerance)
        {
            lapFootprint = null;
            var targetLapSegment = CreateBoundarySegment(targetSetting, targetUseStart);
            var neighborLapSegment = CreateBoundarySegment(neighborSetting, neighborUseStart);
            if (targetLapSegment == null || neighborLapSegment == null)
            {
                return false;
            }

            if (requireThicknessContact)
            {
                var lapEvaluation =
                    allowTargetOneSidedPerpendicularLap ||
                    allowNeighborOneSidedPerpendicularLap
                        ? LinkageRouteLapValidator
                            .EvaluateAllowingOneSidedPerpendicularLap(
                                targetLapSegment,
                                neighborLapSegment,
                                requiredBusbarWidth,
                                (targetSetting.BusBarThickness + neighborSetting.BusBarThickness) / 2d,
                                allowTargetOneSidedPerpendicularLap,
                                allowNeighborOneSidedPerpendicularLap,
                                Math.Max(1e-4d, tolerance))
                        : LinkageRouteLapValidator.Evaluate(
                            targetLapSegment,
                            neighborLapSegment,
                            requiredBusbarWidth,
                            (targetSetting.BusBarThickness + neighborSetting.BusBarThickness) / 2d,
                            Math.Max(1e-4d, tolerance));
                if (!lapEvaluation.IsValid)
                {
                    return false;
                }
            }
            else
            {
                var targetSegmentDirection = new Vector3d(
                    targetLapSegment.Start,
                    targetLapSegment.End).Normalized;
                var neighborSegmentDirection = new Vector3d(
                    neighborLapSegment.Start,
                    neighborLapSegment.End).Normalized;
                var directionDot = Math.Abs(
                    targetSegmentDirection.Dot(neighborSegmentDirection));
                if (directionDot > DirectionTolerance &&
                    directionDot < 1d - DirectionTolerance)
                {
                    return false;
                }
            }

            var targetLengthDirection = new Vector3d(
                targetLapSegment.Start,
                targetLapSegment.End).Normalized;
            var targetWidthDirection = targetSetting.WidthVector.Normalized;
            var targetPlaneNormalVector = targetLengthDirection.Cross(targetWidthDirection);
            if (targetPlaneNormalVector.Norm <= DirectionTolerance)
            {
                return false;
            }

            var targetPlaneNormal = targetPlaneNormalVector.Normalized;
            var neighborLengthDirection = new Vector3d(
                neighborLapSegment.Start,
                neighborLapSegment.End).Normalized;
            var neighborPlaneNormalVector = neighborLengthDirection.Cross(
                neighborSetting.WidthVector.Normalized);
            if (neighborPlaneNormalVector.Norm <= DirectionTolerance ||
                targetPlaneNormal.Cross(
                    neighborPlaneNormalVector.Normalized).Norm >
                    DirectionTolerance)
            {
                return false;
            }
            var targetRectangleCorners = BuildRectangleCorners(
                targetLapSegment.Start,
                targetLapSegment.End,
                targetWidthDirection,
                targetSetting.BusBarWidth);
            var neighborRectangleCorners = BuildRectangleCorners(
                    neighborLapSegment.Start,
                    neighborLapSegment.End,
                    neighborSetting.WidthVector.Normalized,
                    neighborSetting.BusBarWidth)
                .Select(neighborCorner => ProjectToPlane(
                    neighborCorner,
                    targetLapSegment.Start,
                    targetPlaneNormal))
                .ToList();
            var targetLengthCoordinates = GetProjectionCoordinates(
                targetRectangleCorners,
                targetLapSegment.Start,
                targetLengthDirection);
            var neighborLengthCoordinates = GetProjectionCoordinates(
                neighborRectangleCorners,
                targetLapSegment.Start,
                targetLengthDirection);
            var targetWidthCoordinates = GetProjectionCoordinates(
                targetRectangleCorners,
                targetLapSegment.Start,
                targetWidthDirection);
            var neighborWidthCoordinates = GetProjectionCoordinates(
                neighborRectangleCorners,
                targetLapSegment.Start,
                targetWidthDirection);
            var overlapLengthMinimum = Math.Max(
                targetLengthCoordinates.Min(), neighborLengthCoordinates.Min());
            var overlapLengthMaximum = Math.Min(
                targetLengthCoordinates.Max(), neighborLengthCoordinates.Max());
            var overlapWidthMinimum = Math.Max(
                targetWidthCoordinates.Min(), neighborWidthCoordinates.Min());
            var overlapWidthMaximum = Math.Min(
                targetWidthCoordinates.Max(), neighborWidthCoordinates.Max());
            // 存在性仍按计算精度判断；验收容差只用于允许尺寸缺口，不能吞掉真实小面。
            if (overlapLengthMaximum - overlapLengthMinimum <= LinkageRouteGeometryTolerances.PointCoincidence ||
                overlapWidthMaximum - overlapWidthMinimum <= LinkageRouteGeometryTolerances.PointCoincidence)
            {
                return false;
            }

            lapFootprint = new LinkageRouteLapFootprint
            {
                Corners = BuildProjectedRectangleCorners(
                    targetLapSegment.Start,
                    targetLengthDirection,
                    targetWidthDirection,
                    overlapLengthMinimum,
                    overlapLengthMaximum,
                    overlapWidthMinimum,
                    overlapWidthMaximum),
                LengthDirection = targetLengthDirection,
                WidthDirection = targetWidthDirection,
                PlaneNormal = targetPlaneNormal
            };
            return true;
        }

        internal static bool HaveNoPositiveAreaOverlap(
            IEnumerable<LinkageRouteLapFootprint> startLapFootprints,
            IEnumerable<LinkageRouteLapFootprint> endLapFootprints,
            out string failureReason,
            double tolerance = 1e-6d)
        {
            failureReason = null;
            var startFootprintList = (startLapFootprints ??
                Enumerable.Empty<LinkageRouteLapFootprint>()).ToList();
            var endFootprintList = (endLapFootprints ??
                Enumerable.Empty<LinkageRouteLapFootprint>()).ToList();
            if (startFootprintList.Count == 0 || endFootprintList.Count == 0)
            {
                failureReason = "未能建立同一铜排两端的实际搭接矩形。";
                return false;
            }

            foreach (var startLapFootprint in startFootprintList)
            {
                foreach (var endLapFootprint in endFootprintList)
                {
                    if (startLapFootprint.PlaneNormal
                            .Cross(endLapFootprint.PlaneNormal).Norm > DirectionTolerance)
                    {
                        continue;
                    }

                    var lapPlaneDistance = Math.Abs(new Vector3d(
                        startLapFootprint.Corners[0],
                        endLapFootprint.Corners[0]).Dot(startLapFootprint.PlaneNormal));
                    if (lapPlaneDistance > tolerance)
                    {
                        continue;
                    }

                    var lengthAxisOverlap = GetProjectionOverlap(
                        startLapFootprint.Corners,
                        endLapFootprint.Corners,
                        startLapFootprint.Corners[0],
                        startLapFootprint.LengthDirection);
                    var widthAxisOverlap = GetProjectionOverlap(
                        startLapFootprint.Corners,
                        endLapFootprint.Corners,
                        startLapFootprint.Corners[0],
                        startLapFootprint.WidthDirection);
                    var endLengthAxisOverlap = GetProjectionOverlap(
                        startLapFootprint.Corners,
                        endLapFootprint.Corners,
                        startLapFootprint.Corners[0],
                        endLapFootprint.LengthDirection);
                    var endWidthAxisOverlap = GetProjectionOverlap(
                        startLapFootprint.Corners,
                        endLapFootprint.Corners,
                        startLapFootprint.Corners[0],
                        endLapFootprint.WidthDirection);
                    if (lengthAxisOverlap <= tolerance ||
                        widthAxisOverlap <= tolerance ||
                        endLengthAxisOverlap <= tolerance ||
                        endWidthAxisOverlap <= tolerance)
                    {
                        continue;
                    }

                    failureReason = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "同一铜排两端的实际搭接矩形存在正面积重叠：排长方向重叠 {0:F3} mm，排宽方向重叠 {1:F3} mm。",
                        lengthAxisOverlap,
                        widthAxisOverlap);
                    return false;
                }
            }

            return true;
        }

        private static LinkageRouteLapSegment CreateBoundarySegment(
            PlanarBendBusbarSetting busbarSetting,
            bool useStart)
        {
            if (busbarSetting?.PointList == null || busbarSetting.PointList.Count < 2 ||
                busbarSetting.WidthVector == null || busbarSetting.WidthVector.Norm <= DirectionTolerance ||
                busbarSetting.BusBarWidth <= 0d || busbarSetting.BusBarThickness <= 0d)
            {
                return null;
            }

            var segmentIndex = useStart ? 0 : busbarSetting.PointList.Count - 2;
            var segmentDirection = new Vector3d(
                busbarSetting.PointList[segmentIndex],
                busbarSetting.PointList[segmentIndex + 1]);
            if (segmentDirection.Norm <= DirectionTolerance)
            {
                return null;
            }

            var localThicknessDirectionVector = segmentDirection.Normalized
                .Cross(busbarSetting.WidthVector.Normalized);
            if (localThicknessDirectionVector.Norm <= DirectionTolerance)
            {
                return null;
            }

            return new LinkageRouteLapSegment(
                busbarSetting.PointList[segmentIndex],
                busbarSetting.PointList[segmentIndex + 1],
                busbarSetting.WidthVector,
                localThicknessDirectionVector.Normalized,
                busbarSetting.BusBarThickness,
                busbarSetting.BusBarWidth);
        }

        private static IReadOnlyList<Point3d> BuildRectangleCorners(
            Point3d lapStart,
            Point3d lapEnd,
            Vector3d widthDirection,
            double busbarWidth)
        {
            var halfBusbarWidth = busbarWidth / 2d;
            return new[]
            {
                Translate(lapStart, widthDirection, -halfBusbarWidth),
                Translate(lapStart, widthDirection, halfBusbarWidth),
                Translate(lapEnd, widthDirection, -halfBusbarWidth),
                Translate(lapEnd, widthDirection, halfBusbarWidth)
            };
        }

        private static double GetProjectionOverlap(
            IEnumerable<Point3d> firstCorners,
            IEnumerable<Point3d> secondCorners,
            Point3d projectionOrigin,
            Vector3d projectionAxis)
        {
            var normalizedProjectionAxis = projectionAxis.Normalized;
            var firstCoordinates = firstCorners
                .Select(corner => new Vector3d(projectionOrigin, corner)
                    .Dot(normalizedProjectionAxis))
                .ToList();
            var secondCoordinates = secondCorners
                .Select(corner => new Vector3d(projectionOrigin, corner)
                    .Dot(normalizedProjectionAxis))
                .ToList();
            return Math.Min(firstCoordinates.Max(), secondCoordinates.Max()) -
                Math.Max(firstCoordinates.Min(), secondCoordinates.Min());
        }

        private static IReadOnlyList<double> GetProjectionCoordinates(
            IEnumerable<Point3d> corners,
            Point3d projectionOrigin,
            Vector3d projectionAxis)
        {
            var normalizedProjectionAxis = projectionAxis.Normalized;
            return corners.Select(corner => new Vector3d(projectionOrigin, corner)
                    .Dot(normalizedProjectionAxis))
                .ToList();
        }

        private static IReadOnlyList<Point3d> BuildProjectedRectangleCorners(
            Point3d projectionOrigin,
            Vector3d lengthDirection,
            Vector3d widthDirection,
            double lengthMinimum,
            double lengthMaximum,
            double widthMinimum,
            double widthMaximum)
        {
            return new[]
            {
                Translate(Translate(projectionOrigin, lengthDirection, lengthMinimum), widthDirection, widthMinimum),
                Translate(Translate(projectionOrigin, lengthDirection, lengthMinimum), widthDirection, widthMaximum),
                Translate(Translate(projectionOrigin, lengthDirection, lengthMaximum), widthDirection, widthMinimum),
                Translate(Translate(projectionOrigin, lengthDirection, lengthMaximum), widthDirection, widthMaximum)
            };
        }

        private static Point3d ProjectToPlane(
            Point3d sourcePoint,
            Point3d planePoint,
            Vector3d planeNormal)
        {
            var planeOffset = new Vector3d(planePoint, sourcePoint).Dot(planeNormal.Normalized);
            return Translate(sourcePoint, planeNormal, -planeOffset);
        }

        private static Point3d Translate(
            Point3d sourcePoint,
            Vector3d direction,
            double distance)
        {
            var normalizedDirection = direction.Normalized;
            return new Point3d(
                sourcePoint.X + normalizedDirection.X * distance,
                sourcePoint.Y + normalizedDirection.Y * distance,
                sourcePoint.Z + normalizedDirection.Z * distance);
        }
    }
}
