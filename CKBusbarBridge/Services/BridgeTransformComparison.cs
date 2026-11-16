using System;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeTransformDifference
    {
        internal bool Valid;
        internal double TranslationMm;
        internal double RotationDegrees;
        internal double Scale;
        internal bool Equivalent => Valid && TranslationMm <= .01d && RotationDegrees <= .01d && Scale <= 1e-8;
        public override string ToString() => Valid
            ? string.Format(CultureInfo.InvariantCulture, "平移差={0:R}mm；旋转差={1:R}°；缩放差={2:R}", TranslationMm, RotationDegrees, Scale)
            : "有效变换数据缺失、非有限或旋转轴无效";
    }

    internal sealed class BridgeTransformComparison
    {
        internal bool IsValid(double[] values)
        {
            // SW 的 13～15 为未使用槽位，不参与数值、有限性或位姿比较。
            if (values == null || values.Length != 16 || values.Take(13).Any(v => double.IsNaN(v) || double.IsInfinity(v))
                || Math.Abs(values[12]) < 1e-12) return false;
            for (var row = 0; row < 3; row++)
            {
                if (Math.Abs(Dot(values, row, values, row) - 1d) > 1e-6) return false;
                for (var other = row + 1; other < 3; other++)
                    if (Math.Abs(Dot(values, row, values, other)) > 1e-6) return false;
            }
            return true;
        }

        internal BridgeTransformDifference Compare(double[] before, double[] after)
        {
            var result = new BridgeTransformDifference { Valid = IsValid(before) && IsValid(after) };
            if (!result.Valid) return result;
            result.TranslationMm = Math.Sqrt(Enumerable.Range(9, 3).Sum(i => Math.Pow(before[i] - after[i], 2))) * 1000d;
            result.Scale = Math.Abs(before[12] - after[12]);
            var trace = Enumerable.Range(0, 3).Sum(row => Dot(before, row, after, row)
                / Math.Sqrt(Dot(before, row, before, row) * Dot(after, row, after, row)));
            // 镜像手性改变不能被旋转角公式视为等价。
            result.RotationDegrees = Determinant(before) * Determinant(after) < 0d ? 180d
                : Math.Acos(Math.Max(-1d, Math.Min(1d, (trace - 1d) / 2d))) * 180d / Math.PI;
            return result;
        }

        private double Dot(double[] a, int rowA, double[] b, int rowB) =>
            Enumerable.Range(0, 3).Sum(i => a[rowA * 3 + i] * b[rowB * 3 + i]);
        private double Determinant(double[] a) => a[0] * (a[4] * a[8] - a[5] * a[7])
            - a[1] * (a[3] * a[8] - a[5] * a[6]) + a[2] * (a[3] * a[7] - a[4] * a[6]);
    }
}
