using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.Models
{
    internal enum FrameInsulatorBracketSpanSource
    {
        FrameWidth = 0,
        FrameHeight = 1
    }

    /// <summary>
    /// 基架中绝缘子的公共驱动参数。默认沿基架宽度布置；
    /// 侧对侧基座1沿基架高度布置。公共驱动器统一计算“支架长度 = 所在方向尺寸 - 20”。
    /// </summary>
    internal sealed class FaceToFaceFrameInsulatorParameters
    {
        private const double LBracketInsulatorHeightOffset = 6d;

        internal FaceToFaceFrameInsulatorParameters(
            string busbarSpec,
            double phaseSpacing,
            double insulatorHeight,
            int singlePhaseRootCount,
            FrameInsulatorBracketSpanSource bracketSpanSource =
                FrameInsulatorBracketSpanSource.FrameWidth,
            IReadOnlyList<double> transverseOffsets = null, IReadOnlyList<BridgeInsulatorMountPlan> mounts = null)
        {
            if (string.IsNullOrWhiteSpace(busbarSpec))
            {
                throw new ArgumentException("铜排规格不能为空。", nameof(busbarSpec));
            }

            if (!FaceToFaceBusbarCenterDistanceHelper.TryParseBusbarThickness(
                busbarSpec,
                out var busbarThickness))
            {
                throw new ArgumentException(
                    $"无法从铜排规格中解析排厚：{busbarSpec}",
                    nameof(busbarSpec));
            }

            ValidatePositiveFinite(nameof(phaseSpacing), phaseSpacing);
            ValidatePositiveFinite(nameof(insulatorHeight), insulatorHeight);
            if (singlePhaseRootCount < 1 || singlePhaseRootCount > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(singlePhaseRootCount),
                    singlePhaseRootCount,
                    "单相排数必须为 1～4。");
            }

            BusbarSpec = busbarSpec.Trim();
            BusbarThickness = Normalize(busbarThickness);
            PhaseSpacing = Normalize(phaseSpacing);
            InsulatorHeight = Normalize(insulatorHeight);
            SinglePhaseRootCount = singlePhaseRootCount;
            BracketSpanSource = bracketSpanSource;
            if (transverseOffsets != null && (transverseOffsets.Count < 1 || transverseOffsets.Count > 3 ||
                transverseOffsets.Any(v => double.IsNaN(v) || double.IsInfinity(v))))
                throw new ArgumentException("可偏移绝缘子需要每个安装位的有限横移量。", nameof(transverseOffsets));
            TransverseOffsets = transverseOffsets == null ? null : Array.AsReadOnly(transverseOffsets.Select(Normalize).ToArray());
            Mounts = mounts == null ? null : Array.AsReadOnly(mounts.ToArray());
        }

        internal string BusbarSpec { get; }

        internal double BusbarThickness { get; }

        internal double PhaseSpacing { get; }

        internal double InsulatorHeight { get; }

        internal int SinglePhaseRootCount { get; }

        internal FrameInsulatorBracketSpanSource BracketSpanSource { get; }
        internal IReadOnlyList<double> TransverseOffsets { get; }
        internal IReadOnlyList<BridgeInsulatorMountPlan> Mounts { get; }

        internal bool HasSameParameters(FaceToFaceFrameInsulatorParameters other)
        {
            return other != null && BusbarSpec == other.BusbarSpec &&
                FaceToFaceFramePhysicalIdentity.AreEqual(BusbarThickness, other.BusbarThickness) &&
                FaceToFaceFramePhysicalIdentity.AreEqual(PhaseSpacing, other.PhaseSpacing) &&
                FaceToFaceFramePhysicalIdentity.AreEqual(InsulatorHeight, other.InsulatorHeight) &&
                SinglePhaseRootCount == other.SinglePhaseRootCount && BracketSpanSource == other.BracketSpanSource &&
                (TransverseOffsets == null ? other.TransverseOffsets == null : other.TransverseOffsets != null &&
                    TransverseOffsets.SequenceEqual(other.TransverseOffsets)) &&
                (Mounts == null ? other.Mounts == null : other.Mounts != null &&
                    Mounts.Select(m => m?.Identity).SequenceEqual(other.Mounts.Select(m => m?.Identity)));
        }

        internal FaceToFaceFrameInsulatorDrivePlan CreateDrivePlan(
            FaceToFaceFrameModelPlan framePlan)
        {
            if (framePlan == null)
            {
                throw new ArgumentNullException(nameof(framePlan));
            }

            // 新界面逐架使用最终支撑参数；旧入口未传逐架参数时仍使用公共参数。
            if (framePlan.InternalInsulators != null && !ReferenceEquals(this, framePlan.InternalInsulators))
                return framePlan.InternalInsulators.CreateDrivePlan(framePlan);

            var bracketHostSpan = BracketSpanSource
                == FrameInsulatorBracketSpanSource.FrameHeight
                    ? framePlan.Height
                    : framePlan.Width;
            ValidatePositiveFinite(nameof(bracketHostSpan), bracketHostSpan);
            var normalizedBracketHostSpan = Normalize(bracketHostSpan);
            if (normalizedBracketHostSpan <= 20d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bracketHostSpan),
                    bracketHostSpan,
                    "基架绝缘子支架所在方向的尺寸必须大于 20 mm。");
            }

            var useLBracketTemplate =
                FaceToFaceFrameTemplateRules.IsFaceToFaceLargeOffsetCorner(
                    framePlan.FrameTemplateKind);
            if (TransverseOffsets != null && TransverseOffsets.Count != framePlan.InsulatorCount)
                throw InstallationPlanError(framePlan, "普通落地横移计划数量与基架安装位数量不一致");
            if (Mounts != null && Mounts.Count != framePlan.InsulatorCount)
                throw InstallationPlanError(framePlan, "逐相落地/吊装计划数量与基架安装位数量不一致");
            if (useLBracketTemplate && TransverseOffsets != null)
                throw InstallationPlanError(framePlan, "L型支架模板不支持普通落地横移计划");
            if (useLBracketTemplate && Mounts != null)
                throw InstallationPlanError(framePlan, "L型支架模板不支持逐相落地/吊装计划");
            // 逐相安装位由MountPlan完整决定横向位置，普通落地位才使用公共横移。
            // 忽略逐相位的冗余横移，保证反读后指纹和物理模型身份保持一致。
            var offsets = TransverseOffsets;
            if (offsets != null && Mounts != null)
                offsets = Mounts.All(m => m != null) ? null :
                    Mounts.Select((mount, index) => mount == null ? TransverseOffsets[index] : 0d).ToArray();
            var plannedInsulatorHeight = Normalize(
                InsulatorHeight
                - (useLBracketTemplate ? LBracketInsulatorHeightOffset : 0d));
            ValidatePositiveFinite(nameof(plannedInsulatorHeight), plannedInsulatorHeight);

            return new FaceToFaceFrameInsulatorDrivePlan(
                BusbarSpec,
                BusbarThickness,
                PhaseSpacing,
                plannedInsulatorHeight,
                // 公共绝缘子驱动器内部统一减去 20；此处传支架所在方向的原始尺寸。
                normalizedBracketHostSpan,
                SinglePhaseRootCount,
                framePlan.FrameTemplateKind, offsets, Mounts);
        }

        private InvalidOperationException InstallationPlanError(FaceToFaceFrameModelPlan frame, string reason)
        {
            return new InvalidOperationException(reason + "：基架=" + frame.SourceName +
                "；模板=" + frame.TemplateIdentity + "；安装位数量=" + frame.InsulatorCount +
                "；逐相安装计划数量=" + (Mounts?.Count ?? 0) +
                "；横移计划数量=" + (TransverseOffsets?.Count ?? 0) + "；横移量(mm)=" +
                (TransverseOffsets == null ? "无" : string.Join(",", TransverseOffsets.Select(value =>
                    value.ToString("0.###", CultureInfo.InvariantCulture)))) + "。");
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
    }

    internal sealed class FaceToFaceFrameInsulatorDrivePlan
    {
        internal FaceToFaceFrameInsulatorDrivePlan(
            string busbarSpec,
            double busbarThickness,
            double phaseSpacing,
            double insulatorHeight,
            double bracketHostSpan,
            int singlePhaseRootCount,
            FaceToWallFrameTemplateKind frameTemplateKind =
                FaceToWallFrameTemplateKind.Regular,
            IReadOnlyList<double> transverseOffsets = null, IReadOnlyList<BridgeInsulatorMountPlan> mounts = null)
        {
            BusbarSpec = string.IsNullOrWhiteSpace(busbarSpec)
                ? throw new ArgumentException("铜排规格不能为空。", nameof(busbarSpec))
                : busbarSpec.Trim();
            BusbarThickness = busbarThickness;
            PhaseSpacing = phaseSpacing;
            InsulatorHeight = insulatorHeight;
            BracketHostSpan = bracketHostSpan;
            SinglePhaseRootCount = singlePhaseRootCount;
            FrameTemplateKind = frameTemplateKind;
            TransverseOffsets = transverseOffsets == null ? null : Array.AsReadOnly(transverseOffsets.ToArray());
            Mounts = mounts == null ? null : Array.AsReadOnly(mounts.ToArray());
            CacheKey = CKBusbarBridge.Prototype.ThreePadInsulatorIdentity.DirectoryName(
                busbarThickness,
                phaseSpacing,
                insulatorHeight,
                bracketHostSpan,
                singlePhaseRootCount)
                + (UseLBracketTemplate ? "-L" : string.Empty)
                + (UseOffsetTemplate ? "-P-" + string.Join("_", TransverseOffsets.Select(v =>
                    v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) : string.Empty)
                + (Mounts == null ? "" : "-H-" + MountIdentity(Mounts));
        }

        private static string MountIdentity(IReadOnlyList<BridgeInsulatorMountPlan> mounts)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    string.Join("/", mounts.Select(m => m?.Identity ?? "floor"))))).Replace("-", "").Substring(0, 12);
        }

        internal string BusbarSpec { get; }

        internal double BusbarThickness { get; }

        internal double PhaseSpacing { get; }

        internal double InsulatorHeight { get; }

        internal double BracketHostSpan { get; }

        internal int SinglePhaseRootCount { get; }

        internal FaceToWallFrameTemplateKind FrameTemplateKind { get; }

        internal bool UseLBracketTemplate =>
            FaceToFaceFrameTemplateRules.IsFaceToFaceLargeOffsetCorner(FrameTemplateKind);

        internal string CacheKey { get; }
        internal bool UseOffsetTemplate => TransverseOffsets != null;
        internal IReadOnlyList<double> TransverseOffsets { get; }
        internal IReadOnlyList<BridgeInsulatorMountPlan> Mounts { get; }
    }

    internal sealed class FaceToFaceFramePlanInput
    {
        internal FaceToFaceFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed)
            : this(
                sourceName,
                width,
                length,
                height,
                isReversed,
                0,
                Array.Empty<double>())
        {
        }

        internal FaceToFaceFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed,
            int insulatorCount,
            IEnumerable<double> insulatorPositions)
            : this(
                sourceName,
                width,
                length,
                height,
                isReversed,
                insulatorCount,
                insulatorPositions,
                null)
        {
        }

        internal FaceToFaceFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed,
            int insulatorCount,
            IEnumerable<double> insulatorPositions,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame)
            : this(
                sourceName,
                width,
                length,
                height,
                isReversed,
                insulatorCount,
                insulatorPositions,
                FaceToWallFrameTemplateKind.Regular,
                isReversed ? "坐标系-进2" : "坐标系-进1",
                isReversed ? "坐标系-出2" : "坐标系-出1",
                smallOffsetFrame)
        {
        }

        /// <summary>
        /// 创建带显式模板身份和装配坐标系的面对面物理基架输入。
        /// 大偏移必须使用该入口，避免再从反装状态推导转弯路由。
        /// </summary>
        internal FaceToFaceFramePlanInput(
            string sourceName,
            double width,
            double length,
            double height,
            bool isReversed,
            int insulatorCount,
            IEnumerable<double> insulatorPositions,
            FaceToWallFrameTemplateKind frameTemplateKind,
            string inletCoordinateName,
            string outletCoordinateName,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame = null,
            FrameTemplateIdentity? templateIdentity = null)
        {
            SourceName = sourceName;
            Width = width;
            Length = length;
            Height = height;
            IsReversed = frameTemplateKind != FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner && isReversed;
            InsulatorCount = insulatorCount;
            InsulatorPositions = (insulatorPositions ?? Array.Empty<double>()).ToArray();
            FrameTemplateKind = frameTemplateKind;
            InletCoordinateName = inletCoordinateName;
            OutletCoordinateName = outletCoordinateName;
            SmallOffsetFrame = smallOffsetFrame;
            TemplateIdentity = templateIdentity
                ?? FrameTemplateIdentityMetadata.FromLegacyStructure(
                    frameTemplateKind,
                    smallOffsetFrame != null);
        }

        internal string SourceName { get; }

        internal double Width { get; }

        internal double Length { get; }

        internal double Height { get; }

        internal bool IsReversed { get; }

        internal int InsulatorCount { get; }

        internal IReadOnlyList<double> InsulatorPositions { get; }

        /// <summary>
        /// 物理模型使用的模板身份；028 与常规 024 必须严格区分。
        /// </summary>
        internal FaceToWallFrameTemplateKind FrameTemplateKind { get; }

        internal FrameTemplateIdentity TemplateIdentity { get; }


        internal string InletCoordinateName { get; }

        internal string OutletCoordinateName { get; }

        internal FaceToFaceSmallOffsetFrameDrivePlan SmallOffsetFrame { get; }
    }

    internal sealed class FaceToFaceFrameModelPlan
    {
        internal FaceToFaceFrameModelPlan(
            string sourceName,
            string folderName,
            double width,
            double length,
            double height,
            bool isTemplateDefault,
            IReadOnlyDictionary<string, object> globalVariables)
            : this(
                sourceName,
                folderName,
                width,
                length,
                height,
                0,
                Array.Empty<double>(),
                isTemplateDefault,
                globalVariables,
                FaceToWallFrameTemplateKind.Regular)
        {
        }

        internal FaceToFaceFrameModelPlan(
            string sourceName,
            string folderName,
            double width,
            double length,
            double height,
            int insulatorCount,
            IEnumerable<double> insulatorPositions,
            bool isTemplateDefault,
            IReadOnlyDictionary<string, object> globalVariables,
            bool useLargeFrameTemplate = false)
            : this(
                sourceName,
                folderName,
                width,
                length,
                height,
                insulatorCount,
                insulatorPositions,
                isTemplateDefault,
                globalVariables,
                useLargeFrameTemplate
                    ? FaceToWallFrameTemplateKind.Large1
                    : FaceToWallFrameTemplateKind.Regular)
        {
        }

        internal FaceToFaceFrameModelPlan(
            string sourceName,
            string folderName,
            double width,
            double length,
            double height,
            int insulatorCount,
            IEnumerable<double> insulatorPositions,
            bool isTemplateDefault,
            IReadOnlyDictionary<string, object> globalVariables,
            FaceToWallFrameTemplateKind frameTemplateKind,
            bool isSmallOffsetFrame = false,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame = null,
            FrameTemplateIdentity? templateIdentity = null,
            double wallEntryDistance = 0d)
        {
            SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
            FolderName = folderName ?? throw new ArgumentNullException(nameof(folderName));
            Width = width;
            Length = length;
            Height = height;
            InsulatorCount = insulatorCount;
            InsulatorPositions = (insulatorPositions ?? Array.Empty<double>()).ToArray();
            IsTemplateDefault = isTemplateDefault;
            GlobalVariables = globalVariables
                ?? throw new ArgumentNullException(nameof(globalVariables));
            FrameTemplateKind = frameTemplateKind;
            SmallOffsetFrame = smallOffsetFrame;
            IsSmallOffsetFrame = isSmallOffsetFrame || smallOffsetFrame != null;
            TemplateIdentity = templateIdentity
                ?? FrameTemplateIdentityMetadata.FromLegacyStructure(
                    frameTemplateKind,
                    IsSmallOffsetFrame);
            SideCoverPlateWidth = ReadSideCoverPlateWidth(
                frameTemplateKind,
                GlobalVariables);
            WallEntryDistance = frameTemplateKind == FaceToWallFrameTemplateKind.Large1 ? wallEntryDistance : 0d;
        }

        internal string SourceName { get; }

        internal string FolderName { get; }

        internal double Width { get; }

        internal double Length { get; }

        internal double Height { get; }

        internal int InsulatorCount { get; }

        internal IReadOnlyList<double> InsulatorPositions { get; }

        internal bool IsTemplateDefault { get; }

        internal IReadOnlyDictionary<string, object> GlobalVariables { get; }

        /// <summary>
        /// 面对墙大基架使用独立模板和全局变量名。
        /// </summary>
        internal FaceToWallFrameTemplateKind FrameTemplateKind { get; }

        internal FrameTemplateIdentity TemplateIdentity { get; }

        internal bool UseLargeFrameTemplate =>
            FrameTemplateKind != FaceToWallFrameTemplateKind.Regular;

        /// <summary>
        /// 面对面小偏移基架使用独立模板，不能与常规基架共享物理模型。
        /// </summary>
        internal bool IsSmallOffsetFrame { get; }

        internal FaceToFaceSmallOffsetFrameDrivePlan SmallOffsetFrame { get; }

        /// <summary>
        /// 面对墙大基架2本次计划写入的“侧盖板宽度”；其他模板为空。
        /// </summary>
        internal double? SideCoverPlateWidth { get; }

        internal double WallEntryDistance { get; }

        internal FaceToFaceFrameInsulatorParameters InternalInsulators { get; set; }

        internal bool? SuppressTemplateAngleNotches { get; set; }
        internal CKBusbarBridge.Prototype.BridgeAngleNotchPlan AngleNotches { get; set; }

        private static double? ReadSideCoverPlateWidth(
            FaceToWallFrameTemplateKind frameTemplateKind,
            IReadOnlyDictionary<string, object> globalVariables)
        {
            if (frameTemplateKind != FaceToWallFrameTemplateKind.Large2
                || globalVariables == null
                || !globalVariables.TryGetValue(
                    FaceToWallFrameDrivePlanner.SideCoverPlateWidthVariableName,
                    out var rawValue)
                || rawValue == null)
            {
                return null;
            }

            return Convert.ToDouble(rawValue);
        }
    }

    internal sealed class FaceToFaceFramePositionPlan
    {
        internal FaceToFaceFramePositionPlan(
            int positionIndex,
            FaceToFaceFrameModelPlan modelPlan,
            bool isReversed,
            string inletCoordinateName,
            string outletCoordinateName)
            : this(
                positionIndex,
                modelPlan?.SourceName,
                modelPlan,
                isReversed,
                inletCoordinateName,
                outletCoordinateName,
                originalSourceName: null,
                componentInstanceId: null)
        {
        }

        internal FaceToFaceFramePositionPlan(
            int positionIndex,
            string sourceName,
            FaceToFaceFrameModelPlan modelPlan,
            bool isReversed,
            string inletCoordinateName,
            string outletCoordinateName,
            string originalSourceName = null,
            string componentInstanceId = null)
        {
            PositionIndex = positionIndex;
            PositionKey = "位置" + positionIndex;
            SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
            ModelPlan = modelPlan ?? throw new ArgumentNullException(nameof(modelPlan));
            IsReversed = modelPlan.FrameTemplateKind != FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner && isReversed;
            InletCoordinateName = inletCoordinateName
                ?? throw new ArgumentNullException(nameof(inletCoordinateName));
            OutletCoordinateName = outletCoordinateName
                ?? throw new ArgumentNullException(nameof(outletCoordinateName));
            // 独立操作后，新的 SourceName 可能与快照中的原始 SourceName 不同。
            // OriginalSourceName 保存快照时的原始名称，用于规划器定位旧模型。
            OriginalSourceName = originalSourceName;
            // 组件实例ID，用于二次驱动时通过实例身份定位旧物理模型。
            ComponentInstanceId = componentInstanceId;
        }

        internal int PositionIndex { get; }

        internal string PositionKey { get; }

        /// <summary>
        /// 当前计划中的基架名称。独立操作后可能与 <see cref="OriginalSourceName"/> 不同。
        /// </summary>
        internal string SourceName { get; }

        /// <summary>
        /// 快照保存时的原始基架名称。用于规划器在 SourceName 改变后仍能正确定位旧模型。
        /// </summary>
        internal string OriginalSourceName { get; }

        /// <summary>
        /// 组件实例ID。用于二次驱动时通过实例身份定位旧物理模型目录。
        /// </summary>
        internal string ComponentInstanceId { get; }

        internal FaceToFaceFrameModelPlan ModelPlan { get; }

        internal bool IsReversed { get; }

        internal string InletCoordinateName { get; }

        internal string OutletCoordinateName { get; }
    }

    internal sealed class FaceToFaceFrameAssemblyToken
    {
        internal FaceToFaceFrameAssemblyToken(
            FaceToFaceFramePositionPlan positionPlan,
            string modelPath,
            object nativeComponent,
            string componentFullName)
        {
            PositionPlan = positionPlan ?? throw new ArgumentNullException(nameof(positionPlan));
            ModelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
            NativeComponent = nativeComponent;
            ComponentFullName = componentFullName;
        }

        internal FaceToFaceFramePositionPlan PositionPlan { get; }

        internal string ModelPath { get; }

        internal object NativeComponent { get; }

        internal string ComponentFullName { get; }
    }
}
