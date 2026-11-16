using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridgePreview
{
    public sealed class FaceToWallCabinetNominalGeometry
    {
        public double[] ReadCenter(IEnumerable<double[]> profileCorners, double depth, double height,
            double width, double extrusionDepth, double secondDepth, bool reverse)
        {
            var points = profileCorners?.ToArray();
            if (points == null || points.Length != 8 || points.Any(p => p == null || p.Length != 3 ||
                p.Any(v => double.IsNaN(v) || double.IsInfinity(v))) ||
                new[] { depth, height, width, extrusionDepth, secondDepth }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                depth <= 0 || height <= 0 || width <= 0 || reverse ||
                Math.Abs(extrusionDepth - width) > .01 || Math.Abs(secondDepth) > .01)
                throw new InvalidOperationException("面对墙柜体外形草图或单向拉伸与驱动尺寸不一致。");
            foreach (var x in new[] { -depth / 2, depth / 2 })
            foreach (var y in new[] { -height / 2, height / 2 })
                if (points.Count(p => Math.Abs(p[0] - x) < .01 && Math.Abs(p[1] - y) < .01 && Math.Abs(p[2]) < .01) != 2)
                    throw new InvalidOperationException("面对墙柜体外形草图未符合已核验的模板坐标关系。");
            // 草图在深、高方向居中；宽方向从草图平面单向拉伸，原点不是实体中心。
            return new[] { 0d, 0d, width / 2 };
        }
    }
}
