using System.Globalization;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 总装配体“中间排规格”自定义属性约定。
    /// </summary>
    internal static class MiddleBusbarSpecificationMetadata
    {
        internal const string PropertyName = "中间排规格";

        /// <summary>
        /// 按“排宽×排厚|单线根数”构建属性值。
        /// </summary>
        internal static bool TryBuildValue(
            string busbarSpec,
            int singlePhaseRootCount,
            out string propertyValue)
        {
            propertyValue = null;
            if (singlePhaseRootCount <= 0
                || !TopBusbarSpecificationMetadata.TryParseBusbarSpec(
                    busbarSpec,
                    out var width,
                    out var thickness))
            {
                return false;
            }

            propertyValue = string.Format(
                CultureInfo.InvariantCulture,
                "{0}×{1}|{2}",
                TopBusbarSpecificationMetadata.FormatDimension(width),
                TopBusbarSpecificationMetadata.FormatDimension(thickness),
                singlePhaseRootCount);
            return true;
        }
    }
}
