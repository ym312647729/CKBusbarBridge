using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GeometRi;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;

namespace RouteLinkBar.Algorithm.Geometry
{
    /// <summary>实际搭接面上的孔轮廓验收。只处理进入搭接面的孔，不要求搭接面一定含孔。</summary>
    public static partial class LinkageRouteTerminalHoleLapGeometry
    {
        private const double Precision = LinkageRouteGeometryTolerances.DistanceComparison;
        private const double Acceptance = LinkageRouteGeometryTolerances.FinalAcceptanceComparison;

        public sealed class Evaluation
        {
            public bool IsValid { get; internal set; } = true;
            public int ActiveHoleCount { get; internal set; }
            public double MinimumEdgeDistance { get; internal set; } = double.PositiveInfinity;
            public string FailureReason { get; internal set; }
            public IReadOnlyList<Vector3d> RepairDisplacements { get; internal set; } = Array.Empty<Vector3d>();
            public double HoleGroupWidthOffset { get; internal set; }
            public bool IsHoleGroupCentered => ActiveHoleCount == 0 || Math.Abs(HoleGroupWidthOffset) <= Acceptance;
            internal IReadOnlyList<Vector3d> CenteringDisplacements { get; set; } = Array.Empty<Vector3d>();
            internal double StartCutExtension { get; set; }
            internal double EndCutExtension { get; set; }
            internal double PreferredStartCutExtension { get; set; }
            internal double PreferredEndCutExtension { get; set; }
        }

        private struct Point2
        {
            internal double X, Y;
            internal Point2(double x, double y) { X = x; Y = y; }
        }

        internal static bool HavePhysicalFaceContact(PlanarBendBusbarSetting first, int firstIndex,
            PlanarBendBusbarSetting second, int secondIndex)
        {
            if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(first, firstIndex, out var a, out _) ||
                !LinkageRouteRoundedLapGeometry.TryGetFlatSegment(second, secondIndex, out var b, out _)) return false;
            var width = first.WidthVector.Normalized;
            var normal = a.Direction.Cross(width).Normalized;
            if (normal.Cross(b.Direction.Cross(second.WidthVector.Normalized).Normalized).Norm >
                LinkageRouteGeometryTolerances.AssemblyDirection) return false;
            var planeDistance = Math.Abs(new Vector3d(a.Start, b.Start).Dot(normal));
            if (Math.Abs(planeDistance - (first.BusBarThickness + second.BusBarThickness) / 2d) > Acceptance) return false;
            var points = new List<Point2>();
            foreach (var end in new[] { Move(b.Start, b.Direction, b.StartTrim), Move(b.End, b.Direction, -b.EndTrim) })
                foreach (var sign in new[] { -1d, 1d })
                {
                    var corner = Move(end, second.WidthVector, sign * second.BusBarWidth / 2d);
                    var relative = new Vector3d(a.Start, corner);
                    points.Add(new Point2(relative.Dot(a.Direction), relative.Dot(width)));
                }
            var ordered = new List<Point2> { points[0], points[2], points[3], points[1] };
            var clipped = ClipRectangle(ordered, a.StartTrim, a.OriginalLength - a.EndTrim,
                -first.BusBarWidth / 2d, first.BusBarWidth / 2d);
            return clipped.Count >= 3 && Math.Abs(Area(clipped)) > Precision * Precision;
        }

        /// <summary>sourceSegmentIndex/targetSegmentIndex均为最终逐根实体的实际直段索引。</summary>
        public static Evaluation Evaluate(LinkageTerminalMember sourceMember, int sourceSegmentIndex,
            PlanarBendBusbarSetting targetSetting, int targetSegmentIndex,
            bool rotateSlotHole90Degrees, double minimumEdgeDistance = 5d)
        {
            var result = new Evaluation();
            if (sourceMember?.Holes == null || sourceMember.Holes.Count == 0) return result;
            if (!Finite(minimumEdgeDistance) || minimumEdgeDistance < 0d)
                return Failure("始端孔边距必须为非负有限数值。");
            if (!LinkageRouteRoundedLapGeometry.TryGetFlatSegment(targetSetting, targetSegmentIndex,
                    out var targetBoundary, out var targetFailure) ||
                !LinkageRouteRoundedLapGeometry.TryGetFlatSegment(sourceMember.FixedBusbar, sourceSegmentIndex,
                    out var sourceBoundary, out targetFailure))
                return Failure("始端孔约束无法建立实际平直搭接面：" + targetFailure);
            var targetWidth = targetSetting.WidthVector?.Normalized;
            var sourceWidth = sourceMember.FixedBusbar.WidthVector?.Normalized;
            if (targetWidth == null || sourceWidth == null || targetSetting.BusBarWidth <= 0d)
                return Failure("始端孔约束缺少实际排宽方向或排宽。");
            var targetNormal = targetBoundary.Direction.Cross(targetWidth).Normalized;
            var sourceNormal = sourceBoundary.Direction.Cross(sourceWidth).Normalized;
            if (targetNormal.Cross(sourceNormal).Norm > LinkageRouteGeometryTolerances.AssemblyDirection)
                return result; // 非平行厚面不构成该直段的开孔搭接面。
            var targetMinimum = targetBoundary.StartTrim;
            var targetMaximum = targetBoundary.OriginalLength - targetBoundary.EndTrim;
            var halfWidth = targetSetting.BusBarWidth / 2d;
            Func<Point3d, Point2> project = point => new Point2(
                new Vector3d(targetBoundary.Start, point).Dot(targetBoundary.Direction),
                new Vector3d(targetBoundary.Start, point).Dot(targetWidth));
            var sourceStart = Move(sourceBoundary.Start, sourceBoundary.Direction, sourceBoundary.StartTrim);
            var sourceEnd = Move(sourceBoundary.End, sourceBoundary.Direction, -sourceBoundary.EndTrim);
            var sourceHalfWidth = sourceMember.FixedBusbar.BusBarWidth / 2d;
            var sourceRectangle = new List<Point2> {
                project(Move(sourceStart, sourceWidth, -sourceHalfWidth)),
                project(Move(sourceEnd, sourceWidth, -sourceHalfWidth)),
                project(Move(sourceEnd, sourceWidth, sourceHalfWidth)),
                project(Move(sourceStart, sourceWidth, sourceHalfWidth)) };
            var contactPolygon = ClipRectangle(sourceRectangle, targetMinimum, targetMaximum, -halfWidth, halfWidth);
            if (contactPolygon.Count < 3 || Math.Abs(Area(contactPolygon)) <= Precision * Precision) return result;
            var displacementSuggestions = new List<Vector3d>();
            var groupMinimumY = double.PositiveInfinity;
            var groupMaximumY = double.NegativeInfinity;
            var groupMinimumX = double.PositiveInfinity;
            var groupMaximumX = double.NegativeInfinity;
            foreach (var hole in sourceMember.Holes)
            {
                if (hole?.PlaneNormal != null && hole.PlaneNormal.Norm > Precision &&
                    hole.PlaneNormal.Normalized.Cross(targetNormal).Norm > LinkageRouteGeometryTolerances.AssemblyDirection)
                    continue; // 其它开孔面不属于当前搭接面。
                if (!TryGetCapsule(hole, out var originalFirst, out var originalSecond, out var radius))
                    return Failure("始端孔轮廓无效或类型不支持，不能按无孔搭接放行。");
                var sourceFirst = project(originalFirst);
                var sourceSecond = project(originalSecond);
                if (!CapsuleIntersectsPolygon(sourceFirst, sourceSecond, radius + Acceptance, contactPolygon))
                    continue;
                result.ActiveHoleCount++;
                var mappedFirst = sourceFirst;
                var mappedSecond = sourceSecond;
                if (rotateSlotHole90Degrees && hole.HoleTypeValue == 1)
                {
                    var midpointX = (sourceFirst.X + sourceSecond.X) / 2d;
                    var midpointY = (sourceFirst.Y + sourceSecond.Y) / 2d;
                    var halfX = (sourceSecond.X - sourceFirst.X) / 2d;
                    var halfY = (sourceSecond.Y - sourceFirst.Y) / 2d;
                    mappedFirst = new Point2(midpointX + halfY, midpointY - halfX);
                    mappedSecond = new Point2(midpointX - halfY, midpointY + halfX);
                }
                var minimumX = Math.Min(mappedFirst.X, mappedSecond.X) - radius;
                var maximumX = Math.Max(mappedFirst.X, mappedSecond.X) + radius;
                var minimumY = Math.Min(mappedFirst.Y, mappedSecond.Y) - radius;
                var maximumY = Math.Max(mappedFirst.Y, mappedSecond.Y) + radius;
                groupMinimumY = Math.Min(groupMinimumY, minimumY);
                groupMaximumY = Math.Max(groupMaximumY, maximumY);
                groupMinimumX = Math.Min(groupMinimumX, minimumX);
                groupMaximumX = Math.Max(groupMaximumX, maximumX);
                // 切点是可开孔平直区的界限，不将它虚构成一个新的自由切边。
                var edgeDistance = Math.Min(minimumY + halfWidth, halfWidth - maximumY);
                if (targetSegmentIndex == 0) edgeDistance = Math.Min(edgeDistance, minimumX);
                if (targetSegmentIndex == targetSetting.PointList.Count - 2)
                    edgeDistance = Math.Min(edgeDistance, targetBoundary.OriginalLength - maximumX);
                result.MinimumEdgeDistance = Math.Min(result.MinimumEdgeDistance, edgeDistance);
                var containedInFlat = minimumX >= targetMinimum - Acceptance && maximumX <= targetMaximum + Acceptance;
                if (containedInFlat && edgeDistance >= 0d && edgeDistance >= minimumEdgeDistance)
                    continue;
                result.IsValid = false;
                result.FailureReason = string.Format(CultureInfo.InvariantCulture,
                    "始端搭接面内孔边距不合格：实际 {0:F6} mm，严格下限 {1:F6} mm（不扣减尺寸容差）；孔必须完整位于实际可开孔平直区。",
                    edgeDistance, minimumEdgeDistance);
                var required = minimumEdgeDistance;
                var minimumLongitudinalMargin = targetSegmentIndex == 0 ? required : 0d;
                var maximumLongitudinalMargin = targetSegmentIndex == targetSetting.PointList.Count - 2 ? required : 0d;
                var shiftsX = CandidateShifts(minimumX, maximumX, targetMinimum, targetMaximum,
                    minimumLongitudinalMargin, maximumLongitudinalMargin,
                    Math.Min(sourceFirst.X, sourceSecond.X) - radius, Math.Max(sourceFirst.X, sourceSecond.X) + radius);
                var shiftsY = CandidateShifts(minimumY, maximumY, -halfWidth, halfWidth, required, required,
                    Math.Min(sourceFirst.Y, sourceSecond.Y) - radius, Math.Max(sourceFirst.Y, sourceSecond.Y) + radius);
                foreach (var shiftX in shiftsX)
                    foreach (var shiftY in shiftsY)
                    {
                        var vector = Combine(targetBoundary.Direction, shiftX, targetWidth, shiftY);
                        if (vector.Norm > Precision) displacementSuggestions.Add(vector);
                    }
            }
            result.RepairDisplacements = displacementSuggestions.GroupBy(vector => string.Format(CultureInfo.InvariantCulture,
                    "{0:R}|{1:R}|{2:R}", vector.X, vector.Y, vector.Z), StringComparer.Ordinal)
                .Select(group => group.First()).OrderBy(vector => vector.Norm).ToList();
            if (result.ActiveHoleCount > 0)
            {
                // 居中是映射后实际参与孔的完整轮廓包络在排宽方向居中，不移动原孔。
                result.HoleGroupWidthOffset = (groupMinimumY + groupMaximumY) / 2d;
                if (TryGetFreeEndHoleRowOffset(sourceMember, sourceSegmentIndex, targetSetting,
                        targetSegmentIndex, out var preferredRowOffset))
                    result.HoleGroupWidthOffset = preferredRowOffset;
                result.CenteringDisplacements = new[] { Combine(targetWidth, result.HoleGroupWidthOffset,
                    targetBoundary.Direction, 0d) };
                // 按设置值构造并严格验收孔边距，不移动另一端搭接或折弯点。
                if (targetSegmentIndex == 0)
                    result.StartCutExtension = Math.Max(0d, minimumEdgeDistance - groupMinimumX);
                if (targetSegmentIndex == targetSetting.PointList.Count - 2)
                    result.EndCutExtension = Math.Max(0d, groupMaximumX + minimumEdgeDistance - targetBoundary.OriginalLength);
                // 水平搭接只对可延长的自由切边争取额外5mm，不扩大宽向硬门槛。
                if (targetBoundary.Direction.Cross(sourceBoundary.Direction).Norm <= LinkageRouteGeometryTolerances.AssemblyDirection)
                {
                    if (targetSegmentIndex == 0)
                        result.PreferredStartCutExtension = Math.Max(0d, minimumEdgeDistance + 5d - groupMinimumX);
                    if (targetSegmentIndex == targetSetting.PointList.Count - 2)
                        result.PreferredEndCutExtension = Math.Max(0d, groupMaximumX + minimumEdgeDistance + 5d - targetBoundary.OriginalLength);
                }
            }
            return result;
        }

        private static IEnumerable<double> CandidateShifts(double holeMinimum, double holeMaximum,
            double boundaryMinimum, double boundaryMaximum, double minimumMargin, double maximumMargin,
            double sourceMinimum, double sourceMaximum)
        {
            var shifts = new List<double> { 0d };
            var lower = holeMaximum - boundaryMaximum + maximumMargin;
            var upper = holeMinimum - boundaryMinimum - minimumMargin;
            if (lower <= upper + Precision)
            {
                shifts.Add(Math.Max(lower, Math.Min(upper, 0d)));
                shifts.Add(lower); shifts.Add(upper);
            }
            // 允许试移后该孔完全退出本次搭接面；超过容差才能按范围外处理。
            shifts.Add(sourceMinimum - boundaryMaximum - Acceptance - 4d * Precision);
            shifts.Add(sourceMaximum - boundaryMinimum + Acceptance + 4d * Precision);
            return shifts.Where(Finite).Distinct().OrderBy(Math.Abs);
        }

        /// <summary>生成阶段的有限孔组窗口：只新增姿态，后续仍按真实实体和全部门禁验收。</summary>
        internal static IReadOnlyList<PlanarBendBusbarSetting> BuildCenteredLapProposals(
            LinkageTerminalMember member, int sourceIndex, PlanarBendBusbarSetting template,
            bool rotateSlots, double margin)
            => BuildHoleLapProposals(member, sourceIndex, template, rotateSlots, margin, false, true)
                .Concat(BuildHoleLapProposals(member, sourceIndex, template, rotateSlots, margin, false)).ToList();

        internal static IReadOnlyList<PlanarBendBusbarSetting> BuildPerpendicularEdgeLapProposals(
            LinkageTerminalMember member, int sourceIndex, PlanarBendBusbarSetting template,
            bool rotateSlots, double margin)
            => BuildHoleLapProposals(member, sourceIndex, template, rotateSlots, margin, true);

        private static IReadOnlyList<PlanarBendBusbarSetting> BuildHoleLapProposals(
            LinkageTerminalMember member, int sourceIndex, PlanarBendBusbarSetting template,
            bool rotateSlots, double margin, bool eccentricEdges, bool preferFreeCutMargin = false)
        {
            var proposals = new List<PlanarBendBusbarSetting>();
            if (member?.Holes?.Count == 0 || member?.Holes == null ||
                !LinkageRouteRoundedLapGeometry.TryGetFlatSegment(template, 0, out var flat, out _) ||
                !LinkageRouteRoundedLapGeometry.TryGetFlatSegment(member.FixedBusbar, sourceIndex, out var source, out _)) return proposals;
            var width = template.WidthVector.Normalized;
            var normal = flat.Direction.Cross(width).Normalized;
            var sourceWidth = member.FixedBusbar.WidthVector.Normalized;
            if (normal.Cross(source.Direction.Cross(sourceWidth).Normalized).Norm > LinkageRouteGeometryTolerances.AssemblyDirection) return proposals;
            if (preferFreeCutMargin && flat.Direction.Cross(source.Direction).Norm > LinkageRouteGeometryTolerances.AssemblyDirection)
                return proposals;
            var freeCutMargin = margin + (preferFreeCutMargin ? 5d : 0d);
            Func<Point3d, Point2> project = point => new Point2(new Vector3d(flat.Start,point).Dot(flat.Direction),
                new Vector3d(flat.Start,point).Dot(width));
            var sourceCorners = new List<Point2>();
            foreach(var distance in new[] { source.StartTrim, source.OriginalLength-source.EndTrim })
            foreach(var sign in new[] { -1d,1d })
                sourceCorners.Add(project(Move(Move(source.Start,source.Direction,distance),sourceWidth,sign*member.FixedBusbar.BusBarWidth/2d)));
            var polygon = new List<Point2> { sourceCorners[0],sourceCorners[2],sourceCorners[3],sourceCorners[1] };
            // 每项：原孔宽向下/上界、映射孔纵向下/上界、映射孔宽向下/上界。
            var contours = new List<double[]>();
            foreach(var hole in member.Holes)
            {
                if(hole?.PlaneNormal != null && hole.PlaneNormal.Norm > Precision &&
                    hole.PlaneNormal.Normalized.Cross(normal).Norm > LinkageRouteGeometryTolerances.AssemblyDirection) continue;
                if(!TryGetCapsule(hole,out var first,out var second,out var radius)) continue;
                var a=project(first);var b=project(second);
                if(!CapsuleIntersectsPolygon(a,b,radius+Acceptance,polygon)) continue;
                var originalMinimum=Math.Min(a.Y,b.Y)-radius;var originalMaximum=Math.Max(a.Y,b.Y)+radius;
                if(rotateSlots && hole.HoleTypeValue==1)
                {
                    var cx=(a.X+b.X)/2d;var cy=(a.Y+b.Y)/2d;var dx=(b.X-a.X)/2d;var dy=(b.Y-a.Y)/2d;
                    a=new Point2(cx+dy,cy-dx);b=new Point2(cx-dy,cy+dx);
                }
                contours.Add(new[] { originalMinimum,originalMaximum,Math.Min(a.X,b.X)-radius,
                    Math.Max(a.X,b.X)+radius,Math.Min(a.Y,b.Y)-radius,Math.Max(a.Y,b.Y)+radius });
            }
            contours=contours.OrderBy(contour=>(contour[4]+contour[5])/2d).ToList();
            var hasPreferredRows=TryGetFreeEndHoleRowOffset(member,sourceIndex,template,0,out var preferredOffset);
            if(eccentricEdges && !hasPreferredRows) return proposals;
            var centers=new HashSet<double>();
            if(hasPreferredRows && !eccentricEdges) centers.Add(preferredOffset);
            for(var firstIndex=0;(!hasPreferredRows || eccentricEdges) && firstIndex<contours.Count;firstIndex++)
            {
                var low=double.PositiveInfinity;var high=double.NegativeInfinity;
                for(var lastIndex=firstIndex;lastIndex<contours.Count;lastIndex++)
                {
                    low=Math.Min(low,contours[lastIndex][4]);high=Math.Max(high,contours[lastIndex][5]);
                    if(high-low+2d*margin <= template.BusBarWidth+Precision)
                    {
                        if(eccentricEdges)
                        {
                            centers.Add(low+template.BusBarWidth/2d-margin);
                            centers.Add(high-template.BusBarWidth/2d+margin);
                        }
                        else centers.Add((low+high)/2d);
                    }
                }
            }
            foreach(var center in centers.OrderBy(value=>Math.Abs(value-(hasPreferredRows ? preferredOffset : 0d))))
            {
                var active = contours.Where(contour=>contour[0] <= center+template.BusBarWidth/2d+Acceptance &&
                    contour[1] >= center-template.BusBarWidth/2d-Acceptance).ToList();
                if(active.Count==0) continue;
                var startOffset=Math.Min(0d,active.Min(contour=>contour[2])-freeCutMargin);
                var endExtension=Math.Max(0d,active.Max(contour=>contour[3])+freeCutMargin-flat.OriginalLength);
                var candidate=template.Copy();
                candidate.PointList=new List<Point3d> {
                    Move(Move(flat.Start,width,center),flat.Direction,startOffset),
                    Move(Move(flat.End,width,center),flat.Direction,endExtension) };
                var evaluation=Evaluate(member,sourceIndex,candidate,0,rotateSlots,margin);
                if(evaluation.IsValid && evaluation.ActiveHoleCount>0 &&
                    (eccentricEdges || evaluation.IsHoleGroupCentered)) proposals.Add(candidate);
            }
            return proposals;
        }

        private static bool TryGetCapsule(LinkageTerminalHoleInfo hole, out Point3d first,
            out Point3d second, out double radius)
        {
            first = hole?.CenterPoint;
            second = first;
            radius = hole?.HoleTypeValue == 0 ? hole.DimD / 2d : hole?.DimD ?? 0d;
            if (!FinitePoint(first) || !Finite(radius) || radius <= 0d ||
                (hole.HoleTypeValue != 0 && hole.HoleTypeValue != 1)) return false;
            if (hole.HoleTypeValue == 1)
            {
                second = hole.CenterPoint2;
                if (second == null && hole.HoleVector != null && Finite(hole.HoleVector.Norm))
                    second = Move(first, hole.HoleVector, hole.HoleVector.Norm);
                if (!FinitePoint(second) || first.DistanceTo(second) <= Precision) return false;
            }
            return true;
        }

        private static Evaluation Failure(string message) => new Evaluation { IsValid = false, FailureReason = message };
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool FinitePoint(Point3d point) => point != null && Finite(point.X) && Finite(point.Y) && Finite(point.Z);
        private static Point3d Move(Point3d point, Vector3d direction, double distance)
        {
            if (direction.Norm <= Precision) return point.Copy();
            var unit = direction.Normalized;
            return new Point3d(point.X + unit.X * distance, point.Y + unit.Y * distance, point.Z + unit.Z * distance);
        }
        private static Vector3d Combine(Vector3d first, double firstDistance, Vector3d second, double secondDistance)
            => new Vector3d(first.X * firstDistance + second.X * secondDistance,
                first.Y * firstDistance + second.Y * secondDistance, first.Z * firstDistance + second.Z * secondDistance);
        private static double Area(IReadOnlyList<Point2> polygon)
        {
            double sum = 0d;
            for (var index = 0; index < polygon.Count; index++)
            { var next = polygon[(index + 1) % polygon.Count]; sum += polygon[index].X * next.Y - next.X * polygon[index].Y; }
            return sum / 2d;
        }
        private static List<Point2> ClipRectangle(List<Point2> polygon, double minX, double maxX, double minY, double maxY)
        {
            var result = Clip(polygon, p => p.X - minX);
            result = Clip(result, p => maxX - p.X);
            result = Clip(result, p => p.Y - minY);
            return Clip(result, p => maxY - p.Y);
        }
        private static List<Point2> Clip(List<Point2> polygon, Func<Point2, double> signedDistance)
        {
            var result = new List<Point2>();
            if (polygon.Count == 0) return result;
            var previous = polygon[polygon.Count - 1];
            var previousDistance = signedDistance(previous);
            foreach (var current in polygon)
            {
                var distance = signedDistance(current);
                if ((distance >= 0d) != (previousDistance >= 0d))
                {
                    var ratio = previousDistance / (previousDistance - distance);
                    result.Add(new Point2(previous.X + (current.X - previous.X) * ratio,
                        previous.Y + (current.Y - previous.Y) * ratio));
                }
                if (distance >= 0d) result.Add(current);
                previous = current; previousDistance = distance;
            }
            return result;
        }
        private static bool CapsuleIntersectsPolygon(Point2 first, Point2 second, double radius, IReadOnlyList<Point2> polygon)
        {
            if (Inside(first, polygon) || Inside(second, polygon)) return true;
            for (var index = 0; index < polygon.Count; index++)
                if (SegmentDistance(first, second, polygon[index], polygon[(index + 1) % polygon.Count]) <= radius) return true;
            return false;
        }
        private static bool Inside(Point2 point, IReadOnlyList<Point2> polygon)
        {
            bool positive = false, negative = false;
            for (var index = 0; index < polygon.Count; index++)
            {
                var signed = Cross(polygon[index], polygon[(index + 1) % polygon.Count], point);
                positive |= signed > Precision; negative |= signed < -Precision;
            }
            return !(positive && negative);
        }
        private static double Cross(Point2 a, Point2 b, Point2 c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        private static double SegmentDistance(Point2 a, Point2 b, Point2 c, Point2 d)
        {
            var denominator = (b.X-a.X)*(d.Y-c.Y)-(b.Y-a.Y)*(d.X-c.X);
            if (Math.Abs(denominator) > Precision * Precision)
            {
                var t = ((c.X-a.X)*(d.Y-c.Y)-(c.Y-a.Y)*(d.X-c.X))/denominator;
                var u = ((c.X-a.X)*(b.Y-a.Y)-(c.Y-a.Y)*(b.X-a.X))/denominator;
                if (t >= 0d && t <= 1d && u >= 0d && u <= 1d) return 0d;
            }
            return Math.Min(Math.Min(PointSegmentDistance(a,c,d), PointSegmentDistance(b,c,d)),
                Math.Min(PointSegmentDistance(c,a,b), PointSegmentDistance(d,a,b)));
        }
        private static double PointSegmentDistance(Point2 point, Point2 start, Point2 end)
        {
            var dx = end.X-start.X; var dy=end.Y-start.Y;
            var squaredLength = dx*dx+dy*dy;
            var ratio = squaredLength <= Precision*Precision ? 0d : Math.Max(0d,Math.Min(1d,
                ((point.X-start.X)*dx+(point.Y-start.Y)*dy)/squaredLength));
            var x = point.X-start.X-ratio*dx; var y=point.Y-start.Y-ratio*dy;
            return Math.Sqrt(x*x+y*y);
        }
    }
}
