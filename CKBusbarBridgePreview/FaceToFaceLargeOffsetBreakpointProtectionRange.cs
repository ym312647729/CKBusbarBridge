using System;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 面对面大偏移 Z 型路径中的单个连续转角保护区。
    /// 断点必须位于转角区段之外，并在该区段两侧额外越过一个完整排宽。
    /// </summary>
    public sealed class FaceToFaceLargeOffsetBreakpointProtectionRange
    {
        private const double GeometryTolerance = 0.001d;

        private FaceToFaceLargeOffsetBreakpointProtectionRange(
            double minimumCornerFrameCoordinate,
            double maximumCornerFrameCoordinate,
            double busbarWidth)
        {
            MinimumCornerFrameCoordinate = minimumCornerFrameCoordinate;
            MaximumCornerFrameCoordinate = maximumCornerFrameCoordinate;
            BusbarWidth = busbarWidth;
            MinimumProtectedCoordinate = minimumCornerFrameCoordinate - busbarWidth;
            MaximumProtectedCoordinate = maximumCornerFrameCoordinate + busbarWidth;
        }

        public double MinimumCornerFrameCoordinate { get; }

        public double MaximumCornerFrameCoordinate { get; }

        public double BusbarWidth { get; }

        public double MinimumProtectedCoordinate { get; }

        public double MaximumProtectedCoordinate { get; }

        public static FaceToFaceLargeOffsetBreakpointProtectionRange Create(
            double minimumCornerFrameCoordinate,
            double maximumCornerFrameCoordinate,
            double busbarWidth)
        {
            if (double.IsNaN(minimumCornerFrameCoordinate) ||
                double.IsInfinity(minimumCornerFrameCoordinate))
            {
                throw new ArgumentOutOfRangeException(nameof(minimumCornerFrameCoordinate));
            }

            if (double.IsNaN(maximumCornerFrameCoordinate) ||
                double.IsInfinity(maximumCornerFrameCoordinate) ||
                maximumCornerFrameCoordinate < minimumCornerFrameCoordinate)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCornerFrameCoordinate));
            }

            if (double.IsNaN(busbarWidth) ||
                double.IsInfinity(busbarWidth) ||
                busbarWidth <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(busbarWidth));
            }

            return new FaceToFaceLargeOffsetBreakpointProtectionRange(
                minimumCornerFrameCoordinate,
                maximumCornerFrameCoordinate,
                busbarWidth);
        }

        public bool Contains(double breakpointCoordinate)
        {
            return breakpointCoordinate >=
                       MinimumProtectedCoordinate - GeometryTolerance &&
                   breakpointCoordinate <=
                       MaximumProtectedCoordinate + GeometryTolerance;
        }

        public bool IsStrictlySpannedBy(
            double intervalStartCoordinate,
            double intervalEndCoordinate)
        {
            return intervalStartCoordinate <
                       MinimumProtectedCoordinate - GeometryTolerance &&
                   intervalEndCoordinate >
                       MaximumProtectedCoordinate + GeometryTolerance;
        }
    }
}
