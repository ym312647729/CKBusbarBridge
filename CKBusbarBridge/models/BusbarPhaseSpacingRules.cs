using System;
using System.Globalization;
using CKBusbarBridge.Helpers;

namespace CKBusbarBridge.Models
{
    internal static class BusbarPhaseSpacingRules
    {
        internal static bool TryValidate(
            string busbarSpec,
            double phaseSpacing,
            double insulationDistance,
            out string errorMessage)
        {
            if (!TopBusbarSpecificationMetadata.TryParseBusbarSpec(
                    busbarSpec, out var busbarWidth, out _) ||
                !IsPositiveFinite(busbarWidth))
            {
                errorMessage = "无法从铜排规格解析有效排宽，不能校验相间距。";
                return false;
            }

            if (!IsPositiveFinite(insulationDistance))
            {
                errorMessage = "绝缘距离必须为有限正数。";
                return false;
            }

            var minimumPhaseSpacing = busbarWidth + insulationDistance;
            if (!IsPositiveFinite(phaseSpacing) ||
                phaseSpacing + 0.000001d < minimumPhaseSpacing)
            {
                errorMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "相间距不能小于排宽＋绝缘距离：当前 {0:0.###} mm，排宽 {1:0.###} mm，绝缘距离 {2:0.###} mm，至少 {3:0.###} mm。",
                    phaseSpacing, busbarWidth, insulationDistance, minimumPhaseSpacing);
                return false;
            }

            errorMessage = null;
            return true;
        }

        private static bool IsPositiveFinite(double value)
        {
            return value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
