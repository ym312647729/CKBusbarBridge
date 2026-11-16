using System;

namespace CKBusbarBridge.DesignGeometry
{
    // 同一纯函数源码供未加密界面及规划库使用，避免预览、落模、反读各自复制判据。
    internal static class BridgeOffsetRules
    {
        internal const double LengthTolerance = .1d;
        // 零偏移识别决定是否存在偏移结构，独立于实测长度的验收误差。
        private const double ZeroOffsetTolerance = .001d;
        internal const double WidenedOffsetLimit = 200d;

        internal static bool LengthsMatch(double actual, double expected)
        {
            // 比较验收区间，避免相减的浮点尾差把恰好0.1mm误判为超差。
            return !double.IsInfinity(actual) && !double.IsInfinity(expected)
                && actual >= expected - LengthTolerance && actual <= expected + LengthTolerance;
        }

        internal static bool IsWidened(double signedOffset)
        {
            EnsureFinite(signedOffset, nameof(signedOffset));
            return Math.Abs(signedOffset) > ZeroOffsetTolerance
                && Math.Abs(signedOffset) <= WidenedOffsetLimit + LengthTolerance;
        }

        internal static double AverageWidth(double aConnectedBaseWidth, double bConnectedBaseWidth)
        {
            EnsurePositive(aConnectedBaseWidth, nameof(aConnectedBaseWidth));
            EnsurePositive(bConnectedBaseWidth, nameof(bConnectedBaseWidth));
            var average = aConnectedBaseWidth / 2d + bConnectedBaseWidth / 2d;
            EnsurePositive(average, nameof(average));
            return average;
        }

        internal static bool IsLarge(double signedOffset, double aConnectedBaseWidth, double bConnectedBaseWidth)
        {
            EnsureFinite(signedOffset, nameof(signedOffset));
            return Math.Abs(signedOffset) > AverageWidth(aConnectedBaseWidth, bConnectedBaseWidth) + 200d;
        }

        internal static double WidenedWidth(double signedOffset, double aConnectedBaseWidth, double bConnectedBaseWidth)
        {
            EnsureFinite(signedOffset, nameof(signedOffset));
            var width = AverageWidth(aConnectedBaseWidth, bConnectedBaseWidth) + Math.Abs(signedOffset);
            EnsurePositive(width, nameof(width));
            return width;
        }

        private static void EnsurePositive(double value, string name)
        {
            EnsureFinite(value, name);
            if (value <= 0d) throw new ArgumentOutOfRangeException(name, "连接基座宽度必须为有限正数。");
        }

        private static void EnsureFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "偏移及连接基座宽度必须为有限数值。");
        }
    }
}
