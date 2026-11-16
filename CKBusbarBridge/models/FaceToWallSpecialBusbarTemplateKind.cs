using System;

namespace CKBusbarBridge.models
{
    /// <summary>
    /// 旧 ABC 整体特殊铜排模板类型，仅用于现有三维预览类型映射。
    /// 新面对墙末段生成使用 <see cref="FaceToWallPhaseSpecialTemplateKind"/>。
    /// </summary>
    public enum FaceToWallSpecialBusbarTemplateKind
    {
        None = 0,

        /// <summary>立弯-左模板（墙柜偏心值为负）。</summary>
        VerticalBendLeft,

        /// <summary>立弯-右模板（墙柜偏心值为正）。</summary>
        VerticalBendRight,

        /// <summary>偏移模板（|墙柜偏心值| &gt; 排宽）。</summary>
        Offset,

        /// <summary>铣排模板（|墙柜偏心值| ≤ 排宽且排宽 &gt; 80）。</summary>
        MilledBar
    }

    /// <summary>
    /// 旧 ABC 整体特殊铜排的预览类型解析器，不参与新单相模板目录选择。
    /// </summary>
    public static class FaceToWallSpecialBusbarTemplateHelper
    {
        private const double MilledBarWidthThresholdMm = 80;

        /// <summary>
        /// 根据墙柜偏心值与排宽解析最后一段应使用的特殊铜排模板。
        /// </summary>
        /// <param name="wallCabinetEccentricity">墙柜偏心值（mm）；正为右偏，负为左偏。</param>
        /// <param name="barWidth">排宽（mm）。</param>
        public static FaceToWallSpecialBusbarTemplateKind Resolve(double wallCabinetEccentricity, double barWidth)
        {
            if (barWidth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(barWidth), "排宽必须大于 0。");
            }

            var absEccentricity = Math.Abs(wallCabinetEccentricity);
            if (absEccentricity > barWidth)
            {
                return FaceToWallSpecialBusbarTemplateKind.Offset;
            }

            if (barWidth > MilledBarWidthThresholdMm)
            {
                return FaceToWallSpecialBusbarTemplateKind.MilledBar;
            }

            if (wallCabinetEccentricity < 0)
            {
                return FaceToWallSpecialBusbarTemplateKind.VerticalBendLeft;
            }

            return FaceToWallSpecialBusbarTemplateKind.VerticalBendRight;
        }

    }
}
