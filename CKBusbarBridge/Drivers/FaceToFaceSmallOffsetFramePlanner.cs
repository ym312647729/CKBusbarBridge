using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    internal static class FaceToFaceSmallOffsetFramePlanner
    {
        internal static IReadOnlyList<FaceToFaceSmallOffsetCoverPlatePlan> CreateCoverPlatePlans(double width, double length)
        {
            ValidatePositiveFinite(nameof(width), width);
            ValidatePositiveFinite(nameof(length), length);
            if (width <= 1400d)
                return new[] { new FaceToFaceSmallOffsetCoverPlatePlan("8GX.017.222", 1,
                    new Dictionary<string, double> { ["小偏移盖板长度"] = length, ["小偏移盖板宽度"] = width },
                    new Dictionary<string, string> { ["D1@草图1"] = "\"小偏移盖板长度\" - 2mm", ["D2@草图1"] = "\"小偏移盖板宽度\" - 2mm" },
                    new Dictionary<string, double> { ["D1@草图1"] = length - 2d, ["D2@草图1"] = width - 2d }) };
            return new[] {
                new FaceToFaceSmallOffsetCoverPlatePlan("8GX.017.227", 2,
                    new Dictionary<string, double> { ["小偏移盖板长度"] = length, ["小偏移盖板宽度"] = width },
                    new Dictionary<string, string> { ["D1@草图1"] = "\"小偏移盖板长度\" - 2mm", ["D2@草图1"] = "\"小偏移盖板宽度\" / 2 - 2mm" },
                    new Dictionary<string, double> { ["D1@草图1"] = length - 2d, ["D2@草图1"] = width / 2d - 2d }),
                new FaceToFaceSmallOffsetCoverPlatePlan("8GX.160.128", 2,
                    new Dictionary<string, double> { ["小偏移盖板长度"] = length },
                    new Dictionary<string, string> { ["D1@草图1"] = "\"小偏移盖板长度\" - 80mm" },
                    new Dictionary<string, double> { ["D1@草图1"] = length - 80d }) };
        }

        internal static FaceToFaceSmallOffsetFrameLayout CreateLayout(
            int frameCount,
            int offsetFrameIndex,
            double signedEccentricity,
            double aSideBaseWidth,
            double bSideBaseWidth)
        {
            if (frameCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            }

            if (offsetFrameIndex < 0 || offsetFrameIndex >= frameCount)
            {
                throw new ArgumentOutOfRangeException(nameof(offsetFrameIndex));
            }

            ValidatePositiveFinite(nameof(aSideBaseWidth), aSideBaseWidth);
            ValidatePositiveFinite(nameof(bSideBaseWidth), bSideBaseWidth);
            ValidateFinite(nameof(signedEccentricity), signedEccentricity);

            var eccentricity = Math.Abs(signedEccentricity);
            if (eccentricity == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(signedEccentricity),
                    "柜间偏心值为 0 时不生成偏移基架。");
            }

            var averageSideWidth = (aSideBaseWidth + bSideBaseWidth) / 2d;
            if (double.IsInfinity(averageSideWidth))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(aSideBaseWidth),
                    "柜侧基座宽度参与计算后超出有效数值范围。");
            }

            if (Models.RdTypeMetadata.IsLargeOffset(signedEccentricity, aSideBaseWidth, bSideBaseWidth))
            {
                throw new NotSupportedException(
                    $"柜间偏心值 {eccentricity:0.###} mm 超过两端连接基座平均宽加200 mm，应使用大偏移基架。");
            }

            var offsetFrameWidth = CKBusbarBridge.DesignGeometry.BridgeOffsetRules.WidenedWidth(
                signedEccentricity, aSideBaseWidth, bSideBaseWidth);
            if (double.IsInfinity(offsetFrameWidth))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(signedEccentricity),
                    "偏移基架宽度超出有效数值范围。");
            }

            if (offsetFrameWidth < Math.Max(aSideBaseWidth, bSideBaseWidth))
            {
                throw new ArgumentException(
                    "偏移基架宽度小于柜侧基座宽，会产生负的进孔或出孔偏移尺寸。",
                    nameof(signedEccentricity));
            }

            // 布局按桥向记录端口：进孔侧靠 B 柜，出孔侧靠 A 柜。
            var inletSideWidth = bSideBaseWidth;
            var outletSideWidth = aSideBaseWidth;
            var isRightOffset = signedEccentricity > 0;
            var offsetFrame = new FaceToFaceSmallOffsetFrameDrivePlan(
                offsetFrameWidth,
                isRightOffset ? 0 : 1,
                (offsetFrameWidth - inletSideWidth) / 2d,
                isRightOffset ? 1 : 0,
                (offsetFrameWidth - outletSideWidth) / 2d);

            if (CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsWidened(signedEccentricity))
                return CreateWidenedLayout(frameCount, offsetFrameIndex, offsetFrame);

            var frameWidths = new List<double>(frameCount);
            for (var index = 0; index < frameCount; index++)
            {
                frameWidths.Add(index < offsetFrameIndex
                    ? bSideBaseWidth
                    : index == offsetFrameIndex
                        ? offsetFrameWidth
                        : aSideBaseWidth);
            }

            return new FaceToFaceSmallOffsetFrameLayout(
                frameWidths,
                offsetFrameIndex,
                offsetFrame);
        }

        private static FaceToFaceSmallOffsetFrameLayout CreateWidenedLayout(int frameCount,
            int offsetFrameIndex, FaceToFaceSmallOffsetFrameDrivePlan offsetFrame)
        {
            // 全桥同宽，桥内端口居中，只有两个基座连接端保留孔偏移及封板。
            var plans = new FaceToFaceSmallOffsetFrameDrivePlan[frameCount];
            for (var index = 0; index < frameCount; index++)
                if (index == 0 || index == frameCount - 1)
                    plans[index] = new FaceToFaceSmallOffsetFrameDrivePlan(offsetFrame.FrameWidth,
                        offsetFrame.InletOffsetDirection, index == 0 ? offsetFrame.InletOffsetSize : 0d,
                        offsetFrame.OutletOffsetDirection, index == frameCount - 1 ? offsetFrame.OutletOffsetSize : 0d);
            return new FaceToFaceSmallOffsetFrameLayout(
                new List<double>(System.Linq.Enumerable.Repeat(offsetFrame.FrameWidth, frameCount)),
                offsetFrameIndex, offsetFrame, plans);
        }

        internal static bool TryReadWidened(IReadOnlyList<Models.FaceToFaceFrameReadItem> frames,
            out double signedOffset, out double aWidth, out double bWidth)
        {
            signedOffset = aWidth = bWidth = 0d;
            if (frames == null || frames.Count == 0) return false;
            var first = frames[0]; var last = frames[frames.Count - 1];
            if (first == null || last == null || AnyInvalidWidenedMember(frames)) return false;
            var inlet = first.SmallOffsetFrame?.InInstallationOrder(first.IsReversed);
            var outlet = last.SmallOffsetFrame?.InInstallationOrder(last.IsReversed);
            if (inlet == null || outlet == null) return false;
            if ((inlet.InletOffsetDirection != 0 && inlet.InletOffsetDirection != 1)
                || inlet.OutletOffsetDirection != 1 - inlet.InletOffsetDirection) return false;
            if (frames.Count > 1 && (!CKBusbarBridge.DesignGeometry.BridgeOffsetRules.LengthsMatch(inlet.OutletOffsetSize, 0d)
                || !CKBusbarBridge.DesignGeometry.BridgeOffsetRules.LengthsMatch(outlet.InletOffsetSize, 0d))) return false;
            var magnitude = inlet.InletOffsetSize + outlet.OutletOffsetSize;
            if (!CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsWidened(magnitude)) return false;
            if (inlet.InletOffsetDirection != outlet.InletOffsetDirection
                || inlet.OutletOffsetDirection != outlet.OutletOffsetDirection
                || inlet.InletOffsetDirection == inlet.OutletOffsetDirection) return false;
            signedOffset = (inlet.InletOffsetDirection == 0 ? 1d : -1d) * magnitude;
            aWidth = last.Width - 2d * outlet.OutletOffsetSize;
            bWidth = first.Width - 2d * inlet.InletOffsetSize;
            return aWidth > 0d && bWidth > 0d;
        }

        private static bool AnyInvalidWidenedMember(IReadOnlyList<Models.FaceToFaceFrameReadItem> frames)
        {
            for (var index = 0; index < frames.Count; index++)
                if (frames[index] == null || frames[index].IsLargeOffsetMember
                    || double.IsNaN(frames[index].Width) || double.IsInfinity(frames[index].Width) || frames[index].Width <= 0d
                    || (frames[index].SmallOffsetFrame != null && (frames[index].SmallOffsetFrame.InletOffsetSize < 0d
                        || frames[index].SmallOffsetFrame.OutletOffsetSize < 0d
                        || double.IsNaN(frames[index].SmallOffsetFrame.InletOffsetSize) || double.IsInfinity(frames[index].SmallOffsetFrame.InletOffsetSize)
                        || double.IsNaN(frames[index].SmallOffsetFrame.OutletOffsetSize) || double.IsInfinity(frames[index].SmallOffsetFrame.OutletOffsetSize)))
                    || !CKBusbarBridge.DesignGeometry.BridgeOffsetRules.LengthsMatch(frames[index].Width, frames[0].Width)
                    || (index > 0 && index < frames.Count - 1 && frames[index].IsSmallOffsetFrame)) return true;
            return false;
        }

        private static void ValidatePositiveFinite(string parameterName, double value)
        {
            ValidateFinite(parameterName, value);
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        private static void ValidateFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    internal sealed class FaceToFaceSmallOffsetFrameLayout
    {
        internal FaceToFaceSmallOffsetFrameLayout(
            IReadOnlyList<double> frameWidths,
            int offsetFrameIndex,
            FaceToFaceSmallOffsetFrameDrivePlan offsetFrame,
            IReadOnlyList<FaceToFaceSmallOffsetFrameDrivePlan> framePlans = null)
        {
            FrameWidths = frameWidths ?? throw new ArgumentNullException(nameof(frameWidths));
            OffsetFrameIndex = offsetFrameIndex;
            OffsetFrame = offsetFrame ?? throw new ArgumentNullException(nameof(offsetFrame));
            FramePlans = framePlans;
        }

        internal IReadOnlyList<double> FrameWidths { get; }

        internal int OffsetFrameIndex { get; }

        internal FaceToFaceSmallOffsetFrameDrivePlan OffsetFrame { get; }

        private IReadOnlyList<FaceToFaceSmallOffsetFrameDrivePlan> FramePlans { get; }
        internal bool IsWidened => FramePlans != null;
        internal FaceToFaceSmallOffsetFrameDrivePlan GetOffsetFrame(int index)
            => FramePlans != null ? FramePlans[index] : index == OffsetFrameIndex ? OffsetFrame : null;
    }

    internal sealed class FaceToFaceSmallOffsetFrameDrivePlan
    {
        internal FaceToFaceSmallOffsetFrameDrivePlan(
            double frameWidth,
            int inletOffsetDirection,
            double inletOffsetSize,
            int outletOffsetDirection,
            double outletOffsetSize)
        {
            FrameWidth = frameWidth;
            InletOffsetDirection = inletOffsetDirection;
            InletOffsetSize = inletOffsetSize;
            OutletOffsetDirection = outletOffsetDirection;
            OutletOffsetSize = outletOffsetSize;
        }

        internal double FrameWidth { get; }

        internal int InletOffsetDirection { get; }

        internal double InletOffsetSize { get; }

        internal int OutletOffsetDirection { get; }

        internal double OutletOffsetSize { get; }

        // 绕竖直轴转半周，交换端口并反转横向。该变换也用于从桥向换回模板局部。
        internal FaceToFaceSmallOffsetFrameDrivePlan InInstallationOrder(bool reversed) => reversed
            ? new FaceToFaceSmallOffsetFrameDrivePlan(FrameWidth,
                1 - OutletOffsetDirection, OutletOffsetSize, 1 - InletOffsetDirection, InletOffsetSize)
            : this;
    }

    /// <summary>
    /// 根据小偏移孔参数生成四组角钢封板的确定性压缩和驱动计划。
    /// </summary>
    internal static class FaceToFaceSmallOffsetSealPlatePlanner
    {
        private const string InletPlateComponentPrefix = "8GX.017.236";
        private const string OutletPlateComponentPrefix = "8GX.017.237";
        private const string InletOffsetVariableName = "进孔偏移尺寸";
        private const string OutletOffsetVariableName = "出孔偏移尺寸";

        internal static FaceToFaceSmallOffsetSealPlateDrivePlan CreatePlan(
            FaceToFaceSmallOffsetFrameDrivePlan offsetFrame,
            double frameHeight)
        {
            if (offsetFrame == null)
            {
                throw new ArgumentNullException(nameof(offsetFrame));
            }

            ValidatePositiveFinite(nameof(frameHeight), frameHeight);
            ValidatePositiveFinite(nameof(offsetFrame.FrameWidth), offsetFrame.FrameWidth);
            ValidateNonNegativeFinite(nameof(offsetFrame.InletOffsetSize), offsetFrame.InletOffsetSize);
            ValidateNonNegativeFinite(nameof(offsetFrame.OutletOffsetSize), offsetFrame.OutletOffsetSize);

            var isRightOffset = offsetFrame.InletOffsetDirection == 0
                && offsetFrame.OutletOffsetDirection == 1;
            var isLeftOffset = offsetFrame.InletOffsetDirection == 1
                && offsetFrame.OutletOffsetDirection == 0;
            if (!isRightOffset && !isLeftOffset)
            {
                throw new ArgumentException(
                    "小偏移基架的进出孔偏移方向必须互为镜像（0/1 或 1/0）。",
                    nameof(offsetFrame));
            }

            // 现有孔偏移尺寸是柜侧宽度差的一半：
            // 柜侧基座宽 = 偏移基架宽 - 2×孔偏移尺寸，
            // 因而封板宽度严格按“偏移基架宽 - 柜侧基座宽”恢复。
            var inletSideBaseWidth = offsetFrame.FrameWidth
                - (offsetFrame.InletOffsetSize * 2d);
            var outletSideBaseWidth = offsetFrame.FrameWidth
                - (offsetFrame.OutletOffsetSize * 2d);
            ValidatePositiveFinite(nameof(inletSideBaseWidth), inletSideBaseWidth);
            ValidatePositiveFinite(nameof(outletSideBaseWidth), outletSideBaseWidth);
            var inletPlateWidth = Normalize(offsetFrame.FrameWidth - inletSideBaseWidth);
            var outletPlateWidth = Normalize(offsetFrame.FrameWidth - outletSideBaseWidth);

            // 封板实体宽=柜体偏移值-2；孔变量仍为柜体偏移值的一半。
            if (frameHeight <= 2d)
                throw new ArgumentException("小偏移封板要求基架高度大于2mm。");
            var folders = isRightOffset
                ? new[]
                {
                    CreateInletFolder("进左封板角钢", true, inletPlateWidth),
                    CreateInletFolder("进右封板角钢", inletPlateWidth <= 2d, inletPlateWidth),
                    CreateOutletFolder("出左封板角钢", outletPlateWidth <= 2d, outletPlateWidth),
                    CreateOutletFolder("出右封板角钢", true, outletPlateWidth)
                }
                : new[]
                {
                    CreateInletFolder("进左封板角钢", inletPlateWidth <= 2d, inletPlateWidth),
                    CreateInletFolder("进右封板角钢", true, inletPlateWidth),
                    CreateOutletFolder("出左封板角钢", true, outletPlateWidth),
                    CreateOutletFolder("出右封板角钢", outletPlateWidth <= 2d, outletPlateWidth)
                };

            // 只驱动封板所在侧的角钢；未参与偏移的型号及另一端实例保留原配置。
            var angles = new List<FaceToFaceSmallOffsetAnglePlan>();
            var prefix = isRightOffset ? "8GX.160.001" : "8GX.160.003";
            AddAngle(angles, prefix, -1, inletPlateWidth);
            AddAngle(angles, prefix, 1, outletPlateWidth);
            return new FaceToFaceSmallOffsetSealPlateDrivePlan(
                Normalize(frameHeight), folders, angles);
        }

        private static void AddAngle(List<FaceToFaceSmallOffsetAnglePlan> angles,
            string prefix, int end, double offset)
        {
            if (offset <= 2d) return;
            var width = offset > 40d && offset <= 70d ? offset : 40d;
            // D=40时压缩豁口，不能写入零宽度。另一型号的豁口不随此值重置。
            angles.Add(new FaceToFaceSmallOffsetAnglePlan(prefix, end, width,
                offset >= 40d, offset < 40d ? 40d - offset : (double?)null));
        }

        private static FaceToFaceSmallOffsetSealPlateFolderPlan CreateInletFolder(
            string folderName,
            bool suppress,
            double plateWidth)
        {
            return new FaceToFaceSmallOffsetSealPlateFolderPlan(
                folderName,
                suppress,
                InletPlateComponentPrefix,
                InletOffsetVariableName,
                plateWidth);
        }

        private static FaceToFaceSmallOffsetSealPlateFolderPlan CreateOutletFolder(
            string folderName,
            bool suppress,
            double plateWidth)
        {
            return new FaceToFaceSmallOffsetSealPlateFolderPlan(
                folderName,
                suppress,
                OutletPlateComponentPrefix,
                OutletOffsetVariableName,
                plateWidth);
        }

        private static double Normalize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        private static void ValidatePositiveFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "参数必须为有限正数。");
            }
        }

        private static void ValidateNonNegativeFinite(string parameterName, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    "参数必须为有限非负数。");
            }
        }
    }

    internal sealed class FaceToFaceSmallOffsetSealPlateDrivePlan
    {
        internal FaceToFaceSmallOffsetSealPlateDrivePlan(
            double frameHeight,
            IReadOnlyList<FaceToFaceSmallOffsetSealPlateFolderPlan> folders,
            IReadOnlyList<FaceToFaceSmallOffsetAnglePlan> angles)
        {
            FrameHeight = frameHeight;
            Folders = folders ?? throw new ArgumentNullException(nameof(folders));
            Angles = angles ?? throw new ArgumentNullException(nameof(angles));
        }

        internal double FrameHeight { get; }

        internal IReadOnlyList<FaceToFaceSmallOffsetSealPlateFolderPlan> Folders { get; }
        internal IReadOnlyList<FaceToFaceSmallOffsetAnglePlan> Angles { get; }
    }

    internal sealed class FaceToFaceSmallOffsetAnglePlan
    {
        internal FaceToFaceSmallOffsetAnglePlan(string componentPrefix, int end, double width,
            bool suppressNotch, double? notchWidth)
        {
            ComponentPrefix = componentPrefix;
            End = end;
            Width = width;
            SuppressNotch = suppressNotch;
            NotchWidth = notchWidth;
        }
        internal string ComponentPrefix { get; }
        internal int End { get; }
        internal double Width { get; }
        internal bool SuppressNotch { get; }
        internal double? NotchWidth { get; }
    }

    internal sealed class FaceToFaceSmallOffsetCoverPlatePlan
    {
        internal FaceToFaceSmallOffsetCoverPlatePlan(string componentPrefix, int instanceCount,
            IReadOnlyDictionary<string, double> variables, IReadOnlyDictionary<string, string> equations,
            IReadOnlyDictionary<string, double> dimensions)
        {
            foreach (var pair in dimensions)
                if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value) || pair.Value <= 0d)
                    throw new ArgumentOutOfRangeException(nameof(dimensions), "小偏移盖板目标尺寸必须为有效正数：" + componentPrefix + "/" + pair.Key);
            ComponentPrefix = componentPrefix;
            InstanceCount = instanceCount;
            Variables = variables;
            Equations = equations;
            Dimensions = dimensions;
        }

        internal string ComponentPrefix { get; }
        internal int InstanceCount { get; }
        internal IReadOnlyDictionary<string, double> Variables { get; }
        internal IReadOnlyDictionary<string, string> Equations { get; }
        internal IReadOnlyDictionary<string, double> Dimensions { get; }

        internal bool Matches(Func<string, double> read)
        {
            foreach (var pair in Dimensions)
                if (!CKBusbarBridge.DesignGeometry.BridgeOffsetRules.LengthsMatch(read(pair.Key), pair.Value)) return false;
            return true;
        }

        internal void RequireMatches(Func<string, double> read)
        {
            foreach (var pair in Dimensions)
                {
                    var actual = read(pair.Key);
                    if (!CKBusbarBridge.DesignGeometry.BridgeOffsetRules.LengthsMatch(actual, pair.Value))
                        throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "小偏移盖板尺寸校验失败：{0}/{1}；目标={2:0.###} mm；实际={3:0.###} mm。",
                            ComponentPrefix, pair.Key, pair.Value, actual));
                }
        }

        internal bool CorrectIfNeeded(Func<string, double> read, Action correct)
        {
            if (Matches(read)) return false;
            correct();
            RequireMatches(read);
            return true;
        }
    }

    internal sealed class FaceToFaceSmallOffsetSealPlateFolderPlan
    {
        internal FaceToFaceSmallOffsetSealPlateFolderPlan(
            string folderName,
            bool suppress,
            string plateComponentPrefix,
            string offsetVariableName,
            double plateWidth)
        {
            FolderName = folderName ?? throw new ArgumentNullException(nameof(folderName));
            Suppress = suppress;
            PlateComponentPrefix = plateComponentPrefix
                ?? throw new ArgumentNullException(nameof(plateComponentPrefix));
            OffsetVariableName = offsetVariableName
                ?? throw new ArgumentNullException(nameof(offsetVariableName));
            PlateWidth = plateWidth;
        }

        internal string FolderName { get; }

        internal bool Suppress { get; }
        // 有效侧始终保留封板；附加167角钢仅在该侧柜体偏移值>70时启用。
        internal bool SuppressAngleSteel => Suppress || PlateWidth <= 70d;

        internal string PlateComponentPrefix { get; }

        internal string OffsetVariableName { get; }

        internal double PlateWidth { get; }
    }
}
