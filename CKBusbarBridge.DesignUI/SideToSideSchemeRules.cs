using System;

namespace CKBusbarBridge.Models
{
    // 界面、规划和宿主共用的纯业务判定；不修改输入，也不读取模型。
    internal static class SideToSideSchemeRules
    {
        internal static bool HasOffset(double eccentricity)
        {
            if (double.IsNaN(eccentricity) || double.IsInfinity(eccentricity))
                throw new ArgumentOutOfRangeException(nameof(eccentricity), "柜间偏移必须为有限数值。");
            return Math.Abs(eccentricity) > 1.0;
        }

        internal static bool RequiresPhaseChange(string aSequence, string bSequence)
        {
            RequireSequence(aSequence); RequireSequence(bSequence);
            return aSequence != bSequence;
        }

        internal static SideToSideCabinetBaseKind ResolveBaseKind(string aSequence, string bSequence, double eccentricity)
        {
            var phaseChange = RequiresPhaseChange(aSequence, bSequence);
            var offset = HasOffset(eccentricity);
            return phaseChange && !offset ? SideToSideCabinetBaseKind.Base1 : SideToSideCabinetBaseKind.Base2;
        }

        private static void RequireSequence(string sequence)
        {
            if (sequence != SideToSideGeneralParameters.AbcPhaseSequence && sequence != SideToSideGeneralParameters.CbaPhaseSequence)
                throw new ArgumentException("侧对侧伸出排相序仅支持 ABC 或 CBA。");
        }
    }
}
