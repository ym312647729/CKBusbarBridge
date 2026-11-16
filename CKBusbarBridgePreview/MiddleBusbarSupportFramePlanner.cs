using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 中间基架的独立铺排规划器。
    /// 基架不再按铜排分段划分：除偏移基架必须绑定偏移段断点外，
    /// 常规基架短跨度优先单节，其余按标准节连续铺排，单节长度不得超过上限。
    /// </summary>
    public static class MiddleBusbarSupportFramePlanner
    {
        /// <summary>单节基架长度上限。</summary>
        public const double MaximumFrameLength = 1400d;

        /// <summary>标准基架节拍长度；铺排优先使用该长度。</summary>
        public const double StandardFrameLength = 1000d;

        /// <summary>常规基架最小长度，与界面长度规则保持一致。</summary>
        public const double MinimumFrameLength = 200d;

        private const int LengthUnitsPerMillimetre = 10;
        private const double SingleFramePreferredMaximumLength = 1300d;
        private const double ShortRemainderMergeThreshold = 300d;

        /// <summary>
        /// 按参考路径的纵向跨度铺排常规基架长度序列。
        /// 1300 mm 内直接单节；其余标准节分居两侧，300 mm 内的余段优先并入后一节。
        /// </summary>
        public static IReadOnlyList<double> CreateFrameLengths(double spanLength)
        {
            if (double.IsNaN(spanLength) || double.IsInfinity(spanLength) || spanLength <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spanLength),
                    spanLength,
                    "基架铺排跨度必须为有限正数。");
            }

            // 先按界面长度精度归一化，再用 0.1 mm 整数单位判断边界和合并，避免累计尾差。
            var normalizedSpan = Math.Round(spanLength, 1, MidpointRounding.AwayFromZero);
            var spanUnits = checked((long)Math.Round(
                normalizedSpan * LengthUnitsPerMillimetre, MidpointRounding.AwayFromZero));
            if (spanUnits < (long)(MinimumFrameLength * LengthUnitsPerMillimetre))
            {
                throw new InvalidOperationException(
                    "连续普通基架区间不足200 mm，无法生成合法基架。");
            }

            if (spanUnits <= (long)(SingleFramePreferredMaximumLength * LengthUnitsPerMillimetre))
            {
                return Array.AsReadOnly(new[] { spanUnits / (double)LengthUnitsPerMillimetre });
            }

            var standardUnits = (long)(StandardFrameLength * LengthUnitsPerMillimetre);
            var standardCount = checked((int)(spanUnits / standardUnits));
            var remainingUnits = spanUnits % standardUnits;
            var lengths = new List<long>();
            var leftCount = standardCount / 2;
            var rightCount = standardCount - leftCount;
            for (var index = 0; index < leftCount; index++)
            {
                lengths.Add(standardUnits);
            }

            if (remainingUnits > 0)
            {
                lengths.Add(remainingUnits);
            }

            for (var index = 0; index < rightCount; index++)
            {
                lengths.Add(standardUnits);
            }

            MergeShortFrames(lengths);
            return lengths.Select(length => length / (double)LengthUnitsPerMillimetre)
                .ToList().AsReadOnly();
        }

        /// <summary>
        /// 特殊偏移区域必须由单节特殊基架完整覆盖，不允许在工艺区内均分。
        /// 输入是已经按 10 mm 向上取整的初始装配长度。
        /// </summary>
        public static IReadOnlyList<double> CreateOffsetFrameLengths(double offsetSpanLength)
        {
            if (double.IsNaN(offsetSpanLength) || double.IsInfinity(offsetSpanLength)
                || offsetSpanLength <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offsetSpanLength),
                    offsetSpanLength,
                    "偏移基架跨度必须为有限正数。");
            }

            var normalizedSpan = Math.Round(offsetSpanLength, 3,
                MidpointRounding.AwayFromZero);
            if (normalizedSpan > MaximumFrameLength + 0.001d)
            {
                throw new InvalidOperationException(
                    "特殊基架必须完整覆盖偏移工艺区，取整后长度不能超过1400 mm。");
            }

            return new[] { normalizedSpan };
        }

        private static void MergeShortFrames(List<long> lengths)
        {
            var mergeThresholdUnits = (long)(ShortRemainderMergeThreshold * LengthUnitsPerMillimetre);
            var maximumUnits = (long)(MaximumFrameLength * LengthUnitsPerMillimetre);
            while (true)
            {
                var shortIndex = lengths.FindIndex(length =>
                    length > 0 && length <= mergeThresholdUnits);
                if (shortIndex < 0)
                {
                    break;
                }

                var nextIndex = shortIndex + 1;
                var previousIndex = shortIndex - 1;
                if (nextIndex < lengths.Count
                    && lengths[shortIndex] + lengths[nextIndex]
                        <= maximumUnits)
                {
                    lengths[nextIndex] += lengths[shortIndex];
                    lengths.RemoveAt(shortIndex);
                    continue;
                }

                if (previousIndex >= 0
                    && lengths[shortIndex] + lengths[previousIndex]
                        <= maximumUnits)
                {
                    lengths[previousIndex] += lengths[shortIndex];
                    lengths.RemoveAt(shortIndex);
                    continue;
                }

                throw new InvalidOperationException(
                    "普通基架区间无法在200～1400 mm范围内完成确定性铺排。");
            }
        }
    }
}
