using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;

namespace CKBusbarBridgePreview
{
    /// <summary>总装毫米坐标中的回转截段；2D网格和SW圆柱/圆台使用同一几何参数。</summary>
    public sealed class DraftRoundSection
    {
        public const int RingSegments = 48;
        public IReadOnlyList<double> Start { get; }
        public IReadOnlyList<double> End { get; }
        public double StartRadius { get; }
        public double EndRadius { get; }
        public double Length { get; }
        public DraftRoundSection(IEnumerable<double> start, IEnumerable<double> end, double startRadius, double endRadius)
        {
            var a = start.ToArray(); var b = end.ToArray();
            if (a.Length != 3 || b.Length != 3 || a.Concat(b).Concat(new[] { startRadius, endRadius })
                .Any(v => double.IsNaN(v) || double.IsInfinity(v)) || startRadius <= 0 || endRadius <= 0)
                throw new ArgumentException("回转预览截段必须提供有限坐标和正半径。");
            Length = Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
            if (Length < .000001) throw new ArgumentException("回转预览截段高度必须大于零。");
            Start = Array.AsReadOnly(a); End = Array.AsReadOnly(b);
            StartRadius = startRadius; EndRadius = endRadius;
        }
        public DraftRoundSection Transform(Func<double[], double[]> transform)
            => new DraftRoundSection(transform(Start.ToArray()), transform(End.ToArray()), StartRadius, EndRadius);

        public double[][] Vertices()
            // 转发壳（界面几何抽取）：同一源码在DesignUI内直接执行，保留原生预览契约。
            => new CKBusbarBridge.DesignGeometry.BridgeSupportPreviewGeometry().Vertices(this);
        public int[] TriangleIndices()
            // 转发壳（界面几何抽取）：正本BridgeSupportPreviewGeometry。
            => new CKBusbarBridge.DesignGeometry.BridgeSupportPreviewGeometry().TriangleIndices();
    }

    /// <summary>已核验绝缘子旋转1/切除-旋转1外轮廓。轴向从本体底面起算，毫米。</summary>
    public sealed class InsulatorPreviewProfile
    {
        public const double BracketHeight = 25;
        public const double BracketSheetThickness = 3;
        public const double Diameter = 100;
        public const double PadWidth = 80;
        public double[][] Meridian(double height)
            // 转发壳（界面几何抽取）：正本BridgeSupportPreviewGeometry。
            => new CKBusbarBridge.DesignGeometry.BridgeSupportPreviewGeometry().Meridian(height);
    }

    /// <summary>按草稿板件/绝缘子模板生成临时实体；坐标为总装毫米，不读取模型包围盒。</summary>
    public sealed class DraftSupportSolid
    {
        public const double PreviewTransparency = .65;
        public string Id { get; }
        public string Material { get; }
        public DraftRoundSection Round { get; }
        public string RoundGroupId { get; }
        private readonly double[][] _corners;
        public double[][] CopyCorners() => _corners.Select(point => point.ToArray()).ToArray();
        public DraftSupportSolid(string id, string material, IEnumerable<double[]> corners)
        {
            Id = id; Material = material;
            _corners = corners.Select(p => p.ToArray()).ToArray();
            if (_corners.Length != 8 || _corners.Any(p => p.Length != 3 || p.Any(v => double.IsNaN(v) || double.IsInfinity(v))))
                throw new ArgumentException("支撑预览必须提供完整板件角点：" + id);
        }
        public DraftSupportSolid(string id, string material, DraftRoundSection round, string roundGroupId = null)
        {
            Id = id; Material = material;
            Round = round ?? throw new ArgumentNullException(nameof(round));
            RoundGroupId = roundGroupId ?? id;
        }

        public PlanarBendBusbarSetting ToPrismSetting()
        {
            if (Round != null) throw new InvalidOperationException("回转截段不能按矩形板件拉伸。");
            var p = _corners.Select(c => new Point3d(c[0], c[1], c[2])).ToArray();
            var width = new Vector3d(p[0], p[3]);
            var thickness = new Vector3d(p[0], p[4]);
            var length = new Vector3d(p[0], p[1]);
            if (new[] { width.Norm, thickness.Norm, length.Norm }.Any(v => v < .001) ||
                Math.Abs(width.Normalized.Dot(thickness.Normalized)) > .000001 ||
                Math.Abs(length.Normalized.Dot(width.Normalized)) > .000001 ||
                Math.Abs(length.Normalized.Dot(thickness.Normalized)) > .000001)
                throw new InvalidOperationException("支撑板件尺寸或方向无效：" + Id);
            // 复用旧折弯临时实体生成器的直段分支，精确保留已确认的草稿截面与姿态。
            return new PlanarBendBusbarSetting
            {
                IsCenter = true, GroupKey = Id, BusBarWidth = width.Norm, BusBarThickness = thickness.Norm,
                WidthVector = width.Normalized, StartBusbarVector = thickness.Normalized,
                PointList = new List<Point3d>
                {
                    new Point3d((p[0].X+p[3].X+p[4].X+p[7].X)/4, (p[0].Y+p[3].Y+p[4].Y+p[7].Y)/4, (p[0].Z+p[3].Z+p[4].Z+p[7].Z)/4),
                    new Point3d((p[1].X+p[2].X+p[5].X+p[6].X)/4, (p[1].Y+p[2].Y+p[5].Y+p[6].Y)/4, (p[1].Z+p[2].Z+p[5].Z+p[6].Z)/4)
                }
            };
        }
    }
}
