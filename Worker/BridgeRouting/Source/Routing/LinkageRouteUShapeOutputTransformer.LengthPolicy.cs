using GeometRi;
using System;
using System.Collections.Generic;
using System.Linq;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Geometry;

namespace RouteLinkBar.Algorithm.Routing
{
    internal static partial class LinkageRouteUShapeOutputTransformer
    {
        // 关闭允许U形时，M与U仍可按用户确认的实际直线长度关系保留。
        // ContainsUShape继续只描述33版的反向平行形状，不再将所有M段称作U。
        internal static bool ContainsRestrictedReturn(IEnumerable<PlanarBendBusbarSetting> settings)
        {
            return (settings ?? Enumerable.Empty<PlanarBendBusbarSetting>())
                .Any(setting => GetRestrictedReturnSegmentIndices(setting).Count > 0);
        }

        private static IReadOnlyList<int> GetRestrictedReturnSegmentIndices(PlanarBendBusbarSetting setting)
        {
            var points = setting?.PointList;
            if (points == null || points.Count < 4) return Array.Empty<int>();
            var restrictedSegments = new List<int>();
            for (var middleIndex = 1; middleIndex + 2 < points.Count; middleIndex++)
            {
                var incoming = new Vector3d(points[middleIndex - 1], points[middleIndex]);
                var middle = new Vector3d(points[middleIndex], points[middleIndex + 1]);
                var outgoing = new Vector3d(points[middleIndex + 1], points[middleIndex + 2]);
                if (!HasSameDirectionBends(incoming, middle, outgoing)) continue;
                if (!HasLongerMiddleStraight(setting, middleIndex)) restrictedSegments.Add(middleIndex);
            }
            // 非三段的嵌入回折沿用33版拆分规则，不删除已有非局部门禁。
            for (var firstIndex = 0; firstIndex + 4 < points.Count; firstIndex++)
            for (var lastIndex = firstIndex + 3; lastIndex + 1 < points.Count; lastIndex++)
                if (AreOppositeParallelDirections(new Vector3d(points[firstIndex], points[firstIndex + 1]),
                    new Vector3d(points[lastIndex], points[lastIndex + 1])))
                    for (var splitIndex = firstIndex + 1; splitIndex < lastIndex; splitIndex++)
                        restrictedSegments.Add(splitIndex);
            return restrictedSegments.Distinct().OrderByDescending(index => points[index].DistanceTo(points[index + 1]))
                .ThenBy(index => index).ToList();
        }

        private static bool HasSameDirectionBends(Vector3d incoming, Vector3d middle, Vector3d outgoing)
        {
            if (incoming.Norm <= GeometryTolerance || middle.Norm <= GeometryTolerance || outgoing.Norm <= GeometryTolerance)
                return false;
            var firstNormal = incoming.Normalized.Cross(middle.Normalized);
            var lastNormal = middle.Normalized.Cross(outgoing.Normalized);
            return firstNormal.Norm > LinkageRouteGeometryTolerances.Direction &&
                lastNormal.Norm > LinkageRouteGeometryTolerances.Direction &&
                firstNormal.Normalized.Dot(lastNormal.Normalized) >= 1d - LinkageRouteGeometryTolerances.Direction;
        }

        private static bool HasLongerMiddleStraight(PlanarBendBusbarSetting setting, int middleIndex)
        {
            var straightLengths = new double[3];
            for (var localIndex = 0; localIndex < 3; localIndex++)
            {
                if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(setting, middleIndex - 1 + localIndex,
                    out var boundary, out _)) return false;
                straightLengths[localIndex] = boundary.OriginalLength - boundary.StartTrim - boundary.EndTrim;
                if (double.IsNaN(straightLengths[localIndex]) || double.IsInfinity(straightLengths[localIndex]) ||
                    straightLengths[localIndex] <= GeometryTolerance) return false;
            }
            // 严格大于；不借用0.5mm尺寸验收容差把相等或更短判为通过。
            return straightLengths[1] > straightLengths[0] + straightLengths[2];
        }

        private static bool TryNormalizeRestrictedM(PlanarBendBusbarSetting setting, int middleIndex,
            out PlanarBendBusbarSetting replacement)
        {
            replacement = null;
            var points = setting.PointList;
            if (middleIndex <= 0 || middleIndex + 2 >= points.Count) return false;
            var incoming = new Vector3d(points[middleIndex - 1], points[middleIndex]);
            var middle = new Vector3d(points[middleIndex], points[middleIndex + 1]);
            var outgoing = new Vector3d(points[middleIndex + 1], points[middleIndex + 2]);
            if (!HasSameDirectionBends(incoming, middle, outgoing) ||
                incoming.Normalized.Cross(outgoing.Normalized).Norm <= LinkageRouteGeometryTolerances.Direction)
                return false;
            var intersection = new Line3d(points[middleIndex - 1], incoming).IntersectionWith(
                new Line3d(points[middleIndex + 2], outgoing)) as Point3d;
            if (intersection == null || new Vector3d(points[middleIndex - 1], intersection).Dot(incoming.Normalized) <= GeometryTolerance ||
                new Vector3d(intersection, points[middleIndex + 2]).Dot(outgoing.Normalized) <= GeometryTolerance)
                return false;
            var normalizedPoints = points.Take(middleIndex).Select(CopyPoint).Concat(new[] { CopyPoint(intersection) })
                .Concat(points.Skip(middleIndex + 2).Select(CopyPoint)).ToList();
            if (!LinkageRouteBendGeometry.HasNoAcuteAngle(normalizedPoints, out _, out _) ||
                !LinkageRouteBendGeometry.HasNoSelfIntersection(normalizedPoints, out _, out _)) return false;
            replacement = setting.Copy();
            replacement.PointList = normalizedPoints;
            return true;
        }
    }
}
