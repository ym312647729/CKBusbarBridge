using System;
using System.Globalization;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 面对面与侧对侧场景共用的出柜顶母排规格元数据约定。
    /// </summary>
    internal static class TopBusbarSpecificationMetadata
    {
        internal const string PropertyName = "出柜顶母排规格";

        /// <summary>
        /// 按面对面规则构建总装配体“出柜顶母排规格”属性值。
        /// </summary>
        internal static bool TryBuildValue(
            string busbarSpec,
            int singlePhaseRootCount,
            out string propertyValue)
        {
            propertyValue = null;
            if (!TryParseBusbarSpec(busbarSpec, out var width, out var thickness))
            {
                return false;
            }

            // 出柜顶母排根数 = 单相根数 - 1。
            var topBusbarCount = singlePhaseRootCount - 1;
            if (topBusbarCount <= 0)
            {
                return false;
            }

            propertyValue = string.Format(
                CultureInfo.InvariantCulture,
                "{0}×{1}{2}排",
                FormatDimension(width),
                FormatDimension(thickness),
                ToChineseNumeral(topBusbarCount));
            return true;
        }

        internal static bool TryParseBusbarSpec(
            string specification,
            out double width,
            out double thickness)
        {
            width = 0;
            thickness = 0;

            if (string.IsNullOrWhiteSpace(specification))
            {
                return false;
            }

            var parts = specification.Trim()
                .Replace('×', 'x')
                .Replace('*', 'x')
                .Replace('X', 'x')
                .Split(new[] { 'x' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2)
            {
                return false;
            }

            if (!double.TryParse(
                    parts[0].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out width)
                && !double.TryParse(
                    parts[0].Trim(),
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out width))
            {
                return false;
            }

            if (!double.TryParse(
                    parts[1].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out thickness)
                && !double.TryParse(
                    parts[1].Trim(),
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out thickness))
            {
                return false;
            }

            return width > 0 && thickness > 0;
        }

        internal static string FormatDimension(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string ToChineseNumeral(int value)
        {
            if (value < 0)
            {
                return string.Empty;
            }

            var digits = new[] { "〇", "一", "二", "三", "四", "五", "六", "七", "八", "九" };

            if (value < 10)
            {
                return digits[value];
            }

            if (value < 20)
            {
                return value == 10 ? "十" : "十" + digits[value - 10];
            }

            if (value < 100)
            {
                var tens = value / 10;
                var ones = value % 10;
                return digits[tens] + "十" + (ones == 0 ? string.Empty : digits[ones]);
            }

            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
