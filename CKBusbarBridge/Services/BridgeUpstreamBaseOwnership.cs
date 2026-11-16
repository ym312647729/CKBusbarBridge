using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    public sealed class BridgeUpstreamBaseOwnership
    {
        /// <summary>输入同一总装坐标系的实际Box中心。返回A/B柜各自唯一基座索引，不作名称或基架回退。</summary>
        public int[] Resolve(double[] cabinetA, double[] cabinetB, IReadOnlyList<double[]> bases)
        {
            if (bases == null || bases.Count != 2) throw new InvalidOperationException("面对面总装顶层必须唯一定位两个独立基座。");
            var points = new[] { cabinetA, cabinetB }.Concat(bases);
            if (points.Any(p => p == null || p.Length != 3 || p.Any(v => double.IsNaN(v) || double.IsInfinity(v))))
                throw new InvalidOperationException("柜体或基座缺少有效的实际Box中心，不能确定归属。");
            var owners = new int[2];
            for (var i = 0; i < 2; i++)
            {
                var a = DistanceSquared(bases[i], cabinetA); var b = DistanceSquared(bases[i], cabinetB);
                if (Math.Abs(a - b) <= Math.Max(1d, Math.Max(a, b)) * 1e-12)
                    throw new InvalidOperationException("基座到两柜Box中心等距，无法唯一归属。");
                owners[i] = a < b ? 0 : 1;
            }
            if (owners[0] == owners[1]) throw new InvalidOperationException("两个基座归属于同一柜，不能建立A/B柜的一一归属。");
            return owners[0] == 0 ? new[] { 0, 1 } : new[] { 1, 0 };
        }
        private double DistanceSquared(double[] a, double[] b) =>
            (a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]);
    }
}
