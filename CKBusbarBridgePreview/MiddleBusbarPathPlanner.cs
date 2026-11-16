using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CKBusbarBridgePreview
{
    [Flags]
    public enum MiddleBusbarSegmentRole
    {
        Ordinary = 0,
        PhaseChange = 1,
        Offset = 2
    }

    public sealed class MiddleBusbarPathPlanningInput
    {
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
        public double OverlapLength { get; set; } = 80d;
        public double PreferredBreakpointSpacing { get; set; } = 2000d;
        public double MaximumDevelopedLength { get; set; } = 3000d;
        public double MinimumOffsetTransitionSpan { get; set; } = 600d;
        public double OffsetProtectionLength { get; set; }
        /// <summary>
        /// 大偏移使用90°路径，展开长度按纵向与横向投影之和计算。
        /// 小偏移保留既有折弯过渡几何。
        /// </summary>
        public bool UseOrthogonalOffsetTransition { get; set; }
        public double PhaseChangeInsulationClearance { get; set; } = 125d;
        public bool RequiresPhaseChange { get; set; }
        /// <summary>仅在调用方随后执行物理排件分段及校验时启用；路径工艺区不等同于单个排件。</summary>
        public bool DeferOffsetLengthLimitToPhysicalGeometry { get; set; }
    }

    public sealed class MiddleBusbarPathPoint
    {
        internal MiddleBusbarPathPoint(double x, double y, double pathDistance)
        {
            X = x;
            Y = y;
            PathDistance = pathDistance;
        }

        public double X { get; }
        public double Y { get; }
        public double PathDistance { get; }
    }

    public sealed class MiddleBusbarPathSegment
    {
        internal MiddleBusbarPathSegment(int index, MiddleBusbarPathPoint start,
            MiddleBusbarPathPoint end, MiddleBusbarSegmentRole role,
            double overlapLength)
        {
            Index = index;
            Start = start;
            End = end;
            Role = role;
            CenterlineLength = end.PathDistance - start.PathDistance;
            DevelopedLength = CenterlineLength + overlapLength;
        }

        public int Index { get; }
        public MiddleBusbarPathPoint Start { get; }
        public MiddleBusbarPathPoint End { get; }
        public MiddleBusbarSegmentRole Role { get; }
        public double CenterlineLength { get; }
        public double DevelopedLength { get; }
    }

    public sealed class MiddleBusbarPathPlan
    {
        internal MiddleBusbarPathPlan(IEnumerable<MiddleBusbarPathPoint> points,
            IEnumerable<MiddleBusbarPathSegment> segments,
            double? offsetFrameStartX,
            double? offsetFrameEndX, bool deferOffsetLengthLimitToPhysicalGeometry = false, bool? isLargeOffset = null)
        {
            Points = new ReadOnlyCollection<MiddleBusbarPathPoint>(points.ToArray());
            Segments = new ReadOnlyCollection<MiddleBusbarPathSegment>(segments.ToArray());
            OffsetFrameStartX = offsetFrameStartX;
            OffsetFrameEndX = offsetFrameEndX;
            DeferOffsetLengthLimitToPhysicalGeometry = deferOffsetLengthLimitToPhysicalGeometry;
            IsLargeOffset = isLargeOffset;
        }

        public IReadOnlyList<MiddleBusbarPathPoint> Points { get; }
        public IReadOnlyList<MiddleBusbarPathSegment> Segments { get; }
        public double? OffsetFrameStartX { get; }
        public double? OffsetFrameEndX { get; }
        public bool DeferOffsetLengthLimitToPhysicalGeometry { get; }
        public bool? IsLargeOffset { get; }
        public MiddleBusbarPathPlan WithOffsetClassification(bool isLargeOffset)
            => new MiddleBusbarPathPlan(Points, Segments, OffsetFrameStartX, OffsetFrameEndX,
                DeferOffsetLengthLimitToPhysicalGeometry, isLargeOffset);
    }

    /// <summary>
    /// 仅依据两端固定接口和工艺长度规划中间铜排。偏移过渡位于路径中部，
    /// 换相占用第一段；基架及架内绝缘子不是本规划器的输入。
    /// </summary>
    public static class MiddleBusbarPathPlanner
    {
        private const double Tolerance = 0.001d;

        /// <summary>普通段可重新分节；特殊段两端必须保留，工艺类型不能随分节改变。</summary>
        public static MiddleBusbarPathPlan RepartitionBreakpoints(MiddleBusbarPathPlan source,
            IReadOnlyList<double> coordinates, IReadOnlyList<int> originalIndices, double overlapLength,
            bool adjustTerminalJoints)
        {
            return RepartitionBreakpoints(source, coordinates, originalIndices, overlapLength, adjustTerminalJoints, false);
        }

        public static MiddleBusbarPathPlan RepartitionBreakpoints(MiddleBusbarPathPlan source,
            IReadOnlyList<double> coordinates, IReadOnlyList<int> originalIndices, double overlapLength,
            bool adjustTerminalJoints, bool deferOrdinaryLengthLimitToPhysicalGeometry)
        {
            if (source == null || coordinates == null || originalIndices == null ||
                coordinates.Count < 2 || coordinates.Count != originalIndices.Count ||
                originalIndices[0] != 0 || originalIndices.Last() != source.Points.Count - 1)
                throw new ArgumentException("重新分节必须保留两端接口及完整的断点身份。");
            var originalCoordinates = source.Points.Select(p => p.X).ToArray();
            for (var i = 0; i < originalIndices.Count; i++)
                if (originalIndices[i] >= 0) originalCoordinates[originalIndices[i]] = coordinates[i];
            var retained = new HashSet<int>(originalIndices.Where(index => index >= 0));
            for (var i = 1; i < originalCoordinates.Length - 1; i++)
            {
                if (retained.Contains(i)) continue;
                var left = retained.Where(index => index < i).Max();
                var right = retained.Where(index => index > i).Min();
                var ratio = (source.Points[i].X - source.Points[left].X) / (source.Points[right].X - source.Points[left].X);
                originalCoordinates[i] = originalCoordinates[left] + ratio * (originalCoordinates[right] - originalCoordinates[left]);
            }
            var tolerance = deferOrdinaryLengthLimitToPhysicalGeometry ? 1.0 : Tolerance;
            var moved = MoveBreakpoints(source, originalCoordinates, overlapLength, adjustTerminalJoints, tolerance);
            foreach (var special in source.Segments.Where(s => s.Role != MiddleBusbarSegmentRole.Ordinary))
            {
                var start = originalIndices.ToList().IndexOf(special.Index);
                if (start < 0 || start + 1 >= originalIndices.Count || originalIndices[start + 1] != special.Index + 1)
                    throw new InvalidOperationException("特殊段排不允许增加断点或移除边界断点。");
            }
            var points = new List<MiddleBusbarPathPoint>();
            var segments = new List<MiddleBusbarPathSegment>();
            var distance = 0d;
            for (var i = 0; i < coordinates.Count; i++)
            {
                var index = originalIndices[i];
                var y = index >= 0 ? moved.Points[index].Y : moved.Segments
                    .First(s => coordinates[i] >= s.Start.X - tolerance && coordinates[i] <= s.End.X + tolerance &&
                        s.Role == MiddleBusbarSegmentRole.Ordinary).Start.Y;
                if (double.IsNaN(coordinates[i]) || double.IsInfinity(coordinates[i]) ||
                    (i > 0 && coordinates[i] - coordinates[i - 1] <= tolerance))
                    throw new InvalidOperationException("断点必须为有限坐标且不能越过相邻断点。");
                if (i > 0) distance += Distance(coordinates[i - 1], points[i - 1].Y, coordinates[i], y);
                points.Add(new MiddleBusbarPathPoint(coordinates[i], y, distance));
                if (i == 0) continue;
                var previous = originalIndices[i - 1];
                var role = previous >= 0 && index == previous + 1 ? moved.Segments[previous].Role : MiddleBusbarSegmentRole.Ordinary;
                var segment = new MiddleBusbarPathSegment(i - 1, points[i - 1], points[i], role, overlapLength);
                if (!deferOrdinaryLengthLimitToPhysicalGeometry && role == MiddleBusbarSegmentRole.Ordinary &&
                    segment.DevelopedLength > 3000d + tolerance)
                    throw new InvalidOperationException("合并后的段排展开长度超过3000 mm，禁止移除该断点。");
                segments.Add(segment);
            }
            return new MiddleBusbarPathPlan(points, segments, moved.OffsetFrameStartX, moved.OffsetFrameEndX,
                source.DeferOffsetLengthLimitToPhysicalGeometry, source.IsLargeOffset);
        }

        /// <summary>只调整既有断点的桥向坐标，保留端部接口、横向坐标和各段工艺类型。</summary>
        public static MiddleBusbarPathPlan MoveBreakpoints(MiddleBusbarPathPlan source,
            IReadOnlyList<double> coordinates, double overlapLength)
        {
            return MoveBreakpoints(source, coordinates, overlapLength, false);
        }

        public static MiddleBusbarPathPlan MoveBreakpoints(MiddleBusbarPathPlan source,
            IReadOnlyList<double> coordinates, double overlapLength, bool adjustTerminalJoints)
        {
            return MoveBreakpoints(source, coordinates, overlapLength, adjustTerminalJoints, Tolerance);
        }

        private static MiddleBusbarPathPlan MoveBreakpoints(MiddleBusbarPathPlan source,
            IReadOnlyList<double> coordinates, double overlapLength, bool adjustTerminalJoints, double tolerance)
        {
            if (source == null || coordinates == null || coordinates.Count != source.Points.Count ||
                coordinates.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                throw new ArgumentException("断点坐标必须完整且有限。");
            if (!adjustTerminalJoints && (Math.Abs(coordinates[0] - source.Points[0].X) > tolerance ||
                Math.Abs(coordinates.Last() - source.Points.Last().X) > tolerance))
                throw new InvalidOperationException("两端固定接口不允许移动。");
            var points = new List<MiddleBusbarPathPoint>();
            var segments = new List<MiddleBusbarPathSegment>();
            var distance = 0d;
            for (var i = 0; i < coordinates.Count; i++)
            {
                if (i > 0)
                {
                    var dx = coordinates[i] - coordinates[i - 1];
                    if (dx <= tolerance) throw new InvalidOperationException("断点不能越过相邻断点。");
                    var dy = source.Points[i].Y - source.Points[i - 1].Y;
                    distance += Math.Sqrt(dx * dx + dy * dy);
                }
                points.Add(new MiddleBusbarPathPoint(coordinates[i], source.Points[i].Y, distance));
                if (i > 0) segments.Add(new MiddleBusbarPathSegment(i - 1, points[i - 1], points[i],
                    source.Segments[i - 1].Role, overlapLength));
            }
            var offset = segments.FirstOrDefault(s => (s.Role & MiddleBusbarSegmentRole.Offset) != 0);
            var originalOffset = source.Segments.FirstOrDefault(s => (s.Role & MiddleBusbarSegmentRole.Offset) != 0);
            // 移动断点仅更新过渡中心，不能把断点间距变化当成特殊基架伸缩量。
            // 完整工艺尺寸由统一规划器随后重新计算。
            var frameLength = originalOffset == null ? 0d
                : (source.OffsetFrameEndX ?? originalOffset.End.X) -
                  (source.OffsetFrameStartX ?? originalOffset.Start.X);
            var center = offset == null ? 0d : (offset.Start.X + offset.End.X) / 2d;
            return new MiddleBusbarPathPlan(points, segments,
                offset == null ? (double?)null : center - frameLength / 2d,
                offset == null ? (double?)null : center + frameLength / 2d,
                source.DeferOffsetLengthLimitToPhysicalGeometry, source.IsLargeOffset);
        }

        public static MiddleBusbarPathPlan Create(MiddleBusbarPathPlanningInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            ValidateFinite(input.StartX, nameof(input.StartX));
            ValidateFinite(input.StartY, nameof(input.StartY));
            ValidateFinite(input.EndX, nameof(input.EndX));
            ValidateFinite(input.EndY, nameof(input.EndY));
            ValidatePositive(input.PreferredBreakpointSpacing, nameof(input.PreferredBreakpointSpacing));
            ValidatePositive(input.MaximumDevelopedLength, nameof(input.MaximumDevelopedLength));
            // 未启用的特殊段不参与规划，也不要求提供其专用参数。
            if (Math.Abs(input.EndY - input.StartY) > Tolerance)
                ValidatePositive(input.MinimumOffsetTransitionSpan, nameof(input.MinimumOffsetTransitionSpan));
            if (input.RequiresPhaseChange)
                ValidateNonNegative(input.PhaseChangeInsulationClearance,
                    nameof(input.PhaseChangeInsulationClearance));
            ValidateNonNegative(input.OverlapLength, nameof(input.OverlapLength));
            ValidateNonNegative(input.OffsetProtectionLength,
                nameof(input.OffsetProtectionLength));

            var maximumCenterlineLength = input.MaximumDevelopedLength - input.OverlapLength;
            if (maximumCenterlineLength <= Tolerance)
                throw new InvalidOperationException("单段最大展开长度必须大于实际搭接长度。");

            var span = input.EndX - input.StartX;
            if (span <= Tolerance)
                throw new InvalidOperationException("A端接口必须位于B端接口的路径前方。");

            var offset = input.EndY - input.StartY;
            var vertices = new List<PathVertex>
            {
                new PathVertex(input.StartX, input.StartY, false)
            };
            double? offsetFrameStartX = null;
            double? offsetFrameEndX = null;
            if (Math.Abs(offset) > Tolerance)
            {
                if (!input.DeferOffsetLengthLimitToPhysicalGeometry && Math.Abs(offset) + input.OverlapLength > input.MaximumDevelopedLength + Tolerance)
                    throw new InvalidOperationException("横向偏移量已超过单根铜排最大展开长度，当前工艺不能在一个连续折弯段内完成。");

                var frameSpan = input.MinimumOffsetTransitionSpan;
                var transitionSpan = frameSpan
                    + 2d * input.OffsetProtectionLength;
                if (transitionSpan > span + Tolerance)
                {
                    throw new InvalidOperationException(
                        "偏移过渡区最小纵向长度超过两端接口可用跨度。");
                }
                if (!input.DeferOffsetLengthLimitToPhysicalGeometry && OffsetTransitionLength(
                        transitionSpan,
                        offset,
                        input.UseOrthogonalOffsetTransition) >
                    maximumCenterlineLength + Tolerance)
                {
                    throw new InvalidOperationException(
                        "偏移过渡段按工艺最小范围生成后超过3000 mm最大展开长度；" +
                        "不允许缩短过渡区或在工艺区内断开。");
                }

                var center = (input.StartX + input.EndX) / 2d;
                offsetFrameStartX = center - frameSpan / 2d;
                offsetFrameEndX = center + frameSpan / 2d;
                var left = Math.Max(input.StartX, center - transitionSpan / 2d);
                var right = Math.Min(input.EndX, center + transitionSpan / 2d);
                if (left > input.StartX + Tolerance)
                    vertices.Add(new PathVertex(left, input.StartY, false));
                vertices.Add(new PathVertex(right, input.EndY, true));
            }
            vertices.Add(new PathVertex(input.EndX, input.EndY, false));

            var points = new List<MiddleBusbarPathPoint>();
            var offsetSegmentIndexes = new HashSet<int>();
            for (var vertexIndex = 0; vertexIndex < vertices.Count - 1; vertexIndex++)
            {
                var start = vertices[vertexIndex];
                var end = vertices[vertexIndex + 1];
                var length = Distance(start.X, start.Y, end.X, end.Y);
                if (length <= Tolerance) continue;
                var isOffset = end.EndsOffsetTransition;
                var partCount = isOffset
                    ? 1
                    : SelectStraightSegmentCount(
                        length,
                        input.PreferredBreakpointSpacing,
                        maximumCenterlineLength);
                var partLength = length / partCount;
                // 偏移段是待后端拆成多块排件的工艺区；启用实体分段时只约束普通直段。
                if (partLength > maximumCenterlineLength + Tolerance &&
                    !(isOffset && input.DeferOffsetLengthLimitToPhysicalGeometry))
                    throw new InvalidOperationException("铜排直线段无法满足3000 mm最大展开长度约束。");

                for (var partIndex = 0; partIndex <= partCount; partIndex++)
                {
                    if (vertexIndex > 0 && partIndex == 0) continue;
                    var fraction = (double)partIndex / partCount;
                    var x = start.X + (end.X - start.X) * fraction;
                    var y = start.Y + (end.Y - start.Y) * fraction;
                    var distance = points.Count == 0 ? 0d : points[points.Count - 1].PathDistance +
                        (isOffset
                            ? OffsetTransitionLength(
                                x - points[points.Count - 1].X,
                                y - points[points.Count - 1].Y,
                                input.UseOrthogonalOffsetTransition)
                            : Distance(points[points.Count - 1].X,
                                points[points.Count - 1].Y, x, y));
                    points.Add(new MiddleBusbarPathPoint(x, y, distance));
                    if (isOffset && points.Count >= 2) offsetSegmentIndexes.Add(points.Count - 2);
                }
            }

            if (points.Count < 2)
                throw new InvalidOperationException("两端接口之间没有可规划的铜排路径。");

            var segments = new List<MiddleBusbarPathSegment>();
            for (var index = 0; index < points.Count - 1; index++)
            {
                var role = MiddleBusbarSegmentRole.Ordinary;
                if (input.RequiresPhaseChange && index == 0) role |= MiddleBusbarSegmentRole.PhaseChange;
                if (offsetSegmentIndexes.Contains(index)) role |= MiddleBusbarSegmentRole.Offset;
                var segment = new MiddleBusbarPathSegment(index, points[index], points[index + 1], role,
                    input.OverlapLength);
                if (segment.DevelopedLength > input.MaximumDevelopedLength + Tolerance &&
                    !(input.DeferOffsetLengthLimitToPhysicalGeometry && (role & MiddleBusbarSegmentRole.Offset) != 0))
                    throw new InvalidOperationException("规划结果存在超过3000 mm最大展开长度的铜排段。");
                segments.Add(segment);
            }

            if (input.RequiresPhaseChange &&
                segments[0].CenterlineLength + Tolerance <
                2d * input.PhaseChangeInsulationClearance)
            {
                throw new InvalidOperationException(
                    "第一段没有足够空间完成换相绝缘距离约束；请增大端部可用长度或调整分段。");
            }

            return new MiddleBusbarPathPlan(
                points,
                segments,
                offsetFrameStartX,
                offsetFrameEndX, input.DeferOffsetLengthLimitToPhysicalGeometry, input.UseOrthogonalOffsetTransition);
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double OffsetDoglegLength(double longitudinalSpan,
            double lateralOffset)
        {
            longitudinalSpan = Math.Abs(longitudinalSpan);
            lateralOffset = Math.Abs(lateralOffset);
            var straightLead = longitudinalSpan / 4d;
            var diagonalSpan = longitudinalSpan - 2d * straightLead;
            return 2d * straightLead + Math.Sqrt(
                diagonalSpan * diagonalSpan + lateralOffset * lateralOffset);
        }

        private static double OffsetTransitionLength(
            double longitudinalSpan,
            double lateralOffset,
            bool useOrthogonalTransition)
        {
            return useOrthogonalTransition
                ? Math.Abs(longitudinalSpan) + Math.Abs(lateralOffset)
                : OffsetDoglegLength(longitudinalSpan, lateralOffset);
        }

        /// <summary>
        /// 3000 mm是硬上限，2000 mm只是优选值。先确定满足硬上限的
        /// 最少段数，再在靠近2000 mm的相邻候选段数中选择偏差最小者；
        /// 偏差相同时优先接头更少的方案。
        /// </summary>
        private static int SelectStraightSegmentCount(
            double length,
            double preferredLength,
            double maximumLength)
        {
            var minimumCount = Math.Max(1,
                (int)Math.Ceiling((length - Tolerance) / maximumLength));
            var idealCount = Math.Max(1,
                (int)Math.Round(length / preferredLength,
                    MidpointRounding.AwayFromZero));
            var maximumCandidate = Math.Max(
                minimumCount,
                idealCount + 1);
            var selectedCount = minimumCount;
            var selectedDeviation = Math.Abs(
                length / selectedCount - preferredLength);
            for (var count = minimumCount + 1;
                count <= maximumCandidate;
                count++)
            {
                var candidateLength = length / count;
                if (candidateLength > maximumLength + Tolerance)
                {
                    continue;
                }

                var deviation = Math.Abs(candidateLength - preferredLength);
                if (deviation < selectedDeviation - Tolerance)
                {
                    selectedCount = count;
                    selectedDeviation = deviation;
                }
            }

            return selectedCount;
        }

        private static void ValidateFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, "参数必须是有限数值。");
        }

        private static void ValidatePositive(double value, string name)
        {
            ValidateFinite(value, name);
            if (value <= 0d) throw new ArgumentOutOfRangeException(name, "参数必须大于0。");
        }

        private static void ValidateNonNegative(double value, string name)
        {
            ValidateFinite(value, name);
            if (value < 0d) throw new ArgumentOutOfRangeException(name, "参数不能小于0。");
        }

        private sealed class PathVertex
        {
            internal PathVertex(double x, double y, bool endsOffsetTransition)
            {
                X = x;
                Y = y;
                EndsOffsetTransition = endsOffsetTransition;
            }

            internal double X { get; }
            internal double Y { get; }
            internal bool EndsOffsetTransition { get; }
        }
    }
}
