using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    /// <summary>伸出排阵列变量表示实际每相根数E；设计及新生成根数N=E+1。</summary>
    public sealed class BridgeBusbarRootCounts
    {
        public int ExtensionCount { get; }
        public int GeneratedCount => ExtensionCount + 1;
        public BridgeBusbarRootCounts(double? arrayQuantity)
        {
            if (!arrayQuantity.HasValue || double.IsNaN(arrayQuantity.Value) ||
                double.IsInfinity(arrayQuantity.Value) || arrayQuantity.Value < 1d ||
                arrayQuantity.Value > 3d || arrayQuantity.Value != Math.Truncate(arrayQuantity.Value))
                throw new InvalidOperationException("伸出排阵列数量E必须为1到3的整数，对应设计及生成根数N为2到4；不支持无伸出排接口。");
            ExtensionCount = (int)arrayQuantity.Value;
        }

        public void ValidatePhases(int a, int b, int c, int total, string source)
        {
            if (a != ExtensionCount || b != ExtensionCount || c != ExtensionCount || total != 3 * ExtensionCount)
                throw new InvalidOperationException(source + "伸出排数量/相别不一致：阵列E=" + ExtensionCount +
                    "，设计及生成N=" + GeneratedCount + "；期望每相" + ExtensionCount +
                    "根，实际A=" + a + "、B=" + b + "、C=" + c + "、总数=" + total + "。");
        }
    }

    /// <summary>总装毫米坐标中的固定端排；纯快照，不保存COM对象。</summary>
    public sealed class BridgeUpstreamEndpoint
    {
        public string Side { get; }
        public string Phase { get; }
        public int Member { get; }
        public IReadOnlyList<IReadOnlyList<double>> Points { get; }
        public IReadOnlyList<double> Interface { get; }
        private readonly CKBusbarBridgePreview.PlanarBendBusbarSetting _nativePreview;
        public CKBusbarBridgePreview.PlanarBendBusbarSetting CopyNativePreview() => BridgeNativePreviewCopy.Copy(_nativePreview);
        public BridgeUpstreamEndpoint(string side, string phase, int member,
            IEnumerable<double[]> points, double[] connection)
            : this(side, phase, member, points, connection, null) { }

        public BridgeUpstreamEndpoint(string side, string phase, int member,
            IEnumerable<double[]> points, double[] connection,
            CKBusbarBridgePreview.PlanarBendBusbarSetting nativePreview)
        {
            if ((side != "A" && side != "B") || (phase != "A" && phase != "B" && phase != "C") || member < 0)
                throw new ArgumentException("固定端排必须具有有效柜号、相别及成员编号。");
            Side = side; Phase = phase; Member = member;
            var copied = points.Select(p => (IReadOnlyList<double>)Array.AsReadOnly((double[])p.Clone())).ToArray();
            if (copied.Length < 2 || copied.Any(p => p.Count != 3 || p.Any(v => double.IsNaN(v) || double.IsInfinity(v))) ||
                connection == null || connection.Length != 3 || connection.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("固定端排几何必须包含完整的有限三维坐标。");
            Points = Array.AsReadOnly(copied);
            Interface = Array.AsReadOnly((double[])connection.Clone());
            // 点位以端排快照为准（包括人工修改的尾段），加工参数保留原生成器结果。
            _nativePreview = BridgeNativePreviewCopy.Copy(nativePreview);
            if (_nativePreview != null)
                _nativePreview.PointList = copied.Select(p => new GeometRi.Point3d(p[0], p[1], p[2])).ToList();
        }
    }

    /// <summary>只读显示网格；每9个毫米坐标组成一个三角形，不参与工程几何计算。</summary>
    public sealed class BridgePreviewMesh
    {
        public IReadOnlyList<double> Vertices { get; }
        public double? TopSectionHeight { get; }
        public BridgePreviewMesh(IEnumerable<double> vertices) : this(vertices, null) { }
        public BridgePreviewMesh(IEnumerable<double> vertices, double? topSectionHeight)
        {
            var copied = vertices?.ToArray();
            if (copied == null || copied.Length == 0 || copied.Length % 9 != 0 ||
                copied.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("预览网格必须包含完整的有限三角形坐标。", nameof(vertices));
            Vertices = Array.AsReadOnly(copied);
            if (topSectionHeight.HasValue && (double.IsNaN(topSectionHeight.Value) || double.IsInfinity(topSectionHeight.Value)))
                throw new ArgumentException("俯视剖切高度必须为有限数值。", nameof(topSectionHeight));
            TopSectionHeight = topSectionHeight;
        }
    }

    /// <summary>绝缘子配合面中心、朝外轴向及驱动本体高度；外形使用统一预览模板。</summary>
    public sealed class BridgeInsulatorPose
    {
        public IReadOnlyList<double> Connection { get; }
        public IReadOnlyList<double> Axis { get; }
        public double Height { get; }
        public int SinglePhaseRootCount { get; }
        public int PadCount => SinglePhaseRootCount - 1;
        public double PadThickness { get; }
        public IReadOnlyList<double> SpanAxis { get; }
        // 仅三相中间成员携带整套支架，避免重复显示三次。
        public double BracketSpan { get; }
        public BridgeInsulatorPose(IEnumerable<double> connection, IEnumerable<double> axis, double height,
            int singlePhaseRootCount = 1, double padThickness = 0,
            IEnumerable<double> spanAxis = null, double bracketSpan = 0)
        {
            var point = connection?.ToArray(); var direction = axis?.ToArray();
            if (point == null || direction == null || point.Length != 3 || direction.Length != 3 ||
                point.Concat(direction).Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                double.IsNaN(height) || double.IsInfinity(height) || height <= 40d)
                throw new ArgumentException("绝缘子预览缺少有效配合位置、轴向或本体高度。");
            var length = Math.Sqrt(direction.Sum(v => v * v));
            if (double.IsInfinity(length) || length < .000001d) throw new ArgumentException("绝缘子预览轴向必须有限且不得为零。");
            Connection = Array.AsReadOnly(point);
            Axis = Array.AsReadOnly(direction.Select(v => v / length).ToArray());
            Height = height;
            if (singlePhaseRootCount < 1 || singlePhaseRootCount > 4 ||
                double.IsNaN(padThickness) || double.IsInfinity(padThickness) || padThickness < 0 ||
                (singlePhaseRootCount > 1 && padThickness <= 0) ||
                double.IsNaN(bracketSpan) || double.IsInfinity(bracketSpan) || bracketSpan < 0 ||
                (bracketSpan > 0 && bracketSpan < 100))
                throw new ArgumentException("绝缘子垫块必须按单相根数减一配置，并提供有效排厚和支架长度。");
            var span = spanAxis?.ToArray();
            if (span != null)
            {
                if (span.Length != 3 || span.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    throw new ArgumentException("绝缘子支架跨相方向无效。");
                var norm = Math.Sqrt(span.Sum(v => v * v));
                if (double.IsInfinity(norm) || norm < .000001 || Math.Abs(span.Zip(Axis, (a, b) => a * b).Sum()) / norm > .000001)
                    throw new ArgumentException("绝缘子支架跨相方向必须垂直于本体轴向。");
                SpanAxis = Array.AsReadOnly(span.Select(v => v / norm).ToArray());
            }
            if (bracketSpan > 0 && SpanAxis == null)
                throw new ArgumentException("基座绝缘子支架缺少实际跨相方向。");
            SinglePhaseRootCount = singlePhaseRootCount; PadThickness = padThickness; BracketSpan = bracketSpan;
        }
    }

    /// <summary>原模型零件的总装坐标预览快照，不持有COM对象。</summary>
    public sealed class BridgeUpstreamPreviewPart
    {
        public string ComponentName { get; }
        public string Side { get; }
        public string Phase { get; }
        public bool IsInsulator { get; }
        public BridgePreviewMesh Mesh { get; }
        public BridgeInsulatorPose InsulatorPlacement { get; }
        public BridgeUpstreamPreviewPart(string componentName, string side, string phase,
            BridgeInsulatorPose placement)
        {
            if (string.IsNullOrWhiteSpace(componentName) || (side != "A" && side != "B") ||
                (phase != "A" && phase != "B" && phase != "C"))
                throw new ArgumentException("基座绝缘子必须有组件名、柜号及相别。");
            ComponentName = componentName; Side = side; Phase = phase; IsInsulator = true;
            InsulatorPlacement = placement ?? throw new ArgumentNullException(nameof(placement));
        }
        public BridgeUpstreamPreviewPart(string componentName, string side, string phase,
            bool isInsulator, BridgePreviewMesh mesh)
        {
            if (string.IsNullOrWhiteSpace(componentName) || (side != "A" && side != "B") ||
                (phase != "A" && phase != "B" && phase != "C"))
                throw new ArgumentException("原模型预览零件必须有组件名、柜号及相别。");
            ComponentName = componentName; Side = side; Phase = phase; IsInsulator = isInsulator;
            Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        }
    }

    public interface IBridgeUpstreamReader
    {
        BridgeUpstreamSnapshot Read();
    }

    public sealed class BridgeUpstreamField
    {
        internal static string FormatMillimetres(double value)
        {
            // .25/.75 mm 等半格值经 COM 变换后可能跨过一位小数的舍入边界。
            var tenths = value * 10d;
            var midpoint = Math.Floor(tenths) + .5d;
            if (Math.Abs(tenths - midpoint) < .00001d) tenths = midpoint;
            var rounded = Math.Round(tenths, 0, MidpointRounding.AwayFromZero) / 10d;
            return (rounded == 0d ? 0d : rounded).ToString("0.0", CultureInfo.InvariantCulture);
        }

        public string Name { get; }
        public string Value { get; }
        public string Source { get; }
        public bool IsMissing => string.IsNullOrWhiteSpace(Value);
        public BridgeUpstreamField(string name, string value, string source)
        { Name = name; Value = value; Source = source; }
        public string DisplayText => Name + "：" + (IsMissing ? "未读取（" + Source + "）" : Value);
    }

    /// <summary>模型只读快照，与演示草稿完全分开；不持有COM对象，不参与人工覆盖。</summary>
    public sealed class BridgeUpstreamSnapshot
    {
        public string ModelName { get; }
        public string ModelPath { get; }
        public string Configuration { get; }
        public string Scenario { get; }
        public IReadOnlyList<BridgeUpstreamField> Fields { get; }
        public IReadOnlyList<BridgeUpstreamEndpoint> Endpoints { get; }
        public IReadOnlyList<BridgeUpstreamPreviewPart> PreviewParts { get; }
        public SideToSideUpstreamGeometry SideToSide { get; private set; }
        public FaceToWallUpstreamGeometry FaceToWall { get; private set; }
        public BridgeUpstreamSnapshot WithFaceToWall(FaceToWallUpstreamGeometry geometry)
        {
            return new BridgeUpstreamSnapshot(ModelName, ModelPath, Configuration, Scenario, Fields, Endpoints, PreviewParts)
                { SideToSide = SideToSide, FaceToWall = geometry };
        }
        public BridgeUpstreamSnapshot WithSideToSide(SideToSideUpstreamGeometry geometry)
        {
            return new BridgeUpstreamSnapshot(ModelName, ModelPath, Configuration, Scenario, Fields, Endpoints, PreviewParts)
                { SideToSide = geometry, FaceToWall = FaceToWall };
        }
        public bool HasModel => !string.IsNullOrWhiteSpace(ModelName);
        public bool HasMissingValues => Fields.Any(f => f.IsMissing);
        public string State => !HasModel ? "上游读取失败" : HasMissingValues ? "模型上游已读取，部分信息缺失" : "模型上游已读取（只读）";
        public string Summary => State + "\n" + (HasModel ? "总装：" + ModelName + "\n场景：" + Scenario + "\n\n" : "") +
            string.Join("\n", Fields.Select(f => f.DisplayText)) +
            "\n\n参数修改请回柜体驱动，完成后重读上游。";
        public string SourceDetails => "模型读取快照\n路径：" + ModelPath + "\n配置：" + Configuration + "\n\n" +
            string.Join("\n", Fields.Select(f => f.Name + " ← " + f.Source));
        public BridgeUpstreamSnapshot(string modelName, string modelPath, string configuration, string scenario,
            IEnumerable<BridgeUpstreamField> fields)
            : this(modelName, modelPath, configuration, scenario, fields, Array.Empty<BridgeUpstreamEndpoint>())
        { }

        public BridgeUpstreamSnapshot(string modelName, string modelPath, string configuration, string scenario,
            IEnumerable<BridgeUpstreamField> fields, IEnumerable<BridgeUpstreamEndpoint> endpoints)
            : this(modelName, modelPath, configuration, scenario, fields, endpoints, Array.Empty<BridgeUpstreamPreviewPart>())
        { }

        public BridgeUpstreamSnapshot(string modelName, string modelPath, string configuration, string scenario,
            IEnumerable<BridgeUpstreamField> fields, IEnumerable<BridgeUpstreamEndpoint> endpoints,
            IEnumerable<BridgeUpstreamPreviewPart> previewParts)
        {
            ModelName = modelName; ModelPath = modelPath; Configuration = configuration; Scenario = scenario;
            Fields = Array.AsReadOnly(fields.ToArray());
            Endpoints = Array.AsReadOnly(endpoints.ToArray());
            PreviewParts = Array.AsReadOnly(previewParts.ToArray());
        }
    }
}
