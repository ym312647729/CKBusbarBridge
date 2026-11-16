using System;

namespace CKBusbarBridge.models
{
    public sealed class FaceToWallSpecialBusbarLongitudinalLayout
    {
        private FaceToWallSpecialBusbarLongitudinalLayout(
            double currentClearSpan,
            double connectionClearSpan,
            double overlapLength)
        {
            CurrentClearSpan = currentClearSpan;
            ConnectionClearSpan = connectionClearSpan;
            OverlapLength = overlapLength;
            BreakpointDifference = Math.Round(currentClearSpan - connectionClearSpan, 1);
            ConnectionLength = Math.Round(connectionClearSpan + overlapLength, 1);
            CurrentLength = Math.Round(currentClearSpan + overlapLength, 1);
        }

        public double CurrentClearSpan { get; private set; }

        public double ConnectionClearSpan { get; private set; }

        public double OverlapLength { get; private set; }

        public double BreakpointDifference { get; private set; }

        public double ConnectionLength { get; private set; }

        public double CurrentLength { get; private set; }

        public static FaceToWallSpecialBusbarLongitudinalLayout Create(
            double currentClearSpan,
            double connectionClearSpan,
            double overlapLength)
        {
            currentClearSpan = RoundFinitePositive(
                currentClearSpan,
                nameof(currentClearSpan),
                "当前特殊铜排断点净跨距必须为有限正数。");
            connectionClearSpan = RoundFinitePositive(
                connectionClearSpan,
                nameof(connectionClearSpan),
                "连接点特殊铜排断点净跨距必须为有限正数。");
            overlapLength = RoundFiniteNonNegative(
                overlapLength,
                nameof(overlapLength),
                "特殊铜排搭接长度必须为有限非负数。");

            return new FaceToWallSpecialBusbarLongitudinalLayout(
                currentClearSpan,
                connectionClearSpan,
                overlapLength);
        }

        private static double RoundFinitePositive(double value, string name, string message)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value, message);
            }

            var roundedValue = Math.Round(value, 1);
            if (roundedValue <= 0)
            {
                throw new ArgumentOutOfRangeException(name, value, message);
            }

            return roundedValue;
        }

        private static double RoundFiniteNonNegative(double value, string name, string message)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(name, value, message);
            }

            return Math.Round(value, 1);
        }
    }
}
