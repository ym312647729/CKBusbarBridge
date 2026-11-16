using System;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对面与面对墙共用的基座距柜前/柜后计算。
    /// 输入为柜体开孔草图上的「距柜前/后距离」和「接口固定孔间距」，不是旧基座配合尺寸。
    /// </summary>
    public static class CabinetBaseEdgeDistanceHelper
    {
        /// <summary>
        /// 基座距柜边相对开孔「距柜边距离」的固定偏移。
        /// </summary>
        public const double BaseToCabinetEdgeOffset = 20;

        /// <summary>
        /// 开孔所在侧：基座距该侧柜边 = 距柜边距离 − 20。
        /// </summary>
        public static double GetNearSideDistance(double toCabinetEdgeDistance)
        {
            return Math.Round(toCabinetEdgeDistance - BaseToCabinetEdgeOffset, 1);
        }

        /// <summary>
        /// 开孔对侧：基座距对侧柜边 = 柜深 − (距柜边距离 + 接口固定孔间距 + 20)。
        /// </summary>
        public static double GetFarSideDistance(
            double cabinetDepth,
            double toCabinetEdgeDistance,
            double fixedHoleSpacing)
        {
            return Math.Round(
                cabinetDepth - (toCabinetEdgeDistance + fixedHoleSpacing + BaseToCabinetEdgeOffset),
                1);
        }

        /// <summary>
        /// 按当前采用的柜前孔或柜后孔，同时得到基座距柜前与基座距柜后。
        /// </summary>
        public static void Calculate(
            bool isFrontHole,
            double toCabinetEdgeDistance,
            double fixedHoleSpacing,
            double cabinetDepth,
            out double baseToCabinetFrontDistance,
            out double baseToCabinetBackDistance)
        {
            var nearSide = GetNearSideDistance(toCabinetEdgeDistance);
            var farSide = GetFarSideDistance(cabinetDepth, toCabinetEdgeDistance, fixedHoleSpacing);
            if (isFrontHole)
            {
                baseToCabinetFrontDistance = nearSide;
                baseToCabinetBackDistance = farSide;
                return;
            }

            baseToCabinetFrontDistance = farSide;
            baseToCabinetBackDistance = nearSide;
        }
    }
}
