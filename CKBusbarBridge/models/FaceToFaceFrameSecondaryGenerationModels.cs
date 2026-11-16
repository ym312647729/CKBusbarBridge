using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CKBusbarBridge.Drivers;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 二次驱动统一使用 0.001 mm 精度比较物理模型参数；位置、组角色和组标识不属于模型身份。
    /// </summary>
    internal static class FaceToFaceFramePhysicalIdentity
    {
        internal static bool ModelParametersEqual(FaceToFaceFrameModelPlan first, FaceToFaceFrameModelPlan second)
        {
            if (first == null || second == null) return false;
            var snapshot = new FaceToFaceFrameModelSnapshot(first.SourceName, string.Empty,
                first.Width, first.Length, first.Height, first.InsulatorPositions,
                first.FrameTemplateKind, first.SmallOffsetFrame, first.TemplateIdentity,
                first.SideCoverPlateWidth, first.WallEntryDistance);
            return snapshot.HasSameParameters(second)
                && first.SuppressTemplateAngleNotches == second.SuppressTemplateAngleNotches
                && CKBusbarBridge.Prototype.BridgeAngleNotchPlan.Same(first.AngleNotches, second.AngleNotches)
                && (first.InsulatorCount == 0 ||
                    (first.InternalInsulators == null && second.InternalInsulators == null) ||
                    first.InternalInsulators != null && first.InternalInsulators.HasSameParameters(second.InternalInsulators))
                && first.GlobalVariables.Count == second.GlobalVariables.Count
                && first.GlobalVariables.All(pair => second.GlobalVariables.TryGetValue(pair.Key, out var value)
                    && AreEqual(Convert.ToDouble(pair.Value, System.Globalization.CultureInfo.InvariantCulture),
                        Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        internal static bool AreEqual(double first, double second)
        {
            return Normalize(first) == Normalize(second);
        }

        internal static bool SequenceEqual(
            IEnumerable<double> first,
            IEnumerable<double> second)
        {
            var firstValues = (first ?? Enumerable.Empty<double>()).ToArray();
            var secondValues = (second ?? Enumerable.Empty<double>()).ToArray();
            return firstValues.Length == secondValues.Length
                && firstValues.Zip(secondValues, AreEqual).All(value => value);
        }

        private static double Normalize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }
    }

    internal enum FaceToFaceFrameModelChangeKind
    {
        Keep,
        DriveExisting,
        Create,
        /// <summary>
        /// SourceName 保持不变，但物理模板身份发生变化。
        /// 该动作不是原位写变量，而是整组移除旧组件、删除旧目录并在同一 SourceName 下重新生成。
        /// </summary>
        Recreate,
        RetireCandidate
    }

    /// <summary>
    /// 二次驱动开始前按业务差异移除某个旧位置的原因。关闭总装负责统一卸载文档，
    /// 因此这里只记录真正改变装配结构的原因，不再混入模板同名等会话级安全原因。
    /// </summary>
    [Flags]
    internal enum FaceToFaceFrameDetachReason
    {
        None = 0,
        PositionReferenceChanged = 1,
        PositionRemoved = 2,
        SourceRecreated = 4
    }

    internal enum FaceToFaceFramePositionChangeKind
    {
        Keep,
        Remate,
        Replace,
        Insert,
        Remove
    }

    internal sealed class FaceToFaceFrameModelSnapshot
    {
        internal FaceToFaceFrameModelSnapshot(
            string sourceName,
            string modelPath,
            double width,
            double length,
            double height,
            IEnumerable<double> insulatorPositions,
            FaceToWallFrameTemplateKind frameTemplateKind = FaceToWallFrameTemplateKind.Regular,
            FaceToFaceSmallOffsetFrameDrivePlan smallOffsetFrame = null,
            FrameTemplateIdentity? templateIdentity = null,
            double? sideCoverPlateWidth = null,
            double wallEntryDistance = 0d,
            bool hasWallEntryDistanceProperty = true)
        {
            SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
            ModelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
            Width = width;
            Length = length;
            Height = height;
            InsulatorPositions = (insulatorPositions ?? Enumerable.Empty<double>())
                .ToList()
                .AsReadOnly();
            FrameTemplateKind = frameTemplateKind;
            SmallOffsetFrame = smallOffsetFrame;
            TemplateIdentity = templateIdentity
                ?? FrameTemplateIdentityMetadata.FromLegacyStructure(
                    frameTemplateKind,
                    smallOffsetFrame != null);
            SideCoverPlateWidth = sideCoverPlateWidth;
            WallEntryDistance = frameTemplateKind == FaceToWallFrameTemplateKind.Large1 ? wallEntryDistance : 0d;
            HasWallEntryDistanceProperty = hasWallEntryDistanceProperty;
        }

        internal string SourceName { get; }

        internal string ModelPath { get; }

        internal double Width { get; }

        internal double Length { get; }

        internal double Height { get; }

        internal int InsulatorCount => InsulatorPositions.Count;

        internal IReadOnlyList<double> InsulatorPositions { get; }

        /// <summary>
        /// 物理模型所采用的模板类型。面对墙三类模板即使外形尺寸相同，也不能原位互驱。
        /// </summary>
        internal FaceToWallFrameTemplateKind FrameTemplateKind { get; }

        internal FrameTemplateIdentity TemplateIdentity { get; }

        internal FaceToFaceSmallOffsetFrameDrivePlan SmallOffsetFrame { get; }

        internal bool IsSmallOffsetFrame => SmallOffsetFrame != null;

        /// <summary>
        /// 面对墙大基架2的“侧盖板宽度”物理反读值；旧测试快照及其他模板为空。
        /// </summary>
        internal double? SideCoverPlateWidth { get; }

        internal double WallEntryDistance { get; }

        internal bool HasWallEntryDistanceProperty { get; }

        internal bool HasSameParameters(FaceToFaceFrameModelSnapshot other)
        {
            return other != null
                && FaceToFaceFramePhysicalIdentity.AreEqual(Width, other.Width)
                && FaceToFaceFramePhysicalIdentity.AreEqual(Length, other.Length)
                && FaceToFaceFramePhysicalIdentity.AreEqual(Height, other.Height)
                && FaceToFaceFramePhysicalIdentity.AreEqual(WallEntryDistance, other.WallEntryDistance)
                && FrameTemplateKind == other.FrameTemplateKind
                && (FrameTemplateKind != FaceToWallFrameTemplateKind.Large1
                    || HasWallEntryDistanceProperty == other.HasWallEntryDistanceProperty)
                && TemplateIdentity == other.TemplateIdentity
                && HasSameSmallOffsetParameters(SmallOffsetFrame, other.SmallOffsetFrame)
                && OptionalDrivenParameterEqual(
                    SideCoverPlateWidth,
                    other.SideCoverPlateWidth)
                && FaceToFaceFramePhysicalIdentity.SequenceEqual(
                    InsulatorPositions,
                    other.InsulatorPositions);
        }

        internal bool HasSameParameters(FaceToFaceFrameModelPlan plan)
        {
            return plan != null
                && FaceToFaceFramePhysicalIdentity.AreEqual(Width, plan.Width)
                && FaceToFaceFramePhysicalIdentity.AreEqual(Length, plan.Length)
                && FaceToFaceFramePhysicalIdentity.AreEqual(Height, plan.Height)
                && FaceToFaceFramePhysicalIdentity.AreEqual(WallEntryDistance, plan.WallEntryDistance)
                && FrameTemplateKind == plan.FrameTemplateKind
                && (FrameTemplateKind != FaceToWallFrameTemplateKind.Large1 || HasWallEntryDistanceProperty)
                && TemplateIdentity == plan.TemplateIdentity
                && HasSameSmallOffsetParameters(SmallOffsetFrame, plan.SmallOffsetFrame)
                && OptionalDrivenParameterEqual(
                    SideCoverPlateWidth,
                    plan.SideCoverPlateWidth)
                && InsulatorCount == plan.InsulatorCount
                && FaceToFaceFramePhysicalIdentity.SequenceEqual(
                    InsulatorPositions,
                    plan.InsulatorPositions);
        }

        private static bool HasSameSmallOffsetParameters(
            FaceToFaceSmallOffsetFrameDrivePlan first,
            FaceToFaceSmallOffsetFrameDrivePlan second)
        {
            if (first == null || second == null)
            {
                return first == null && second == null;
            }

            return FaceToFaceFramePhysicalIdentity.AreEqual(
                    first.FrameWidth,
                    second.FrameWidth)
                && first.InletOffsetDirection == second.InletOffsetDirection
                && FaceToFaceFramePhysicalIdentity.AreEqual(
                    first.InletOffsetSize,
                    second.InletOffsetSize)
                && first.OutletOffsetDirection == second.OutletOffsetDirection
                && FaceToFaceFramePhysicalIdentity.AreEqual(
                    first.OutletOffsetSize,
                    second.OutletOffsetSize);
        }

        private static bool OptionalDrivenParameterEqual(double? first, double? second)
        {
            // 空值仅用于兼容不经过 SolidWorks 真实反读的旧测试/旧快照。
            // 生产反读的大基架2两侧都有值，必须严格参与二次驱动差异判断。
            return !first.HasValue
                || !second.HasValue
                || FaceToFaceFramePhysicalIdentity.AreEqual(first.Value, second.Value);
        }
    }

    internal sealed class FaceToFaceFramePositionSnapshot
    {
        internal FaceToFaceFramePositionSnapshot(
            int positionIndex,
            string componentInstanceId,
            FaceToFaceFrameModelSnapshot model,
            bool isReversed,
            string inletCoordinateName = null,
            string outletCoordinateName = null)
        {
            if (positionIndex <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(positionIndex));
            }

            PositionIndex = positionIndex;
            PositionKey = "位置" + positionIndex;
            ComponentInstanceId = componentInstanceId
                ?? throw new ArgumentNullException(nameof(componentInstanceId));
            Model = model ?? throw new ArgumentNullException(nameof(model));
            IsReversed = model.FrameTemplateKind != FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner && isReversed;
            InletCoordinateName = inletCoordinateName;
            OutletCoordinateName = outletCoordinateName;
        }

        internal int PositionIndex { get; }

        internal string PositionKey { get; }

        internal string ComponentInstanceId { get; }

        internal FaceToFaceFrameModelSnapshot Model { get; }

        internal string SourceName => Model.SourceName;

        internal string ModelPath => Model.ModelPath;

        internal bool IsReversed { get; }

        /// <summary>
        /// 真实配合链读取到的入口/出口坐标系；二次驱动据此判断是否需要重新配合。
        /// </summary>
        internal string InletCoordinateName { get; }

        internal string OutletCoordinateName { get; }
    }

    internal sealed class FaceToFaceFrameParameterSnapshot
    {
        private FaceToFaceFrameParameterSnapshot(
            IEnumerable<FaceToFaceFramePositionSnapshot> positions,
            IDictionary<string, FaceToFaceFrameModelSnapshot> models,
            IDictionary<string, FaceToFaceFrameModelSnapshot> modelsByInstanceId)
        {
            Positions = (positions ?? Enumerable.Empty<FaceToFaceFramePositionSnapshot>())
                .ToList()
                .AsReadOnly();
            Models = new ReadOnlyDictionary<string, FaceToFaceFrameModelSnapshot>(
                new Dictionary<string, FaceToFaceFrameModelSnapshot>(
                    models ?? new Dictionary<string, FaceToFaceFrameModelSnapshot>(),
                    StringComparer.OrdinalIgnoreCase));
            // 保留安装实例的原模型信息；目标模型始终按目标 SourceName 查找。
            ModelsByInstanceId = new ReadOnlyDictionary<string, FaceToFaceFrameModelSnapshot>(
                new Dictionary<string, FaceToFaceFrameModelSnapshot>(
                    modelsByInstanceId ?? new Dictionary<string, FaceToFaceFrameModelSnapshot>(
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase));
        }

        internal IReadOnlyList<FaceToFaceFramePositionSnapshot> Positions { get; }

        internal IReadOnlyDictionary<string, FaceToFaceFrameModelSnapshot> Models { get; }

        /// <summary>
        /// 安装实例的原模型信息，仅用于追踪旧位置；不能据此选择目标名称的模型。
        /// </summary>
        internal IReadOnlyDictionary<string, FaceToFaceFrameModelSnapshot> ModelsByInstanceId { get; }

        internal static FaceToFaceFrameParameterSnapshot FromReadResult(
            FaceToFaceFrameReadResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var positions = new List<FaceToFaceFramePositionSnapshot>();
            var models = new Dictionary<string, FaceToFaceFrameModelSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            // 实例信息用于追踪安装位置，名称用于归并物理模型。
            var modelsByInstanceId = new Dictionary<string, FaceToFaceFrameModelSnapshot>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in result.Frames)
            {
                if (item == null)
                {
                    throw new ArgumentException("基架反读结果不能包含空位置。", nameof(result));
                }

                var componentInstanceId = NormalizeRequiredText(
                    item.ComponentInstanceId,
                    "基架组件实例标识");
                var sourceName = NormalizeRequiredText(item.SourceName, "基架名称");
                var modelPath = NormalizeRequiredText(item.ModelPath, "基架模型路径");
                var width = NormalizePositiveFinite(item.Width, "基架宽度");
                var length = NormalizePositiveFinite(item.Length, "基架长度");
                var height = NormalizePositiveFinite(item.Height, "基架高度");
                if (double.IsNaN(item.WallEntryDistance) || double.IsInfinity(item.WallEntryDistance))
                    throw new ArgumentException("大基架1进墙距离必须为有限数值。", nameof(result));
                var sideCoverPlateWidth = item.SideCoverPlateWidth.HasValue
                    ? NormalizeNonNegativeFinite(
                        item.SideCoverPlateWidth.Value,
                        "大基架2侧盖板宽度")
                    : (double?)null;
                var insulatorPositions = NormalizeInsulatorPositions(
                    sourceName,
                    item.InsulatorPositions,
                    length);
                var candidate = new FaceToFaceFrameModelSnapshot(
                    sourceName,
                    modelPath,
                    width,
                    length,
                    height,
                    insulatorPositions,
                    item.FrameTemplateKind,
                    item.SmallOffsetFrame,
                    item.TemplateIdentity,
                    sideCoverPlateWidth,
                    item.WallEntryDistance,
                    item.HasWallEntryDistanceProperty);

                if (models.TryGetValue(sourceName, out var existingModel))
                {
                    if (!string.Equals(
                            existingModel.ModelPath,
                            modelPath,
                            StringComparison.OrdinalIgnoreCase)
                        || !existingModel.HasSameParameters(candidate))
                    {
                        throw new ArgumentException(
                            $"同一基架名称“{existingModel.SourceName}”的反读路径或参数不一致。",
                            nameof(result));
                    }

                    candidate = existingModel;
                }
                else
                {
                    // 名称唯一标识物理模型；安装实例仍逐个保存在 Positions 中。
                    if (models.Values.Any(model => string.Equals(model.ModelPath, modelPath,
                        StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("不同基架名称不能引用同一物理模型：" + modelPath, nameof(result));
                    models.Add(sourceName, candidate);
                }

                // 独立操作后仍可追踪原安装实例，但不得继续复用其旧物理模型。
                if (modelsByInstanceId.ContainsKey(componentInstanceId))
                    throw new ArgumentException("基架组件实例标识重复：" + componentInstanceId, nameof(result));
                modelsByInstanceId.Add(componentInstanceId, candidate);

                positions.Add(new FaceToFaceFramePositionSnapshot(
                    positions.Count + 1,
                    componentInstanceId,
                    candidate,
                    item.IsReversed,
                    item.InletCoordinateName,
                    item.OutletCoordinateName));
            }

            return new FaceToFaceFrameParameterSnapshot(positions, models, modelsByInstanceId);
        }

        private static string NormalizeRequiredText(string value, string description)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(description + "不能为空。");
            }

            return value.Trim();
        }

        private static double NormalizePositiveFinite(double value, string description)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(description, value, description + "必须为正的有限数值。");
            }

            return Normalize(value);
        }

        private static double NormalizeNonNegativeFinite(double value, string description)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    description,
                    value,
                    description + "必须为非负有限数值。");
            }

            return Normalize(value);
        }

        private static IReadOnlyList<double> NormalizeInsulatorPositions(
            string sourceName,
            IEnumerable<double> positions,
            double frameLength)
        {
            var normalized = new List<double>();
            var unique = new HashSet<double>();
            foreach (var position in positions ?? Enumerable.Empty<double>())
            {
                if (double.IsNaN(position) || double.IsInfinity(position))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(positions),
                        position,
                        $"基架“{sourceName}”的绝缘子定位必须为有限数值。");
                }

                var value = Normalize(position);
                if (value < 0 || value > frameLength)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(positions),
                        position,
                        $"基架“{sourceName}”的绝缘子定位必须在 0～{frameLength:0.###} mm 内。");
                }

                if (!unique.Add(value))
                {
                    throw new ArgumentException(
                        $"基架“{sourceName}”存在重复的绝缘子定位值：{value:0.###} mm。",
                        nameof(positions));
                }

                normalized.Add(value);
            }

            if (normalized.Count > 3)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(positions),
                    normalized.Count,
                    $"基架“{sourceName}”的绝缘子数量不能超过 3。");
            }

            return normalized.AsReadOnly();
        }

        private static double Normalize(double value)
        {
            return Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }
    }

    internal sealed class FaceToFaceFrameModelChange
    {
        internal FaceToFaceFrameModelChange(
            FaceToFaceFrameModelChangeKind kind,
            string sourceName,
            FaceToFaceFrameModelSnapshot existingModel,
            FaceToFaceFrameModelPlan targetPlan)
        {
            Kind = kind;
            SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
            ExistingModel = existingModel;
            TargetPlan = targetPlan;
        }

        internal FaceToFaceFrameModelChangeKind Kind { get; }

        internal string SourceName { get; }

        internal FaceToFaceFrameModelSnapshot ExistingModel { get; }

        internal FaceToFaceFrameModelPlan TargetPlan { get; }
    }

    internal sealed class FaceToFaceFramePositionChange
    {
        internal FaceToFaceFramePositionChange(
            FaceToFaceFramePositionChangeKind kind,
            int positionIndex,
            FaceToFaceFramePositionSnapshot existingPosition,
            FaceToFaceFramePositionPlan targetPlan)
        {
            if (positionIndex <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(positionIndex));
            }

            Kind = kind;
            PositionIndex = positionIndex;
            PositionKey = "位置" + positionIndex;
            ExistingPosition = existingPosition;
            TargetPlan = targetPlan;
        }

        internal FaceToFaceFramePositionChangeKind Kind { get; }

        internal int PositionIndex { get; }

        internal string PositionKey { get; }

        internal FaceToFaceFramePositionSnapshot ExistingPosition { get; }

        internal FaceToFaceFramePositionPlan TargetPlan { get; }
    }

    internal sealed class FaceToFaceFrameSecondaryDrivePlan
    {
        internal FaceToFaceFrameSecondaryDrivePlan(
            IEnumerable<FaceToFaceFrameModelChange> modelChanges,
            IEnumerable<FaceToFaceFramePositionChange> positionChanges,
            IEnumerable<FaceToFaceFramePositionPlan> targetPlans)
        {
            ModelChanges = (modelChanges ?? Enumerable.Empty<FaceToFaceFrameModelChange>())
                .ToList()
                .AsReadOnly();
            PositionChanges = (positionChanges ?? Enumerable.Empty<FaceToFaceFramePositionChange>())
                .OrderBy(change => change.PositionIndex)
                .ToList()
                .AsReadOnly();
            TargetPlans = (targetPlans ?? Enumerable.Empty<FaceToFaceFramePositionPlan>())
                .ToList()
                .AsReadOnly();
        }

        internal IReadOnlyList<FaceToFaceFrameModelChange> ModelChanges { get; }

        internal IReadOnlyList<FaceToFaceFramePositionChange> PositionChanges { get; }

        internal IReadOnlyList<FaceToFaceFramePositionPlan> TargetPlans { get; }

        internal bool IsEmpty => ModelChanges.All(change => change.Kind == FaceToFaceFrameModelChangeKind.Keep)
            && PositionChanges.All(change => change.Kind == FaceToFaceFramePositionChangeKind.Keep);
    }

    /// <summary>
    /// 一次二次驱动中需要预先从总装移除的旧位置。
    /// 这里只保存稳定快照对象和原因，不保存任何 SolidWorks COM 对象。
    /// </summary>
    internal sealed class FaceToFaceFrameDetachPlan
    {
        internal FaceToFaceFrameDetachPlan(
            FaceToFaceFramePositionSnapshot position,
            FaceToFaceFrameDetachReason reason)
        {
            Position = position ?? throw new ArgumentNullException(nameof(position));
            Reason = reason;
            if (reason == FaceToFaceFrameDetachReason.None)
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }

        internal FaceToFaceFramePositionSnapshot Position { get; }

        internal FaceToFaceFrameDetachReason Reason { get; }
    }

    /// <summary>
    /// “业务预拆全部完成后再关闭总装、全部模型完成后再重开装配”的统一执行批次。
    /// 关闭总装本身承担文档卸载职责，批次只需要保存实际要改变装配结构的旧位置。
    /// </summary>
    internal sealed class FaceToFaceFrameSecondaryDriveBatch
    {
        internal FaceToFaceFrameSecondaryDriveBatch(
            FaceToFaceFrameSecondaryDrivePlan differencePlan,
            IEnumerable<FaceToFaceFrameDetachPlan> detachments)
        {
            DifferencePlan = differencePlan
                ?? throw new ArgumentNullException(nameof(differencePlan));
            Detachments = (detachments ?? Enumerable.Empty<FaceToFaceFrameDetachPlan>())
                .OrderByDescending(item => item.Position.PositionIndex)
                .ToList()
                .AsReadOnly();
        }

        internal FaceToFaceFrameSecondaryDrivePlan DifferencePlan { get; }

        internal IReadOnlyList<FaceToFaceFrameDetachPlan> Detachments { get; }
    }

    internal sealed class FaceToFaceFrameSecondaryDriveResult
    {
        internal FaceToFaceFrameSecondaryDriveResult(
            bool hasChanges,
            int drivenModelCount,
            int createdModelCount,
            int changedPositionCount,
            int deletedDirectoryCount,
            IEnumerable<string> warnings,
            FaceToFaceFrameParameterSnapshot committedSnapshot)
        {
            HasChanges = hasChanges;
            DrivenModelCount = drivenModelCount;
            CreatedModelCount = createdModelCount;
            ChangedPositionCount = changedPositionCount;
            DeletedDirectoryCount = deletedDirectoryCount;
            Warnings = (warnings ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
            CommittedSnapshot = committedSnapshot;
        }

        internal bool HasChanges { get; }

        internal int DrivenModelCount { get; }

        internal int CreatedModelCount { get; }

        internal int ChangedPositionCount { get; }

        internal int DeletedDirectoryCount { get; }

        internal IReadOnlyList<string> Warnings { get; }

        internal FaceToFaceFrameParameterSnapshot CommittedSnapshot { get; }
    }
}
