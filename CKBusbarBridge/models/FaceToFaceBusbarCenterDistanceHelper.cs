using System;
using System.Globalization;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对面场景铜排中心至柜前/后距离的推导计算。
    /// 与面对墙场景的固定偏移算法不同，此处由基座深、绝缘子高度与排厚动态计算。
    /// </summary>
    public static class FaceToFaceBusbarCenterDistanceHelper
    {
        public const double FrontHoleDefaultToCabinetEdgeDistance = 470;
        public const double FrontHoleBaseDefaultToCabinetFrontDistance = 450;
        public const double BackHoleDefaultToCabinetEdgeDistance = 30;
        public const double BackHoleBaseDefaultToCabinetBackDistance = 516.5;
        public const double HoleBaseLengthOffset = 41.5;
        public const double BaseSupportBracketHeight =
            InsulatorHeightRules.BaseSupportBracketHeight;
        public const double BusbarBendThicknessFactor = 3.5;

        /// <summary>
        /// 由接口固定孔间距推导基座深（与驱动基座时的公式一致）。
        /// </summary>
        public static double GetBaseDepth(double fixedHoleSpacing)
        {
            return fixedHoleSpacing + HoleBaseLengthOffset;
        }

        /// <summary>
        /// 铜排中心距基座前缘距离 = 基座深 −
        /// (绝缘子+支架高度 + 41.5 + 3.5×排厚)。
        /// </summary>
        public static double GetBusbarCenterToBaseDistance(double baseDepth, double insulatorHeight, double busbarThickness)
        {
            var occupiedLength = insulatorHeight + BaseSupportBracketHeight +
                BusbarBendThicknessFactor * busbarThickness;
            return Math.Max(0, baseDepth - occupiedLength);
        }

        /// <summary>
        /// 由柜前孔“距柜前距离”推导基座距柜前距离：距柜前距离 − 20。
        /// </summary>
        public static double GetBaseToCabinetFrontDistance(double toCabinetFrontDistance)
        {
            return CabinetBaseEdgeDistanceHelper.GetNearSideDistance(toCabinetFrontDistance);
        }

        /// <summary>
        /// 由柜后孔“距柜后距离”推导铜排中心计算用的基座距柜后距离。
        /// 基架总长使用的基座距柜后见 <see cref="CabinetBaseEdgeDistanceHelper"/>。
        /// </summary>
        public static double GetBaseToCabinetBackDistance(double toCabinetBackDistance)
        {
            return BackHoleBaseDefaultToCabinetBackDistance
                + (toCabinetBackDistance - BackHoleDefaultToCabinetEdgeDistance);
        }

        /// <summary>
        /// 计算铜排中心至柜前/后距离。
        /// 柜前孔：基座距柜前 + 铜排中心距基座；柜后孔：基座距柜后 − 铜排中心距基座。
        /// </summary>
        public static double CalculateBusbarCenterToCabinetEdgeDistance(
            bool isFrontHole,
            double toCabinetEdgeDistance,
            double fixedHoleSpacing,
            double insulatorHeight,
            double busbarThickness)
        {
            var baseDepth = GetBaseDepth(fixedHoleSpacing);
            var busbarCenterToBaseDistance = GetBusbarCenterToBaseDistance(baseDepth, insulatorHeight, busbarThickness);

            if (isFrontHole)
            {
                return GetBaseToCabinetFrontDistance(toCabinetEdgeDistance) + busbarCenterToBaseDistance;
            }

            return Math.Max(0, GetBaseToCabinetBackDistance(toCabinetEdgeDistance) - busbarCenterToBaseDistance);
        }

        /// <summary>
        /// 从铜排规格解析排厚；解析失败时回退到默认规格「80×8」的排厚。
        /// </summary>
        public static double ResolveBusbarThickness(string busbarSpec)
        {
            if (TryParseBusbarThickness(busbarSpec, out var thickness))
            {
                return thickness;
            }

            TryParseBusbarThickness(CabinetGeneralParameters.DefaultBusbarSpec, out thickness);
            return thickness > 0 ? thickness : 8;
        }

        /// <summary>
        /// 解析铜排规格文本（兼容 “宽×厚” / “宽x厚” / “宽*厚”）并提取厚度。
        /// </summary>
        public static bool TryParseBusbarThickness(string specification, out double thickness)
        {
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

            if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out thickness) &&
                !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out thickness))
            {
                return false;
            }

            return thickness > 0;
        }
    }

    /// <summary>
    /// 换相时基座开孔高度的统一工艺下限。
    /// </summary>
    internal static class PhaseChangeBaseOpeningHeightRules
    {
        internal const double BaseSupportBracketHeight =
            InsulatorHeightRules.BaseSupportBracketHeight;
        internal const double PhaseRoutingClearanceAddition =
            CKBusbarBridgePreview.FaceToFaceModelBusbarPlanner.PhaseChangePreferredClearanceAddition;
        internal const double UpperAngleSteelHeight = 40d;
        private const double EqualityTolerance = 0.001d;

        internal static double CalculateMinimum(
            double insulatorHeight,
            double busbarThickness,
            int singlePhaseRootCount,
            double insulationDistance)
        {
            ValidatePositiveFinite(insulatorHeight, "绝缘子及支架高度");
            ValidatePositiveFinite(busbarThickness, "排厚");
            ValidatePositiveFinite(insulationDistance, "绝缘距离");
            if (singlePhaseRootCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "单相根数", singlePhaseRootCount, "单相根数必须为正整数。");
            }

            // 当前叠排节距为两倍排厚，包络厚度为 (2N-1)t。
            // 高层抬升为相间净距+(2N-1)t+t，最高横排另抬升 t；
            // 再加最外层中心偏移 (N-1)t 和半个排厚，合计 (3N+0.5)t。
            // 上角钢下伸及对上方金属的净距必须单独预留，不能与相间净距共用。
            return Math.Round(
                BaseSupportBracketHeight
                + insulatorHeight
                + insulationDistance + PhaseRoutingClearanceAddition
                + (3d * singlePhaseRootCount + 0.5d) * busbarThickness
                + UpperAngleSteelHeight
                + insulationDistance,
                3);
        }

        internal static bool TryValidate(
            double baseOpeningHeight,
            double insulatorHeight,
            double busbarThickness,
            int singlePhaseRootCount,
            double insulationDistance,
            out string validationMessage)
        {
            validationMessage = null;
            if (!(baseOpeningHeight > 0d)
                || double.IsNaN(baseOpeningHeight)
                || double.IsInfinity(baseOpeningHeight))
            {
                validationMessage = "换相校验缺少有效的基架高度。";
                return false;
            }

            double minimumHeight;
            try
            {
                minimumHeight = CalculateMinimum(
                    insulatorHeight,
                    busbarThickness,
                    singlePhaseRootCount,
                    insulationDistance);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                validationMessage = "换相基架高度校验失败：" + exception.Message;
                return false;
            }

            if (baseOpeningHeight + EqualityTolerance >= minimumHeight)
            {
                return true;
            }

            validationMessage = string.Format(
                CultureInfo.InvariantCulture,
                "换相时基架高度不能小于 41.5+绝缘子及支架高度+绝缘距离+{2:0.###}+(3×单相根数+0.5)×排厚+{3:0.###}+绝缘距离={0:0.###} mm；当前为 {1:0.###} mm。"
                + "绝缘子及支架高度={5:0.###} mm，单相根数={6}，排厚={7:0.###} mm；"
                + "绝缘距离={4:0.###} mm，换相相间规划净距={8:0.###} mm，上角钢高度={3:0.###} mm，上方金属净距={4:0.###} mm。"
                + "请在对应柜体的基座参数页面手动调整基架高度，建议值至少为 {9:0.###} mm（含 10 mm 净距余量）。",
                minimumHeight,
                baseOpeningHeight,
                PhaseRoutingClearanceAddition,
                UpperAngleSteelHeight,
                insulationDistance,
                insulatorHeight,
                singlePhaseRootCount,
                busbarThickness,
                insulationDistance + PhaseRoutingClearanceAddition,
                minimumHeight + 10d);
            return false;
        }

        private static void ValidatePositiveFinite(double value, string name)
        {
            if (!(value > 0d) || double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    name + "必须为有限正数。");
            }
        }
    }

    /// <summary>不换相时基座开孔高度的工艺下限。</summary>
    internal static class NoPhaseChangeBaseOpeningHeightRules
    {
        private const double EqualityTolerance = 0.001d;

        internal static double CalculateMinimum(
            double insulatorHeight,
            double busbarThickness,
            int singlePhaseRootCount,
            double insulationDistance)
        {
            ValidatePositiveFinite(insulatorHeight, "绝缘子及支架高度");
            ValidatePositiveFinite(busbarThickness, "排厚");
            ValidatePositiveFinite(insulationDistance, "绝缘距离");
            if (singlePhaseRootCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "单相根数", singlePhaseRootCount, "单相根数必须为正整数。");
            }

            return Math.Round(
                InsulatorHeightRules.BaseSupportBracketHeight
                + insulatorHeight
                + (2d * singlePhaseRootCount - 1d) * busbarThickness
                + insulationDistance
                + PhaseChangeBaseOpeningHeightRules.UpperAngleSteelHeight,
                3);
        }

        internal static bool TryValidate(
            double baseOpeningHeight,
            double insulatorHeight,
            double busbarThickness,
            int singlePhaseRootCount,
            double insulationDistance,
            out string validationMessage)
        {
            validationMessage = null;
            if (!(baseOpeningHeight > 0d)
                || double.IsNaN(baseOpeningHeight)
                || double.IsInfinity(baseOpeningHeight))
            {
                validationMessage = "不换相校验缺少有效的基架高度。";
                return false;
            }

            double minimumHeight;
            try
            {
                minimumHeight = CalculateMinimum(
                    insulatorHeight, busbarThickness, singlePhaseRootCount,
                    insulationDistance);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                validationMessage = "不换相基架高度校验失败：" + exception.Message;
                return false;
            }

            if (baseOpeningHeight + EqualityTolerance >= minimumHeight)
            {
                return true;
            }

            validationMessage = string.Format(
                CultureInfo.InvariantCulture,
                "不换相时基架高度不能小于 41.5+绝缘子及支架高度+(2×单相根数-1)×排厚+绝缘距离+40={0:0.###} mm；当前为 {1:0.###} mm。"
                + "绝缘子及支架高度={2:0.###} mm，单相根数={3}，排厚={4:0.###} mm，绝缘距离={5:0.###} mm。",
                minimumHeight, baseOpeningHeight, insulatorHeight,
                singlePhaseRootCount, busbarThickness, insulationDistance);
            return false;
        }

        private static void ValidatePositiveFinite(double value, string name)
        {
            if (!(value > 0d) || double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    name, value, name + "必须为有限正数。");
            }
        }
    }
}
