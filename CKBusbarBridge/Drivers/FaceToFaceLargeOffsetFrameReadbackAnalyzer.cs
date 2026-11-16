using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 根据真实 028 模板身份、物理尺寸和配合坐标系重建唯一大偏移组。
    /// </summary>
    internal static class FaceToFaceLargeOffsetFrameReadbackAnalyzer
    {
        // 与界面首次展开和 JSON 持久化使用同一个固定标识，避免反读后组身份无故变化。
        internal const string StableGroupId = "面对面大偏移组1";
        private const double Tolerance = 0.001d;

        internal static FaceToFaceFrameReadResult Analyze(
            IReadOnlyList<FaceToFaceFrameReadItem> frames,
            double? expectedSignedEccentricity = null,
            bool validateCoordinateRoutes = true)
        {
            if (frames == null)
            {
                throw new ArgumentNullException(nameof(frames));
            }

            var cornerIndexes = frames
                .Select((frame, index) => new { frame, index })
                .Where(item => item.frame?.FrameTemplateKind
                    == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner)
                .Select(item => item.index)
                .ToList();
            if (cornerIndexes.Count == 0)
            {
                return BuildResult(frames, 0d);
            }

            if (cornerIndexes.Count != 2)
            {
                throw new InvalidOperationException("面对面大偏移必须且只能包含两个 028 拐角基架。");
            }

            if (frames.Any(frame => frame == null))
            {
                throw new InvalidOperationException("基架物理反读结果不能包含空项。");
            }

            if (frames.Any(frame => frame.IsSmallOffsetFrame))
            {
                throw new InvalidOperationException("同一面对面装配体不能同时包含小偏移基架和 028 大偏移组。");
            }

            var bCornerIndex = cornerIndexes[0];
            var aCornerIndex = cornerIndexes[1];
            if (aCornerIndex <= bCornerIndex + 1)
            {
                throw new InvalidOperationException("两个 028 拐角基架之间必须包含至少一个常规连接基架。");
            }

            var bCorner = frames[bCornerIndex];
            var aCorner = frames[aCornerIndex];
            var connectors = frames.Skip(bCornerIndex + 1)
                .Take(aCornerIndex - bCornerIndex - 1)
                .ToList();
            if (connectors.Any(item => item.FrameTemplateKind != FaceToWallFrameTemplateKind.Regular))
            {
                throw new InvalidOperationException("028 拐角之间只能包含常规 024 连接基架。");
            }

            EnsureEqual(bCorner.Length, aCorner.Length, "两个拐角基架长度必须一致。");
            EnsureEqual(bCorner.Height, aCorner.Height, "两个拐角基架高度必须一致。");
            foreach (var connector in connectors)
            {
                EnsureEqual(connector.Width, bCorner.Length, "连接基架宽度必须等于拐角基架长度。");
                EnsureEqual(connector.Height, bCorner.Height, "连接基架高度必须等于拐角基架高度。");
            }

            EnsureSideWidths(frames.Take(bCornerIndex), bCorner.Width, "B 柜侧");
            EnsureSideWidths(frames.Skip(aCornerIndex + 1), aCorner.Width, "A 柜侧");

            var isRightOffset = ResolveOffsetDirection(
                bCorner,
                expectedSignedEccentricity,
                validateCoordinateRoutes);
            var connectorSpan = connectors.Sum(item => item.Length);
            var signedEccentricity = (isRightOffset ? 1d : -1d)
                * (connectorSpan + (bCorner.Width + aCorner.Width) / 2d);
            // 复用规划器校验大偏移总跨度及公共截面，但不能再按规划器的标准分段逐件比长度。
            // 连接基架长度允许在界面中调整，只要各段总和与柜间偏心值闭合，反读就应保留真实分段。
            FaceToFaceLargeOffsetFramePlanner.Create(
                signedEccentricity,
                aCorner.Width,
                bCorner.Width,
                bCorner.Length,
                bCorner.Height);
            ValidateMemberRoutes(
                bCorner,
                connectors,
                aCorner,
                isRightOffset,
                validateCoordinateRoutes);

            var rebuilt = frames.ToList();
            rebuilt[bCornerIndex] = bCorner.WithLargeOffsetMetadata(
                StableGroupId,
                FaceToFaceLargeOffsetFrameRole.BSideCorner);
            for (var index = bCornerIndex + 1; index < aCornerIndex; index++)
            {
                rebuilt[index] = rebuilt[index].WithLargeOffsetMetadata(
                    StableGroupId,
                    FaceToFaceLargeOffsetFrameRole.Connector);
            }

            rebuilt[aCornerIndex] = aCorner.WithLargeOffsetMetadata(
                StableGroupId,
                FaceToFaceLargeOffsetFrameRole.ASideCorner);
            return BuildResult(rebuilt, Math.Round(signedEccentricity, 3));
        }

        private static FaceToFaceFrameReadResult BuildResult(
            IReadOnlyList<FaceToFaceFrameReadItem> frames,
            double signedEccentricity)
        {
            var grouped = frames.Where(item => item?.IsLargeOffsetMember == true).ToList();
            var totalLength = grouped.Count == 0
                ? frames.Where(item => item != null).Sum(item => item.Length)
                : frames.Where(item => item != null && !item.IsLargeOffsetMember).Sum(item => item.Length)
                    + grouped.First(item => item.LargeOffsetRole == FaceToFaceLargeOffsetFrameRole.BSideCorner).Length;
            var widths = frames.Where(item => item != null)
                .Select(item => Math.Round(item.Width, 3))
                .Distinct()
                .ToList();
            return new FaceToFaceFrameReadResult(
                frames,
                widths.Count == 1 ? widths[0] : 0d,
                Math.Round(totalLength, 3),
                signedEccentricity);
        }

        private static bool ResolveOffsetDirection(
            FaceToFaceFrameReadItem bCorner,
            double? expectedSignedEccentricity,
            bool validateCoordinateRoutes)
        {
            if (!validateCoordinateRoutes)
            {
                if (!expectedSignedEccentricity.HasValue ||
                    double.IsNaN(expectedSignedEccentricity.Value) ||
                    double.IsInfinity(expectedSignedEccentricity.Value) ||
                    Math.Abs(expectedSignedEccentricity.Value) <= Tolerance)
                {
                    throw new InvalidOperationException(
                        "侧对侧大偏移按空间顺序反读时缺少有效的带符号柜间偏心值。");
                }

                return expectedSignedEccentricity.Value > 0d;
            }

            if (HasRoute(bCorner, FaceToFaceFrameChainAnalyzer.InletCoordinate1, FaceToFaceFrameChainAnalyzer.OutletCoordinate1))
            {
                return true;
            }

            if (HasRoute(bCorner, FaceToFaceFrameChainAnalyzer.InletCoordinate2, FaceToFaceFrameChainAnalyzer.OutletCoordinate2))
            {
                return false;
            }

            throw new InvalidOperationException("B 柜侧 028 拐角基架的实际进/出坐标系无法确定偏移方向。");
        }

        private static void ValidateMemberRoutes(
            FaceToFaceFrameReadItem bCorner,
            IReadOnlyList<FaceToFaceFrameReadItem> connectors,
            FaceToFaceFrameReadItem aCorner,
            bool isRightOffset,
            bool validateCoordinateRoutes)
        {
            if (!validateCoordinateRoutes)
            {
                return;
            }

            var bCornerCoordinateIndex = isRightOffset ? 1 : 2;
            var aCornerCoordinateIndex = isRightOffset ? 2 : 1;
            ValidateMemberRoute(bCorner, bCornerCoordinateIndex, 0);
            for (var connectorIndex = 0; connectorIndex < connectors.Count; connectorIndex++)
            {
                ValidateMemberRoute(connectors[connectorIndex], 1, connectorIndex + 1);
            }

            ValidateMemberRoute(aCorner, aCornerCoordinateIndex, connectors.Count + 1);
        }

        private static void ValidateMemberRoute(
            FaceToFaceFrameReadItem actual,
            int coordinateIndex,
            int memberIndex)
        {
            var expectedInlet = "坐标系-进" + coordinateIndex;
            var expectedOutlet = "坐标系-出" + coordinateIndex;
            if (!string.Equals(actual.InletCoordinateName, expectedInlet, StringComparison.Ordinal)
                || (actual.OutletCoordinateName != null
                    && !string.Equals(actual.OutletCoordinateName, expectedOutlet, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"大偏移组第 {memberIndex + 1} 个物理基架的实际进/出坐标系不符合规划。");
            }
        }

        private static bool HasRoute(
            FaceToFaceFrameReadItem frame,
            string inlet,
            string outlet)
        {
            return string.Equals(frame.InletCoordinateName, inlet, StringComparison.Ordinal)
                && string.Equals(frame.OutletCoordinateName, outlet, StringComparison.Ordinal);
        }

        private static void EnsureSideWidths(
            IEnumerable<FaceToFaceFrameReadItem> frames,
            double expectedWidth,
            string sideName)
        {
            var invalid = frames.FirstOrDefault(frame =>
                frame.FrameTemplateKind != FaceToWallFrameTemplateKind.Regular
                || Math.Abs(frame.Width - expectedWidth) > Tolerance);
            if (invalid != null)
            {
                throw new InvalidOperationException($"{sideName}常规基架宽度必须等于对应拐角基架宽度。");
            }
        }

        private static void EnsureEqual(double actual, double expected, string message)
        {
            if (Math.Abs(actual - expected) > Tolerance)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
