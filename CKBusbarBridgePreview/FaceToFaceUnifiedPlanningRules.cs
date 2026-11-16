using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CKBusbarBridgePreview
{
    internal sealed class SmallOffsetClearanceException : InvalidOperationException
    {
        internal double MinimumDistance { get; }
        internal SmallOffsetClearanceException(string message, double minimumDistance) : base(message)
        { MinimumDistance = minimumDistance; }
    }
    public sealed class FaceToFaceUnifiedPlanningInput
    {
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
        public double ACabinetBaseWidth { get; set; }
        public double BCabinetBaseWidth { get; set; }
        public double PhaseSpacing { get; set; }
        public double BusbarWidth { get; set; }
        public double BusbarThickness { get; set; }
        public bool RequiresPhaseChange { get; set; }
        public bool DeferOffsetLengthLimitToPhysicalGeometry { get; set; }
        // 旧输入兼容字段，已不参与工艺选择或方案指纹。
        public bool UseMilledBarForLargeSmallOffset { get; set; }
        public double PhaseChangeInsulationClearance { get; set; } = 125d;
    }

    public sealed class FaceToFaceUnifiedPlanningResult
    {
        internal FaceToFaceUnifiedPlanningResult(
            MiddleBusbarPathPlan middlePath,
            bool isLargeOffset,
            FaceToFaceSmallOffsetTransitionKind_e? smallOffsetTransitionKind,
            double? rawSpecialFrameMinimumLength,
            double? initialSpecialFrameLength,
            string inputFingerprint,
            IReadOnlyList<PlanGeometryDiagnostic> diagnostics)
        {
            MiddlePath = middlePath;
            IsLargeOffset = isLargeOffset;
            SmallOffsetTransitionKind = smallOffsetTransitionKind;
            RawSpecialFrameMinimumLength = rawSpecialFrameMinimumLength;
            InitialSpecialFrameLength = initialSpecialFrameLength;
            InputFingerprint = inputFingerprint;
            AlgorithmVersion = FaceToFaceUnifiedPlanner.AlgorithmVersion;
            Diagnostics = diagnostics;
        }

        public MiddleBusbarPathPlan MiddlePath { get; }
        public bool HasOffset => RawSpecialFrameMinimumLength.HasValue;
        public bool IsLargeOffset { get; }
        public FaceToFaceSmallOffsetTransitionKind_e?
            SmallOffsetTransitionKind { get; }
        public double? RawSpecialFrameMinimumLength { get; }
        public double? InitialSpecialFrameLength { get; }
        public string InputFingerprint { get; }
        public string AlgorithmVersion { get; }
        public IReadOnlyList<PlanGeometryDiagnostic> Diagnostics { get; }
        public bool HasErrors => Diagnostics.Any(item =>
            item.Severity == PlanGeometryDiagnosticSeverity.Error);
    }

    /// <summary>
    /// 面对面固定接口的唯一铜排规划入口。预览和正式生成只适配输入，
    /// 不再各自判定偏移类型、工艺、特殊基架尺寸或铜排分段。
    /// </summary>
    public static class FaceToFaceUnifiedPlanner
    {
        public const string AlgorithmVersion = "FaceToFaceUnified-R7-ThreeOffsetRanges";
        private const double Tolerance = .001d;

        public static FaceToFaceUnifiedPlanningResult Create(
            FaceToFaceUnifiedPlanningInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            ValidatePositiveFinite(input.PhaseSpacing, nameof(input.PhaseSpacing));
            ValidatePositiveFinite(input.BusbarWidth, nameof(input.BusbarWidth));
            ValidatePositiveFinite(input.BusbarThickness, nameof(input.BusbarThickness));
            var signedOffset = input.EndY - input.StartY;
            var absoluteOffset = Math.Abs(signedOffset);
            var hasOffset = absoluteOffset > Tolerance;
            var isLargeOffset = hasOffset && CKBusbarBridge.DesignGeometry.BridgeOffsetRules.IsLarge(
                signedOffset, input.ACabinetBaseWidth, input.BCabinetBaseWidth);
            FaceToFaceSmallOffsetTransitionKind_e? transitionKind = null;
            double? rawMinimum = null;
            double? initialLength = null;
            if (hasOffset)
            {
                rawMinimum = CalculateOffsetFrameMinimum(absoluteOffset, input.PhaseSpacing,
                    input.BusbarWidth, input.BusbarThickness,
                    input.PhaseChangeInsulationClearance, out transitionKind, isLargeOffset);
                initialLength = FaceToFaceUnifiedPlanningRules
                    .RoundInitialSpecialFrameLength(rawMinimum.Value);
                FaceToFaceUnifiedPlanningRules.ValidateSpecialFrameLength(
                    initialLength.Value,
                    rawMinimum.Value,
                    nameof(initialLength));
            }

            var middlePath = MiddleBusbarPathPlanner.Create(
                new MiddleBusbarPathPlanningInput
                {
                    StartX = input.StartX,
                    StartY = input.StartY,
                    EndX = input.EndX,
                    EndY = input.EndY,
                    OverlapLength = input.BusbarWidth,
                    PreferredBreakpointSpacing = 2000d,
                    MaximumDevelopedLength = 3000d,
                    DeferOffsetLengthLimitToPhysicalGeometry = input.DeferOffsetLengthLimitToPhysicalGeometry,
                    MinimumOffsetTransitionSpan = initialLength ?? 0d,
                    OffsetProtectionLength = hasOffset
                        ? input.BusbarWidth
                        : 0d,
                    UseOrthogonalOffsetTransition = isLargeOffset,
                    PhaseChangeInsulationClearance =
                        input.PhaseChangeInsulationClearance,
                    RequiresPhaseChange = input.RequiresPhaseChange
                });
            var diagnostics = PlanGeometryValidator.ValidatePath(
                middlePath,
                3000d,
                input.PhaseChangeInsulationClearance,
                input.RequiresPhaseChange);
            PlanGeometryValidator.ThrowIfErrors(diagnostics);
            return new FaceToFaceUnifiedPlanningResult(
                middlePath,
                isLargeOffset,
                transitionKind,
                rawMinimum,
                initialLength,
                CreateInputFingerprint(input),
                diagnostics);
        }

        /// <summary>编辑铜排后按当前工艺重新确定特殊基架尺寸，断点之间多出的直段由普通基架承托。</summary>
        public static MiddleBusbarPathPlan ReplanOffsetSupportRange(MiddleBusbarPathPlan path,
            double phaseSpacing, double busbarWidth, double busbarThickness,
            double insulationClearance = FaceToFaceUnifiedPlanningRules.StructureClearance)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var offset = path.Segments.SingleOrDefault(s => (s.Role & MiddleBusbarSegmentRole.Offset) != 0);
            if (offset == null) return path;
            var minimumSpan = CalculateMinimumOffsetCopperSpan(Math.Abs(offset.End.Y - offset.Start.Y),
                phaseSpacing, busbarWidth, busbarThickness, insulationClearance, path.IsLargeOffset == true);
            var length = minimumSpan - 2d * busbarWidth;
            if (offset.End.X - offset.Start.X < minimumSpan - Tolerance)
                throw new InvalidOperationException("铜排偏移段不足以容纳按工艺计算的偏移基架及两侧保护长度。");
            // 正式过渡模板的错开点/转弯点以该段中点为中心布置，基架跟随此中心。
            var center = (offset.Start.X + offset.End.X) / 2d;
            return new MiddleBusbarPathPlan(path.Points, path.Segments,
                center - length / 2d, center + length / 2d, path.DeferOffsetLengthLimitToPhysicalGeometry, path.IsLargeOffset);
        }

        /// <summary>段长编辑只移动已核验的工艺区，保留其跨度及原过渡工艺参数。</summary>
        public static MiddleBusbarPathPlan RecenterOffsetSafetyRange(MiddleBusbarPathPlan path, double safetyLength)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            ValidatePositiveFinite(safetyLength, nameof(safetyLength));
            var offset = path.Segments.Single(s => (s.Role & MiddleBusbarSegmentRole.Offset) != 0);
            var center = (offset.Start.X + offset.End.X) / 2d;
            return new MiddleBusbarPathPlan(path.Points, path.Segments, center - safetyLength / 2d,
                center + safetyLength / 2d, path.DeferOffsetLengthLimitToPhysicalGeometry, path.IsLargeOffset);
        }

        /// <summary>偏移段断点间的最小桥向跨度，不含搭接长度；反读编辑与首次规划共用工艺下限。</summary>
        [Obsolete("请改用 CKBusbarBridgePreview.FaceToFaceUnifiedPlanner.CalculateMinimumOffsetCopperSpan(..., bool isLargeOffset)，显式传入按实际连接基座宽度判定的结构。旧入口仅兼容小偏移尺寸计算。")]
        public static double CalculateMinimumOffsetCopperSpan(double absoluteOffset,
            double phaseSpacing, double busbarWidth, double busbarThickness,
            double insulationClearance = FaceToFaceUnifiedPlanningRules.StructureClearance)
            => CalculateMinimumOffsetCopperSpan(absoluteOffset, phaseSpacing, busbarWidth, busbarThickness, insulationClearance, false);

        public static double CalculateMinimumOffsetCopperSpan(double absoluteOffset,
            double phaseSpacing, double busbarWidth, double busbarThickness,
            double insulationClearance, bool isLargeOffset)
        {
            ValidatePositiveFinite(absoluteOffset, nameof(absoluteOffset));
            var minimum = CalculateOffsetFrameMinimum(absoluteOffset, phaseSpacing,
                busbarWidth, busbarThickness, insulationClearance, out var transitionKind, isLargeOffset);
            var length = FaceToFaceUnifiedPlanningRules.RoundInitialSpecialFrameLength(minimum);
            FaceToFaceUnifiedPlanningRules.ValidateSpecialFrameLength(length, minimum, "偏移基架长度");
            return length + 2d * busbarWidth;
        }

        public static IReadOnlyList<PlanarBendBusbarSetting> CreateWithAutomaticOffsetExpansion(
            MiddleBusbarPathPlan initial, double width,
            Func<MiddleBusbarPathPlan, IReadOnlyList<PlanarBendBusbarSetting>> create,
            out MiddleBusbarPathPlan accepted)
        {
            return CreateWithAutomaticOffsetExpansion(initial, width, create, out accepted, null);
        }

        public static IReadOnlyList<PlanarBendBusbarSetting> CreateWithAutomaticOffsetExpansion(
            MiddleBusbarPathPlan initial, double width,
            Func<MiddleBusbarPathPlan, IReadOnlyList<PlanarBendBusbarSetting>> create,
            out MiddleBusbarPathPlan accepted, double? initialFrameMinimumLength)
        {
            accepted = initial;
            SmallOffsetClearanceException bestFailure;
            try { return create(initial); }
            catch (SmallOffsetClearanceException ex) { bestFailure = ex; }
            var offset = initial.Segments.SingleOrDefault(s => (s.Role & MiddleBusbarSegmentRole.Offset) != 0);
            if (offset == null || initial.IsLargeOffset == true)
                throw new InvalidOperationException(bestFailure.Message, bestFailure);
            // 只压缩邻接普通段；固定柜端、换相区及其它折弯区不得被吞入偏移区。
            var reserve = 2d * width + 100d;
            var left = offset.Index > 0 ? initial.Segments[offset.Index - 1] : null;
            var right = offset.Index + 1 < initial.Segments.Count ? initial.Segments[offset.Index + 1] : null;
            var leftCapacity = left?.Role == MiddleBusbarSegmentRole.Ordinary ? Math.Max(0d, left.End.X - left.Start.X - reserve) : 0d;
            var rightCapacity = right?.Role == MiddleBusbarSegmentRole.Ordinary ? Math.Max(0d, right.End.X - right.Start.X - reserve) : 0d;
            var frameLength = (initial.OffsetFrameEndX ?? offset.End.X) - (initial.OffsetFrameStartX ?? offset.Start.X);
            var frameMinimum = initialFrameMinimumLength ?? frameLength;
            FaceToFaceUnifiedPlanningRules.ValidateSpecialFrameLength(frameLength, frameMinimum, "偏移基架初始长度");
            var capacity = Math.Min(leftCapacity + rightCapacity,
                Math.Max(0d, FaceToFaceUnifiedPlanningRules.MaximumSpecialFrameLength - frameMinimum));
            for (var expansion = Math.Min(20d, capacity); expansion > .001d; expansion = Math.Min(expansion + 20d, capacity))
            {
                var leftGrowth = Math.Min(leftCapacity, expansion / 2d);
                var rightGrowth = Math.Min(rightCapacity, expansion - leftGrowth);
                leftGrowth = Math.Min(leftCapacity, expansion - rightGrowth);
                var coordinates = initial.Points.Select(p => p.X).ToArray();
                coordinates[offset.Index] -= leftGrowth;
                coordinates[offset.Index + 1] += rightGrowth;
                var moved = MiddleBusbarPathPlanner.MoveBreakpoints(initial, coordinates, width);
                var center = (coordinates[offset.Index] + coordinates[offset.Index + 1]) / 2d;
                // 未取整的原值包含K及两侧预留；过渡区增长计入K，最后按原公式取整并校验。
                var expandedMinimum = frameMinimum + leftGrowth + rightGrowth;
                var expandedFrameLength = FaceToFaceUnifiedPlanningRules.RoundInitialSpecialFrameLength(expandedMinimum);
                FaceToFaceUnifiedPlanningRules.ValidateSpecialFrameLength(
                    expandedFrameLength, expandedMinimum, "偏移基架初始长度");
                var candidate = new MiddleBusbarPathPlan(moved.Points, moved.Segments,
                    center - expandedFrameLength / 2d, center + expandedFrameLength / 2d,
                    moved.DeferOffsetLengthLimitToPhysicalGeometry, moved.IsLargeOffset);
                try
                {
                    var pieces = create(candidate);
                    accepted = candidate;
                    return pieces;
                }
                catch (SmallOffsetClearanceException ex)
                {
                    // 净距不足是本次搜索的预期结果；记录最佳候选后继续，其他异常直接上抛。
                    if (ex.MinimumDistance > bestFailure.MinimumDistance) bestFailure = ex;
                }
                if (expansion >= capacity - .001d) break;
            }
            throw new InvalidOperationException(bestFailure.Message + " 已自动扩大过渡区至可用边界，仍无法满足净距；请增加可用桥向跨度或相间距。", bestFailure);
        }

        private static double CalculateOffsetFrameMinimum(double absoluteOffset, double phaseSpacing,
            double busbarWidth, double busbarThickness, double insulationClearance,
            out FaceToFaceSmallOffsetTransitionKind_e? transitionKind, bool isLargeOffset)
        {
            ValidatePositiveFinite(phaseSpacing, nameof(phaseSpacing));
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            ValidatePositiveFinite(busbarThickness, nameof(busbarThickness));
            ValidatePositiveFinite(insulationClearance, nameof(insulationClearance));
            transitionKind = null;
            if (isLargeOffset)
                return FaceToFaceUnifiedPlanningRules.CalculateLargeOffsetMinimumSharedLength(
                    phaseSpacing, busbarWidth, insulationClearance);
            transitionKind = FaceToFaceModelBusbarPlanner.ResolveSmallOffsetTransitionKind(absoluteOffset, busbarWidth);
            return FaceToFaceUnifiedPlanningRules.CalculateSmallOffsetMinimumFrameLength(
                transitionKind.Value, phaseSpacing, busbarWidth, busbarThickness, absoluteOffset,
                insulationClearance: insulationClearance);
        }

        private static void ValidatePositiveFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    "统一规划输入必须为有限正数。");
            }
        }

        private static string CreateInputFingerprint(
            FaceToFaceUnifiedPlanningInput input)
        {
            var canonical = string.Join("|", new[]
            {
                input.StartX.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.StartY.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.EndX.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.EndY.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.ACabinetBaseWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.BCabinetBaseWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.PhaseSpacing.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.BusbarWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.BusbarThickness.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.RequiresPhaseChange ? "1" : "0",
                input.PhaseChangeInsulationClearance.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                input.DeferOffsetLengthLimitToPhysicalGeometry ? "PhysicalLengthValidation" : "PathLengthValidation"
            });
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                return BitConverter.ToString(bytes).Replace("-", string.Empty);
            }
        }
    }

    /// <summary>
    /// 面对面铜排先行规划所用的统一尺寸规则。返回的最小值保留计算精度；
    /// 只有创建特殊基架初始值时才向上取 10 mm 整数倍。
    /// </summary>
    public static class FaceToFaceUnifiedPlanningRules
    {
        public const double BaseAngleSteelWidth = 19.5d;
        public const double SmallOffsetAngleSteelWidth = 40d;
        public const double LargeOffsetAngleSteelWidth = 30d;
        public const double BaseClearance = 125d;
        public const double BaseCompensation = 5d;
        public const double SmallOffsetClearance = 125d;
        public const double SmallOffsetCompensation = 5d;
        public const double LargeOffsetClearance = 125d;
        public const double LargeOffsetCompensation = 5d;
        // 保留公共常量兼容既有调用；新规则按各自的工艺语义取值。
        public const double StructureClearance = 125d;
        public const double Compensation = 5d;
        public const double MilledPlateAdditionalLength = 20d;
        // 保留旧公开常量供二进制兼容；实际偏移分类只使用连接基座宽度计算。
        [Obsolete("偏移分界改为两端连接基座平均宽度加200 mm。请传递实际连接基座宽度。基架长度限制不受影响。")]
        public const double LargeOffsetEccentricityThreshold = 1400d;
        public const double MaximumSpecialFrameLength = 1400d;
        public const double MinimumLargeOffsetConnectorSpan = 200d;
        public const double InitialFrameLengthStep = 10d;

        public static double CalculateMinimumBaseWidth(
            double phaseSpacing,
            double busbarWidth,
            double insulationClearance = BaseClearance)
        {
            ValidatePositiveFinite(phaseSpacing, nameof(phaseSpacing));
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            ValidatePositiveFinite(insulationClearance, nameof(insulationClearance));
            return 2d * phaseSpacing + busbarWidth
                + 2d * (BaseAngleSteelWidth + insulationClearance + BaseCompensation);
        }

        public static double CalculateSmallOffsetMinimumFrameLength(
            FaceToFaceSmallOffsetTransitionKind_e transitionKind,
            double phaseSpacing,
            double busbarWidth,
            double busbarThickness,
            double absoluteWidthOffset,
            double? actualTransitionEnvelopeLength = null,
            double insulationClearance = SmallOffsetClearance)
        {
            ValidatePositiveFinite(phaseSpacing, nameof(phaseSpacing));
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            ValidatePositiveFinite(busbarThickness, nameof(busbarThickness));
            ValidatePositiveFinite(absoluteWidthOffset, nameof(absoluteWidthOffset));
            ValidatePositiveFinite(insulationClearance, nameof(insulationClearance));

            double transitionLength;
            switch (transitionKind)
            {
                case FaceToFaceSmallOffsetTransitionKind_e.MiddleParallelConnection:
                    transitionLength = 2d * phaseSpacing + busbarWidth;
                    break;
                case FaceToFaceSmallOffsetTransitionKind_e.MilledBar:
                    transitionLength = 3d *
                        (busbarWidth + MilledPlateAdditionalLength)
                        + 2d * insulationClearance;
                    break;
                case FaceToFaceSmallOffsetTransitionKind_e.VerticalBend:
                    if (actualTransitionEnvelopeLength.HasValue)
                    {
                        ValidatePositiveFinite(
                            actualTransitionEnvelopeLength.Value,
                            nameof(actualTransitionEnvelopeLength));
                        transitionLength = actualTransitionEnvelopeLength.Value;
                        break;
                    }

                    var bendSpan = 2d * busbarWidth;
                    if (absoluteWidthOffset >= bendSpan)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(absoluteWidthOffset),
                            absoluteWidthOffset,
                            "小偏移立弯的宽向偏移必须小于两个排宽。");
                    }

                    var nominalProjection = Math.Sqrt(
                        bendSpan * bendSpan
                        - absoluteWidthOffset * absoluteWidthOffset);
                    // 斜段净距取法向距离，不能用相间距减排宽代替。
                    // P*L-t*D >= (W+C)*sqrt(L²+D²)，t是相邻相沿桥向的错开量。
                    var requiredNormalSpacing = busbarWidth + insulationClearance;
                    var spacingSquareDifference = phaseSpacing * phaseSpacing -
                        requiredNormalSpacing * requiredNormalSpacing;
                    if (spacingSquareDifference <= .001d)
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                            "小偏移立弯相间距不足：实际相间距 {0:0.###} mm，排宽 {1:0.###} mm，绝缘距离 {2:0.###} mm；" +
                            "相间距必须大于排宽与绝缘距离之和 {3:0.###} mm，延长过渡段仍无法满足净距。",
                            phaseSpacing, busbarWidth, insulationClearance, requiredNormalSpacing));
                    var staggerProjection = busbarThickness * absoluteWidthOffset;
                    var clearanceProjection = (phaseSpacing * staggerProjection + requiredNormalSpacing *
                        Math.Sqrt(staggerProjection * staggerProjection + spacingSquareDifference *
                            absoluteWidthOffset * absoluteWidthOffset)) / spacingSquareDifference;
                    transitionLength = Math.Max(nominalProjection, clearanceProjection) + 2d * busbarThickness;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(transitionKind), transitionKind, "未知的小偏移过渡工艺。");
            }

            return transitionLength + 2d *
                (SmallOffsetAngleSteelWidth + insulationClearance + SmallOffsetCompensation);
        }

        public static double CalculateLargeOffsetMinimumSharedLength(
            double phaseSpacing,
            double busbarWidth,
            double insulationClearance = LargeOffsetClearance)
        {
            ValidatePositiveFinite(phaseSpacing, nameof(phaseSpacing));
            ValidatePositiveFinite(busbarWidth, nameof(busbarWidth));
            ValidatePositiveFinite(insulationClearance, nameof(insulationClearance));
            return 2d * phaseSpacing + busbarWidth
                + 2d * (LargeOffsetAngleSteelWidth
                    + insulationClearance + LargeOffsetCompensation);
        }

        public static double RoundInitialSpecialFrameLength(double minimumLength)
        {
            ValidatePositiveFinite(minimumLength, nameof(minimumLength));
            return Math.Ceiling(minimumLength / InitialFrameLengthStep)
                * InitialFrameLengthStep;
        }

        public static void ValidateSpecialFrameLength(
            double frameLength,
            double minimumLength,
            string parameterName)
        {
            ValidatePositiveFinite(frameLength, parameterName);
            ValidatePositiveFinite(minimumLength, nameof(minimumLength));
            if (frameLength + 0.001d < minimumLength)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    frameLength,
                    string.Format(
                        "特殊基架长度不能小于工艺计算值 {0:0.###} mm。",
                        minimumLength));
            }

            if (frameLength > MaximumSpecialFrameLength + 0.001d)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    frameLength,
                    string.Format(
                        "特殊基架长度不能超过 {0:0.###} mm。",
                        MaximumSpecialFrameLength));
            }
        }

        public static double CalculateLargeOffsetConnectorSpan(
            double signedEccentricity,
            double aSideBaseWidth,
            double bSideBaseWidth)
        {
            ValidateFinite(signedEccentricity, nameof(signedEccentricity));
            ValidatePositiveFinite(aSideBaseWidth, nameof(aSideBaseWidth));
            ValidatePositiveFinite(bSideBaseWidth, nameof(bSideBaseWidth));
            return Math.Abs(signedEccentricity)
                - (aSideBaseWidth + bSideBaseWidth) / 2d;
        }

        public static void ValidateLargeOffsetConnectorSpan(
            double signedEccentricity,
            double aSideBaseWidth,
            double bSideBaseWidth)
        {
            var connectorSpan = CalculateLargeOffsetConnectorSpan(
                signedEccentricity, aSideBaseWidth, bSideBaseWidth);
            if (connectorSpan < MinimumLargeOffsetConnectorSpan - 0.001d)
            {
                throw new InvalidOperationException(string.Format(
                    "当前已分类为大偏移，但该基座宽度组合的连接空间只有 {0:0.###} mm，"
                    + "不能小于 {1:0.###} mm。",
                    connectorSpan,
                    MinimumLargeOffsetConnectorSpan));
            }
        }

        private static void ValidatePositiveFinite(double value, string name)
        {
            ValidateFinite(value, name);
            if (value <= 0d)
            {
                throw new ArgumentOutOfRangeException(name, value, "参数必须为有限正数。");
            }
        }

        private static void ValidateFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value, "参数必须为有限数值。");
            }
        }
    }
}
