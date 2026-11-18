using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Linq;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static class LinkageRouteDoubleStartLapGeometry
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.PointCoincidence;

        public static bool TryCreateCollectiveFixedBusbar(
            LinkageTerminalAssembly doubleStartTerminalAssembly,
            out PlanarBendBusbarSetting collectiveFixedBusbar)
        {
            return TryCreateWidthSeparatedCollectiveFixedBusbar(
                doubleStartTerminalAssembly, out collectiveFixedBusbar) ||
                TryCreateLengthSeparatedCollectiveFixedBusbar(
                    doubleStartTerminalAssembly, out collectiveFixedBusbar);
        }

        internal static bool TryCreateLengthSeparatedCollectiveFixedBusbar(
            LinkageTerminalAssembly terminalAssembly,
            out PlanarBendBusbarSetting collectiveFixedBusbar)
        {
            collectiveFixedBusbar = null;
            var fixedSettings = terminalAssembly?.Members?
                .Select(member => member?.FixedBusbar).ToList();
            if (fixedSettings == null || fixedSettings.Count < 2 ||
                fixedSettings.Any(setting => !IsUsableFixedBusbar(setting) ||
                    setting.PointList.Count != 2)) return false;
            var referenceSetting = fixedSettings[0];
            var referenceOrigin = referenceSetting.PointList[0];
            var sourceLengthDirection = new Vector3d(referenceOrigin,
                referenceSetting.PointList[1]).Normalized;
            var sourceWidthDirection = referenceSetting.WidthVector.Normalized;
            var sourceThicknessDirection = referenceSetting.StartBusbarVector.Normalized;
            var spanMinimum = double.PositiveInfinity;
            var spanMaximum = double.NegativeInfinity;
            foreach (var fixedSetting in fixedSettings)
            {
                var settingDirection = new Vector3d(fixedSetting.PointList[0], fixedSetting.PointList[1]);
                if (settingDirection.Norm <= GeometryTolerance ||
                    settingDirection.Normalized.Cross(sourceLengthDirection).Norm > GeometryTolerance ||
                    fixedSetting.WidthVector.Normalized.Cross(sourceWidthDirection).Norm > GeometryTolerance ||
                    fixedSetting.StartBusbarVector.Normalized.Cross(sourceThicknessDirection).Norm > GeometryTolerance ||
                    Math.Abs(fixedSetting.BusBarThickness - referenceSetting.BusBarThickness) > GeometryTolerance ||
                    Math.Abs(fixedSetting.BusBarWidth - referenceSetting.BusBarWidth) > GeometryTolerance)
                    return false;
                foreach (var fixedPoint in fixedSetting.PointList)
                {
                    var pointOffset = new Vector3d(referenceOrigin, fixedPoint);
                    if (Math.Abs(pointOffset.Dot(sourceWidthDirection)) > GeometryTolerance ||
                        Math.Abs(pointOffset.Dot(sourceThicknessDirection)) > GeometryTolerance)
                        return false;
                    var coordinate = pointOffset.Dot(sourceLengthDirection);
                    spanMinimum = Math.Min(spanMinimum, coordinate);
                    spanMaximum = Math.Max(spanMaximum, coordinate);
                }
            }
            var referenceLength = referenceSetting.PointList[0].DistanceTo(referenceSetting.PointList[1]);
            if (spanMaximum - spanMinimum <= referenceLength + GeometryTolerance) return false;
            var spanCenter = Translate(referenceOrigin, sourceLengthDirection,
                (spanMinimum + spanMaximum) / 2d);
            collectiveFixedBusbar = referenceSetting.Copy();
            collectiveFixedBusbar.PointList = new System.Collections.Generic.List<Point3d>
            {
                Translate(spanCenter, sourceWidthDirection, -referenceSetting.BusBarWidth / 2d),
                Translate(spanCenter, sourceWidthDirection, referenceSetting.BusBarWidth / 2d)
            };
            collectiveFixedBusbar.WidthVector = -sourceThicknessDirection.Cross(sourceWidthDirection);
            collectiveFixedBusbar.BusBarWidth = spanMaximum - spanMinimum;
            return true;
        }

        private static bool TryCreateWidthSeparatedCollectiveFixedBusbar(
            LinkageTerminalAssembly doubleStartTerminalAssembly,
            out PlanarBendBusbarSetting collectiveFixedBusbar)
        {
            collectiveFixedBusbar = null;
            if (doubleStartTerminalAssembly?.Members?.Count < 2)
            {
                return false;
            }

            var doubleStartFixedBusbars = doubleStartTerminalAssembly.Members
                .Select(doubleStartMember => doubleStartMember?.FixedBusbar)
                .ToList();
            if (doubleStartFixedBusbars.Any(doubleStartFixedBusbar =>
                    !IsUsableFixedBusbar(doubleStartFixedBusbar)))
            {
                return false;
            }

            var referenceFixedBusbar = doubleStartFixedBusbars[0];
            var referenceWidthDirection = referenceFixedBusbar.WidthVector.Normalized;
            var referenceThicknessDirection = referenceFixedBusbar
                .StartBusbarVector.Normalized;
            var collectiveWidthMinimum = double.PositiveInfinity;
            var collectiveWidthMaximum = double.NegativeInfinity;
            foreach (var doubleStartFixedBusbar in doubleStartFixedBusbars)
            {
                if (doubleStartFixedBusbar.PointList.Count !=
                        referenceFixedBusbar.PointList.Count ||
                    Math.Abs(doubleStartFixedBusbar.BusBarThickness -
                        referenceFixedBusbar.BusBarThickness) > GeometryTolerance ||
                    doubleStartFixedBusbar.WidthVector.Normalized
                        .Cross(referenceWidthDirection).Norm > GeometryTolerance ||
                    doubleStartFixedBusbar.StartBusbarVector.Normalized
                        .Cross(referenceThicknessDirection).Norm > GeometryTolerance)
                {
                    return false;
                }

                var currentCenterWidthCoordinate = new Vector3d(
                        referenceFixedBusbar.PointList[0],
                        doubleStartFixedBusbar.PointList[0])
                    .Dot(referenceWidthDirection);
                for (var fixedPointIndex = 0;
                    fixedPointIndex < referenceFixedBusbar.PointList.Count;
                    fixedPointIndex++)
                {
                    var correspondingPointDifference = new Vector3d(
                        referenceFixedBusbar.PointList[fixedPointIndex],
                        doubleStartFixedBusbar.PointList[fixedPointIndex]);
                    var nonWidthDifference = new Vector3d(
                        correspondingPointDifference.X -
                            referenceWidthDirection.X * currentCenterWidthCoordinate,
                        correspondingPointDifference.Y -
                            referenceWidthDirection.Y * currentCenterWidthCoordinate,
                        correspondingPointDifference.Z -
                            referenceWidthDirection.Z * currentCenterWidthCoordinate);
                    if (nonWidthDifference.Norm > GeometryTolerance)
                    {
                        return false;
                    }
                }

                var currentFixedHalfWidth = doubleStartFixedBusbar.BusBarWidth / 2d;
                collectiveWidthMinimum = Math.Min(
                    collectiveWidthMinimum,
                    currentCenterWidthCoordinate - currentFixedHalfWidth);
                collectiveWidthMaximum = Math.Max(
                    collectiveWidthMaximum,
                    currentCenterWidthCoordinate + currentFixedHalfWidth);
            }

            var collectiveBusbarWidth = collectiveWidthMaximum - collectiveWidthMinimum;
            if (collectiveBusbarWidth <= GeometryTolerance)
            {
                return false;
            }

            var collectiveCenterWidthCoordinate =
                (collectiveWidthMinimum + collectiveWidthMaximum) / 2d;
            collectiveFixedBusbar = referenceFixedBusbar.Copy();
            collectiveFixedBusbar.PointList = referenceFixedBusbar.PointList
                .Select(referenceFixedPoint => Translate(
                    referenceFixedPoint,
                    referenceWidthDirection,
                    collectiveCenterWidthCoordinate))
                .ToList();
            collectiveFixedBusbar.WidthVector = new Vector3d(
                referenceWidthDirection.X,
                referenceWidthDirection.Y,
                referenceWidthDirection.Z);
            collectiveFixedBusbar.BusBarWidth = collectiveBusbarWidth;
            return true;
        }

        private static bool IsUsableFixedBusbar(
            PlanarBendBusbarSetting fixedBusbar)
        {
            return fixedBusbar?.PointList != null &&
                fixedBusbar.PointList.Count >= 2 &&
                fixedBusbar.WidthVector != null &&
                fixedBusbar.WidthVector.Norm > GeometryTolerance &&
                fixedBusbar.StartBusbarVector != null &&
                fixedBusbar.StartBusbarVector.Norm > GeometryTolerance &&
                fixedBusbar.BusBarWidth > GeometryTolerance &&
                fixedBusbar.BusBarThickness > GeometryTolerance;
        }

        private static Point3d Translate(
            Point3d sourcePoint,
            Vector3d direction,
            double distance)
        {
            return new Point3d(
                sourcePoint.X + direction.X * distance,
                sourcePoint.Y + direction.Y * distance,
                sourcePoint.Z + direction.Z * distance);
        }
    }
}
