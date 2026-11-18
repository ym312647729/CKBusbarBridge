using System;
using System.Collections.Generic;

namespace RouteLinkBar.Algorithm.Contracts
{
    public static class LinkageRouteStackGeometry
    {
        /// <summary>
        /// U 型两个外侧折弯之间的中心线跨距倍率。跨距严格大于
        /// 4×(排厚+折弯半径) 时，扣除两端折弯切线后，中间直线段
        /// 仍严格大于两个单侧折弯切线长度。
        /// </summary>
        public const double UShapeMiddleDistanceBendUnitMultiplier = 4d;

        public static double GetUShapeMinimumMiddleDistance(
            double singleBusbarThickness,
            double bendingRadius)
        {
            if (singleBusbarThickness <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(singleBusbarThickness));
            }

            if (bendingRadius < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(bendingRadius));
            }

            return (singleBusbarThickness + bendingRadius) *
                UShapeMiddleDistanceBendUnitMultiplier;
        }

        public static double GetStackEnvelopeThickness(double singleBusbarThickness, int stackCount)
        {
            return GetStackEnvelopeThickness(singleBusbarThickness, stackCount, 0d);
        }

        public static double GetStackEnvelopeThickness(
            double singleBusbarThickness,
            int stackCount,
            double middleAdditionalSpace)
        {
            Validate(singleBusbarThickness, stackCount, middleAdditionalSpace);
            var memberCenterPitch = GetMemberCenterPitch(
                singleBusbarThickness,
                middleAdditionalSpace);
            return singleBusbarThickness + (stackCount - 1) * memberCenterPitch;
        }

        public static double GetStackBendAllowance(
            double singleBusbarThickness,
            int stackCount,
            double bendAllowance)
        {
            if (bendAllowance < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(bendAllowance));
            }

            return GetStackEnvelopeThickness(singleBusbarThickness, stackCount) / 2d + bendAllowance;
        }

        public static double GetStackBendAllowance(
            double singleBusbarThickness,
            int stackCount,
            double middleAdditionalSpace,
            double bendAllowance)
        {
            if (bendAllowance < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(bendAllowance));
            }

            return GetStackEnvelopeThickness(
                singleBusbarThickness,
                stackCount,
                middleAdditionalSpace) / 2d + bendAllowance;
        }

        public static double GetUShapeReturnMinimumBaseSegmentLength(
            double minimumActualSegmentLength,
            double singleBusbarThickness,
            int stackCount,
            double middleAdditionalSpace)
        {
            if (minimumActualSegmentLength < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minimumActualSegmentLength));
            }

            Validate(singleBusbarThickness, stackCount, middleAdditionalSpace);
            if (stackCount <= 1)
            {
                return minimumActualSegmentLength;
            }

            // U 型回程段的两个同向 90° 转角，会让最内侧预览面在每端
            // 各缩短半个叠排总包络厚度。因此基准中心线必须额外保留一个
            // 完整 StackEnvelopeThickness，才能保证最内侧实际段仍满足
            // 用户输入的最短长度。
            return minimumActualSegmentLength + GetStackEnvelopeThickness(
                singleBusbarThickness,
                stackCount,
                middleAdditionalSpace);
        }

        public static IReadOnlyList<double> GetCenteredMemberOffsets(
            double singleBusbarThickness,
            int stackCount)
        {
            return GetCenteredMemberOffsets(singleBusbarThickness, stackCount, 0d);
        }

        public static IReadOnlyList<double> GetCenteredMemberOffsets(
            double singleBusbarThickness,
            int stackCount,
            double middleAdditionalSpace)
        {
            Validate(singleBusbarThickness, stackCount, middleAdditionalSpace);
            var memberCenterPitch = GetMemberCenterPitch(
                singleBusbarThickness,
                middleAdditionalSpace);
            var centeredMemberOffsets = new List<double>(stackCount);
            for (var memberIndex = 0; memberIndex < stackCount; memberIndex++)
            {
                centeredMemberOffsets.Add(
                    (memberIndex - (stackCount - 1) / 2d) * memberCenterPitch);
            }

            return centeredMemberOffsets;
        }

        private static double GetMemberCenterPitch(
            double singleBusbarThickness,
            double middleAdditionalSpace)
        {
            // 默认保留一层单排厚度的夹层，继续满足 2 叠排 -t/+t、
            // 3 叠排 -2t/0/+2t；配置值只会把中间夹层继续加大。
            return singleBusbarThickness + Math.Max(
                singleBusbarThickness,
                middleAdditionalSpace);
        }

        private static void Validate(
            double singleBusbarThickness,
            int stackCount,
            double middleAdditionalSpace)
        {
            if (singleBusbarThickness <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(singleBusbarThickness));
            }

            if (stackCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stackCount));
            }

            if (middleAdditionalSpace < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(middleAdditionalSpace));
            }
        }
    }
}
