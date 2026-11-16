using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Prototype
{
    /// <summary>草稿(X桥向、Y横向、Z高度)到总装毫米坐标的正交变换；方向不带平移。</summary>
    public sealed class BridgeCoordinateFrame
    {
        public IReadOnlyList<double> Origin { get; }
        public IReadOnlyList<double> Along { get; }
        public IReadOnlyList<double> Across { get; }
        public IReadOnlyList<double> Up { get; }

        public BridgeCoordinateFrame(IEnumerable<double> origin, IEnumerable<double> along,
            IEnumerable<double> across, IEnumerable<double> up)
        {
            Origin = Copy(origin); Along = Copy(along); Across = Copy(across); Up = Copy(up);
            var axes = new[] { Along, Across, Up };
            if (axes.Any(a => Math.Abs(Dot(a, a) - 1d) > 1e-6) ||
                Math.Abs(Dot(Along, Across)) > 1e-6 || Math.Abs(Dot(Along, Up)) > 1e-6 ||
                Math.Abs(Dot(Across, Up)) > 1e-6)
                throw new ArgumentException("草稿坐标轴必须为两两正交的单位向量。");
        }

        public static BridgeCoordinateFrame Facing(double x, double z) => new BridgeCoordinateFrame(
            new[] { x, 0d, z }, new[] { 1d, 0d, 0d }, new[] { 0d, 0d, 1d }, new[] { 0d, 1d, 0d });

        public double[] ToAssembly(IReadOnlyList<double> point) => Add(ToAssemblyDirection(point), Origin);
        public double[] ToDraft(IReadOnlyList<double> point) => ToDraftDirection(Add(point, Origin, -1));
        public double[] ToAssemblyDirection(IReadOnlyList<double> vector)
        {
            var v = Copy(vector);
            return Enumerable.Range(0, 3).Select(i => v[0] * Along[i] + v[1] * Across[i] + v[2] * Up[i]).ToArray();
        }
        public double[] ToDraftDirection(IReadOnlyList<double> vector)
        {
            var v = Copy(vector);
            return new[] { Dot(v, Along), Dot(v, Across), Dot(v, Up) };
        }

        // 原生几何算法仍采用X桥向/Y高度/Z横向；只在此边界交换草稿轴。
        public PlanarBendBusbarSetting ToAssembly(PlanarBendBusbarSetting local) => Transform(local, true);
        public PlanarBendBusbarSetting ToPlanning(PlanarBendBusbarSetting assembly) => Transform(assembly, false);
        private PlanarBendBusbarSetting Transform(PlanarBendBusbarSetting source, bool forward)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var result = BridgeNativePreviewCopy.Copy(source);
            result.PointList = source.PointList.Select(p =>
            {
                var q = forward ? ToAssembly(new[] { p.X, p.Z, p.Y }) : ToDraft(new[] { p.X, p.Y, p.Z });
                return forward ? new Point3d(q[0], q[1], q[2]) : new Point3d(q[0], q[2], q[1]);
            }).ToList();
            Func<Vector3d, Vector3d> direction = v =>
            {
                if (v == null) throw new ArgumentException("铜排原生加工参数缺少截面方向。");
                var q = forward ? ToAssemblyDirection(new[] { v.X, v.Z, v.Y }) : ToDraftDirection(new[] { v.X, v.Y, v.Z });
                return forward ? new Vector3d(q[0], q[1], q[2]) : new Vector3d(q[0], q[2], q[1]);
            };
            result.StartBusbarVector = direction(source.StartBusbarVector);
            result.WidthVector = direction(source.WidthVector);
            return result;
        }

        public bool Matches(BridgeCoordinateFrame other, double tolerance = .000001d) => other != null &&
            Origin.Concat(Along).Concat(Across).Concat(Up)
                .Zip(other.Origin.Concat(other.Along).Concat(other.Across).Concat(other.Up), (a, b) => Math.Abs(a - b))
                .All(d => d <= tolerance);

        private static IReadOnlyList<double> Copy(IEnumerable<double> values)
        {
            var result = values?.ToArray();
            if (result == null || result.Length != 3 || result.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("坐标或方向必须包含三个有限数值。");
            return Array.AsReadOnly(result);
        }
        private static double Dot(IReadOnlyList<double> a, IReadOnlyList<double> b) => a.Zip(b, (x, y) => x * y).Sum();
        private static double[] Add(IReadOnlyList<double> a, IReadOnlyList<double> b, double sign = 1) =>
            Copy(a).Zip(Copy(b), (x, y) => x + sign * y).ToArray();
    }
}
