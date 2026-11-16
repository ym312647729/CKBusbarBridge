using GeometRi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>把中心线铜排转换为现有 SolidWorks 铜排实体预览契约。</summary>
    public static class PlanarBendPreviewAdapter
    {
        public static BendPreviewSetting Create(PlanarBendBusbarSetting centerlineSetting)
        {
            if (centerlineSetting?.PointList == null || centerlineSetting.PointList.Count < 2 ||
                centerlineSetting.StartBusbarVector == null ||
                centerlineSetting.StartBusbarVector.Norm < 0.001d ||
                centerlineSetting.WidthVector == null ||
                centerlineSetting.WidthVector.Norm < 0.001d)
            {
                return null;
            }

            var hasExplicitPreviewBendPointKinds =
                centerlineSetting.PreviewBendPointKinds != null &&
                centerlineSetting.PreviewBendPointKinds.Count > 0;
            if (hasExplicitPreviewBendPointKinds &&
                centerlineSetting.PreviewBendPointKinds.Count !=
                centerlineSetting.PointList.Count)
            {
                throw new InvalidOperationException(
                    "铜排预览折弯类型数量必须与路径点数量一致。");
            }

            var previewPoints = centerlineSetting.IsCenter && centerlineSetting.BusBarThickness > 0d
                ? GetOffsetPointsPreservingSourceCount(
                    centerlineSetting,
                    -centerlineSetting.BusBarThickness / 2d)
                : PlanarBendBusbarSetting.CopyPoints(centerlineSetting.PointList);
            if (centerlineSetting.IsCenter && centerlineSetting.BusBarThickness > 0d &&
                HasSamePoints(previewPoints, centerlineSetting.PointList))
            {
                // RouteLinkBar 的逐段偏移在路径段与初始宽度向量平行时会保持原点集。
                // 面对墙 G 排是同一平面内的平弯，此时排厚方向恒定，按初始厚度方向平移即可。
                previewPoints = centerlineSetting.PointList
                    .Select(point => PlanarBendBusbarSetting.Translate(
                        point,
                        centerlineSetting.StartBusbarVector.Normalized,
                        -centerlineSetting.BusBarThickness / 2d))
                    .ToList();
            }

            var previewSetting = new BendPreviewSetting
            {
                IntStyle = 1,
                Style = centerlineSetting.IsMilledBar
                    ? "铣排"
                    : centerlineSetting.PointList.Count <= 3 ? "L" : "G",
                StartPoint = previewPoints[0].Copy(),
                StartBusbarVector = centerlineSetting.StartBusbarVector.Normalized.Copy(),
                WidthVector = centerlineSetting.WidthVector.Normalized.Copy(),
                PointGather = previewPoints.Select(point => point.Copy()).ToList(),
                BusBarWidth = centerlineSetting.BusBarWidth,
                BusBarThickness = centerlineSetting.BusBarThickness,
                BarBendingRadius = centerlineSetting.BarBendingRadius,
                BusBarVerticalRadius =
                    centerlineSetting.VerticalBendingRadius > 0d
                        ? centerlineSetting.VerticalBendingRadius
                        : centerlineSetting.BarBendingRadius,
                StartLapWidth = centerlineSetting.StartLapWidth,
                EndLapWidth = centerlineSetting.EndLapWidth,
                FixedForm = 1,
                PartTypeE = PartType_e.Busbar,
                IsMilledBar = centerlineSetting.IsMilledBar,
                MilledBarOriginalWidth =
                    centerlineSetting.MilledBarOriginalWidth,
                MilledBarAdditionalWidth =
                    centerlineSetting.MilledBarAdditionalWidth,
                MilledBarStartLength =
                    centerlineSetting.MilledBarStartLength,
                MilledBarEndLength =
                    centerlineSetting.MilledBarEndLength,
                MilledBarFeatureSide =
                    centerlineSetting.MilledBarFeatureSide,
                Phase = ResolvePhase(centerlineSetting.GroupKey)
            };
            if (hasExplicitPreviewBendPointKinds)
            {
                previewSetting.ListPoint = previewPoints
                    .Select((previewPoint, previewPointIndex) =>
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0:R},{1:R},{2:R},{3}",
                            previewPoint.X,
                            previewPoint.Y,
                            previewPoint.Z,
                            centerlineSetting.PreviewBendPointKinds[
                                previewPointIndex] ==
                            BusbarPreviewBendPointKind_e.Vertical
                                ? "立"
                                : "平"))
                    .ToList();
                var firstVerticalPointIndex = centerlineSetting
                    .PreviewBendPointKinds.FindIndex(
                        previewBendPointKind =>
                            previewBendPointKind ==
                            BusbarPreviewBendPointKind_e.Vertical);
                previewSetting.IntStyle = firstVerticalPointIndex < 0
                    ? 1
                    : centerlineSetting.PreviewBendPointKinds
                        .Skip(firstVerticalPointIndex + 1)
                        .Any(previewBendPointKind =>
                            previewBendPointKind ==
                            BusbarPreviewBendPointKind_e.Planar)
                        ? 3
                        : 2;
            }
            else
            {
                previewSetting.SetPointGather(record: false);
            }
            previewSetting.Style = previewSetting.PointGather.Count <= 3 ? "L" : "G";
            return previewSetting.PointGather.Count >= 2 ? previewSetting : null;
        }

        private static List<Point3d> GetOffsetPointsPreservingSourceCount(
            PlanarBendBusbarSetting centerlineSetting,
            double offsetDistance)
        {
            var compactPoints = new List<Point3d>();
            var sourceToCompactIndexes = new List<int>();
            foreach (var sourcePoint in centerlineSetting.PointList)
            {
                if (compactPoints.Count == 0 ||
                    compactPoints[compactPoints.Count - 1].DistanceTo(sourcePoint) > 0.001d)
                {
                    compactPoints.Add(sourcePoint.Copy());
                }

                sourceToCompactIndexes.Add(compactPoints.Count - 1);
            }

            var compactSetting = centerlineSetting.Copy();
            compactSetting.PointList = compactPoints;
            var offsetCompactPoints = compactSetting.GetOffsetCenterlinePointList(offsetDistance);
            if (offsetCompactPoints.Count != compactPoints.Count)
            {
                return PlanarBendBusbarSetting.CopyPoints(centerlineSetting.PointList);
            }

            return sourceToCompactIndexes
                .Select(compactPointIndex => offsetCompactPoints[compactPointIndex].Copy())
                .ToList();
        }

        private static bool HasSamePoints(
            IReadOnlyList<Point3d> firstPoints,
            IReadOnlyList<Point3d> secondPoints)
        {
            if (firstPoints == null || secondPoints == null || firstPoints.Count != secondPoints.Count)
            {
                return false;
            }

            for (var pointIndex = 0; pointIndex < firstPoints.Count; pointIndex++)
            {
                if (firstPoints[pointIndex] == null || secondPoints[pointIndex] == null ||
                    firstPoints[pointIndex].DistanceTo(secondPoints[pointIndex]) > 0.001d)
                {
                    return false;
                }
            }

            return true;
        }

        private static BusbarPhase_e ResolvePhase(string groupKey)
        {
            var normalizedGroupKey = (groupKey ?? string.Empty).Trim();
            var groupKeyTokens = normalizedGroupKey.Split(
                new[] { '-', '_', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            if (groupKeyTokens.Any(groupKeyToken => string.Equals(
                    groupKeyToken,
                    "A",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return BusbarPhase_e.A;
            }

            if (groupKeyTokens.Any(groupKeyToken => string.Equals(
                    groupKeyToken,
                    "B",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return BusbarPhase_e.B;
            }

            if (groupKeyTokens.Any(groupKeyToken => string.Equals(
                    groupKeyToken,
                    "C",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return BusbarPhase_e.C;
            }

            return BusbarPhase_e.Unknow;
        }
    }
}
