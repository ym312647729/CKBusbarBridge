using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace CKBusbarBridge.Prototype
{
    public sealed class BridgeHangerSetting
    {
        [JsonProperty] public string Id { get; private set; }
        [JsonProperty] public double? Distance { get; private set; }
        [JsonProperty] public string PreferredFrameId { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public BridgeHangerSectionKind? Section { get; private set; }
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool IsAutomatic { get; private set; }

        [JsonConstructor]
        public BridgeHangerSetting(string id, double? distance, string preferredFrameId, BridgeHangerSectionKind? section = null,
            bool isAutomatic = false)
        {
            if (string.IsNullOrWhiteSpace(id) || (distance.HasValue &&
                (!HangerVectors.Finite(distance.Value) || distance.Value < 0)))
                throw new ArgumentException("吊架标识或中心线距离无效。");
            Id = id;
            Distance = distance;
            PreferredFrameId = preferredFrameId;
            Section = section;
            IsAutomatic = isAutomatic;
        }
    }

    public sealed class BridgeHangerLayout
    {
        [JsonProperty] public string StartFrameId { get; private set; }
        [JsonProperty] public double? Direction { get; private set; }
        [JsonProperty] public IReadOnlyList<BridgeHangerSetting> Settings { get; private set; }

        [JsonConstructor]
        public BridgeHangerLayout(string startFrameId, double? direction, IEnumerable<BridgeHangerSetting> settings)
        {
            StartFrameId = startFrameId;
            Direction = direction;
            Settings = Array.AsReadOnly(settings.ToArray());
            if (direction.HasValue && !HangerVectors.Finite(direction.Value) ||
                Settings.Any(item => item == null) || Settings.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != Settings.Count)
                throw new ArgumentException("吊架起始方向或吊架记录无效。");
        }
    }

    public sealed class BridgeHangerPlacement
    {
        public BridgeHangerSetting Setting { get; }
        public string Id => Setting.Id;
        public string Name { get; }
        public BridgeHangerSectionKind Section { get; internal set; }
        internal bool HasSection { get; set; }
        public string Status { get; internal set; }
        public string Problem { get; internal set; }
        public BridgeDesignObject Frame { get; internal set; }
        public string FrameLabel => Frame == null ? "—" : Describe(Frame);
        public IReadOnlyList<BridgeDesignObject> Candidates { get; internal set; } = Array.Empty<BridgeDesignObject>();
        public BridgeHangerPose Pose { get; internal set; }
        public BridgeHangerPlan Plan { get; internal set; }
        public double? Transverse { get; internal set; }
        public bool IsValid => Problem == null && Plan != null;

        internal BridgeHangerPlacement(BridgeHangerSetting setting, int index)
        {
            Setting = setting;
            Name = "吊架" + (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        internal void Reject(string status, string problem) { Status = status; Problem = problem; }
        public static string Describe(BridgeDesignObject frame) => frame.Name + " · 位置 " + frame.FramePositionIndex;
    }

    public sealed partial class BridgeHangerLayoutPlanner
    {
        private readonly BusbarBridgeDraftPreviewSceneFactory _frameGeometry = new BusbarBridgeDraftPreviewSceneFactory();
        private readonly BridgeHangerGeometry _hangerGeometry = new BridgeHangerGeometry();
        private const double Tolerance = BridgeHangerGeometry.Tolerance;

        internal static BridgeDesignObject[] Frames(IEnumerable<BridgeDesignObject> objects) => objects
            .Where(item => item.Kind == DesignObjectKind.Frame).OrderBy(item => item.FramePositionIndex).ToArray();

        private static double[] Forward(double direction)
        {
            var angle = direction * Math.PI / 180.0;
            return new[] { Math.Cos(angle), Math.Sin(angle), 0.0 };
        }

        internal double[] Start(BridgeDesignObject frame) => _frameGeometry.FrameInterfacePoints(frame, useDrivenPosition: true)[0];
        internal bool TryStart(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, BridgeHangerLayout layout,
            out double[] startPoint)
        {
            startPoint = null;
            var frame = objects.SingleOrDefault(item => item.Kind == DesignObjectKind.Frame && item.Id == layout?.StartFrameId);
            if (frame == null || sample?.Coordinates == null || layout?.Direction == null ||
                frame.FrameTemplateKey == "LargeOffsetCorner" && frame.CornerPreview == null ||
                frame.FrameTemplateKey == "SmallOffset" && frame.SmallOffsetPreview == null)
                return false;
            startPoint = Start(frame);
            return true;
        }
        internal double Distance(BridgeDesignSample sample, BridgeDesignObject start, double direction, double[] installationOrigin) =>
            HangerVectors.Dot(HangerVectors.Sub(sample.Coordinates.ToDraft(installationOrigin), Start(start)), Forward(direction));

        public IReadOnlyList<BridgeHangerPlacement> Locate(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects,
            BridgeHangerLayout layout, BridgeHangerRoof roof, string roofProblem = null,
            Func<BridgeDesignObject, BridgeHangerPose> readPose = null)
        {
            if (sample == null || layout == null || layout.Settings.Count == 0) return Array.Empty<BridgeHangerPlacement>();
            var rows = layout.Settings.Select((setting, index) => new BridgeHangerPlacement(setting, index)).ToArray();
            if (!TryStart(sample, objects, layout, out var startPoint))
            {
                foreach (var row in rows)
                {
                    row.Section = row.Setting.Section ?? BridgeHangerSectionKind.Whole;
                    row.Reject("基准失效", "原起始边无法识别，请重新确认吊架基准。");
                }
                return rows;
            }
            try
            {
                var sections = Sections(sample, objects, layout);
                var ranges = sections.ToDictionary(section => section.Kind, section => section.Frames.Select(frame =>
                {
                    var stations = Ports(frame).Select(section.Station).ToArray();
                    return new { Frame = frame, Start = stations.Min(), End = stations.Max() };
                }).ToArray());
                var forward = Forward(layout.Direction.Value);
                foreach (var row in rows)
                {
                    var section = SectionFor(row.Setting, sections);
                    row.Section = section?.Kind ?? row.Setting.Section ?? BridgeHangerSectionKind.Whole;
                    row.HasSection = section != null;
                    if (!row.Setting.Distance.HasValue) { row.Reject("待填", "请填写中心线距起始边。"); continue; }
                    var distance = row.Setting.Distance.Value;
                    if (section == null) { row.Reject("尺寸缺失", "该吊架不在当前结构可用的直段，请检查分段数量或调整归属。"); continue; }
                    if (distance < section.Start - Tolerance || distance > section.End + Tolerance)
                    { row.Reject("越界", "中心线距离超出" + BridgeHangerSection.Label(section.Kind) + "。"); continue; }
                    var candidates = ranges[section.Kind].Where(range => distance >= range.Start - Tolerance &&
                        distance <= range.End + Tolerance).ToArray();
                    row.Candidates = Array.AsReadOnly(candidates.Select(range => range.Frame).ToArray());
                    if (candidates.Length == 0) { row.Reject("越界", "该处未匹配到连续的基架安装区。"); continue; }
                    var candidate = candidates.Length == 1 ? candidates[0] :
                        candidates.SingleOrDefault(item => item.Frame.Id == row.Setting.PreferredFrameId);
                    if (candidate == null) { row.Reject("归属待选", "请从覆盖该位置的基架中选择。"); continue; }
                    row.Frame = candidate.Frame;
                    if (!_hangerGeometry.RequiresHanger(row.Frame.FrameTemplateKey))
                    { row.Reject("尺寸缺失", "该拐角缺少已核验的吊架安装面及可安装范围。"); continue; }
                    try
                    {
                        var frame = row.Frame;
                        if (frame.DriveValue(DesignField.Width) <= 0 || frame.DriveValue(DesignField.Height) <= 0 ||
                            frame.DriveValue(DesignField.Length) <= 0)
                            throw new InvalidOperationException("基架长、宽或高无有效驱动尺寸。");
                        var pose = readPose == null ? _hangerGeometry.DraftPose(sample, frame, objects) : readPose(frame);
                        var draftOrigin = sample.Coordinates.ToDraft(pose.Origin);
                        var draftAxis = sample.Coordinates.ToDraftDirection(pose.Along);
                        var longitudinal = HangerVectors.Dot(draftAxis, section.Axis);
                        var axisAngle = Math.Acos(Math.Min(1.0, Math.Abs(longitudinal))) * 180.0 / Math.PI;
                        if (axisAngle > Tolerance)
                            throw new InvalidOperationException("该基架安装轴与本段方向不平行，未核验可安装区域。");
                        var offset = (distance - section.Station(draftOrigin)) / longitudinal;
                        var footprint = BridgeHangerGeometry.LowerHalfLength * Math.Abs(longitudinal);
                        if (distance - footprint < candidate.Start - Tolerance || distance + footprint > candidate.End + Tolerance)
                        { row.Reject("越界", "吊架下横梁跨出本节基架的安装范围。"); continue; }
                        row.Pose = pose.At(row.Id, offset);
                        row.Transverse = HangerVectors.Dot(HangerVectors.Sub(sample.Coordinates.ToDraft(row.Pose.Origin), startPoint),
                            new[] { -forward[1], forward[0], 0.0 });
                        if (roof == null) throw new InvalidOperationException(roofProblem ?? "未读取总装横梁下表面。");
                        row.Plan = _hangerGeometry.Plan(row.Pose, roof);
                        row.Status = "有效";
                    }
                    catch (InvalidOperationException error) { row.Reject("尺寸缺失", error.Message); }
                }
            }
            catch (InvalidOperationException error)
            {
                foreach (var row in rows)
                {
                    row.Section = row.Setting.Section ?? BridgeHangerSectionKind.Whole;
                    row.Reject("尺寸缺失", error.Message);
                }
            }

            // 以模板下横梁和屋顶安装板的实际占位判重；没有额外的经验间距。
            var axes = new[] { sample.Coordinates.Along.ToArray(), sample.Coordinates.Across.ToArray(), sample.Coordinates.Up.ToArray() };
            var positioned = rows.Where(row => row.Plan != null).Select(row =>
            {
                var parts = _hangerGeometry.Preview(row.Plan).Select(solid => Ranges(solid, axes)).ToArray();
                return new { Row = row, Parts = parts, Start = parts.Min(part => part[0][0]), End = parts.Max(part => part[0][1]) };
            }).OrderBy(item => item.Start).ToArray();
            for (var i = 0; i < positioned.Length; i++)
                for (var j = i + 1; j < positioned.Length; j++)
                {
                    var first = positioned[i].Row; var second = positioned[j].Row;
                    if (positioned[j].Start > positioned[i].End + Tolerance) break;
                    var sameStation = first.Section == second.Section &&
                        Math.Abs(second.Setting.Distance.Value - first.Setting.Distance.Value) <= Tolerance;
                    if (!sameStation && !InstallationIntersects(positioned[i].Parts, positioned[j].Parts)) continue;
                    first.Reject("重叠", "与" + second.Name + "的安装位置重叠。");
                    second.Reject("重叠", "与" + first.Name + "的安装位置重叠。");
                }
            return rows;
        }

        private static bool InstallationIntersects(double[][][] firstRanges, double[][][] secondRanges)
        {
            // 大偏移的两条安装轴属同一主桥正交坐标，正式生成仍以实体交集复检。
            return firstRanges.Any(a => secondRanges.Any(b => Enumerable.Range(0, 3)
                .All(axis => Math.Min(a[axis][1], b[axis][1]) - Math.Max(a[axis][0], b[axis][0]) > Tolerance)));
        }

        private static double[][] Ranges(CKBusbarBridgePreview.DraftSupportSolid solid, double[][] axes)
        {
            return axes.Select(axis =>
            {
                if (solid.Round != null)
                {
                    var round = solid.Round;
                    var start = HangerVectors.Dot(round.Start.ToArray(), axis);
                    var end = HangerVectors.Dot(round.End.ToArray(), axis);
                    var direction = HangerVectors.Unit(HangerVectors.Sub(round.End.ToArray(), round.Start.ToArray()));
                    var dot = HangerVectors.Dot(direction, axis);
                    var radius = Math.Max(round.StartRadius, round.EndRadius) * Math.Sqrt(Math.Max(0.0, 1.0 - dot * dot));
                    return new[] { Math.Min(start, end) - radius, Math.Max(start, end) + radius };
                }
                var values = solid.CopyCorners().Select(point => HangerVectors.Dot(point, axis)).ToArray();
                return new[] { values.Min(), values.Max() };
            }).ToArray();
        }
    }
}
