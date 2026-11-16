using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.DesignGeometry
{
    // 界面直接执行未加密实现；预览库链接同一源码保留公开入口，避免两套几何规则漂移。
    internal sealed class BridgeSupportPreviewGeometry
    {
        internal double[][] Vertices(DraftRoundSection section)
        {
            var start = section.Start.ToArray(); var end = section.End.ToArray();
            var startRadius = section.StartRadius; var endRadius = section.EndRadius;
            var axis = new Vector3d(new Point3d(start), new Point3d(end)).Normalized;
            var reference = Math.Abs(axis.Z) < .9 ? new Vector3d(0, 0, 1) : new Vector3d(0, 1, 0);
            var x = reference.Cross(axis).Normalized; var y = axis.Cross(x).Normalized;
            return Enumerable.Range(0, 2).SelectMany(ring => Enumerable.Range(0, DraftRoundSection.RingSegments).Select(i =>
            {
                var angle = i * 2 * Math.PI / DraftRoundSection.RingSegments;
                var radius = ring == 0 ? startRadius : endRadius;
                var center = ring == 0 ? start : end;
                return new[] { center[0] + radius * (x.X * Math.Cos(angle) + y.X * Math.Sin(angle)),
                    center[1] + radius * (x.Y * Math.Cos(angle) + y.Y * Math.Sin(angle)),
                    center[2] + radius * (x.Z * Math.Cos(angle) + y.Z * Math.Sin(angle)) };
            })).ToArray();
        }

        internal int[] TriangleIndices()
        {
            var n = DraftRoundSection.RingSegments; var triangles = new List<int>();
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                triangles.AddRange(new[] { i, j, n + j, i, n + j, n + i });
            }
            for (var i = 1; i < n - 1; i++) triangles.AddRange(new[] { 0, i + 1, i, n, n + i, n + i + 1 });
            return triangles.ToArray();
        }

        internal double[][] Meridian(double height)
        {
            // H205模板的伞裙及凹槽尺寸由顶部约束；H变化仅改变下部直柱。
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 120.627174484597)
                throw new ArgumentException("绝缘子本体高度不足以容纳已核验模板的伞裙与凹槽。");
            var points = new List<double[]> { new[] { 0d, 50d } };
            Action<double, double> add = (z, r) => points.Add(new[] { height - 205 + z, r });
            add(84.3728255154036, 50); add(84.3728255154036, 36);
            add(87.3728255154036, 36); add(87.3728255154036, 50);
            add(117.372825515404, 50); add(117.372825515404, 12.5);
            add(126.372825515404, 50); add(128.372825515404, 50);
            add(134.372825515403, 25); add(136.372825515403, 25);
            Action<double> arc = center =>
            {
                var theta = Math.Acos((40.4667459049506 - 40) / 2);
                // R2圆角以八段离散，二维和原生3D共用；最大轮廓误差小于0.03mm。
                for (var i = 0; i <= 8; i++)
                {
                    var angle = -theta + 2 * theta * i / 8;
                    add(center + 2 * Math.Sin(angle), 40 + 2 * Math.Cos(angle));
                }
            };
            arc(142.029619136553);
            add(147.686412757702, 25); add(149.686412757702, 25);
            add(155.686412757702, 50); add(157.686412757702, 50);
            add(163.686412757702, 25); add(165.686412757702, 25);
            arc(171.343206378851);
            add(177, 25); add(179, 25); add(185, 50); add(205, 50);
            return points.ToArray();
        }
    }
}
