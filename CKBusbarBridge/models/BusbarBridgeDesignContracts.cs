using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public enum DesignObjectKind { Cabinet, Base, FixedInsulator, FixedEnd, ControlPoint, Breakpoint, Lap, Frame, Insulator, FixedJoint, FixedRoutePoint, FixedBusbar, Wall, WallBushing }
    public enum DesignField
    {
        X, Y, Z, Length, Width, Direction, InsulatorCount, Group, Height, Installation, InsulatorPosition,
        JointDistanceFromPrevious, PreviousSegmentLength, NextSegmentLength, CopperSegmentLength,
        BaseJointPenetrationDistance, BaseJointEdgeDistance,
        WallEntryDistance, WallVerticalClearance, WallHorizontalClearance,
        InsulatorDistanceFromPrevious,
        WallTopClearance, WallBottomClearance, WallLeftClearance, WallRightClearance
    }
    public enum DesignSessionMode { Prototype }
    public enum BridgeCopperHeadKind { Bridge, Straight, LTemplate }

    internal static class BridgeDesignPrecision
    {
        internal const double NumericTolerance = 1.0;
        internal const double LengthToleranceMm = NumericTolerance;
        internal const double LengthComparisonToleranceMm = LengthToleranceMm;
        internal static double PositionDistance(double[] a, double[] b) =>
            Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
        public static double OneDecimal(double value) => double.IsNaN(value) || double.IsInfinity(value)
            ? value : Math.Round(value, 1, MidpointRounding.AwayFromZero);
        public static double Normalize(DesignField field, double value) =>
            field == DesignField.InsulatorCount || field == DesignField.Group || field == DesignField.Installation
                ? value : OneDecimal(value);
        public static double Normalize(DesignObjectKind kind, DesignField field, double value) =>
            // 基架尺寸和位姿均参与补偿，界面格式化不能改写计算结果。
            kind == DesignObjectKind.Frame
                ? value :
            // 断点和实际铜排加工点共用桥向基准；只量化相对距离，避免200mm被绝对坐标舍入成199.9mm。
            field == DesignField.X && (kind == DesignObjectKind.FixedJoint || kind == DesignObjectKind.Breakpoint ||
                kind == DesignObjectKind.Lap || kind == DesignObjectKind.FixedRoutePoint)
                ? value : Normalize(field, value);
    }

    /// <summary>沿路径顺序的单根段排；普通段一桥头一直头，基座L排两端为直头，成形尺寸仍待模板确认。</summary>
    public sealed class BridgeCopperSegment
    {
        public string Id { get; }
        public IReadOnlyList<BridgeDesignObject> Points { get; }
        public bool IsL { get; }
        public bool StraightLapAtStart { get; }
        public bool IsPhysicalGeometry { get; private set; }
        public double SectionWidth { get; private set; }
        public double SectionThickness { get; private set; }
        public BridgeCopperSegment WithPhysicalGeometry(double width, double thickness)
        {
            var copy = new BridgeCopperSegment(Id, Points, IsL, StraightLapAtStart);
            copy.IsPhysicalGeometry = true; copy.SectionWidth = width; copy.SectionThickness = thickness;
            return copy;
        }
        public BridgeCopperHeadKind StartHead => IsL ? BridgeCopperHeadKind.Straight : BridgeCopperHeadKind.Bridge;
        public BridgeCopperHeadKind EndHead => BridgeCopperHeadKind.Straight;
        public BridgeCopperSegment(string id, IEnumerable<BridgeDesignObject> points, bool isL)
            : this(id, points, isL, false) { }
        public BridgeCopperSegment(string id, IEnumerable<BridgeDesignObject> points, bool isL, bool straightLapAtStart)
        {
            if (straightLapAtStart && !isL) throw new ArgumentException("直直端部搭接仅用于已标识的L排。", nameof(straightLapAtStart));
            Id = id; Points = Array.AsReadOnly(points.ToArray()); IsL = isL; StraightLapAtStart = straightLapAtStart;
        }
    }

    /// <summary>断点沿主铜排路径的显示尺寸；三项均以ABC联动断点的代表相计算。</summary>
    public sealed class BridgeJointMeasurements
    {
        public double DistanceFromPrevious { get; }
        public double PreviousSegmentLength { get; }
        public double NextSegmentLength { get; }

        public BridgeJointMeasurements(double distanceFromPrevious, double previousSegmentLength, double nextSegmentLength)
            : this(distanceFromPrevious, previousSegmentLength, nextSegmentLength, false)
        {
        }

        internal BridgeJointMeasurements(double distanceFromPrevious, double previousSegmentLength, double nextSegmentLength,
            bool preservePrecision)
        {
            DistanceFromPrevious = preservePrecision ? distanceFromPrevious : BridgeDesignPrecision.OneDecimal(distanceFromPrevious);
            PreviousSegmentLength = preservePrecision ? previousSegmentLength : BridgeDesignPrecision.OneDecimal(previousSegmentLength);
            NextSegmentLength = preservePrecision ? nextSegmentLength : BridgeDesignPrecision.OneDecimal(nextSegmentLength);
        }
    }

    /// <summary>桥内ABC三相共用的段排选择项；序号沿B柜出排到A柜进排方向排列。</summary>
    public sealed class BridgeCopperSection
    {
        public string Id { get; }
        public string Name { get; }
        public int Index { get; }
        public string StartPointId { get; }
        public string EndPointId { get; }
        public double Length { get; }
        public bool CanAdjustLength { get; }

        public BridgeCopperSection(int index, string startPointId, string endPointId, double length, bool canAdjustLength)
            : this(index, startPointId, endPointId, length, canAdjustLength, false)
        {
        }

        internal BridgeCopperSection(int index, string startPointId, string endPointId, double length, bool canAdjustLength,
            bool preservePrecision)
        {
            if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
            Index = index;
            Id = IdFor(index);
            Name = "段排" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            StartPointId = startPointId;
            EndPointId = endPointId;
            Length = preservePrecision ? length : BridgeDesignPrecision.OneDecimal(length);
            CanAdjustLength = canAdjustLength;
        }

        public static string IdFor(int index) => "bridge-copper-section-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>来自小偏移规划器的进出孔驱动参数；只读快照，复制及撤销时保留。</summary>
    public sealed class BridgeSmallOffsetPreview
    {
        public int InletDirection { get; }
        public double InletSize { get; }
        public int OutletDirection { get; }
        public double OutletSize { get; }
        internal double InstalledPortOffset(int port, bool reversed)
        {
            var inlet = reversed ? port == 1 : port == 0;
            var direction = inlet ? InletDirection : OutletDirection;
            var size = inlet ? InletSize : OutletSize;
            return (direction == 0 ? -size : size) * (reversed ? -1d : 1d);
        }

        public BridgeSmallOffsetPreview(int inletDirection, double inletSize, int outletDirection, double outletSize)
        {
            if ((inletDirection != 0 && inletDirection != 1) || outletDirection != 1 - inletDirection ||
                new[] { inletSize, outletSize }.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v < 0))
                throw new ArgumentException("小偏移预览必须提供互为镜像的进出孔方向和非负偏移尺寸。");
            InletDirection = inletDirection; InletSize = inletSize;
            OutletDirection = outletDirection; OutletSize = outletSize;
        }
    }

    /// <summary>拐角局部X/Y封闭面的符号；相反两面为相邻的铜排通道。</summary>
    public sealed class BridgeCornerPreview
    {
        public int ClosedEnd { get; }
        public int ClosedSide { get; }
        internal double? MinimumSafeLengthMm { get; }
        public BridgeCornerPreview(int closedEnd, int closedSide) : this(closedEnd, closedSide, null) { }
        internal BridgeCornerPreview(int closedEnd, int closedSide, double? minimumSafeLengthMm)
        {
            if (Math.Abs(closedEnd) != 1 || Math.Abs(closedSide) != 1)
                throw new ArgumentException("拐角封闭面方向必须为-1或1。");
            if (minimumSafeLengthMm.HasValue && (!(minimumSafeLengthMm > 0) || double.IsInfinity(minimumSafeLengthMm.Value)))
                throw new ArgumentException("大偏移最小安全长度无效。");
            ClosedEnd = closedEnd; ClosedSide = closedSide;
            MinimumSafeLengthMm = minimumSafeLengthMm;
        }
    }

    internal sealed class BridgeAngleNotchPlan
    {
        internal const double MaximumDepthMm = 25.0;
        internal static readonly IReadOnlyList<double> RegularDepthsMm = Array.AsReadOnly(new[] { 0d, 22d, MaximumDepthMm });
        internal static readonly IReadOnlyList<double> FourDepthsMm = Array.AsReadOnly(new[] { 0d, 10d, 22d, MaximumDepthMm });
        internal bool Suppress { get; }
        // 旧记录只有开关。显式深度0表示压缩。028分别记录连接侧和普通侧。
        internal double? DepthMm { get; }
        internal double? OuterDepthMm { get; }
        // 直通模板按局部进负、进正、出负、出正记录。028按140、141连接翼、141外翼、148记录。
        internal IReadOnlyList<double> InstanceDepthsMm { get; }

        internal BridgeAngleNotchPlan(bool suppress, double? depthMm = null, double? outerDepthMm = null)
        {
            if (!Valid(depthMm) || !Valid(outerDepthMm) || depthMm.HasValue && suppress != (depthMm.Value == 0))
                throw new ArgumentException("角钢豁口状态或深度无效，深度必须在0.0～25.0 mm内。");
            Suppress = suppress;
            DepthMm = depthMm;
            OuterDepthMm = outerDepthMm;
        }

        internal BridgeAngleNotchPlan(IReadOnlyList<double> instanceDepthsMm)
        {
            // 032的偏移专用豁口为40-D，独立于通用豁口的25 mm制造上限。
            if (instanceDepthsMm == null || instanceDepthsMm.Count != 4 ||
                instanceDepthsMm.Any(depth => !(depth >= 0d && depth <= 40d)))
                throw new ArgumentException("角钢豁口计划需要四个有效的模板局部深度。", nameof(instanceDepthsMm));
            InstanceDepthsMm = Array.AsReadOnly(instanceDepthsMm.ToArray());
            Suppress = InstanceDepthsMm.All(depth => depth == 0d);
        }

        internal static int Slot(double end, double side) => (end > 0d ? 2 : 0) + (side > 0d ? 1 : 0);

        internal double StraightDepth(int slot) =>
            InstanceDepthsMm?[slot] ?? DepthMm ?? (Suppress ? 0d : 22d);

        internal double? CornerDepth(int slot) =>
            InstanceDepthsMm?[slot] ?? (slot < 2 ? DepthMm : OuterDepthMm);

        internal static bool Valid(double? value) => !value.HasValue || value >= 0 && value <= MaximumDepthMm;

        internal static bool Same(BridgeAngleNotchPlan first, BridgeAngleNotchPlan second) =>
            ReferenceEquals(first, second) || first != null && second != null && first.Suppress == second.Suppress &&
            first.DepthMm == second.DepthMm && first.OuterDepthMm == second.OuterDepthMm &&
            (first.InstanceDepthsMm == null ? second.InstanceDepthsMm == null :
                second.InstanceDepthsMm != null && first.InstanceDepthsMm.SequenceEqual(second.InstanceDepthsMm));
    }

    /// <summary>纯数据、毫米：X沿桥跨度，Y横向，Z向上；+X从B柜指向A柜。</summary>
    public sealed class BridgeWallFramePreview
    {
        public double InletWidth { get; }
        public double InletHeight { get; }
        public double InletOffset { get; }
        public BridgeWallFramePreview(double inletWidth, double inletHeight, double inletOffset)
        {
            if (new[] { inletWidth, inletHeight, inletOffset }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                inletWidth <= 0 || inletHeight <= 0) throw new ArgumentException("面对墙基架孔口参数无效。");
            InletWidth = inletWidth; InletHeight = inletHeight; InletOffset = inletOffset;
        }
    }

    public sealed class BridgeDesignObject
    {
        public string Id { get; }
        public string Name { get; private set; }
        public string SourceName => Kind == DesignObjectKind.Frame ? Name : null;
        public bool SharesFrameSourceWith(BridgeDesignObject other) => Kind == DesignObjectKind.Frame &&
            other?.Kind == DesignObjectKind.Frame && !string.IsNullOrWhiteSpace(SourceName) &&
            string.Equals(SourceName, other.SourceName, StringComparison.OrdinalIgnoreCase);
        public int FramePositionIndex { get; private set; }
        public BridgeComponentBinding ComponentBinding { get; private set; }
        public BridgeDesignObject WithComponentBinding(BridgeComponentBinding binding)
        {
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.ComponentBinding = FrameTemplateKey == "LargeOffsetCorner" ? binding?.WithoutFrameInstallation() : binding;
            return copy;
        }
        public bool HasAutomaticInsulatorPlacement { get; private set; }
        public string PositionLabel => FramePositionIndex > 0 ? "位置" + FramePositionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        public string DisplayName => Kind == DesignObjectKind.Frame && FramePositionIndex > 0 ? Name + "（" + PositionLabel + "）" : Name;
        public string ExplicitFrameTemplateKey { get; private set; }
        public string FrameTemplateKey => Kind == DesignObjectKind.Frame ? ExplicitFrameTemplateKey ?? (Role.Contains("小偏移") ? "SmallOffset" :
            Role.Contains("大偏移") && Role.Contains("拐角") ? "LargeOffsetCorner" : "Regular") : null;
        public DesignObjectKind Kind { get; }
        public string ParentId { get; }
        public string Phase { get; }
        public string JointGroupId { get; private set; }
        public bool IsJointGroup => !string.IsNullOrEmpty(JointGroupId);
        public string InsulatorGroupId { get; private set; }
        public bool IsInsulatorGroup => !string.IsNullOrEmpty(InsulatorGroupId);
        public bool IsGrouped => IsJointGroup || IsInsulatorGroup;
        public string PhaseLabel => MountPlan?.IsFloorStanding == true ? MountPlan.Phases[MountGroupIndex] :
            IsInsulatorGroup ? "整组" : IsJointGroup ? "ABC（联动）" : Phase;
        public string Role { get; private set; }
        public IReadOnlyDictionary<DesignField, double> Values { get; }
        public IReadOnlyCollection<DesignField> EditableFields { get; private set; }
        public bool IsEditable => EditableFields.Count > 0;
        public double X => Value(DesignField.X);
        public double Y => Value(DesignField.Y);
        public double Z => Value(DesignField.Z);
        public double Length => Value(DesignField.Length);
        public double Width => Value(DesignField.Width);
        public double Height => Value(DesignField.Height);
        // 拐角无正反装定义；旧配方或配合链的进2标记不能旋转其预览几何。
        public bool IsReversed => FrameTemplateKey != "LargeOffsetCorner" && Value(DesignField.Installation) > .5;
        public double ModelDirection => Direction + (Kind == DesignObjectKind.Frame && IsReversed ? 180 : 0);
        public double Direction => Value(DesignField.Direction);
        // 保留旧枚举/读取契约，当前原型不填充、不编辑，不能用于同源判断。
        public double Group => Value(DesignField.Group);
        public string KindLabel => Role;
        public BridgePreviewMesh ModelMesh { get; private set; }
        public BridgeInsulatorPose InsulatorPlacement { get; private set; }
        public BridgeInsulatorMountPlan MountPlan { get; private set; }
        public int MountGroupIndex { get; private set; }
        public string InsulatorMountProblem { get; private set; }
        public bool IsSuppressedInsulator => InsulatorMountProblem != null || MountPlan != null && MountPlan.BodyHeights[MountGroupIndex] <= 0;
        public BridgeDesignObject WithMountPlan(BridgeInsulatorMountPlan plan, int groupIndex, string problem = null)
        {
            if (Kind != DesignObjectKind.Insulator || groupIndex < 0 || groupIndex > 2)
                throw new ArgumentException("换相吊装仅用于架内绝缘子的三个模板组。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.MountPlan = plan; copy.MountGroupIndex = plan == null ? 0 : groupIndex; copy.InsulatorMountProblem = problem;
            if (plan?.IsFloorStanding == true)
                copy.Role = "小偏移搭接单相支撑：" + (plan.Phases[groupIndex] ?? "未安装") + "相；定位由中间搭接排中心确定";
            return copy;
        }
        public int InsulatorPadCount { get; private set; }
        public double InsulatorPadThickness { get; private set; }
        public double InsulatorPhaseSpacing { get; private set; }
        public bool UseOffsetInsulators { get; private set; }
        public BridgeDesignObject WithOffsetInsulators(bool enabled = true)
        {
            if (Kind != DesignObjectKind.Frame) throw new InvalidOperationException("可偏移绝缘子参数仅用于基架。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.UseOffsetInsulators = enabled;
            return copy;
        }
        // 所属基架落底面到首排接触面，含25mm折边支架，不含排间垫片。
        public double InsulatorAssemblyHeight { get; private set; }
        public BridgeSmallOffsetPreview SmallOffsetPreview { get; private set; }
        public BridgeCornerPreview CornerPreview { get; private set; }
        public BridgeWallFramePreview WallFramePreview { get; private set; }
        // null表示旧记录尚未作豁口决策；小偏移的专用角钢尺寸计划仍独立生效。
        public bool? SuppressTemplateAngleNotches => AngleNotches?.Suppress;
        internal BridgeAngleNotchPlan AngleNotches { get; private set; }
        public BridgeDesignObject WithTemplateAngleNotches(bool suppress) => WithAngleNotches(new BridgeAngleNotchPlan(suppress));

        internal BridgeDesignObject WithAngleNotchDepth(double depth, double? outerDepth = null) =>
            WithAngleNotches(new BridgeAngleNotchPlan(depth == 0, depth, outerDepth));

        internal BridgeDesignObject WithAngleNotches(BridgeAngleNotchPlan plan)
        {
            if (Kind != DesignObjectKind.Frame) throw new InvalidOperationException("角钢豁口状态仅用于基架。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.AngleNotches = plan;
            return copy;
        }
        public BridgeDesignObject WithWallFramePreview(BridgeWallFramePreview parameters)
        {
            if (Kind != DesignObjectKind.Frame || parameters == null ||
                !new[] { "Regular", "FaceToWallLarge1", "FaceToWallLarge2" }.Contains(FrameTemplateKey))
                throw new ArgumentException("面对墙预览参数仅用于024/026/027基架。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.WallFramePreview = parameters;
            return copy;
        }

        public BridgeDesignObject WithCornerPreview(BridgeCornerPreview parameters)
        {
            if (FrameTemplateKey != "LargeOffsetCorner" || parameters == null)
                throw new ArgumentException("拐角封闭面参数仅用于大偏移拐角基架。");
            var copy = WithFrameTemplate("LargeOffsetCorner");
            copy.CornerPreview = parameters;
            return copy;
        }

        public BridgeDesignObject WithSmallOffsetPreview(BridgeSmallOffsetPreview parameters)
        {
            if (FrameTemplateKey != "SmallOffset" || parameters == null)
                throw new ArgumentException("进出孔预览参数仅用于小偏移基架。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.SmallOffsetPreview = parameters;
            return copy;
        }

        public BridgeDesignObject WithInsulatorStack(int padCount, double thickness, double phaseSpacing, double assemblyHeight = 0)
        {
            if (Kind != DesignObjectKind.Frame || padCount < 0 || padCount > 3 ||
                new[] { thickness, phaseSpacing }.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v <= 0) ||
                double.IsNaN(assemblyHeight) || double.IsInfinity(assemblyHeight) || assemblyHeight < 0)
                throw new ArgumentException("基架绝缘子垫片参数无效。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.InsulatorPadCount = padCount; copy.InsulatorPadThickness = thickness;
            copy.InsulatorPhaseSpacing = phaseSpacing;
            copy.InsulatorAssemblyHeight = assemblyHeight;
            return copy;
        }

        public BridgeDesignObject WithInsulatorPlacement(BridgeInsulatorPose placement)
        {
            if (IsEditable || Kind != DesignObjectKind.FixedInsulator)
                throw new InvalidOperationException("实际绝缘子安装位姿仅用于只读基座绝缘子。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.InsulatorPlacement = placement ?? throw new ArgumentNullException(nameof(placement));
            copy.ModelMesh = null;
            return copy;
        }

        public BridgeDesignObject WithModelMesh(BridgePreviewMesh mesh)
        {
            if (IsEditable || (Kind != DesignObjectKind.FixedBusbar && Kind != DesignObjectKind.FixedInsulator &&
                Kind != DesignObjectKind.Wall && Kind != DesignObjectKind.WallBushing))
                throw new InvalidOperationException("原模型网格仅用于只读铜排、绝缘子、墙体和穿墙套管。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.ModelMesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
            copy.InsulatorPlacement = null;
            return copy;
        }

        public BridgeDesignObject(string id, string name, DesignObjectKind kind,
            IDictionary<DesignField, double> values, IEnumerable<DesignField> editableFields,
            string role, string parentId = null, string phase = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("对象必须有稳定ID。", nameof(id));
            Id = id; Name = name; Kind = kind; ParentId = parentId; Phase = phase; Role = role;
            var corner = FrameTemplateKey == "LargeOffsetCorner";
            if (corner) Role = Role.Replace("正装", "").Replace("反装", "");
            Values = new ReadOnlyDictionary<DesignField, double>(values.Where(p => !corner || p.Key != DesignField.Installation).ToDictionary(p => p.Key,
                p => BridgeDesignPrecision.Normalize(kind, p.Key, p.Value)));
            EditableFields = Array.AsReadOnly(editableFields.Where(f => !corner || f != DesignField.Installation).Distinct().ToArray());
        }

        public double Value(DesignField field) => Values.TryGetValue(field, out var value) ? value : 0;
        public double DriveValue(DesignField field)
        {
            if (field == DesignField.Installation && FrameTemplateKey == "LargeOffsetCorner") return 0;
            var value = Value(field);
            if (ComponentBinding?.DrivenValues?.TryGetValue(field, out var actual) == true &&
                Math.Abs(value - (ComponentBinding.DisplayValues?.TryGetValue(field, out var displayed) == true
                    ? displayed : BridgeDesignPrecision.Normalize(Kind, field, actual))) < .000001) return actual;
            return value;
        }

        public BridgeDesignObject WithValues(IDictionary<DesignField, double> values)
        {
            var copiedValues = FrameTemplateKey != "LargeOffsetCorner" ? values : values.Where(p => p.Key != DesignField.Installation).ToDictionary(p => p.Key, p => p.Value);
            return new BridgeDesignObject(Id, Name, Kind, copiedValues, EditableFields, Role, ParentId, Phase)
                { ComponentBinding = ComponentBinding, ExplicitFrameTemplateKey = ExplicitFrameTemplateKey, JointGroupId = JointGroupId, InsulatorGroupId = InsulatorGroupId, FramePositionIndex = FramePositionIndex, HasAutomaticInsulatorPlacement = HasAutomaticInsulatorPlacement, ModelMesh = ModelMesh, InsulatorPlacement = InsulatorPlacement,
                    MountPlan = MountPlan, MountGroupIndex = MountGroupIndex, InsulatorMountProblem = InsulatorMountProblem,
                    UseOffsetInsulators = UseOffsetInsulators, InsulatorPadCount = InsulatorPadCount, InsulatorPadThickness = InsulatorPadThickness, InsulatorPhaseSpacing = InsulatorPhaseSpacing, InsulatorAssemblyHeight = InsulatorAssemblyHeight, SmallOffsetPreview = SmallOffsetPreview, CornerPreview = CornerPreview, WallFramePreview = WallFramePreview, AngleNotches = AngleNotches };
        }

        internal BridgeDesignObject WithIdentity(string id, string parentId, string insulatorGroupId)
        {
            if (Kind != DesignObjectKind.Insulator || string.IsNullOrWhiteSpace(parentId))
                throw new InvalidOperationException("仅架内绝缘子可随已核验宿主重新绑定身份。");
            return new BridgeDesignObject(id, Name, Kind, Values.ToDictionary(p => p.Key, p => p.Value),
                EditableFields, Role, parentId, Phase)
            {
                ComponentBinding = ComponentBinding, InsulatorGroupId = insulatorGroupId,
                FramePositionIndex = FramePositionIndex, MountPlan = MountPlan, MountGroupIndex = MountGroupIndex
            };
        }

        public BridgeDesignObject WithJointGroup(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId) || (Kind != DesignObjectKind.FixedJoint && Kind != DesignObjectKind.Lap && Kind != DesignObjectKind.Breakpoint))
                throw new ArgumentException("三相断点组必须有稳定标识且仅用于断点节点。", nameof(groupId));
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.JointGroupId = groupId;
            return copy;
        }

        public BridgeDesignObject WithBreakpointNumber(int number)
        {
            if (Kind != DesignObjectKind.Breakpoint || number < 1)
                throw new ArgumentException("普通断点编号必须从1开始。", nameof(number));
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.Name = "断点" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return copy;
        }

        public BridgeDesignObject WithSourceName(string sourceName)
        {
            if (Kind != DesignObjectKind.Frame || string.IsNullOrWhiteSpace(sourceName))
                throw new ArgumentException("基架模型名称不能为空且仅用于基架。", nameof(sourceName));
            return new BridgeDesignObject(Id, sourceName, Kind, Values.ToDictionary(p => p.Key, p => p.Value), EditableFields, Role, ParentId, Phase)
                { ComponentBinding = ComponentBinding, ExplicitFrameTemplateKey = ExplicitFrameTemplateKey, JointGroupId = JointGroupId, InsulatorGroupId = InsulatorGroupId, FramePositionIndex = FramePositionIndex, HasAutomaticInsulatorPlacement = HasAutomaticInsulatorPlacement,
                    UseOffsetInsulators = UseOffsetInsulators, InsulatorPadCount = InsulatorPadCount, InsulatorPadThickness = InsulatorPadThickness, InsulatorPhaseSpacing = InsulatorPhaseSpacing, InsulatorAssemblyHeight = InsulatorAssemblyHeight, SmallOffsetPreview = SmallOffsetPreview, CornerPreview = CornerPreview, WallFramePreview = WallFramePreview, AngleNotches = AngleNotches };
        }

        public BridgeDesignObject WithAutomaticInsulatorPlacement()
        {
            if (Kind != DesignObjectKind.Frame) throw new InvalidOperationException("仅基架记录架内绝缘子自动避让来源。");
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.HasAutomaticInsulatorPlacement = true;
            return copy;
        }

        public BridgeDesignObject WithFrameTemplate(string key)
        {
            if (Kind != DesignObjectKind.Frame || !new[] { "Regular", "SmallOffset", "LargeOffsetCorner", "SideToSidePhaseChange029", "FaceToWallLarge1", "FaceToWallLarge2" }.Contains(key))
                throw new ArgumentException("基架模板身份无效。");
            var copy = WithValues(Values.Where(p => key != "LargeOffsetCorner" || p.Key != DesignField.Installation).ToDictionary(p => p.Key, p => p.Value));
            copy.ExplicitFrameTemplateKey = key;
            if (key == "LargeOffsetCorner")
            {
                if (FrameTemplateKey != key) copy.AngleNotches = null;
                copy.EditableFields = Array.AsReadOnly(copy.EditableFields.Where(f => f != DesignField.Installation).ToArray());
                copy.ComponentBinding = copy.ComponentBinding?.WithoutFrameInstallation();
                copy.Role = copy.Role.Replace("正装", "").Replace("反装", "");
            }
            return copy;
        }

        public BridgeDesignObject WithFramePosition(int index)
        {
            if (index < 1 || (Kind != DesignObjectKind.Frame && Kind != DesignObjectKind.Insulator))
                throw new ArgumentException("位置标识仅用于基架及其内部绝缘子，编号从1开始。", nameof(index));
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.FramePositionIndex = index;
            return copy;
        }

        public BridgeDesignObject WithInsulatorGroup(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId) || (Kind != DesignObjectKind.Insulator && Kind != DesignObjectKind.FixedInsulator))
                throw new ArgumentException("绝缘子组必须有稳定标识且仅用于绝缘子节点。", nameof(groupId));
            var copy = WithValues(Values.ToDictionary(p => p.Key, p => p.Value));
            copy.InsulatorGroupId = groupId;
            return copy;
        }
    }

    public sealed class BridgeDesignRoute
    {
        public BridgeComponentBinding ComponentBinding { get; private set; }
        public BridgeDesignRoute WithComponentBinding(BridgeComponentBinding binding)
        {
            var copy = WithPhysicalGeometry(SectionWidth, SectionThickness, PhysicalSolids);
            copy.ComponentBinding = binding;
            return copy;
        }
        public string GenerationGroup { get; private set; }
        public bool IsBridge { get; private set; }
        public BridgeDesignRoute WithGenerationGroup(string group, bool isBridge)
        {
            if (group != "A柜连接段" && group != "B柜连接段" && group != "中间排")
                throw new ArgumentException("铜排生成组身份无效。");
            var copy = WithPhysicalGeometry(SectionWidth, SectionThickness, PhysicalSolids);
            copy.GenerationGroup = group; copy.IsBridge = isBridge;
            return copy;
        }
        private CKBusbarBridgePreview.PlanarBendBusbarSetting _nativePreview;
        private CKBusbarBridgePreview.PlanarBendBusbarSetting _raisedLapBaselineNative;
        private IReadOnlyList<string> _raisedLapBaselinePointIds = Array.Empty<string>();
        public bool HasRaisedLapBaseline => _raisedLapBaselineNative != null;
        public IReadOnlyList<string> RaisedLapBaselinePointIds => _raisedLapBaselinePointIds;
        public CKBusbarBridgePreview.PlanarBendBusbarSetting CopyRaisedLapBaselineNative() =>
            BridgeNativePreviewCopy.Copy(_raisedLapBaselineNative);
        public CKBusbarBridgePreview.PlanarBendBusbarSetting CopyUnraisedNativePreview() =>
            BridgeNativePreviewCopy.Copy(_raisedLapBaselineNative ?? _nativePreview);
        public BridgeDesignRoute WithRaisedLapBaseline(CKBusbarBridgePreview.PlanarBendBusbarSetting native,
            IEnumerable<string> pointIds)
        {
            if (native == null) throw new ArgumentNullException(nameof(native));
            if (pointIds == null) throw new ArgumentNullException(nameof(pointIds));
            var ids = pointIds.ToArray();
            if (native.PointList == null || native.PointList.Count < 2 || ids.Length != native.PointList.Count ||
                ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new ArgumentException("抬高搭接的原始加工点与路径身份不匹配。");
            var copy = WithPhysicalGeometry(SectionWidth, SectionThickness, PhysicalSolids);
            copy._raisedLapBaselineNative = BridgeNativePreviewCopy.Copy(native);
            copy._raisedLapBaselinePointIds = Array.AsReadOnly(ids);
            return copy;
        }
        public BridgeDesignRoute ForRaisedLapEditing(BridgeCoordinateFrame coordinates)
        {
            if (!HasRaisedLapBaseline) return this;
            if (coordinates == null) throw new ArgumentNullException(nameof(coordinates));
            var native = CopyRaisedLapBaselineNative();
            var local = coordinates.ToPlanning(native);
            // 基线只服务于显式编辑；正式生成和反读验收仍使用当前原生几何。
            return WithPointIds(_raisedLapBaselinePointIds)
                .WithPhysicalGeometry(local.BusBarWidth, local.BusBarThickness,
                    CKBusbarBridge.DesignGeometry.BridgeCopperClearance.CreateFixedInterfacePreviewSolids(local)
                        .Select(s => s.Select(p => new[] { p[0], p[2], p[1] }).ToArray()))
                .WithNativePreview(local, coordinates);
        }
        // 生成器使用总装毫米坐标；返回深拷贝，旧实体生成器不能污染草稿或历史快照。
        public CKBusbarBridgePreview.PlanarBendBusbarSetting CopyNativePreview() => BridgeNativePreviewCopy.Copy(_nativePreview);
        public BridgeDesignRoute WithNativePreview(CKBusbarBridgePreview.PlanarBendBusbarSetting setting,
            double originX, double originZ)
        {
            if (setting == null) throw new ArgumentNullException(nameof(setting));
            if (double.IsNaN(originX) || double.IsInfinity(originX) || double.IsNaN(originZ) || double.IsInfinity(originZ))
                throw new ArgumentException("总装预览原点必须为有限坐标。");
            var copy = WithPhysicalGeometry(SectionWidth, SectionThickness, PhysicalSolids);
            copy._nativePreview = BridgeNativePreviewCopy.Copy(setting);
            copy._nativePreview.PointList = setting.PointList.Select(p =>
                new GeometRi.Point3d(p.X + originX, p.Y, p.Z + originZ)).ToList();
            copy._raisedLapBaselineNative = null;
            copy._raisedLapBaselinePointIds = Array.Empty<string>();
            return copy;
        }
        public BridgeDesignRoute WithNativePreview(CKBusbarBridgePreview.PlanarBendBusbarSetting setting, BridgeCoordinateFrame coordinates)
        {
            var copy = WithPhysicalGeometry(SectionWidth, SectionThickness, PhysicalSolids);
            copy._nativePreview = (coordinates ?? throw new ArgumentNullException(nameof(coordinates))).ToAssembly(setting);
            copy._raisedLapBaselineNative = null;
            copy._raisedLapBaselinePointIds = Array.Empty<string>();
            return copy;
        }
        public string CopperSectionId { get; private set; }
        public BridgeDesignRoute WithCopperSection(int index)
        {
            var copy = WithPhysicalGeometry(SectionWidth, SectionThickness, PhysicalSolids);
            copy.CopperSectionId = BridgeCopperSection.IdFor(index);
            return copy;
        }
        public IReadOnlyList<double[][]> PhysicalSolids { get; private set; } = Array.Empty<double[][]>();
        public BridgeDesignRoute WithPhysicalGeometry(double width, double thickness, IEnumerable<double[][]> solids)
        {
            var copy = WithPhysicalGeometry(width, thickness);
            copy.PhysicalSolids = Array.AsReadOnly(solids.Select(s => s.Select(p => p.ToArray()).ToArray()).ToArray());
            return copy;
        }
        public bool IsPhysicalGeometry { get; private set; }
        public double SectionWidth { get; private set; }
        public double SectionThickness { get; private set; }
        public BridgeDesignRoute WithPhysicalGeometry(double width, double thickness)
        {
            if (width <= 0d || thickness <= 0d || double.IsNaN(width) || double.IsNaN(thickness) ||
                double.IsInfinity(width) || double.IsInfinity(thickness)) throw new ArgumentException("物理排件规格无效。");
            var copy = new BridgeDesignRoute(Id, Phase, PointIds, LSegmentStartIds, StraightLapStartIds);
            copy.IsPhysicalGeometry = true; copy.SectionWidth = width; copy.SectionThickness = thickness;
            copy.CopperSectionId = CopperSectionId;
            copy.GenerationGroup = GenerationGroup; copy.IsBridge = IsBridge;
            copy.ComponentBinding = ComponentBinding;
            copy._nativePreview = BridgeNativePreviewCopy.Copy(_nativePreview);
            copy._raisedLapBaselineNative = BridgeNativePreviewCopy.Copy(_raisedLapBaselineNative);
            copy._raisedLapBaselinePointIds = Array.AsReadOnly(_raisedLapBaselinePointIds.ToArray());
            return copy;
        }
        public string Id { get; }
        public string Phase { get; }
        public IReadOnlyList<string> PointIds { get; }
        public BridgeDesignRoute WithPointIds(IEnumerable<string> pointIds)
        {
            var ids = pointIds.ToArray();
            Func<string, string> mapped = id => {
                var index = PointIds.ToList().IndexOf(id);
                return index >= 0 && index < ids.Length - 1 ? ids[index] : null;
            };
            return new BridgeDesignRoute(Id, Phase, ids,
                LSegmentStartIds.Select(mapped).Where(id => id != null),
                StraightLapStartIds.Select(mapped).Where(id => id != null)) {
                IsPhysicalGeometry = IsPhysicalGeometry, SectionWidth = SectionWidth, SectionThickness = SectionThickness,
                PhysicalSolids = PhysicalSolids, CopperSectionId = CopperSectionId,
                GenerationGroup = GenerationGroup, IsBridge = IsBridge, ComponentBinding = ComponentBinding,
                _nativePreview = BridgeNativePreviewCopy.Copy(_nativePreview),
                _raisedLapBaselineNative = BridgeNativePreviewCopy.Copy(_raisedLapBaselineNative),
                _raisedLapBaselinePointIds = Array.AsReadOnly(_raisedLapBaselinePointIds.ToArray())
            };
        }
        public IReadOnlyCollection<string> LSegmentStartIds { get; }
        public IReadOnlyCollection<string> StraightLapStartIds { get; }
        public BridgeDesignRoute(string id, string phase, IEnumerable<string> pointIds)
            : this(id, phase, pointIds, Array.Empty<string>()) { }
        // 原三参入口继续有效；L排必须显式提供段起点ID，不能根据转角自动推断。
        public BridgeDesignRoute(string id, string phase, IEnumerable<string> pointIds, IEnumerable<string> lSegmentStartIds)
            : this(id, phase, pointIds, lSegmentStartIds, Array.Empty<string>()) { }
        // 兼容已有路径入口；A端进基座的直直搭接显式标识，不根据柜名或路径转角推断。
        public BridgeDesignRoute(string id, string phase, IEnumerable<string> pointIds, IEnumerable<string> lSegmentStartIds, IEnumerable<string> straightLapStartIds)
        {
            Id = id; Phase = phase; PointIds = Array.AsReadOnly(pointIds.ToArray());
            LSegmentStartIds = Array.AsReadOnly(lSegmentStartIds.Distinct().ToArray());
            StraightLapStartIds = Array.AsReadOnly(straightLapStartIds.Distinct().ToArray());
            if (LSegmentStartIds.Any(start => !PointIds.Take(Math.Max(0, PointIds.Count - 1)).Contains(start)))
                throw new ArgumentException("L排标识必须属于路径中的有效段起点。", nameof(lSegmentStartIds));
            if (StraightLapStartIds.Any(start => !LSegmentStartIds.Contains(start)))
                throw new ArgumentException("直直搭接必须指向已标识的L排段起点。", nameof(straightLapStartIds));
        }
    }

    public sealed class BridgeDesignIssue
    {
        public string Message { get; }
        public string TargetId { get; }
        public string Source { get; }
        public bool SideView { get; }
        public bool BlocksGeneration { get; }
        public BridgeIssueLocation Location { get; private set; }
        public BridgeDesignIssue WithLocation(BridgeIssueLocation location)
        {
            return new BridgeDesignIssue(Message, TargetId, Source, SideView, BlocksGeneration) { Location = location };
        }
        public string LevelText => BlocksGeneration ? "阻断" : "提示";
        // 保留原入口；未明确分类的问题仍阻止生成。
        public BridgeDesignIssue(string message, string targetId, string source = "演示工程问题", bool sideView = false)
            : this(message, targetId, source, sideView, true) { }
        public BridgeDesignIssue(string message, string targetId, string source, bool sideView, bool blocksGeneration)
        {
            Message = message; TargetId = targetId; Source = source; SideView = sideView;
            BlocksGeneration = blocksGeneration;
        }
    }

    /// <summary>校验时实际参与计算的局部包络；坐标与草稿预览一致，不从提示文本猜测位置。</summary>
    public sealed class BridgeIssueLocation
    {
        public string RelatedObjectId { get; }
        public string FirstLabel { get; }
        public string SecondLabel { get; }
        public double ActualDistance { get; }
        public double RequiredDistance { get; }
        public IReadOnlyList<IReadOnlyList<double>> CopperCorners { get; }
        public IReadOnlyList<IReadOnlyList<double>> MetalCorners { get; }
        public string DistanceText => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "实测 {0:0.0} mm / 要求 ≥ {1:0.0} mm", ActualDistance, RequiredDistance);

        public BridgeIssueLocation(string relatedObjectId, double actualDistance, double requiredDistance,
            IEnumerable<double[]> copperCorners, IEnumerable<double[]> metalCorners)
            : this(relatedObjectId, actualDistance, requiredDistance, copperCorners, metalCorners, null, null) { }

        public BridgeIssueLocation(string relatedObjectId, double actualDistance, double requiredDistance,
            IEnumerable<double[]> copperCorners, IEnumerable<double[]> metalCorners, string firstLabel, string secondLabel)
        {
            FirstLabel = firstLabel; SecondLabel = secondLabel;
            RelatedObjectId = relatedObjectId; ActualDistance = actualDistance; RequiredDistance = requiredDistance;
            CopperCorners = Array.AsReadOnly(copperCorners.Select(p => (IReadOnlyList<double>)Array.AsReadOnly(p.ToArray())).ToArray());
            MetalCorners = Array.AsReadOnly(metalCorners.Select(p => (IReadOnlyList<double>)Array.AsReadOnly(p.ToArray())).ToArray());
        }
    }

    public sealed class BridgeDesignSource
    {
        public string Scenario { get; }
        public string Summary { get; }
        public double BusbarWidth { get; }
        public double BusbarThickness { get; }
        public string SourceLabel { get; }
        public bool IsModelDerived { get; }
        public BridgeDesignSource(string scenario, string summary, double width, double thickness)
            : this(scenario, summary, width, thickness, "内置演示快照（未读取模型）", false)
        {
        }
        public BridgeDesignSource(string scenario, string summary, double width, double thickness,
            string sourceLabel, bool isModelDerived)
        {
            Scenario = scenario; Summary = summary;
            BusbarWidth = BridgeDesignPrecision.OneDecimal(width);
            BusbarThickness = BridgeDesignPrecision.OneDecimal(thickness);
            SourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? "未标识的数据源" : sourceLabel;
            IsModelDerived = isModelDerived;
        }
    }

    /// <summary>桥体设计参数，独立于柜体驱动的只读规格；当前为暂定快照，后续可接用户确认流程。</summary>
    public sealed class BridgeDesignParameters
    {
        public double OverlapLengthMm { get; }
        public double InsulationClearanceMm { get; }
        public BridgeDesignParameters(double overlapLengthMm)
            : this(overlapLengthMm, 125d) { }
        public BridgeDesignParameters(double overlapLengthMm, double insulationClearanceMm)
            : this(overlapLengthMm, insulationClearanceMm, false) { }
        internal BridgeDesignParameters(double overlapLengthMm, double insulationClearanceMm, bool preserveOverlapPrecision)
        {
            var overlap = preserveOverlapPrecision ? overlapLengthMm : BridgeDesignPrecision.OneDecimal(overlapLengthMm);
            if (double.IsNaN(overlap) || double.IsInfinity(overlap) || overlap <= 0)
                throw new ArgumentOutOfRangeException(nameof(overlapLengthMm), "搭接长度必须为有限正数。");
            OverlapLengthMm = overlap;
            if (double.IsNaN(insulationClearanceMm) || double.IsInfinity(insulationClearanceMm) || insulationClearanceMm <= 0d)
                throw new ArgumentOutOfRangeException(nameof(insulationClearanceMm), "相间绝缘距离必须为有限正数。");
            // 保留原规划精度，不能因显示量化而降低相间校验门槛。
            InsulationClearanceMm = insulationClearanceMm;
        }
    }

    /// <summary>基座L排断点的显式归属及原始直段边界，均为预览坐标中的毫米值。</summary>
    public sealed class BridgeBaseJointConstraint
    {
        public string JointGroupId { get; }
        public string BaseId { get; }
        public double BoundaryX { get; }
        public double OutwardSign { get; }
        public double MinimumX { get; }
        public double MaximumX { get; }
        public BridgeBaseJointConstraint(string groupId, string baseId, double boundaryX,
            double outwardSign, double minimumX, double maximumX)
        {
            JointGroupId = groupId; BaseId = baseId; BoundaryX = boundaryX;
            OutwardSign = outwardSign; MinimumX = minimumX; MaximumX = maximumX;
        }
    }

    // 仅记录长度编辑约束和选择，不创建组件，也不替代实物反读几何。
    internal sealed class BridgeFrameLengthEditing
    {
        internal IReadOnlyList<BridgeDesignObject> BaselineFrames { get; }
        internal IReadOnlyList<string> IndependentFrames { get; }
        internal IReadOnlyList<string> FixedEndFrames { get; }
        internal IReadOnlyList<string> LengthAdjustedFrames { get; }
        internal double? OffsetCopperMinimumLengthMm { get; }
        internal BridgeFrameLengthEditing(IEnumerable<BridgeDesignObject> baseline, IEnumerable<string> independent,
            IEnumerable<string> fixedEnd, IEnumerable<string> adjusted, double? offsetCopperMinimumLengthMm = null)
        {
            if (offsetCopperMinimumLengthMm.HasValue && (double.IsNaN(offsetCopperMinimumLengthMm.Value) ||
                double.IsInfinity(offsetCopperMinimumLengthMm.Value) || offsetCopperMinimumLengthMm.Value <= 0))
                throw new ArgumentException("偏移铜排工艺最小长度无效。");
            OffsetCopperMinimumLengthMm = offsetCopperMinimumLengthMm;
            BaselineFrames = Array.AsReadOnly(baseline.ToArray());
            IndependentFrames = Array.AsReadOnly(independent.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray());
            FixedEndFrames = Array.AsReadOnly(fixedEnd.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray());
            LengthAdjustedFrames = Array.AsReadOnly(adjusted.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray());
        }
        internal BridgeFrameLengthEditing RetainFor(IEnumerable<BridgeDesignObject> objects)
        {
            var ids = new HashSet<string>(objects.Where(o => o.Kind == DesignObjectKind.Frame).Select(o => o.Id));
            // 被删除节仍是缺口的边界基线；只有当前节的编辑标记需要过滤。
            return new BridgeFrameLengthEditing(BaselineFrames, IndependentFrames.Where(ids.Contains),
                FixedEndFrames.Where(id => ids.Contains(id) && IndependentFrames.Contains(id)), LengthAdjustedFrames.Where(ids.Contains), OffsetCopperMinimumLengthMm);
        }
    }

    public sealed class BridgeDesignSample
    {
        internal BridgeHangerLayout SavedHangers { get; private set; }
        internal BridgeHangerRoof HangerRoof { get; private set; }
        internal string HangerRoofProblem { get; private set; }
        internal BridgeDesignSample WithHangers(BridgeHangerLayout layout)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.SavedHangers = layout;
            return copy;
        }
        internal BridgeDesignSample WithHangerRoof(BridgeHangerRoof roof, string problem)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.HangerRoof = roof;
            copy.HangerRoofProblem = problem;
            return copy;
        }
        internal IReadOnlyList<BridgeDesignObject> CopperLengthBaseline { get; private set; }
        internal BridgeDesignSample WithCopperLengthBaseline(IEnumerable<BridgeDesignObject> controls)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.CopperLengthBaseline = controls == null ? null : Array.AsReadOnly(controls.ToArray()); return copy;
        }
        internal double OffsetCopperSafetyShift { get; private set; }
        internal BridgeDesignSample WithOffsetCopperSafetyShift(double shift)
        {
            if (double.IsNaN(shift) || double.IsInfinity(shift)) throw new ArgumentOutOfRangeException(nameof(shift));
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper); copy.OffsetCopperSafetyShift = shift; return copy;
        }
        internal BridgeFrameLengthEditing SavedFrameLengthEditing { get; private set; }
        internal IReadOnlyList<BridgeDesignObject> FrameLengthBaseline => SavedFrameLengthEditing?.BaselineFrames ??
            Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
        internal BridgeDesignSample WithFrameLengthEditing(BridgeFrameLengthEditing editing)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper); copy.SavedFrameLengthEditing = editing; return copy;
        }
        public BridgeReadbackBaseline ReadbackBaseline { get; private set; }
        public BridgeDesignSample WithReadbackBaseline(BridgeReadbackBaseline baseline)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.ReadbackBaseline = baseline;
            return copy;
        }
        public string ReadbackBaselineWarning { get; private set; }
        internal BridgeDesignSample WithReadbackBaselineWarning(string warning)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.ReadbackBaselineWarning = warning;
            return copy;
        }
        internal IReadOnlyList<string> SavedLockedObjectIds { get; private set; } = Array.Empty<string>();
        internal BridgeDesignSample WithSavedLocks(IEnumerable<string> ids)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.SavedLockedObjectIds = Array.AsReadOnly(ids.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray());
            return copy;
        }
        internal IReadOnlyList<string> SavedManualInsulatorIds { get; private set; } = Array.Empty<string>();
        internal IReadOnlyList<string> SavedManualJointIds { get; private set; } = Array.Empty<string>();
        internal BridgeDesignSample WithSavedManualJoints(IEnumerable<string> ids)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.SavedManualJointIds = Array.AsReadOnly(ids.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray());
            return copy;
        }
        internal BridgeDesignSample WithSavedManualInsulators(IEnumerable<string> ids)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.SavedManualInsulatorIds = Array.AsReadOnly(ids.Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray());
            return copy;
        }
        public double? PreviewOriginX { get; private set; }
        public double? PreviewOriginZ { get; private set; }
        public BridgeCoordinateFrame Coordinates { get; private set; }
        public string ScenarioKey { get; private set; } = "FaceToFace";
        public bool SupportsFormalGeneration => Source.IsModelDerived && (ScenarioKey != "FaceToWall" || WallPlanningRecipe != null);
        internal string WallPlanningRecipe { get; private set; }
        private BridgeWallPlanningCodec.WallPlanningData _wallPlanningState;
        internal BridgeWallPlanningCodec.WallPlanningData WallPlanningState =>
            WallPlanningRecipe == null ? null : _wallPlanningState ??
                (_wallPlanningState = new BridgeWallPlanningCodec().DecodeWallPlanning(WallPlanningRecipe));
        internal BridgeDesignSample WithWallPlanningState(BridgeWallPlanningCodec.WallPlanningData state)
        {
            var copy = WithWallPlanningRecipe(new BridgeWallPlanningCodec().EncodeWallPlanning(state));
            copy._wallPlanningState = state;
            return copy;
        }
        internal bool IsSideToSide => ScenarioKey == "SideToSideBase1" || ScenarioKey == "SideToSideBase2";

        internal double CopperSectionLength(BridgeDesignObject start, BridgeDesignObject end)
        {
            var endX = end.X;
            if (ScenarioKey == "FaceToWall" && end.Id.StartsWith("wall-edit-end-", StringComparison.Ordinal) && WallPlanningState != null)
            {
                var terminal = WallPlanningState.Request.Groups.Single(g => g.Phase == "B").EndTerminal;
                // 编辑端点是Worker虚拟接口；长度量至真实铜排末端，不能少算末端这一段搭接。
                endX = terminal.RouteAnchor.X + terminal.LengthDirection.X * terminal.Members[0].FixedBusbar.EndLapWidth;
            }
            return Math.Abs(endX - start.X) + Parameters.OverlapLengthMm;
        }
        internal BridgeDesignSample WithWallPlanningRecipe(string recipe)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.WallPlanningRecipe = recipe;
            copy._wallPlanningState = recipe == WallPlanningRecipe ? _wallPlanningState : null;
            return copy;
        }
        public BridgeDesignSample WithCoordinates(BridgeCoordinateFrame coordinates, string scenario)
        {
            if (scenario != "FaceToFace" && scenario != "SideToSideBase1" && scenario != "SideToSideBase2" &&
                scenario != "FaceToWall")
                throw new ArgumentException("草稿场景身份无效。");
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.Coordinates = coordinates ?? throw new ArgumentNullException(nameof(coordinates));
            copy.ScenarioKey = scenario;
            copy.PreviewOriginX = coordinates.Origin[0]; copy.PreviewOriginZ = coordinates.Origin[2];
            return copy;
        }
        public BridgeDesignSample WithPreviewOrigin(double x, double z)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(z) || double.IsInfinity(z))
                throw new ArgumentException("总装预览原点必须为有限坐标。");
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.PreviewOriginX = x; copy.PreviewOriginZ = z;
            copy.Coordinates = BridgeCoordinateFrame.Facing(x, z);
            return copy;
        }
        public IReadOnlyList<BridgeBaseJointConstraint> BaseJointConstraints { get; private set; } = Array.Empty<BridgeBaseJointConstraint>();
        public BridgeDesignSample WithBaseJointConstraints(IEnumerable<BridgeBaseJointConstraint> constraints)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.BaseJointConstraints = Array.AsReadOnly(constraints.ToArray());
            return copy;
        }
        // 编辑路径只承载断点参数，不绘制成第二套铜排；提交时由原几何规划器重新计算实体。
        public IReadOnlyList<BridgeDesignRoute> CopperEditRoutes { get; private set; } = Array.Empty<BridgeDesignRoute>();
        public IReadOnlyCollection<string> SpecialCopperSectionIds { get; private set; }
        public BridgeDesignSample WithSpecialCopperSections(IEnumerable<int> indices)
            => WithSpecialCopperSectionIds(indices.Select(BridgeCopperSection.IdFor));

        internal BridgeDesignSample WithSpecialCopperSectionIds(IEnumerable<string> ids)
        {
            var copy = WithCopperEditing(CopperEditRoutes, ReplanCopper);
            copy.SpecialCopperSectionIds = ids == null ? null : Array.AsReadOnly(ids.Distinct().ToArray());
            return copy;
        }
        public Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample> ReplanCopper { get; private set; }
        public BridgeDesignSample WithCopperEditing(IEnumerable<BridgeDesignRoute> routes,
            Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample> replan)
        {
            var copy = new BridgeDesignSample(Id, Source, Objects, Routes, Issues, Parameters);
            copy.CopperEditRoutes = Array.AsReadOnly(routes.ToArray());
            copy.CopperLengthBaseline = CopperLengthBaseline ?? (copy.CopperEditRoutes.Count == 0 ? null :
                Array.AsReadOnly(Objects.Where(o => o.IsJointGroup).ToArray()));
            copy.ReplanCopper = replan;
            copy.BaseJointConstraints = BaseJointConstraints;
            copy.PreviewOriginX = PreviewOriginX; copy.PreviewOriginZ = PreviewOriginZ;
            copy.Coordinates = Coordinates; copy.ScenarioKey = ScenarioKey;
            copy.SavedManualInsulatorIds = SavedManualInsulatorIds;
            copy.SavedManualJointIds = SavedManualJointIds;
            copy.SavedLockedObjectIds = SavedLockedObjectIds;
            copy.SavedFrameLengthEditing = SavedFrameLengthEditing;
            copy.OffsetCopperSafetyShift = OffsetCopperSafetyShift;
            copy.WallPlanningRecipe = WallPlanningRecipe;
            copy._wallPlanningState = _wallPlanningState;
            copy.SpecialCopperSectionIds = SpecialCopperSectionIds;
            copy.ReadbackBaselineWarning = ReadbackBaselineWarning;
            copy.ReadbackBaseline = ReadbackBaseline;
            copy.SavedHangers = SavedHangers; copy.HangerRoof = HangerRoof; copy.HangerRoofProblem = HangerRoofProblem;
            return copy;
        }
        internal BridgeDesignSample WithGeometry(IEnumerable<BridgeDesignObject> objects, IEnumerable<BridgeDesignRoute> routes,
            IEnumerable<BridgeDesignIssue> issues = null)
        {
            return new BridgeDesignSample(Id, Source, objects, routes, issues ?? Issues, Parameters)
            {
                CopperEditRoutes = CopperEditRoutes, ReplanCopper = ReplanCopper, SpecialCopperSectionIds = SpecialCopperSectionIds,
                BaseJointConstraints = BaseJointConstraints, PreviewOriginX = PreviewOriginX,
                PreviewOriginZ = PreviewOriginZ, Coordinates = Coordinates, ScenarioKey = ScenarioKey,
                SavedManualInsulatorIds = SavedManualInsulatorIds, SavedLockedObjectIds = SavedLockedObjectIds, WallPlanningRecipe = WallPlanningRecipe,
                SavedManualJointIds = SavedManualJointIds,
                _wallPlanningState = _wallPlanningState,
                SavedFrameLengthEditing = SavedFrameLengthEditing,
                CopperLengthBaseline = CopperLengthBaseline,
                OffsetCopperSafetyShift = OffsetCopperSafetyShift,
                ReadbackBaselineWarning = ReadbackBaselineWarning, ReadbackBaseline = ReadbackBaseline,
                SavedHangers = SavedHangers, HangerRoof = HangerRoof, HangerRoofProblem = HangerRoofProblem
            };
        }
        public string Id { get; }
        public BridgeDesignSource Source { get; }
        public BridgeDesignParameters Parameters { get; }
        public IReadOnlyList<BridgeDesignObject> Objects { get; }
        public IReadOnlyList<BridgeDesignRoute> Routes { get; }
        public IReadOnlyList<BridgeDesignIssue> Issues { get; }
        public BridgeDesignSample(string id, BridgeDesignSource source, IEnumerable<BridgeDesignObject> objects,
            IEnumerable<BridgeDesignRoute> routes, IEnumerable<BridgeDesignIssue> issues)
            : this(id, source, objects, routes, issues, new BridgeDesignParameters(source.BusbarWidth)) { }
        // 旧入口仅在初始化时以排宽给暂定默认值；显式传入后，搭接长度不再依赖排宽。
        public BridgeDesignSample(string id, BridgeDesignSource source, IEnumerable<BridgeDesignObject> objects,
            IEnumerable<BridgeDesignRoute> routes, IEnumerable<BridgeDesignIssue> issues, BridgeDesignParameters parameters)
        {
            Id = id; Source = source; Objects = Array.AsReadOnly(objects.ToArray());
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            Routes = Array.AsReadOnly(routes.ToArray()); Issues = Array.AsReadOnly(issues.ToArray());
            if (Objects.Select(o => o.Id).Distinct().Count() != Objects.Count)
                throw new ArgumentException("示例对象ID重复。", nameof(objects));
            foreach (var group in Objects.Where(o => o.IsJointGroup).GroupBy(o => o.JointGroupId))
                if (group.Count() != 3 || !group.Select(o => o.Phase).OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(new[] { "A", "B", "C" }) || group.Select(o => o.Kind).Distinct().Count() != 1)
                    throw new ArgumentException("同一断点组必须包含同类型的ABC三相各一个节点。", nameof(objects));
            foreach (var group in Objects.Where(o => o.IsInsulatorGroup).GroupBy(o => o.InsulatorGroupId))
                if (group.Select(o => o.Kind).Distinct().Count() != 1 || group.Select(o => o.ParentId).Distinct().Count() != 1)
                    throw new ArgumentException("绝缘子组不能混合不同安装归属或固定/可调类型。", nameof(objects));
        }
    }

    public sealed class BridgeEditResult
    {
        public bool Success { get; }
        public string Message { get; }
        public BridgeEditResult(bool success, string message = null) { Success = success; Message = message; }
    }
}
