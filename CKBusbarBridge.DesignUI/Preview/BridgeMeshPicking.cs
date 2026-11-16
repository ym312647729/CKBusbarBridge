using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.Prototype;

namespace CKBusbarBridge.DesignPreview
{
    internal struct ViewportVector
    {
        internal float X, Y, Z;
        internal ViewportVector(float x, float y, float z) { X = x; Y = y; Z = z; }
        internal float this[int axis] => axis == 0 ? X : axis == 1 ? Y : Z;
        internal float Length => (float)Math.Sqrt(Dot(this, this));
        internal ViewportVector Unit => Length == 0f ? this : this / Length;
        public static ViewportVector operator +(ViewportVector a, ViewportVector b) => new ViewportVector(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static ViewportVector operator -(ViewportVector a, ViewportVector b) => new ViewportVector(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static ViewportVector operator *(ViewportVector a, float scale) => new ViewportVector(a.X * scale, a.Y * scale, a.Z * scale);
        public static ViewportVector operator /(ViewportVector a, float scale) => a * (1f / scale);
        internal static float Dot(ViewportVector a, ViewportVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static ViewportVector Cross(ViewportVector a, ViewportVector b) => new ViewportVector(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    internal struct ViewportRay
    {
        internal ViewportVector Origin, Direction;
        internal ViewportRay(ViewportVector origin, ViewportVector direction) { Origin = origin; Direction = direction; }
    }

    internal static class ViewportMatrix
    {
        internal static float[] Multiply(float[] left, float[] right)
        {
            var result = new float[16];
            for (var col = 0; col < 4; col++) for (var row = 0; row < 4; row++)
                for (var k = 0; k < 4; k++) result[col * 4 + row] += left[k * 4 + row] * right[col * 4 + k];
            return result;
        }
        internal static float[] Inverse(float[] matrix)
        {
            var work = new float[4, 8];
            for (var row = 0; row < 4; row++) for (var col = 0; col < 4; col++) { work[row, col] = matrix[col * 4 + row]; work[row, col + 4] = row == col ? 1f : 0f; }
            for (var axis = 0; axis < 4; axis++)
            {
                var pivot = axis;
                for (var row = axis + 1; row < 4; row++) if (Math.Abs(work[row, axis]) > Math.Abs(work[pivot, axis])) pivot = row;
                if (work[pivot, axis] == 0f) throw new InvalidOperationException("视口矩阵不可逆。");
                if (pivot != axis) for (var col = 0; col < 8; col++) { var swap = work[pivot, col]; work[pivot, col] = work[axis, col]; work[axis, col] = swap; }
                var divisor = work[axis, axis]; for (var col = 0; col < 8; col++) work[axis, col] /= divisor;
                for (var row = 0; row < 4; row++) if (row != axis) { var factor = work[row, axis]; for (var col = 0; col < 8; col++) work[row, col] -= factor * work[axis, col]; }
            }
            var result = new float[16]; for (var row = 0; row < 4; row++) for (var col = 0; col < 4; col++) result[col * 4 + row] = work[row, col + 4];
            return result;
        }
        internal static ViewportVector Transform(float[] matrix, ViewportVector point, bool direction = false)
        {
            var w = direction ? 0f : 1f;
            var denominator = direction ? 1f : matrix[3] * point.X + matrix[7] * point.Y + matrix[11] * point.Z + matrix[15];
            return new ViewportVector((matrix[0] * point.X + matrix[4] * point.Y + matrix[8] * point.Z + matrix[12] * w) / denominator,
                (matrix[1] * point.X + matrix[5] * point.Y + matrix[9] * point.Z + matrix[13] * w) / denominator,
                (matrix[2] * point.X + matrix[6] * point.Y + matrix[10] * point.Z + matrix[14] * w) / denominator);
        }
        internal static ViewportRay ScreenRay(float[] inverseViewProjection, int[] viewport, float x, float y)
        {
            var clip = new ViewportVector(2f * (x - viewport[0]) / viewport[2] - 1f, 1f - 2f * (y - viewport[1]) / viewport[3], -1f);
            var near = Transform(inverseViewProjection, clip) * 1000f;
            clip.Z = 1f; var far = Transform(inverseViewProjection, clip) * 1000f;
            return new ViewportRay(near, (far - near).Unit);
        }
    }

    internal struct ViewportBounds
    {
        internal ViewportVector Minimum, Maximum;
        internal static ViewportBounds Empty => new ViewportBounds { Minimum = new ViewportVector(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity), Maximum = new ViewportVector(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity) };
        internal void Include(ViewportVector point)
        {
            Minimum = new ViewportVector(Math.Min(Minimum.X, point.X), Math.Min(Minimum.Y, point.Y), Math.Min(Minimum.Z, point.Z));
            Maximum = new ViewportVector(Math.Max(Maximum.X, point.X), Math.Max(Maximum.Y, point.Y), Math.Max(Maximum.Z, point.Z));
        }
        internal bool Intersects(ViewportRay ray, float nearest)
        {
            var entry = 0f; var exit = nearest;
            for (var axis = 0; axis < 3; axis++)
            {
                var direction = ray.Direction[axis]; var origin = ray.Origin[axis];
                if (direction == 0f) { if (origin < Minimum[axis] || origin > Maximum[axis]) return false; continue; }
                var a = (Minimum[axis] - origin) / direction; var b = (Maximum[axis] - origin) / direction;
                entry = Math.Max(entry, Math.Min(a, b)); exit = Math.Min(exit, Math.Max(a, b)); if (exit < entry) return false;
            }
            return true;
        }
    }

    // Constructed once with each immutable shape, outside the mouse/GL callback.
    internal sealed class BridgeTriangleIndex
    {
        private sealed class Node { internal ViewportBounds Bounds; internal Node Left, Right; internal int Start, Count; }
        private readonly float[] _positions;
        private readonly int[] _triangles, _order;
        private readonly ViewportBounds[] _bounds;
        private readonly Node _root;
        internal ViewportBounds Bounds => _root.Bounds;
        internal BridgeTriangleIndex(BridgeMeshShape shape)
        {
            _positions = shape.Positions;
            var triangles = new List<int>(shape.Triangles);
            for (var i = 0; i < shape.Quads.Length; i += 4) triangles.AddRange(new[] { shape.Quads[i], shape.Quads[i + 1], shape.Quads[i + 2], shape.Quads[i], shape.Quads[i + 2], shape.Quads[i + 3] });
            _triangles = triangles.ToArray(); _order = Enumerable.Range(0, _triangles.Length / 3).ToArray(); _bounds = new ViewportBounds[_order.Length];
            for (var i = 0; i < _order.Length; i++) { var bounds = ViewportBounds.Empty; for (var k = 0; k < 3; k++) bounds.Include(Vertex(_triangles[i * 3 + k])); _bounds[i] = bounds; }
            _root = Build(0, _order.Length);
            if (_order.Length == 0) for (var index = 0; index < _positions.Length / 3; index++) _root.Bounds.Include(Vertex(index));
        }
        private ViewportVector Vertex(int index) => new ViewportVector(_positions[index * 3], _positions[index * 3 + 1], _positions[index * 3 + 2]);
        private Node Build(int start, int count)
        {
            var node = new Node { Start = start, Count = count, Bounds = ViewportBounds.Empty };
            for (var i = start; i < start + count; i++) { node.Bounds.Include(_bounds[_order[i]].Minimum); node.Bounds.Include(_bounds[_order[i]].Maximum); }
            if (count <= 8) return node;
            var size = node.Bounds.Maximum - node.Bounds.Minimum; var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            Array.Sort(_order, start, count, Comparer<int>.Create((a, b) => (_bounds[a].Minimum[axis] + _bounds[a].Maximum[axis]).CompareTo(_bounds[b].Minimum[axis] + _bounds[b].Maximum[axis])));
            node.Count = 0; node.Left = Build(start, count / 2); node.Right = Build(start + count / 2, count - count / 2); return node;
        }
        internal bool Intersect(ViewportRay ray, ref float nearest) => _order.Length > 0 && Intersect(_root, ray, ref nearest);
        private bool Intersect(Node node, ViewportRay ray, ref float nearest)
        {
            if (!node.Bounds.Intersects(ray, nearest)) return false;
            if (node.Left != null) { var left = Intersect(node.Left, ray, ref nearest); return Intersect(node.Right, ray, ref nearest) || left; }
            var found = false;
            for (var i = node.Start; i < node.Start + node.Count; i++)
            {
                var offset = _order[i] * 3; var a = Vertex(_triangles[offset]); var ab = Vertex(_triangles[offset + 1]) - a; var ac = Vertex(_triangles[offset + 2]) - a;
                var normal = ViewportVector.Cross(ray.Direction, ac); var determinant = ViewportVector.Dot(ab, normal);
                if (determinant == 0f) continue; // Algebraic degeneracy, not a business tolerance.
                var inverse = 1f / determinant; var delta = ray.Origin - a; var u = ViewportVector.Dot(delta, normal) * inverse;
                if (u < 0f || u > 1f) continue;
                var cross = ViewportVector.Cross(delta, ab); var v = ViewportVector.Dot(ray.Direction, cross) * inverse;
                if (v < 0f || u + v > 1f) continue;
                var distance = ViewportVector.Dot(ac, cross) * inverse;
                if (distance >= 0f && distance < nearest) { nearest = distance; found = true; }
            }
            return found;
        }
    }

    internal sealed class BridgeMeshPicker
    {
        private sealed class Entry { internal BridgeMeshInstance Instance; internal float[] Inverse; internal ViewportBounds Bounds; }
        private readonly Entry[] _entries;
        internal BridgeMeshPicker(IEnumerable<BridgeMeshInstance> instances)
        {
            _entries = instances.Select(instance =>
            {
                var bounds = ViewportBounds.Empty; var local = instance.Shape.PickingIndex.Bounds;
                foreach (var x in new[] { local.Minimum.X, local.Maximum.X }) foreach (var y in new[] { local.Minimum.Y, local.Maximum.Y }) foreach (var z in new[] { local.Minimum.Z, local.Maximum.Z })
                    bounds.Include(ViewportMatrix.Transform(instance.Transform, new ViewportVector(x, y, z)));
                return new Entry { Instance = instance, Inverse = ViewportMatrix.Inverse(instance.Transform), Bounds = bounds };
            }).ToArray();
        }
        internal BridgeMeshInstance Pick(ViewportRay ray, Func<BridgeMeshInstance, bool> visible)
        {
            var nearest = float.PositiveInfinity; BridgeMeshInstance hit = null;
            foreach (var entry in _entries)
            {
                if (!visible(entry.Instance) || !entry.Bounds.Intersects(ray, nearest)) continue;
                var local = new ViewportRay(ViewportMatrix.Transform(entry.Inverse, ray.Origin), ViewportMatrix.Transform(entry.Inverse, ray.Direction, true));
                if (entry.Instance.Shape.PickingIndex.Intersect(local, ref nearest)) hit = entry.Instance;
            }
            return hit;
        }
    }
}
