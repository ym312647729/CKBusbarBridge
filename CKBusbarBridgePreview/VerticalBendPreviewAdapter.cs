using System;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 将中心线路径转换为铜排大师立弯数据类。预览与正式生成共用
    /// 此入口，避免两套立弯方向和规格定义分叉。
    /// </summary>
    public static class VerticalBendPreviewAdapter
    {
        public static VerticalBendSetting Create(
            PlanarBendBusbarSetting centerlineSetting)
        {
            if (centerlineSetting == null)
            {
                throw new ArgumentNullException(nameof(centerlineSetting));
            }
            if (!centerlineSetting.IsVerticalBend)
            {
                throw new InvalidOperationException(
                    "当前中心线设置没有标记为立弯铜排。");
            }
            if (centerlineSetting.PointList == null ||
                centerlineSetting.PointList.Count < 2)
            {
                throw new InvalidOperationException(
                    "立弯铜排中心线路径至少需要两个点。");
            }
            if (centerlineSetting.StartBusbarVector == null ||
                centerlineSetting.StartBusbarVector.Norm <= 0.000001d)
            {
                throw new InvalidOperationException(
                    "立弯铜排缺少有效的初始排厚方向。");
            }
            if (centerlineSetting.BusBarThickness <= 0d ||
                double.IsNaN(centerlineSetting.BusBarThickness) ||
                double.IsInfinity(centerlineSetting.BusBarThickness))
            {
                throw new InvalidOperationException(
                    "立弯铜排的排厚必须是有限正数。");
            }

            var legacyPreviewSetting = PlanarBendPreviewAdapter.Create(
                centerlineSetting);
            var reverseThicknessDirection = centerlineSetting
                .StartBusbarVector.Normalized.Negate();
            var thicknessSideOffset =
                centerlineSetting.BusBarThickness / 2d;
            var thicknessSidePointList = centerlineSetting.PointList
                .ConvertAll(centerlinePoint => centerlinePoint.Move(
                    reverseThicknessDirection,
                    thicknessSideOffset));
            FaceToWallPreviewTrace.Info(
                "立弯设置适配-排厚侧面偏移",
                "组=" + (centerlineSetting.GroupKey ?? "未命名") +
                "，方向=" + reverseThicknessDirection +
                "，偏移=" + thicknessSideOffset.ToString("0.###") +
                " mm，点数=" + thicknessSidePointList.Count);
            return new VerticalBendSetting
            {
                GroupKey = centerlineSetting.GroupKey,
                Phase = legacyPreviewSetting.Phase,
                StartBusbarVector =
                    centerlineSetting.StartBusbarVector?.Copy(),
                WidthVector = centerlineSetting.WidthVector?.Copy(),
                PointList = thicknessSidePointList,
                BarBendingRadius = centerlineSetting.BarBendingRadius,
                BusBarVerticalRadius =
                    centerlineSetting.VerticalBendingRadius > 0d
                        ? centerlineSetting.VerticalBendingRadius
                        : centerlineSetting.BarBendingRadius,
                BusBarWidth = centerlineSetting.BusBarWidth,
                BusBarThickness = centerlineSetting.BusBarThickness,
                StartLapWidth = centerlineSetting.StartLapWidth,
                EndLapWidth = centerlineSetting.EndLapWidth
            };
        }
    }
}
