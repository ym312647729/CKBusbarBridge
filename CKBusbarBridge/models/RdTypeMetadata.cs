using System;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 总装配体“默认”配置下的“RD类型”属性约定及场景判定。
    /// </summary>
    internal static class RdTypeMetadata
    {
        internal const string ConfigurationName = "默认";
        internal const string PropertyName = "RD类型";

        internal const string SmallOffset = "小偏移";
        internal const string LargeOffset = "大偏移";
        internal const string LeftRightOpening = "左右开孔";
        internal const string FrontRearOpening = "前后开孔";
        internal const string FrontRearSmallOffset = "前后开孔小偏移";
        internal const string FrontRearLargeOffset = "前后开孔大偏移";
        internal const string FrontRearBase1 = "前后开孔基座1";


        /// <summary>
        /// 面对面仅在非零偏移时返回“小偏移”或“大偏移”；零偏移返回 null。
        /// 当前连接孔与母线桥反读规则一致：柜前孔优先，否则取柜后孔。
        /// </summary>
        internal static string ResolveFaceToFace(CabinetDriverParameters parameters)
        {
            if (parameters?.General == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            var eccentricity = parameters.General.CabinetEccentricity;
            EnsureFinite(eccentricity, "柜间偏心值");
            if (eccentricity == 0d)
            {
                return null;
            }

            // 前置校验两侧柜顶孔参数完整，避免缺失参数被静默当成小偏移。
            var aWidth = ResolveFaceToFaceBaseWidth(parameters.ACabinet, "A柜");
            var bWidth = ResolveFaceToFaceBaseWidth(parameters.BCabinet, "B柜");
            return IsLargeOffset(eccentricity, aWidth, bWidth) ? LargeOffset : SmallOffset;
        }

        /// <summary>
        /// 侧对侧开孔统一为前后开孔，按独立基座类型和偏移量登记细分场景。
        /// </summary>
        internal static string ResolveSideToSide(SideToSideCabinetDriverParameters parameters)
        {
            if (parameters?.General == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (parameters.General.BaseKind
                == SideToSideCabinetBaseKind.Base1)
            {
                return FrontRearBase1;
            }

            var eccentricity = parameters.General.CabinetEccentricity;
            EnsureFinite(eccentricity, "柜间偏心值");
            if (!SideToSideSchemeRules.HasOffset(eccentricity))
            {
                return FrontRearOpening;
            }

            var aFront = ResolveSideToSideFront(parameters.ACabinet, "A柜");
            var bFront = ResolveSideToSideFront(parameters.BCabinet, "B柜");
            if (aFront != bFront)
            {
                throw new InvalidOperationException(
                    "侧对侧当前只允许前↔前、后↔后连接，不能交叉连接柜顶孔。");
            }

            var aHole = aFront
                ? parameters.ACabinet.FrontHole
                : parameters.ACabinet.BackHole;
            var bHole = bFront
                ? parameters.BCabinet.FrontHole
                : parameters.BCabinet.BackHole;
            if (aHole == null || bHole == null)
            {
                throw new InvalidOperationException("侧对侧柜顶孔参数不完整。");
            }

            EnsurePositiveFinite(aHole.Base2?.BaseWidth ?? 0d, "A柜当前基座宽");
            EnsurePositiveFinite(bHole.Base2?.BaseWidth ?? 0d, "B柜当前基座宽");
            return IsLargeOffset(eccentricity, aHole.Base2.BaseWidth, bHole.Base2.BaseWidth)
                ? FrontRearLargeOffset
                : FrontRearSmallOffset;
        }

        internal static bool IsLargeOffset(double eccentricity, double aConnectedBaseWidth, double bConnectedBaseWidth)
            => CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsLarge(
                eccentricity, aConnectedBaseWidth, bConnectedBaseWidth);

        private static double ResolveFaceToFaceBaseWidth(
            CabinetSideParameters cabinet,
            string cabinetName)
        {
            if (cabinet?.FrontHole == null || cabinet.BackHole == null)
            {
                throw new InvalidOperationException(cabinetName + "柜顶孔参数不完整。");
            }

            var hole = cabinet.FrontHole.IsEnabled
                ? cabinet.FrontHole
                : cabinet.BackHole.IsEnabled
                    ? cabinet.BackHole
                    : null;
            if (hole == null)
            {
                throw new InvalidOperationException(
                    cabinetName + "前孔和后孔均未启用，无法判定RD类型。");
            }

            // 柜体驱动用此值写入面对面连接基座宽；已有模型的分类调用者传入实际反读宽度。
            var width = cabinet.CabinetWidth;
            EnsurePositiveFinite(width, cabinetName + "当前基座宽");
            return width;
        }

        private static bool ResolveSideToSideFront(
            SideToSideCabinetParameters cabinet,
            string cabinetName)
        {
            if (cabinet?.FrontHole == null || cabinet.BackHole == null)
            {
                throw new InvalidOperationException(
                    "侧对侧" + cabinetName + "柜顶孔参数不完整。");
            }

            var enabledCount = (cabinet.FrontHole.IsEnabled ? 1 : 0)
                + (cabinet.BackHole.IsEnabled ? 1 : 0);
            if (enabledCount != 1)
            {
                throw new InvalidOperationException(
                    "侧对侧" + cabinetName + "必须且只能启用一个柜顶孔。");
            }

            return cabinet.FrontHole.IsEnabled;
        }

        private static void EnsurePositiveFinite(double value, string displayName)
        {
            EnsureFinite(value, displayName);
            if (value <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    displayName,
                    value,
                    displayName + "必须为正数。");
            }
        }

        private static void EnsureFinite(double value, string displayName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    displayName,
                    value,
                    displayName + "必须为有限数值。");
            }
        }
    }
}
