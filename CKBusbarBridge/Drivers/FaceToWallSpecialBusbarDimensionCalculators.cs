using CKBusbarBridge.models;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 已配置的面对墙单相特殊铜排尺寸计算器共享输入校验。
    /// </summary>
    internal abstract class ConfiguredFaceToWallSpecialBusbarDimensionCalculator
        : IFaceToWallSpecialBusbarDimensionCalculator
    {
        private const double ConnectionFirstSegmentBase = 170.0;
        private const double CompensatedFirstSegmentMinimumBase = 190.0;
        private const double BPhaseAdditionalLength = 125.0;

        protected sealed class LongitudinalAllocation
        {
            public double First { get; set; }

            public double Last { get; set; }
        }

        public bool IsConfigured => true;

        public abstract IReadOnlyDictionary<string, double> Calculate(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult);

        protected static void ValidateInputs(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (preparationResult == null)
            {
                throw new ArgumentNullException(nameof(preparationResult));
            }

            if (!IsFinite(plan.DeltaX) || plan.DeltaX <= 0)
            {
                throw new InvalidOperationException(
                    string.Format("{0}相特殊铜排排长DeltaX必须大于0：{1}", plan.Phase, plan.DeltaX));
            }

            if (!IsFinite(plan.DeltaY) || plan.DeltaY < 0)
            {
                throw new InvalidOperationException(
                    string.Format("{0}相特殊铜排抬高值DeltaY不能小于0：{1}", plan.Phase, plan.DeltaY));
            }

            if (!IsFinite(preparationResult.BusbarWidth) || preparationResult.BusbarWidth <= 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相特殊铜排排宽必须大于0：{1}",
                        plan.Phase,
                        preparationResult.BusbarWidth));
            }

            if (!IsFinite(preparationResult.OverlapLength)
                || preparationResult.OverlapLength < 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相特殊铜排搭接尺寸不能小于0：{1}",
                        plan.Phase,
                        preparationResult.OverlapLength));
            }
        }

        protected static double RoundMillimeters(double value)
        {
            return Math.Round(value, 1);
        }

        protected static LongitudinalAllocation CalculateLongitudinalAllocation(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            var layout = ResolveLongitudinalLayout(plan, preparationResult);
            var width = preparationResult.BusbarWidth;
            var overlap = layout.OverlapLength;
            var phaseAddition = plan.Phase == BusbarPhaseSequence.B
                ? width + BPhaseAdditionalLength
                : 0;
            var baseFirst = Math.Max(
                layout.ConnectionLength / 4.0,
                ConnectionFirstSegmentBase + width + overlap) + phaseAddition;
            var minimumFirst = CompensatedFirstSegmentMinimumBase + width + phaseAddition;
            var difference = layout.BreakpointDifference;
            var first = difference >= 0
                ? baseFirst + difference
                : Math.Max(minimumFirst, baseFirst + difference);
            var remainder = difference - (first - baseFirst);
            var baseLast = plan.TemplateKind == FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly
                ? layout.ConnectionLength - baseFirst
                : layout.ConnectionLength / 4.0;
            var last = baseLast + remainder;

            first = RoundMillimeters(first);
            last = RoundMillimeters(last);
            if (last <= 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相{1}特殊铜排补偿后的末段尺寸必须大于0：{2:0.0}",
                        plan.Phase,
                        plan.TemplateKind,
                        last));
            }

            return new LongitudinalAllocation
            {
                First = first,
                Last = last
            };
        }

        private static FaceToWallSpecialBusbarLongitudinalLayout ResolveLongitudinalLayout(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            var layout = preparationResult.FaceToWallSpecialBusbarLongitudinalLayout;
            if (layout == null)
            {
                var clearSpan = plan.DeltaX - preparationResult.OverlapLength;
                layout = FaceToWallSpecialBusbarLongitudinalLayout.Create(
                    clearSpan,
                    clearSpan,
                    preparationResult.OverlapLength);
            }

            if (Math.Abs(layout.CurrentLength - plan.DeltaX) > 0.11)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相特殊铜排当前总长与纵向布局不一致。",
                        plan.Phase));
            }

            return layout;
        }

        protected static void EnsureNonNegative(
            FaceToWallSpecialBusbarPhasePlan plan,
            string segmentName,
            double value)
        {
            if (!IsFinite(value) || value < 0)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相{1}特殊铜排{2}不能小于0：{3:0.0}",
                        plan.Phase,
                        plan.TemplateKind,
                        segmentName,
                        value));
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>
    /// 立弯模板尺寸计算器；桥头和直头使用相同公式。
    /// </summary>
    internal sealed class VerticalBendFaceToWallSpecialBusbarDimensionCalculator
        : ConfiguredFaceToWallSpecialBusbarDimensionCalculator
    {
        public override IReadOnlyDictionary<string, double> Calculate(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            ValidateInputs(plan, preparationResult);

            var allocation = CalculateLongitudinalAllocation(plan, preparationResult);
            var raiseLength = allocation.Last;
            var offsetTotalLength = plan.DeltaX - raiseLength;
            var secondOffsetLength =
                (offsetTotalLength - allocation.First) / 3.0;

            EnsureNonNegative(plan, "偏移排总长度", offsetTotalLength);
            EnsureNonNegative(plan, "偏移第二段长度", secondOffsetLength);

            return new Dictionary<string, double>
            {
                { "偏移排总长度@控制骨架草图1", RoundMillimeters(offsetTotalLength) },
                { "折弯高度@控制骨架草图1", RoundMillimeters(plan.DeltaY) },
                { "折弯排抬高长度@控制骨架草图1", raiseLength },
                { "偏移第一段长度尺寸@控制骨架草图2", allocation.First },
                { "偏移第二段长度尺寸@控制骨架草图2", RoundMillimeters(secondOffsetLength) }
            };
        }
    }

    /// <summary>
    /// 零偏移直排仅抬高模板尺寸计算器；桥头和直头使用相同公式。
    /// </summary>
    internal sealed class StraightRaiseOnlyFaceToWallSpecialBusbarDimensionCalculator
        : ConfiguredFaceToWallSpecialBusbarDimensionCalculator
    {
        public override IReadOnlyDictionary<string, double> Calculate(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            ValidateInputs(plan, preparationResult);

            var allocation = CalculateLongitudinalAllocation(plan, preparationResult);

            return new Dictionary<string, double>
            {
                {
                    "折弯排第一段长度@控制骨架草图1",
                    allocation.First
                },
                {
                    "折弯排第二段长度@控制骨架草图1",
                    allocation.Last
                },
                { "抬高尺寸@控制骨架草图1", RoundMillimeters(plan.DeltaY) }
            };
        }
    }

    /// <summary>
    /// 偏移连接排模板尺寸计算器；桥头和直头使用相同公式。
    /// </summary>
    internal sealed class OffsetConnectionFaceToWallSpecialBusbarDimensionCalculator
        : ConfiguredFaceToWallSpecialBusbarDimensionCalculator
    {
        public override IReadOnlyDictionary<string, double> Calculate(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            ValidateInputs(plan, preparationResult);

            var allocation = CalculateLongitudinalAllocation(plan, preparationResult);
            var firstBendLength = plan.DeltaX
                - allocation.First
                - allocation.Last
                + preparationResult.OverlapLength;

            EnsureNonNegative(plan, "折弯排第一段长度", firstBendLength);

            return new Dictionary<string, double>
            {
                { "第一节直排长度@控制骨架草图1", allocation.First },
                { "折弯排第一段长度@控制骨架草图1", RoundMillimeters(firstBendLength) },
                { "抬高高度@控制骨架草图1", RoundMillimeters(plan.DeltaY) },
                { "折弯排第二段长度@控制骨架草图1", allocation.Last }
            };
        }
    }

    /// <summary>
    /// 铣排模板尺寸计算器；桥头和直头使用相同公式。
    /// </summary>
    internal sealed class MilledBarFaceToWallSpecialBusbarDimensionCalculator
        : ConfiguredFaceToWallSpecialBusbarDimensionCalculator
    {
        public override IReadOnlyDictionary<string, double> Calculate(
            FaceToWallSpecialBusbarPhasePlan plan,
            BusbarGenerationPreparationResult preparationResult)
        {
            ValidateInputs(plan, preparationResult);

            var allocation = CalculateLongitudinalAllocation(plan, preparationResult);
            var firstBarLength = plan.DeltaX - allocation.Last;

            EnsureNonNegative(plan, "第一段排长度", firstBarLength);
            if (allocation.First + preparationResult.BusbarWidth > firstBarLength)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相铣排第一段尺寸与排宽之和不能大于第一段排长度。",
                        plan.Phase));
            }

            return new Dictionary<string, double>
            {
                { "第一段排长度尺寸@控制骨架草图1", RoundMillimeters(firstBarLength) },
                {
                    "第一段铣排尺寸@控制骨架草图1",
                    allocation.First
                },
                { "折弯高度@控制骨架草图1", RoundMillimeters(plan.DeltaY) },
                { "折弯排第二段长度@控制骨架草图1", allocation.Last }
            };
        }
    }
}
