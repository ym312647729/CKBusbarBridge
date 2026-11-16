using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对面大偏移基架的纯计算规划器。
    /// 只计算物理成员、尺寸和装配坐标系，不访问磁盘或 SolidWorks。
    /// </summary>
    internal static class FaceToFaceLargeOffsetFramePlanner
    {
        internal const double StandardConnectorLength = 1000d;
        internal const double MinimumConnectorSpan =
            CKBusbarBridgePreview.FaceToFaceUnifiedPlanningRules
                .MinimumLargeOffsetConnectorSpan;

        internal static FaceToFaceLargeOffsetFramePlan Create(
            double signedEccentricity,
            double aSideBaseWidth,
            double bSideBaseWidth,
            double sharedLength,
            double height)
        {
            ValidateSignedFiniteNonZero(nameof(signedEccentricity), signedEccentricity);
            ValidatePositiveFinite(nameof(aSideBaseWidth), aSideBaseWidth);
            ValidatePositiveFinite(nameof(bSideBaseWidth), bSideBaseWidth);
            ValidatePositiveFinite(nameof(sharedLength), sharedLength);
            ValidatePositiveFinite(nameof(height), height);

            var eccentricity = Normalize(Math.Abs(signedEccentricity));
            var normalizedAWidth = Normalize(aSideBaseWidth);
            var normalizedBWidth = Normalize(bSideBaseWidth);
            var normalizedSharedLength = Normalize(sharedLength);
            var normalizedHeight = Normalize(height);
            var connectorSpan = Normalize(
                CKBusbarBridgePreview.FaceToFaceUnifiedPlanningRules
                    .CalculateLargeOffsetConnectorSpan(
                        signedEccentricity,
                        normalizedAWidth,
                        normalizedBWidth));
            if (connectorSpan < MinimumConnectorSpan)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(signedEccentricity),
                    signedEccentricity,
                    "当前已分类为大偏移，但该基座宽度组合的连接空间不足 200 mm。");
            }

            var isRightOffset = signedEccentricity > 0d;
            var members = new List<FaceToFaceLargeOffsetFrameMemberPlan>
            {
                CreateCorner(
                    FaceToFaceLargeOffsetFrameRole.BSideCorner,
                    normalizedBWidth,
                    normalizedSharedLength,
                    normalizedHeight,
                    isRightOffset ? 1 : 2)
            };

            foreach (var connectorLength in SplitLengths(
                connectorSpan,
                StandardConnectorLength))
            {
                // 连接基架始终正装；正负偏移只改变两个拐角所选坐标系。
                members.Add(new FaceToFaceLargeOffsetFrameMemberPlan(
                    FaceToFaceLargeOffsetFrameRole.Connector,
                    FaceToWallFrameTemplateKind.Regular,
                    normalizedSharedLength,
                    connectorLength,
                    normalizedHeight,
                    "坐标系-进1",
                    "坐标系-出1"));
            }

            members.Add(CreateCorner(
                FaceToFaceLargeOffsetFrameRole.ASideCorner,
                normalizedAWidth,
                normalizedSharedLength,
                normalizedHeight,
                isRightOffset ? 2 : 1));

            return new FaceToFaceLargeOffsetFramePlan(
                Normalize(signedEccentricity),
                connectorSpan,
                normalizedSharedLength,
                members);
        }

        /// <summary>
        /// 按现有基架规则拆分长度：标准段分居两侧，非标准余段放在中间。
        /// </summary>
        internal static IReadOnlyList<double> SplitLengths(
            double totalLength,
            double standardLength)
        {
            ValidatePositiveFinite(nameof(totalLength), totalLength);
            ValidatePositiveFinite(nameof(standardLength), standardLength);

            var normalizedTotal = Normalize(totalLength);
            var normalizedStandard = Normalize(standardLength);
            var standardCount = (int)Math.Floor(normalizedTotal / normalizedStandard);
            var remainingLength = Normalize(
                normalizedTotal - standardCount * normalizedStandard);
            var leftFrameCount = standardCount / 2;
            var rightFrameCount = standardCount - leftFrameCount;
            var lengths = new List<double>();

            for (var index = 0; index < leftFrameCount; index++)
            {
                lengths.Add(normalizedStandard);
            }

            if (remainingLength > 0d)
            {
                lengths.Add(remainingLength);
            }

            for (var index = 0; index < rightFrameCount; index++)
            {
                lengths.Add(normalizedStandard);
            }

            return lengths.AsReadOnly();
        }

        private static FaceToFaceLargeOffsetFrameMemberPlan CreateCorner(
            FaceToFaceLargeOffsetFrameRole role,
            double width,
            double length,
            double height,
            int coordinateIndex)
        {
            return new FaceToFaceLargeOffsetFrameMemberPlan(
                role,
                FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner,
                width,
                length,
                height,
                "坐标系-进" + coordinateIndex,
                "坐标系-出" + coordinateIndex);
        }

        private static double Normalize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        private static void ValidateSignedFiniteNonZero(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value == 0d)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "柜间偏心值必须为有限非零数值。");
            }
        }

        private static void ValidatePositiveFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "尺寸必须为有限正数。");
            }
        }
    }
}
