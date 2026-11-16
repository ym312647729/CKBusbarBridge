using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>通用模板的坐标契约；只转换已有铜排定位结果，不改变安装规划。</summary>
    internal sealed class UniversalInsulatorLayout
    {
        internal const double ToleranceMm = .01d;
        internal static readonly IReadOnlyList<string> DistanceNames = Array.AsReadOnly(new[] {
            "第一组绝缘子距边距离", "第二组绝缘子距边距离", "第三组绝缘子距边距离" });
        internal double BracketLength { get; }
        internal IReadOnlyList<double> Distances { get; }

        internal UniversalInsulatorLayout(double bracketLength, IEnumerable<double> groupLocalZ)
        {
            var positions = groupLocalZ == null ? null : groupLocalZ.ToArray();
            if (!Finite(bracketLength) || bracketLength <= 0 || positions == null || positions.Length != 3
                || positions.Any(v => !Finite(v)))
                throw new ArgumentException("通用绝缘子需要有效支架长度和三个铜排对应组基准点。");
            var distances = positions.Select(z => bracketLength / 2d - z).ToArray();
            if (distances.Any(d => d < -ToleranceMm || d > bracketLength + ToleranceMm))
                throw new ArgumentOutOfRangeException(nameof(groupLocalZ), "铜排对应的绝缘子组位置超出支架长度。");
            BracketLength = bracketLength;
            Distances = Array.AsReadOnly(distances);
        }

        internal static UniversalInsulatorLayout FromExistingCopperLayout(double length, double spacing, double offset)
        {
            if (!Finite(spacing) || spacing <= 0 || !Finite(offset))
                throw new ArgumentException("绝缘子原铜排相间距或横移量无效。");
            // 旧模板实际三相位置为+P、0、-P，原横移计划整体叠加在该位置上。
            return new UniversalInsulatorLayout(length, new[] { spacing + offset, offset, -spacing + offset });
        }

        internal static UniversalInsulatorLayout FromSideToSideBase1(double length, double spacing)
        {
            // 基座安装孔阵列中心X=0.75，宿主坐标原点X=-2.5；
            // 沿通用模板的局部距边方向换算为-3.25mm。孔距位于正交方向，不参与三相横移。
            const double copperOffset = -3.25d;
            return FromExistingCopperLayout(length, spacing, copperOffset);
        }

        internal static UniversalInsulatorLayout FromSideToSideBase1(
            double length, double spacing, double fixedHoleSpacing)
        {
            if (!Finite(fixedHoleSpacing) || fixedHoleSpacing <= 0)
                throw new ArgumentOutOfRangeException(nameof(fixedHoleSpacing), "接口固定孔间距必须为有限正数。");
            return FromSideToSideBase1(length, spacing);
        }

        internal Dictionary<string, object> Variables()
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal) { ["支架长度"] = BracketLength };
            for (var index = 0; index < 3; index++) values.Add(DistanceNames[index], Distances[index]);
            return values;
        }

        internal void ValidateDriveDimensions(double length, double spacing)
        {
            if (!Finite(length) || !Finite(spacing)
                || Math.Abs(length - BracketLength) > ToleranceMm
                || Math.Abs(spacing - PhaseSpacing()) > ToleranceMm)
                throw new ArgumentException("绝缘子距边布局与本次支架长度或相间距不一致。");
        }

        internal double PhaseSpacing()
        {
            var first = Distances[1] - Distances[0];
            var second = Distances[2] - Distances[1];
            if (first <= 0 || Math.Abs(first - second) > ToleranceMm)
                throw new InvalidOperationException("通用绝缘子三组位置不能用原有单一相间距参数表达。");
            return first;
        }

        internal double Offset => BracketLength / 2d - Distances[1];
        internal string Identity => string.Join("_", Distances.Select(d => d.ToString("0.###", CultureInfo.InvariantCulture)));
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
