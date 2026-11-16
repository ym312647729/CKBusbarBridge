using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace CKBusbarBridge.Prototype
{
    public sealed class BridgeModelMeshProjection
    {
        public Geometry Create(BridgePreviewMesh mesh, bool sideView)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (!sideView && mesh.TopSectionHeight.HasValue) return HorizontalSection(mesh);
            var vertices = mesh.Vertices;
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var context = geometry.Open())
            {
                for (var i = 0; i < vertices.Count; i += 9)
                {
                    var a = new Point(vertices[i], sideView ? -vertices[i + 2] : vertices[i + 1]);
                    var b = new Point(vertices[i + 3], sideView ? -vertices[i + 5] : vertices[i + 4]);
                    var c = new Point(vertices[i + 6], sideView ? -vertices[i + 8] : vertices[i + 7]);
                    var area = Vector.CrossProduct(b - a, c - a);
                    if (Math.Abs(area) < .000001) continue;
                    // 实体表面投影统一绕序合并，保留实际孔洞，不画三角网格内部线。
                    context.BeginFigure(a, true, true);
                    context.LineTo(area > 0 ? b : c, false, false);
                    context.LineTo(area > 0 ? c : b, false, false);
                }
            }
            return geometry;
        }

        // 整面墙俯视投影会被洞口上下实体填满；在实际洞口高度剖切表面网格。
        // 轮廓来自三角形与平面交线，不使用Box或猜测墙体尺寸。
        private Geometry HorizontalSection(BridgePreviewMesh mesh)
        {
            const double tolerance = .01d;
            var segments = new List<Point[]>();
            var vertices = mesh.Vertices;
            var height = mesh.TopSectionHeight.Value;
            for (var i = 0; i < vertices.Count; i += 9)
            {
                var crossings = new List<Point>();
                for (var edge = 0; edge < 3; edge++)
                {
                    var a = i + edge * 3; var b = i + (edge + 1) % 3 * 3;
                    var za = vertices[a + 2] - height; var zb = vertices[b + 2] - height;
                    if ((za < 0) == (zb < 0) || Math.Abs(za - zb) < .0000001) continue;
                    var t = za / (za - zb);
                    var point = new Point(vertices[a] + t * (vertices[b] - vertices[a]),
                        vertices[a + 1] + t * (vertices[b + 1] - vertices[a + 1]));
                    if (crossings.All(p => (p - point).Length > tolerance)) crossings.Add(point);
                }
                if (crossings.Count == 2) segments.Add(crossings.ToArray());
            }
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var context = geometry.Open())
            while (segments.Count > 0)
            {
                var start = segments[0][0]; var end = segments[0][1];
                var contour = new List<Point> { start, end }; segments.RemoveAt(0);
                while ((end - start).Length > tolerance)
                {
                    var next = segments.FindIndex(s => s.Any(p => (p - end).Length <= tolerance));
                    if (next < 0) throw new InvalidOperationException("墙体实际网格的俯视剖切轮廓未闭合。");
                    var segment = segments[next]; segments.RemoveAt(next);
                    end = (segment[0] - end).Length <= tolerance ? segment[1] : segment[0];
                    contour.Add(end);
                }
                if (contour.Count < 4) continue;
                context.BeginFigure(start, true, true);
                context.PolyLineTo(contour.Skip(1).ToArray(), false, false);
            }
            return geometry;
        }
    }
}
