using CKBusbarBridge.Models;
using System;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 将已严格反读的侧对侧柜体参数转换为不依赖文件和 COM 的基架输入。
    /// </summary>
    internal static class SideToSideFrameInputAdapter
    {
        private const double EqualityTolerance = 1.0;
        private const double Base1FrameInsulatorHeightOffset = 6d;

        internal static SideToSideFrameInitialization Create(
            SideToSideCabinetDriverParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            // 孔位准入必须先于模板访问和所有模型修改。
            var aFront = ResolveFront(parameters.ACabinet, "A柜");
            var bFront = ResolveFront(parameters.BCabinet, "B柜");
            if (aFront != bFront)
            {
                throw new InvalidOperationException(
                    "侧对侧当前只允许前↔前、后↔后连接，不能交叉连接柜顶孔。");
            }

            var openingMode = parameters.General.CabinetTopOpeningMode;
            var baseKind = parameters.General.BaseKind == SideToSideCabinetBaseKind.Base1
                ? SideToSideFrameBaseKind.Base1
                : SideToSideFrameBaseKind.Base2;
            var aHole = aFront ? parameters.ACabinet.FrontHole : parameters.ACabinet.BackHole;
            var bHole = bFront ? parameters.BCabinet.FrontHole : parameters.BCabinet.BackHole;

            EnsureCommonInputs(parameters, aHole, bHole);

            double aBaseWidth;
            double bBaseWidth;
            double aBaseDepth;
            double bBaseDepth;
            double aBaseHeight;
            double bBaseHeight;
            double aOpeningHeight;
            double bOpeningHeight;
            if (baseKind == SideToSideFrameBaseKind.Base1)
            {
                aBaseWidth = aHole.Base1.BaseWidth;
                bBaseWidth = bHole.Base1.BaseWidth;
                aBaseDepth = aHole.Base1.BaseDepth;
                bBaseDepth = bHole.Base1.BaseDepth;
                aBaseHeight = aHole.Base1.BaseHeight;
                bBaseHeight = bHole.Base1.BaseHeight;
                aOpeningHeight = aHole.Base1.BaseOpeningHeight;
                bOpeningHeight = bHole.Base1.BaseOpeningHeight;
            }
            else
            {
                // 基架接在基座2孔口，宽深取该连接对象；下部增高叠装的安装总高独立处理。
                aBaseWidth = aHole.Base2.BaseWidth;
                bBaseWidth = bHole.Base2.BaseWidth;
                aBaseDepth = aHole.Base2.BaseDepth;
                bBaseDepth = bHole.Base2.BaseDepth;
                aBaseHeight = aHole.Base2.BaseHeight;
                bBaseHeight = bHole.Base2.BaseHeight;
                aOpeningHeight = aHole.Base2.BaseOpeningHeight;
                bOpeningHeight = bHole.Base2.BaseOpeningHeight;
            }

            EnsurePositiveFinite(aBaseWidth, "A柜当前基座宽");
            EnsurePositiveFinite(bBaseWidth, "B柜当前基座宽");
            EnsurePositiveFinite(aBaseDepth, "A柜当前基座深");
            EnsurePositiveFinite(bBaseDepth, "B柜当前基座深");
            EnsurePositiveFinite(aBaseHeight, "A柜当前基座安装总高");
            EnsurePositiveFinite(bBaseHeight, "B柜当前基座安装总高");
            EnsurePositiveFinite(aOpeningHeight, "A柜当前基架高度");
            EnsurePositiveFinite(bOpeningHeight, "B柜当前基架高度");
            if (Math.Abs(aOpeningHeight - bOpeningHeight) > EqualityTolerance)
            {
                throw new InvalidOperationException(
                    "侧对侧 A/B 柜同位置、同基座类型的基架高度必须相同。");
            }

            var totalFrameLength = parameters.General.CabinetSpacing
                + (parameters.ACabinet.CabinetWidth - aBaseDepth) / 2d
                + (parameters.BCabinet.CabinetWidth - bBaseDepth) / 2d;
            EnsurePositiveFinite(totalFrameLength, "总基架长");

            var eccentricity = parameters.General.CabinetEccentricity;
            EnsureFinite(eccentricity, "柜间偏心值");
            var offsetKind = baseKind == SideToSideFrameBaseKind.Base1
                || !SideToSideSchemeRules.HasOffset(eccentricity)
                    ? SideToSideFrameOffsetKind.None
                    : RdTypeMetadata.IsLargeOffset(eccentricity, aBaseWidth, bBaseWidth)
                        ? SideToSideFrameOffsetKind.Large
                        : SideToSideFrameOffsetKind.Small;

            return new SideToSideFrameInitialization
            {
                OpeningMode = openingMode,
                IsFrontHole = aFront,
                BCabinetBasePositionName = bFront ? "B柜前" : "B柜后",
                BaseKind = baseKind,
                OffsetKind = offsetKind,
                TotalFrameLength = totalFrameLength,
                FrameWidth = Math.Max(aBaseWidth, bBaseWidth),
                FrameHeight = aOpeningHeight,
                ACabinetBaseWidth = aBaseWidth,
                BCabinetBaseWidth = bBaseWidth,
                ACabinetBaseDepth = aBaseDepth,
                BCabinetBaseDepth = bBaseDepth,
                ACabinetInstallationHeight = aBaseHeight,
                BCabinetInstallationHeight = bBaseHeight,
                ACabinetBusbarCenterToBaseOffset = aHole.BusbarCenterToBaseOffset,
                BCabinetBusbarCenterToBaseOffset = bHole.BusbarCenterToBaseOffset,
                CabinetEccentricity = eccentricity,
                PhaseSpacing = parameters.General.ExposedBusbarPhaseSpacing,
                BusbarSpec = parameters.General.BusbarSpec,
                BusbarMaterial = parameters.General.BusbarMaterial,
                SinglePhaseRootCount = parameters.General.SinglePhaseRootCount,
                // 基架绝缘子模型使用本体高度；基座1连接基架
                // 再按工艺要求增加6 mm。
                InsulatorBodyHeight = InsulatorHeightRules.ToBodyHeight(
                        parameters.General.InsulatorHeight)
                    + (baseKind == SideToSideFrameBaseKind.Base1
                        ? Base1FrameInsulatorHeightOffset
                        : 0d),
                ACabinetFrontHoleEnabled = parameters.ACabinet.FrontHole.IsEnabled,
                ACabinetBackHoleEnabled = parameters.ACabinet.BackHole.IsEnabled,
                BCabinetFrontHoleEnabled = parameters.BCabinet.FrontHole.IsEnabled,
                BCabinetBackHoleEnabled = parameters.BCabinet.BackHole.IsEnabled
            };
        }

        private static bool ResolveFront(
            SideToSideCabinetParameters cabinet,
            string cabinetName)
        {
            if (cabinet == null || cabinet.FrontHole == null || cabinet.BackHole == null)
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

        private static void EnsureCommonInputs(
            SideToSideCabinetDriverParameters parameters,
            SideToSideCabinetHoleParameters aHole,
            SideToSideCabinetHoleParameters bHole)
        {
            EnsurePositiveFinite(parameters.General.CabinetSpacing, "柜间距");
            EnsurePositiveFinite(parameters.ACabinet.CabinetWidth, "A柜柜宽");
            EnsurePositiveFinite(parameters.BCabinet.CabinetWidth, "B柜柜宽");
            EnsurePositiveFinite(
                parameters.General.ExposedBusbarPhaseSpacing,
                "相间距");
            EnsurePositiveFinite(
                parameters.General.InsulatorHeight,
                "绝缘子+支架高度");
            EnsurePositiveFinite(
                aHole.BusbarCenterToBaseOffset,
                "A柜铜排中心距基座偏移");
            EnsurePositiveFinite(
                bHole.BusbarCenterToBaseOffset,
                "B柜铜排中心距基座偏移");
            if (parameters.General.SinglePhaseRootCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(parameters.General.SinglePhaseRootCount),
                    "单相根数必须为正整数。");
            }
            if (string.IsNullOrWhiteSpace(parameters.General.BusbarSpec))
            {
                throw new ArgumentException("铜排规格不能为空。", nameof(parameters));
            }
            if (string.IsNullOrWhiteSpace(parameters.General.BusbarMaterial))
            {
                throw new ArgumentException("铜排材质不能为空。", nameof(parameters));
            }
        }

        private static void EnsurePositiveFinite(double value, string displayName)
        {
            if (!(value > 0d) || double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    displayName,
                    value,
                    displayName + "必须为有限正数。");
            }
        }

        private static void EnsureFinite(double value, string displayName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    displayName,
                    value,
                    displayName + "必须为有限数。");
            }
        }

    }
}
