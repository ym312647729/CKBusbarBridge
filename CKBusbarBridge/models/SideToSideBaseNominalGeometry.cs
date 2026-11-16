using System;

namespace CKBusbarBridge.Prototype
{
    /// <summary>由驱动尺寸和已核验的安装坐标系关系确定名义外形，不读取包围盒。</summary>
    internal sealed class SideToSideBaseNominalGeometry
    {
        internal double[] Center { get; }
        internal double[] Dimensions { get; }

        private SideToSideBaseNominalGeometry(double[] center, double[] dimensions)
        {
            Center = center;
            Dimensions = dimensions;
        }

        internal static SideToSideBaseNominalGeometry FromElevatedTemplate(
            double height, double depth, double width, double[] bottom, double[] top)
        {
            if (!Positive(height) || !Positive(depth) || !Positive(width))
                throw new InvalidOperationException("增高基座缺少有效的高度、深度或宽度。");
            // 5GX.355.021模板：局部-X为上，局部Y为深度；上下安装原点均在
            // 名义前边内侧21.5mm。C00003实际坐标系及叠装配合已只读核验。
            // 先核对模板关系，不能把其他姿态或模板按该关系强行显示。
            const double mountingInset = 21.5d;
            RequireCoordinate(bottom, "下", height / 2d, -depth / 2d + mountingInset);
            RequireCoordinate(top, "上", -height / 2d, -depth / 2d + mountingInset);
            return new SideToSideBaseNominalGeometry(new[]
            {
                (bottom[9] + top[9]) * 500d,
                (bottom[10] + top[10]) * 500d + depth / 2d - mountingInset,
                (bottom[11] + top[11]) * 500d
            }, new[] { height, depth, width });
        }

        private static void RequireCoordinate(double[] transform, string name, double x, double y)
        {
            if (transform == null || transform.Length < 13 ||
                Array.Exists(transform, value => double.IsNaN(value) || double.IsInfinity(value)))
                throw new InvalidOperationException("增高基座缺少有效的“" + name + "”安装坐标系。");
            var expectedAxes = new[] { 0d, 1d, 0d, -1d, 0d, 0d, 0d, 0d, 1d };
            for (var i = 0; i < expectedAxes.Length; i++)
                if (Math.Abs(transform[i] - expectedAxes[i]) > 1e-6d)
                    throw new InvalidOperationException("增高基座“" + name + "”坐标轴不符合已核验的模板关系。");
            if (Math.Abs(transform[12] - 1d) > 1e-6d ||
                Math.Abs(transform[9] * 1000d - x) > .01d ||
                Math.Abs(transform[10] * 1000d - y) > .01d ||
                Math.Abs(transform[11] * 1000d) > .01d)
                throw new InvalidOperationException("增高基座“" + name + "”安装原点与驱动尺寸不一致，不能推测名义外形。");
        }

        private static bool Positive(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value > 0d;
    }
}
