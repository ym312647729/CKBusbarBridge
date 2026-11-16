using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeContactFace
    {
        internal double[] Normal;
        internal double[][][] Loops;
        // 每次反读创建新的实际面快照，网格仅随该快照存活，不跨实例或反读共享。
        private BridgeBusinessGeometry.ContactMeshCache _meshCache;
        internal BridgeBusinessGeometry.ContactTriangle[][] ProjectedTriangles(double[] origin, double[] u, double[] v)
        {
            if (_meshCache == null) _meshCache = new BridgeBusinessGeometry.ContactMeshCache();
            return _meshCache.Get(Loops, origin, u, v);
        }
    }
    internal static class BridgeBusinessGeometry
    {
        internal static double Distance(double[] a, double[] b) => Math.Sqrt(a.Select((v, i) => (v - b[i]) * (v - b[i])).Sum());
        internal static double Dot(double[] a, double[] b) => a.Select((v, i) => v * b[i]).Sum();
        internal static double[] Sub(double[] a, double[] b) => a.Select((v, i) => v - b[i]).ToArray();
        internal static double[] Unit(double[] a) { var n = Math.Sqrt(Dot(a, a)); if (n < 1e-9) throw new InvalidOperationException("连接几何方向退化。"); return a.Select(x => x / n).ToArray(); }
        internal static double[] Cross(double[] a, double[] b) => new[] { a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0] };
        internal static double[] TransformWorld(double[] point,double[] transform) => Enumerable.Range(0,3)
            .Select(axis=>(point[0]*transform[axis]+point[1]*transform[3+axis]+point[2]*transform[6+axis])*transform[12]+transform[9+axis]*1000).ToArray();
        // 实体求值返回米；复用已读取的组件矩阵，避免每个采样点创建COM数学对象。
        internal static double[] TransformModelPoint(double[] point, double[] transform) =>
            TransformWorld(point.Select(v => v * 1000).ToArray(), transform);
        internal static bool SamePath(double[][] a, double[][] b, double tolerance = .03) => a != null && b != null && a.Length == b.Length &&
            (a.Where((p, i) => Distance(p, b[i]) > tolerance).Any() == false || a.Where((p, i) => Distance(p, b[b.Length - 1 - i]) > tolerance).Any() == false);
        internal static double[][] OrderPath(IEnumerable<double[][]> edges, double tolerance, bool closed = false, string context = null)
        {
            var remaining = edges.Where(e => Distance(e[0], e[1]) > tolerance).ToList();
            var vertices = remaining.SelectMany(e => e).ToArray();
            var ends = vertices.Where(p => vertices.Count(q => Distance(p, q) <= tolerance) == 1).ToArray();
            if (remaining.Count == 0 || (!closed && ends.Length != 2) || (closed && ends.Length != 0))
                throw new InvalidOperationException((closed ? "实际接触面边界不是唯一闭合环。" : "原驱动草图不是唯一连续路径。") +
                    (context == null ? "" : " " + context) + " 有效线段=" + remaining.Count + "，悬空端点=" + ends.Length + "。");
            var start = (closed ? vertices : ends).OrderBy(p => Dot(p, p)).ThenBy(p => p[0]).ThenBy(p => p[1]).First();
            var result = new List<double[]> { start };
            while (remaining.Count > 0)
            {
                var candidates = remaining.Where(e => Distance(e[0], result.Last()) <= tolerance || Distance(e[1], result.Last()) <= tolerance).ToArray();
                if (candidates.Length != 1 && !(closed && result.Count == 1 && candidates.Length == 2))
                    throw new InvalidOperationException((closed ? "实际接触面边界包含分支或不连接线段。" : "原驱动草图包含分支或不连接线段。") +
                        (context == null ? "" : " " + context));
                var edge = candidates[0]; remaining.Remove(edge);
                result.Add(Distance(edge[0], result.Last()) <= tolerance ? edge[1] : edge[0]);
            }
            if (closed) result.RemoveAt(result.Count - 1);
            return result.ToArray();
        }

        // 返回真实接触多边形的桥向范围。凹多边形按耳切分，内环按面积扣除。
        internal static double[] Contact(BridgeContactFace a, BridgeContactFace b, BridgeCoordinateFrame coordinates)
        {
            if (Dot(a.Normal, b.Normal) > -.99999) return null;
            var origin = a.Loops[0][0];
            if (Math.Abs(Dot(Sub(b.Loops[0][0], origin), a.Normal)) > .02) return null;
            var u = Unit(Sub(a.Loops[0][1], origin)); var v = Cross(a.Normal, u);
            var points = new List<double[]>(); var area = 0d;
            var aTriangles = a.ProjectedTriangles(origin, u, v);
            var bTriangles = b.ProjectedTriangles(origin, u, v);
            for (var i = 0; i < a.Loops.Length; i++) for (var j = 0; j < b.Loops.Length; j++)
            {
                foreach (var first in aTriangles[i])
                    foreach (var second in bTriangles[j])
                    {
                        var intersection = ClipPrepared(first.Points, second); var weight = (i == 0 ? 1 : -1) * (j == 0 ? 1 : -1);
                        area += weight * Math.Abs(Area(intersection));
                        if (i == 0 && j == 0) points.AddRange(intersection);
                    }
            }
            if (area <= .5 || points.Count == 0) return null;
            var along = points.Select(p => coordinates.ToDraft(origin.Select((x, k) => x + u[k] * p[0] + v[k] * p[1]).ToArray())[0]).ToArray();
            return new[] { along.Min(), along.Max() };
        }
        internal sealed class ContactTriangle
        {
            internal readonly double[][] Points;
            internal readonly ContactEdge[] Edges;
            internal ContactTriangle(double[][] points)
            {
                Points = points;
                if (points.Length != 3) return;
                Edges = new[] { new ContactEdge(points[0], points[1]), new ContactEdge(points[1], points[2]), new ContactEdge(points[2], points[0]) };
            }
        }
        internal struct ContactEdge
        {
            private readonly double _x, _y, _dx, _dy;
            internal ContactEdge(double[] a, double[] b) { _x = a[0]; _y = a[1]; _dx = b[0] - a[0]; _dy = b[1] - a[1]; }
            internal double Side(double[] point) => _dx * (point[1] - _y) - _dy * (point[0] - _x);
        }
        internal sealed class ContactMeshCache
        {
            private int[] _sourceShape;
            private long[] _sourceValues;
            private long[] _projection;
            private ContactTriangle[][] _triangles;
            private int[][][] _indices;
            private double[][][] _polygons;
            internal ContactTriangle[][] Get(double[][][] loops, double[] origin, double[] u, double[] v)
            {
                var sameGeometry = _triangles != null && Matches(loops);
                if (sameGeometry && ProjectionMatches(origin, u, v)) return _triangles;
                var indices = sameGeometry ? (int[][][])_indices.Clone() : new int[loops.Length][][];
                var triangles = new double[loops.Length][][][];
                var polygons = new double[loops.Length][][];
                var sameAxes = sameGeometry && AxesMatch(u, v);
                Func<double[], double[]> project = p => new[] { Dot(Sub(p, origin), u), Dot(Sub(p, origin), v) };
                for (var i = 0; i < loops.Length; i++)
                {
                    var polygon = loops[i].Select(project).ToArray();
                    polygons[i] = polygon;
                    // 平移后所有坐标差精确相同且绕序不变时，耳判据必然相同，可直接复用三维顶点索引。
                    if (indices[i] != null && sameAxes && SameDifferences(_polygons[i], polygon))
                    {
                        triangles[i] = new double[indices[i].Length][][];
                        for (var j = 0; j < indices[i].Length; j++)
                        {
                            var triangle = indices[i][j];
                            triangles[i][j] = new[] { polygon[triangle[0]], polygon[triangle[1]], polygon[triangle[2]] };
                        }
                        continue;
                    }
                    triangles[i] = Triangles(polygon).ToArray();
                    indices[i] = triangles[i].Select(triangle => triangle.Select(point => Array.IndexOf(polygon, point)).ToArray()).ToArray();
                }
                var sourceShape = _sourceShape; var sourceValues = _sourceValues;
                if (!sameGeometry) Capture(loops, out sourceShape, out sourceValues);
                var projection = new long[9];
                for (var i = 0; i < 3; i++)
                {
                    projection[i] = BitConverter.DoubleToInt64Bits(origin[i]);
                    projection[i + 3] = BitConverter.DoubleToInt64Bits(u[i]);
                    projection[i + 6] = BitConverter.DoubleToInt64Bits(v[i]);
                }
                var prepared = triangles.Select(loop => loop.Select(triangle => new ContactTriangle(triangle)).ToArray()).ToArray();
                _sourceShape = sourceShape; _sourceValues = sourceValues; _projection = projection;
                _indices = indices; _triangles = prepared; _polygons = polygons;
                return prepared;
            }
            private bool AxesMatch(double[] u, double[] v)
            {
                for (var i = 0; i < 3; i++)
                    if (_projection[i + 3] != BitConverter.DoubleToInt64Bits(u[i]) ||
                        _projection[i + 6] != BitConverter.DoubleToInt64Bits(v[i])) return false;
                return true;
            }
            private bool SameDifferences(double[][] before, double[][] after)
            {
                if (after.Length < 3 || (Area(before) < 0) != (Area(after) < 0)) return false;
                for (var i = 1; i < after.Length; i++) for (var j = 0; j < i; j++)
                    if (before[j][0] - before[i][0] != after[j][0] - after[i][0] ||
                        before[j][1] - before[i][1] != after[j][1] - after[i][1]) return false;
                return true;
            }
            private bool ProjectionMatches(double[] origin, double[] u, double[] v)
            {
                // 缓存身份按原始位比较，不按几何公差合并不同的浮点运算基准。
                for (var i = 0; i < 3; i++)
                    if (_projection[i] != BitConverter.DoubleToInt64Bits(origin[i]) ||
                        _projection[i + 3] != BitConverter.DoubleToInt64Bits(u[i]) ||
                        _projection[i + 6] != BitConverter.DoubleToInt64Bits(v[i])) return false;
                return true;
            }
            private bool Matches(double[][][] loops)
            {
                if (_sourceShape[0] != loops.Length) return false;
                var shapeIndex = 1; var valueIndex = 0;
                for (var i = 0; i < loops.Length; i++)
                {
                    if (_sourceShape[shapeIndex++] != loops[i].Length) return false;
                    for (var j = 0; j < loops[i].Length; j++)
                    {
                        if (_sourceShape[shapeIndex++] != loops[i][j].Length) return false;
                        for (var k = 0; k < loops[i][j].Length; k++)
                            if (_sourceValues[valueIndex++] != BitConverter.DoubleToInt64Bits(loops[i][j][k])) return false;
                    }
                }
                return true;
            }
            private void Capture(double[][][] loops, out int[] sourceShape, out long[] sourceValues)
            {
                var pointCount = 0; var valueCount = 0;
                foreach (var loop in loops)
                {
                    pointCount = checked(pointCount + loop.Length);
                    foreach (var point in loop) valueCount = checked(valueCount + point.Length);
                }
                sourceShape = new int[1 + loops.Length + pointCount];
                sourceValues = new long[valueCount];
                sourceShape[0] = loops.Length;
                var shapeIndex = 1; var valueIndex = 0;
                foreach (var loop in loops)
                {
                    sourceShape[shapeIndex++] = loop.Length;
                    foreach (var point in loop)
                    {
                        sourceShape[shapeIndex++] = point.Length;
                        foreach (var value in point) sourceValues[valueIndex++] = BitConverter.DoubleToInt64Bits(value);
                    }
                }
            }
        }
        private static double Turn(double[] a, double[] b, double[] c) => (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);
        private static double Area(IReadOnlyList<double[]> p) => p.Count < 3 ? 0 : p.Select((a,i) => a[0]*p[(i+1)%p.Count][1]-a[1]*p[(i+1)%p.Count][0]).Sum()/2;
        private static IEnumerable<double[][]> Triangles(double[][] polygon)
        {
            var points = polygon.ToList(); if (Area(points) < 0) points.Reverse();
            while (points.Count > 3)
            {
                var index = Enumerable.Range(0, points.Count).Where(i =>
                {
                    var a = points[(i+points.Count-1)%points.Count]; var b = points[i]; var c = points[(i+1)%points.Count];
                    return Turn(a,b,c) > 1e-7 && !points.Where((p,k) => k != i && k != (i+1)%points.Count && k != (i+points.Count-1)%points.Count)
                        .Any(p => Turn(a,b,p)>=-1e-7 && Turn(b,c,p)>=-1e-7 && Turn(c,a,p)>=-1e-7);
                }).DefaultIfEmpty(-1).First();
                if(index<0) throw new InvalidOperationException("实际搭接面边界不能唯一解析。");
                var triangle = new[] { points[(index+points.Count-1)%points.Count], points[index], points[(index+1)%points.Count] };
                if (Turn(triangle[0],triangle[1],triangle[2]) <= 1e-7) throw new InvalidOperationException("实际搭接面边界不能唯一解析。");
                yield return triangle; points.RemoveAt(index);
            }
            yield return points.ToArray();
        }
        private static IReadOnlyList<double[]> ClipPrepared(double[][] polygon, ContactTriangle triangle)
        {
            if (triangle.Edges == null || polygon.Length != 3) return Clip(polygon, triangle.Points);
            var firstEdge = triangle.Edges[0];
            var firstSide = firstEdge.Side(polygon[0]); var secondSide = firstEdge.Side(polygon[1]); var thirdSide = firstEdge.Side(polygon[2]);
            // 与原裁剪的第一条边完全相同；全部在外侧时结果必为空，直接省去临时多边形分配。
            if (firstSide < 0 && secondSide < 0 && thirdSide < 0) return Array.Empty<double[]>();
            var result = new List<double[]>(4);
            var previous = polygon[2]; var d0 = thirdSide;
            for (var i = 0; i < 3; i++)
            {
                var current = polygon[i]; var d1 = i == 0 ? firstSide : i == 1 ? secondSide : thirdSide;
                if ((d0 >= 0) != (d1 >= 0)) result.Add(previous.Select((x, k) => x + (current[k] - x) * d0 / (d0 - d1)).ToArray());
                if (d1 >= 0) result.Add(current); previous = current; d0 = d1;
            }
            for (var i = 1; i < 3 && result.Count > 0; i++)
            {
                var input = result; result = new List<double[]>(); var edge = triangle.Edges[i];
                previous = input.Last(); d0 = edge.Side(previous);
                foreach (var current in input)
                {
                    var d1 = edge.Side(current);
                    if ((d0 >= 0) != (d1 >= 0)) result.Add(previous.Select((x, k) => x + (current[k] - x) * d0 / (d0 - d1)).ToArray());
                    if (d1 >= 0) result.Add(current); previous = current; d0 = d1;
                }
            }
            return result;
        }
        private static List<double[]> Clip(double[][] polygon, double[][] triangle)
        {
            var result = polygon.ToList();
            for (var i=0; i<3 && result.Count>0; i++)
            {
                var input = result; result = new List<double[]>(); var a=triangle[i]; var b=triangle[(i+1)%3];
                var previous=input.Last(); var d0=Turn(a,b,previous);
                foreach (var current in input)
                {
                    var d1=Turn(a,b,current);
                    if ((d0>=0)!=(d1>=0)) result.Add(previous.Select((x,k)=>x+(current[k]-x)*d0/(d0-d1)).ToArray());
                    if (d1>=0) result.Add(current); previous=current; d0=d1;
                }
            }
            return result;
        }
    }

    // 在 COM 边界外校验采样结果；求值委托必须来自边本身，不能混用底层曲线的参数化。
    internal sealed class BridgeContactBoundarySampling
    {
        private const double EndpointTolerance = .001; // mm，与闭合环排序一致。

        internal double[] ReadPoint(double[] evaluated, string context)
        {
            // IEdge.Evaluate2 在零阶求值时返回 XYZ 和一个打包的状态值，低 32 位为 1 才是成功。
            if (evaluated == null || evaluated.Length != 4 ||
                BitConverter.ToInt32(BitConverter.GetBytes(evaluated[3]), 0) != 1 ||
                evaluated.Take(3).Any(v => !Finite(v)))
                throw new InvalidOperationException("实际接触面边界求值失败：" + context);
            return evaluated.Take(3).ToArray();
        }

        internal double[][] SampleArc(double minimum, double maximum, double radius,
            Func<double, double[]> evaluatePoint, double[] startVertex, double[] endVertex, string context)
        {
            if (!Finite(minimum) || !Finite(maximum) || maximum <= minimum || !Finite(radius) || radius <= 0)
                throw new InvalidOperationException("实际接触面圆弧参数无效：" + context);
            var halfAngle = Math.Acos(Math.Max(-1, 1 - .01 / radius));
            var count = Math.Ceiling((maximum - minimum) / (2 * halfAngle));
            if (!Finite(count) || count > int.MaxValue - 1)
                throw new InvalidOperationException("实际接触面圆弧采样步长无效：" + context);
            var steps = Math.Max(3, (int)count);
            var points = Enumerable.Range(0, steps + 1)
                .Select(i => evaluatePoint(minimum + (maximum - minimum) * i / steps)).ToArray();
            if (points.Any(p => p == null || p.Length != 3 || p.Any(v => !Finite(v))))
                throw new InvalidOperationException("实际接触面圆弧采样坐标无效：" + context);
            var first = points.First(); var last = points.Last();
            var matched = startVertex == null && endVertex == null
                ? BridgeBusinessGeometry.Distance(first, last) <= EndpointTolerance
                : startVertex != null && endVertex != null &&
                    ((BridgeBusinessGeometry.Distance(first, startVertex) <= EndpointTolerance &&
                      BridgeBusinessGeometry.Distance(last, endVertex) <= EndpointTolerance) ||
                     (BridgeBusinessGeometry.Distance(first, endVertex) <= EndpointTolerance &&
                      BridgeBusinessGeometry.Distance(last, startVertex) <= EndpointTolerance));
            if (!matched)
                throw new InvalidOperationException("实际接触面圆弧采样端点与实体顶点不一致：" + context);
            return points;
        }

        private bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
