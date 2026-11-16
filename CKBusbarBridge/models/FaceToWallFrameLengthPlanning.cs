using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 面对墙基架使用的模板种类。
    /// </summary>
    public enum FaceToWallFrameTemplateKind
    {
        Regular = 0,
        Large1 = 1,
        Large2 = 2,
        // 面对面大偏移拐角使用 5GX.355.028，不能复用面对墙大基架身份。
        FaceToFaceLargeOffsetCorner = 3
    }

    internal sealed class FaceToWallFrameLengthPlanItem
    {
        internal FaceToWallFrameLengthPlanItem(
            FaceToWallFrameTemplateKind templateKind,
            double length)
        {
            TemplateKind = templateKind;
            Length = length;
        }

        internal FaceToWallFrameTemplateKind TemplateKind { get; }

        internal double Length { get; }
    }

    /// <summary>
    /// 面对墙双大基架模式的纯长度规划规则。
    /// </summary>
    internal static class FaceToWallFrameLengthPlanner
    {
        internal const double RegularStandardLength = 1000d;
        internal const double RegularMinimumLength = 200d;
        internal const double LargeStandardLength = 1300d;
        internal const double LargeMinimumLength = 300d;
        internal const double DualLargeMinimumTotalLength = LargeMinimumLength * 2d;
        internal const double HeightTolerance = .01d;

        // 二次驱动优先替换原分段，不能因为开孔高度改变而重新编号整个物理链。
        // 无法保留时由调用方按真实铜排重新分节，不能延长总跨度。
        internal static IReadOnlyList<FaceToWallFrameLengthPlanItem> ReconcileExisting(
            IReadOnlyList<FaceToWallFrameLengthPlanItem> existing, bool rising)
        {
            if (existing == null || existing.Count < (rising ? 2 : 1)) return null;
            var result = new List<FaceToWallFrameLengthPlanItem>();
            for (var i = 0; i < existing.Count; i++)
            {
                var kind = i == existing.Count - 1 ? FaceToWallFrameTemplateKind.Large1 :
                    rising && i == existing.Count - 2 ? FaceToWallFrameTemplateKind.Large2 : FaceToWallFrameTemplateKind.Regular;
                if (existing[i] == null || !Enum.IsDefined(typeof(FaceToWallFrameTemplateKind), existing[i].TemplateKind) ||
                    existing[i].TemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner ||
                    (existing[i].TemplateKind == FaceToWallFrameTemplateKind.Large1) != (i == existing.Count - 1) ||
                    existing[i].TemplateKind == FaceToWallFrameTemplateKind.Large2 && i != existing.Count - 2)
                    throw new InvalidOperationException("面对墙原基架链必须为024（可选）→027（可选）→026。");
                var length = existing[i].Length;
                if (double.IsNaN(length) || double.IsInfinity(length) ||
                    length < (kind == FaceToWallFrameTemplateKind.Regular ? RegularMinimumLength : LargeMinimumLength) - .01d)
                    return null;
                result.Add(new FaceToWallFrameLengthPlanItem(kind, length));
            }
            return result;
        }

        internal static bool RequiresLargeFrame2(
            double baseOpeningHeight,
            double wallHoleHeight)
        {
            ValidateHeight(baseOpeningHeight, nameof(baseOpeningHeight));
            ValidateHeight(wallHoleHeight, nameof(wallHoleHeight));

            if (baseOpeningHeight - wallHoleHeight > HeightTolerance + 1e-9d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(baseOpeningHeight),
                    "基架高度不能大于墙孔高度");
            }

            return wallHoleHeight - baseOpeningHeight > HeightTolerance + 1e-9d;
        }

        internal static IReadOnlyList<FaceToWallFrameLengthPlanItem> CreateDualLargeFramePlan(
            double totalLength)
        {
            ValidateLength(totalLength, nameof(totalLength));
            if (totalLength < DualLargeMinimumTotalLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(totalLength),
                    $"双大基架总长度不能小于 {DualLargeMinimumTotalLength:0.##} mm。");
            }

            if (totalLength < 1600d)
            {
                return CreateLargeOnlyPlan(LargeMinimumLength, totalLength - LargeMinimumLength);
            }

            if (totalLength < 2600d)
            {
                return CreateLargeOnlyPlan(totalLength - LargeStandardLength, LargeStandardLength);
            }

            if (totalLength <= 2600d)
            {
                return CreateLargeOnlyPlan(LargeStandardLength, LargeStandardLength);
            }

            if (totalLength < 2800d)
            {
                return new[]
                {
                    new FaceToWallFrameLengthPlanItem(
                        FaceToWallFrameTemplateKind.Regular,
                        RegularMinimumLength),
                    new FaceToWallFrameLengthPlanItem(
                        FaceToWallFrameTemplateKind.Large2,
                        totalLength - 1500d),
                    new FaceToWallFrameLengthPlanItem(
                        FaceToWallFrameTemplateKind.Large1,
                        LargeStandardLength)
                };
            }

            var result = SplitRegularFrames(totalLength - 2600d);
            result.Add(new FaceToWallFrameLengthPlanItem(
                FaceToWallFrameTemplateKind.Large2,
                LargeStandardLength));
            result.Add(new FaceToWallFrameLengthPlanItem(
                FaceToWallFrameTemplateKind.Large1,
                LargeStandardLength));
            return result;
        }

        private static IReadOnlyList<FaceToWallFrameLengthPlanItem> CreateLargeOnlyPlan(
            double large2Length,
            double large1Length)
        {
            return new[]
            {
                new FaceToWallFrameLengthPlanItem(
                    FaceToWallFrameTemplateKind.Large2,
                    large2Length),
                new FaceToWallFrameLengthPlanItem(
                    FaceToWallFrameTemplateKind.Large1,
                    large1Length)
            };
        }

        private static List<FaceToWallFrameLengthPlanItem> SplitRegularFrames(double totalLength)
        {
            var result = new List<FaceToWallFrameLengthPlanItem>();
            var fullFrameCount = (int)Math.Floor(totalLength / RegularStandardLength);
            var remainder = totalLength - fullFrameCount * RegularStandardLength;

            for (var index = 0; index < fullFrameCount; index++)
            {
                result.Add(new FaceToWallFrameLengthPlanItem(
                    FaceToWallFrameTemplateKind.Regular,
                    RegularStandardLength));
            }

            if (remainder <= 0d)
            {
                return result;
            }

            if (remainder >= RegularMinimumLength)
            {
                result.Add(new FaceToWallFrameLengthPlanItem(
                    FaceToWallFrameTemplateKind.Regular,
                    remainder));
                return result;
            }

            var adjustedPreviousLength = RegularStandardLength - RegularMinimumLength + remainder;
            result.RemoveAt(result.Count - 1);
            result.Add(new FaceToWallFrameLengthPlanItem(
                FaceToWallFrameTemplateKind.Regular,
                adjustedPreviousLength));
            result.Add(new FaceToWallFrameLengthPlanItem(
                FaceToWallFrameTemplateKind.Regular,
                RegularMinimumLength));
            return result;
        }

        private static void ValidateHeight(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
            {
                throw new ArgumentOutOfRangeException(parameterName, "高度必须为有效正数。");
            }
        }

        private static void ValidateLength(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
            {
                throw new ArgumentOutOfRangeException(parameterName, "长度必须为有效正数。");
            }
        }
    }
}
