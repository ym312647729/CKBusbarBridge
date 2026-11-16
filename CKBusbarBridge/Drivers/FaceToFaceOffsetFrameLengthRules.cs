using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对面偏移基架的旧长度调整辅助。新面对面规划的尺寸下限由
    /// <see cref="CKBusbarBridgePreview.FaceToFaceUnifiedPlanningRules"/> 按工艺计算。
    /// </summary>
    internal static class FaceToFaceOffsetFrameLengthRules
    {
        private const double PhaseBusbarCount = 3d;
        private const double PhaseClearance = 125d;
        private const double PhaseClearanceCount = 4d;
        private const double EdgeAllowance = 30d;
        private const double EdgeAllowanceCount = 2d;
        internal const double RegularFrameMinimumLength = 200d;
        internal const double RegularFrameMaximumLength = 1400d;

        /// <summary>
        /// 旧模型兼容下限：排宽×3+125×4+30×2。
        /// 新面对面基架和基座不得再调用本方法。
        /// </summary>
        internal static double GetMinimumLength(double busbarWidth)
        {
            if (double.IsNaN(busbarWidth)
                || double.IsInfinity(busbarWidth)
                || busbarWidth <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(busbarWidth), "排宽必须为有限正数。");
            }

            var minimumLength = busbarWidth * PhaseBusbarCount
                + PhaseClearance * PhaseClearanceCount
                + EdgeAllowance * EdgeAllowanceCount;
            if (double.IsInfinity(minimumLength))
            {
                throw new ArgumentOutOfRangeException(nameof(busbarWidth), "偏移基架最小长度超出有效数值范围。");
            }

            return Math.Round(minimumLength, 3, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// 从“排宽×厚度”或“排数×排宽×厚度”规格中提取排宽。
        /// </summary>
        internal static bool TryGetBusbarWidth(string busbarSpec, out double busbarWidth)
        {
            busbarWidth = 0d;
            if (string.IsNullOrWhiteSpace(busbarSpec))
            {
                return false;
            }

            var matches = Regex.Matches(busbarSpec, @"\d+(?:\.\d+)?");
            var widthIndex = matches.Count >= 3 ? 1 : 0;
            return matches.Count >= 2
                && double.TryParse(
                    matches[widthIndex].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out busbarWidth)
                && busbarWidth > 0d;
        }

        internal static bool TryGetBusbarThickness(
            string busbarSpec,
            out double busbarThickness)
        {
            busbarThickness = 0d;
            if (string.IsNullOrWhiteSpace(busbarSpec))
            {
                return false;
            }

            var matches = Regex.Matches(busbarSpec, @"\d+(?:\.\d+)?");
            var thicknessIndex = matches.Count >= 3 ? 2 : 1;
            return matches.Count > thicknessIndex
                && double.TryParse(
                    matches[thicknessIndex].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out busbarThickness)
                && busbarThickness > 0d;
        }

        /// <summary>
        /// 将初始偏移段设为 A/B 柜侧基座宽度较小值，并通过后、前相邻常规基架保持总长。
        /// 常规基架最短为 200 mm；仍不足时删除相邻常规段，但不改变目标总长。
        /// </summary>
        internal static int ApplyInitialOffsetLength(
            IList<double> frameLengths,
            int offsetFrameIndex,
            double targetOffsetLength)
        {
            if (frameLengths == null)
            {
                throw new ArgumentNullException(nameof(frameLengths));
            }

            if (frameLengths.Count == 0
                || offsetFrameIndex < 0
                || offsetFrameIndex >= frameLengths.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(offsetFrameIndex), "偏移基架位置超出逻辑段范围。");
            }

            if (!IsPositiveFinite(targetOffsetLength))
            {
                throw new ArgumentOutOfRangeException(nameof(targetOffsetLength), "偏移基架初始长度必须为有限正数。");
            }

            if (frameLengths.Any(length => !IsPositiveFinite(length)))
            {
                throw new ArgumentException("初始基架长度必须全部为有限正数。", nameof(frameLengths));
            }

            var originalTotalLength = frameLengths.Sum();
            var remainingRegularLength = originalTotalLength - targetOffsetLength;
            if (remainingRegularLength < -0.001d
                || (remainingRegularLength > 0.001d
                    && remainingRegularLength < RegularFrameMinimumLength - 0.001d))
            {
                throw new ArgumentException("目标总长不足以同时容纳偏移基架和最短 200 mm 的常规基架。");
            }

            var requiredIncrease = targetOffsetLength - frameLengths[offsetFrameIndex];
            frameLengths[offsetFrameIndex] = targetOffsetLength;
            if (requiredIncrease < -0.001d)
            {
                // 偏移段缩短时，按业务规则把释放的长度优先还给后一段。
                var releasedLength = -requiredIncrease;
                if (frameLengths.Count == 1)
                {
                    if (releasedLength < RegularFrameMinimumLength - 0.001d
                        || releasedLength > RegularFrameMaximumLength + 0.001d)
                    {
                        throw new ArgumentException(
                            "偏移基架缩短后的剩余区间无法生成200～1400 mm普通基架。");
                    }

                    frameLengths.Add(releasedLength);
                }
                else
                {
                    AddToFollowingOrPreviousFrame(
                        frameLengths,
                        offsetFrameIndex,
                        releasedLength);
                }
            }

            var remainingIncrease = requiredIncrease;
            while (remainingIncrease > 0.001d)
            {
                remainingIncrease = ReduceAdjacentFrame(
                    frameLengths,
                    offsetFrameIndex + 1,
                    remainingIncrease);
                remainingIncrease = ReduceAdjacentFrame(
                    frameLengths,
                    offsetFrameIndex - 1,
                    remainingIncrease);
                if (remainingIncrease <= 0.001d)
                {
                    break;
                }

                // 前后段都已到 200 mm 下限后，优先删除后一段，再删除前一段。
                if (offsetFrameIndex + 1 < frameLengths.Count)
                {
                    remainingIncrease = RemoveAdjacentFrame(
                        frameLengths,
                        offsetFrameIndex + 1,
                        offsetFrameIndex,
                        remainingIncrease);
                }
                else if (offsetFrameIndex > 0)
                {
                    remainingIncrease = RemoveAdjacentFrame(
                        frameLengths,
                        offsetFrameIndex - 1,
                        offsetFrameIndex,
                        remainingIncrease);
                    offsetFrameIndex--;
                }
                else
                {
                    throw new ArgumentException("基架总长不足以容纳目标偏移基架长度。");
                }
            }

            if (Math.Abs(frameLengths.Sum() - originalTotalLength) > 0.001d)
            {
                throw new InvalidOperationException("偏移基架初始化后的基架总长发生变化。");
            }
            if (frameLengths.Where((length, index) => index != offsetFrameIndex)
                .Any(length => length < RegularFrameMinimumLength - 0.001d
                    || length > RegularFrameMaximumLength + 0.001d))
            {
                throw new InvalidOperationException(
                    "偏移基架初始化后的普通基架长度必须保持在200～1400 mm范围内。");
            }

            return offsetFrameIndex;
        }

        private static double ReduceAdjacentFrame(
            IList<double> frameLengths,
            int frameIndex,
            double remainingIncrease)
        {
            if (remainingIncrease <= 0.001d
                || frameIndex < 0
                || frameIndex >= frameLengths.Count)
            {
                return remainingIncrease;
            }

            var removableLength = Math.Max(
                0d,
                frameLengths[frameIndex] - RegularFrameMinimumLength);
            var reduction = Math.Min(removableLength, remainingIncrease);
            frameLengths[frameIndex] -= reduction;
            return remainingIncrease - reduction;
        }

        private static double RemoveAdjacentFrame(
            IList<double> frameLengths,
            int removeIndex,
            int offsetFrameIndex,
            double remainingIncrease)
        {
            var removedLength = frameLengths[removeIndex];
            frameLengths.RemoveAt(removeIndex);
            remainingIncrease -= removedLength;
            if (remainingIncrease >= -0.001d)
            {
                return Math.Max(0d, remainingIncrease);
            }

            // 删除整段超出所需扣减量时，把余量还给尚存的相邻常规段。
            var adjustedOffsetIndex = removeIndex < offsetFrameIndex
                ? offsetFrameIndex - 1
                : offsetFrameIndex;
            AddToFollowingOrPreviousFrame(
                frameLengths,
                adjustedOffsetIndex,
                -remainingIncrease);
            return 0d;
        }

        private static void AddToFollowingOrPreviousFrame(
            IList<double> frameLengths,
            int offsetFrameIndex,
            double length)
        {
            if (length <= 0.001d)
            {
                return;
            }

            var remainingLength = length;
            for (var distance = 1;
                distance < frameLengths.Count && remainingLength > 0.001d;
                distance++)
            {
                var followingIndex = offsetFrameIndex + distance;
                if (followingIndex < frameLengths.Count)
                {
                    remainingLength = AddWithinMaximum(
                        frameLengths,
                        followingIndex,
                        remainingLength);
                }

                var previousIndex = offsetFrameIndex - distance;
                if (previousIndex >= 0 && remainingLength > 0.001d)
                {
                    remainingLength = AddWithinMaximum(
                        frameLengths,
                        previousIndex,
                        remainingLength);
                }
            }

            if (remainingLength > 0.001d)
            {
                throw new ArgumentException(
                    "现有普通基架无法在1400 mm上限内吸收偏移基架释放的长度。");
            }
        }

        private static double AddWithinMaximum(
            IList<double> frameLengths,
            int frameIndex,
            double remainingLength)
        {
            var capacity = Math.Max(0d,
                RegularFrameMaximumLength - frameLengths[frameIndex]);
            var addition = Math.Min(capacity, remainingLength);
            frameLengths[frameIndex] += addition;
            return remainingLength - addition;
        }

        private static bool IsPositiveFinite(double value)
        {
            return !double.IsNaN(value)
                && !double.IsInfinity(value)
                && value > 0d;
        }
    }
}
