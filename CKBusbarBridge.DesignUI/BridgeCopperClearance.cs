using CKBusbarBridgePreview;
using GeometRi;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.DesignGeometry
{
    internal sealed class BridgeUiSolid
    {
        internal string GroupKey { get; set; }
        internal string ClearanceGroupKey { get; set; }
        internal int SegmentIndex { get; set; }
        internal Point3d Center { get; set; }
        internal Vector3d LengthDirection { get; set; }
        internal Vector3d WidthDirection { get; set; }
        internal Vector3d ThicknessDirection { get; set; }
        internal double HalfLength { get; set; }
        internal double HalfWidth { get; set; }
        internal double HalfThickness { get; set; }
    }

    internal sealed class BridgeCopperClearanceEvaluation
    {
        internal bool IsClear { get; set; }
        internal double MinimumDistance { get; set; }
        internal string FirstGroupKey { get; set; }
        internal string SecondGroupKey { get; set; }
        internal int FirstPieceIndex { get; set; }
        internal int SecondPieceIndex { get; set; }
        internal double[][] FirstCorners { get; set; }
        internal double[][] SecondCorners { get; set; }
    }

    /// <summary>
    /// 参考联络排的实体净距检查：把每条中心线路径段转换成有方向长方体，
    /// 再按“顶点到实体 + 棱边到棱边”计算跨相实体表面的真实最短距离。
    /// </summary>
    internal static class BridgeCopperClearance
    {
        internal static IReadOnlyList<double[][]> CreateFixedInterfacePreviewSolids(PlanarBendBusbarSetting piece)
        {
            if (piece == null) throw new ArgumentNullException(nameof(piece));
            var result = new List<double[][]>();
            for (var i = 1; i < piece.PointList.Count; i++)
            {
                var direction = new Vector3d(piece.PointList[i - 1], piece.PointList[i]).Normalized;
                var width = piece.WidthVector.Normalized;
                var projection = width.Dot(direction);
                width = new Vector3d(width.X - projection * direction.X,
                    width.Y - projection * direction.Y, width.Z - projection * direction.Z);
                if (width.Norm <= GeometryTolerance)
                    throw new InvalidOperationException("预览截面方向与铜排路径平行：" + piece.GroupKey);
                width = width.Normalized;
                var thickness = direction.Cross(width).Normalized;
                if (thickness.Dot(piece.StartBusbarVector) < 0d)
                    thickness = new Vector3d(-thickness.X, -thickness.Y, -thickness.Z);
                var corners = new List<double[]>();
                foreach (var point in new[] { piece.PointList[i - 1], piece.PointList[i] })
                foreach (var signs in new[] { new[] { -1d, -1d }, new[] { 1d, -1d }, new[] { 1d, 1d }, new[] { -1d, 1d } })
                {
                    var w = signs[0] * piece.BusBarWidth / 2d;
                    var t = (signs[1] + (piece.IsCenter ? 0d : 1d)) * piece.BusBarThickness / 2d;
                    corners.Add(new[] { point.X + width.X * w + thickness.X * t,
                        point.Y + width.Y * w + thickness.Y * t, point.Z + width.Z * w + thickness.Z * t });
                }
                result.Add(corners.ToArray());
            }
            return result.AsReadOnly();
        }

        private const double GeometryTolerance = 1e-8d;
        private const double DistanceComparisonTolerance = 1e-6d;
        private static readonly int[,] SolidEdgeVertexIndexPairs =
        {
            { 0, 1 }, { 0, 2 }, { 0, 4 },
            { 1, 3 }, { 1, 5 }, { 2, 3 },
            { 2, 6 }, { 3, 7 }, { 4, 5 },
            { 4, 6 }, { 5, 7 }, { 6, 7 }
        };

        internal static BridgeCopperClearanceEvaluation Evaluate(
            IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> clearanceGroupSelector,
            double requiredInsulationClearance)
        {
            if (busbars == null)
            {
                throw new ArgumentNullException(nameof(busbars));
            }
            if (clearanceGroupSelector == null)
            {
                throw new ArgumentNullException(nameof(clearanceGroupSelector));
            }
            if (double.IsNaN(requiredInsulationClearance) ||
                double.IsInfinity(requiredInsulationClearance) ||
                requiredInsulationClearance < 0d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredInsulationClearance));
            }

            return EvaluateWithRouteLinkBarGeometry(
                busbars,
                clearanceGroupSelector,
                requiredInsulationClearance);

            var solidRecords = new List<PlanarBusbarSolidRecord>();
            for (var busbarIndex = 0;
                busbarIndex < busbars.Count;
                busbarIndex++)
            {
                var busbar = busbars[busbarIndex];
                var clearanceGroupKey = clearanceGroupSelector(busbar);
                solidRecords.AddRange(CreateSolidRecords(
                    busbar,
                    clearanceGroupKey,
                    busbarIndex));
            }

            var evaluation = new BridgeCopperClearanceEvaluation
            {
                IsClear = true,
                MinimumDistance = double.PositiveInfinity
            };
            for (var firstSolidIndex = 0;
                firstSolidIndex < solidRecords.Count - 1;
                firstSolidIndex++)
            {
                var firstRecord = solidRecords[firstSolidIndex];
                for (var secondSolidIndex = firstSolidIndex + 1;
                    secondSolidIndex < solidRecords.Count;
                    secondSolidIndex++)
                {
                    var secondRecord = solidRecords[secondSolidIndex];
                    if (string.Equals(
                            firstRecord.ClearanceGroupKey,
                            secondRecord.ClearanceGroupKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var axisAlignedDistanceLowerBound =
                        GetAxisAlignedBoundingBoxDistanceLowerBound(
                            firstRecord.Solid,
                            secondRecord.Solid);
                    if (axisAlignedDistanceLowerBound >=
                        evaluation.MinimumDistance -
                        DistanceComparisonTolerance)
                    {
                        continue;
                    }

                    var actualDistance = GetMinimumDistance(
                        firstRecord.Solid,
                        secondRecord.Solid);
                    if (actualDistance >= evaluation.MinimumDistance)
                    {
                        continue;
                    }

                    evaluation.MinimumDistance = actualDistance;
                    evaluation.FirstGroupKey = firstRecord.DisplayKey;
                    evaluation.SecondGroupKey = secondRecord.DisplayKey;
                    evaluation.IsClear = actualDistance +
                        DistanceComparisonTolerance >=
                        requiredInsulationClearance;
                }
            }

            return evaluation;
        }

        // 同相搭接允许表面接触，但禁止实体相穿；不能用正净距门槛拒绝正常夹接。
        // 这里仅检查真实直段截面，跨相仍由 Evaluate 扫描，圆角另做实体核验。
        internal static BridgeCopperClearanceEvaluation EvaluateMemberStraightSegments(
            IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> memberSelector)
        {
            if (busbars == null) throw new ArgumentNullException(nameof(busbars));
            if (memberSelector == null) throw new ArgumentNullException(nameof(memberSelector));
            var records = busbars.SelectMany((bar, index) =>
                CreateSolidRecords(bar, memberSelector(bar), index)).ToArray();
            for (var first = 0; first < records.Length; first++)
            for (var second = first + 1; second < records.Length; second++)
            {
                var a = records[first]; var b = records[second];
                if (string.Equals(a.ClearanceGroupKey, b.ClearanceGroupKey, StringComparison.Ordinal)) continue;
                if (GetAxisAlignedBoundingBoxDistanceLowerBound(a.Solid, b.Solid) > .001d) continue;
                var difference = new Vector3d(a.Solid.Center, b.Solid.Center);
                if (BuildSeparationAxes(a.Solid, b.Solid).Any(axis =>
                    GetProjectionRadius(a.Solid, axis) + GetProjectionRadius(b.Solid, axis) -
                    Math.Abs(difference.Dot(axis)) <= .001d)) continue;
                return new BridgeCopperClearanceEvaluation { IsClear = false, MinimumDistance = 0d,
                    FirstGroupKey = a.DisplayKey, SecondGroupKey = b.DisplayKey };
            }
            return new BridgeCopperClearanceEvaluation { IsClear = true, MinimumDistance = double.PositiveInfinity };
        }

        // 柜端不变时只检查涉及改动排件的实体对；中间排保持原绝缘净距，柜端搭接保持原相穿门槛。
        internal static BridgeCopperClearanceEvaluation EvaluateChanges(IReadOnlyList<PlanarBendBusbarSetting> busbars,
            ISet<int> changed, ISet<int> middle, double clearance)
        {
            if (double.IsNaN(clearance) || double.IsInfinity(clearance) || clearance <= 0d)
                throw new ArgumentOutOfRangeException(nameof(clearance));
            var records = CreateRouteLinkBarSolidRecords(busbars, s => s.GroupKey.Substring(0, 1), true);
            var result = new BridgeCopperClearanceEvaluation { IsClear = true, MinimumDistance = double.PositiveInfinity };
            for (var first = 0; first < records.Count - 1; first++)
            for (var second = first + 1; second < records.Count; second++)
            {
                var a = records[first]; var b = records[second];
                if (!changed.Contains(a.BusbarIndex) && !changed.Contains(b.BusbarIndex)) continue;
                var required = middle.Contains(a.BusbarIndex) && middle.Contains(b.BusbarIndex) ? clearance : .01d;
                // 这里只需要判定是否安全；下界达到对应门槛的实体对无须再求精确最短距。
                if (!a.CanImproveDistance(b, required)) continue;
                var pair = LinkageRouteClearanceEvaluator.EvaluatePair(a.Solid, b.Solid, required);
                if (pair.IsSameGroupExcluded || pair.IsClear || pair.ActualDistance >= result.MinimumDistance) continue;
                result.IsClear = false; result.MinimumDistance = pair.ActualDistance;
                result.FirstGroupKey = a.DisplayKey; result.SecondGroupKey = b.DisplayKey;
            }
            return result;
        }

        internal static double DevelopedLength(PlanarBendBusbarSetting shape) => shape.PointList == null ? 0d :
            shape.PointList.Zip(shape.PointList.Skip(1), (a, b) => a.DistanceTo(b)).Sum() +
            Math.Max(0, shape.PointList.Count - 2) * Math.Max(0d, shape.BendAllowance);

        // 交互新增几何持有自身实体DTO，不经外部算法程序集执行高频构造和净距计算。
        internal static BridgeUiSolid CreateUiSegmentSolid(string groupKey, Point3d segmentStart,
            Point3d segmentEnd, Vector3d widthDirection, Vector3d thicknessDirection,
            double width, double thickness, int segmentIndex, string clearanceGroupKey = null)
        {
            if (segmentStart == null || segmentEnd == null || widthDirection == null || thicknessDirection == null ||
                new[] { segmentStart.X, segmentStart.Y, segmentStart.Z, segmentEnd.X, segmentEnd.Y, segmentEnd.Z,
                    widthDirection.X, widthDirection.Y, widthDirection.Z,
                    thicknessDirection.X, thicknessDirection.Y, thicknessDirection.Z, width, thickness }
                    .Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                segmentStart.DistanceTo(segmentEnd) <= GeometryTolerance || widthDirection.Norm <= GeometryTolerance ||
                thicknessDirection.Norm <= GeometryTolerance || width <= 0d || thickness <= 0d)
                throw new ArgumentException("实体路径、方向及截面必须是有效的有限数值。");
            var solid = CreateSegmentSolid(segmentStart, segmentEnd, widthDirection, thicknessDirection, width, thickness, true);
            return ToUiSolid(solid, groupKey, segmentIndex, clearanceGroupKey);
        }

        private static BridgeUiSolid ToUiSolid(PlanarBusbarOrientedSolid solid, string groupKey,
            int segmentIndex, string clearanceGroupKey = null)
        {
            return new BridgeUiSolid
            {
                GroupKey = groupKey,
                ClearanceGroupKey = clearanceGroupKey ?? groupKey,
                SegmentIndex = segmentIndex,
                Center = solid.Center,
                LengthDirection = solid.LengthDirection,
                WidthDirection = solid.WidthDirection,
                ThicknessDirection = solid.ThicknessDirection,
                HalfLength = solid.HalfLength,
                HalfWidth = solid.HalfWidth,
                HalfThickness = solid.HalfThickness
            };
        }

        // 是否排除同组由业务调用方决定，此入口始终计算实际实体之间的表面距离。
        internal static double UiPairDistance(BridgeUiSolid first, BridgeUiSolid second)
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            return GetMinimumDistance(ToUiOrientedSolid(first), ToUiOrientedSolid(second));
        }

        internal static bool UiPenetrates(BridgeUiSolid first, BridgeUiSolid second, double tolerance)
        {
            var firstBox = ToUiOrientedSolid(first);
            var secondBox = ToUiOrientedSolid(second);
            var difference = new Vector3d(firstBox.Center, secondBox.Center);
            return BuildSeparationAxes(firstBox, secondBox).All(axis =>
                GetProjectionRadius(firstBox, axis) + GetProjectionRadius(secondBox, axis) -
                Math.Abs(difference.Dot(axis)) > tolerance);
        }

        internal static BridgeUiSolid CreateUiBendSweepSolid(string groupKey, Point3d bendCenter,
            Vector3d incoming, Vector3d outgoing, Vector3d barWidthDirection, double halfSpan,
            double barWidth, int bendIndex, string clearanceGroupKey = null)
        {
            if (bendCenter == null || incoming == null || outgoing == null || barWidthDirection == null ||
                incoming.Norm <= GeometryTolerance || outgoing.Norm <= GeometryTolerance ||
                incoming.Normalized.Cross(outgoing.Normalized).Norm <= GeometryTolerance)
                throw new ArgumentException("折弯扫描需要有效的非共线相邻方向。");
            var length = incoming.Normalized;
            // 与已安装几何DLL的多角度纯函数探针核对：扫掠盒固定沿入段及其平面内法向，
            // 两个半跨度均为halfSpan，真实排宽沿第三轴；出段只决定该点是否形成折弯。
            var width = barWidthDirection.Cross(length).Normalized;
            return CreateUiSegmentSolid(groupKey, Translate(bendCenter, length, -halfSpan),
                Translate(bendCenter, length, halfSpan), width, barWidthDirection,
                2d * halfSpan, barWidth, bendIndex, clearanceGroupKey);
        }

        private static PlanarBusbarOrientedSolid ToUiOrientedSolid(BridgeUiSolid source)
        {
            if (source.Center == null || source.LengthDirection == null || source.WidthDirection == null || source.ThicknessDirection == null)
                throw new ArgumentException("实体缺少中心或截面方向。", nameof(source));
            return new PlanarBusbarOrientedSolid
            {
                Center = source.Center,
                LengthDirection = source.LengthDirection,
                WidthDirection = source.WidthDirection,
                ThicknessDirection = source.ThicknessDirection,
                HalfLength = source.HalfLength,
                HalfWidth = source.HalfWidth,
                HalfThickness = source.HalfThickness
            };
        }

        private static BridgeCopperClearanceEvaluation
            EvaluateWithRouteLinkBarGeometry(
            IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> clearanceGroupSelector,
            double requiredInsulationClearance, bool includeBendSweeps = true)
        {
            var solidRecords = CreateRouteLinkBarSolidRecords(busbars, clearanceGroupSelector, includeBendSweeps);
            var evaluation = new BridgeCopperClearanceEvaluation
            {
                IsClear = true,
                MinimumDistance = double.PositiveInfinity
            };
            for (var firstIndex = 0; firstIndex < solidRecords.Count - 1; firstIndex++)
            {
                var firstRecord = solidRecords[firstIndex];
                for (var secondIndex = firstIndex + 1; secondIndex < solidRecords.Count; secondIndex++)
                {
                    var secondRecord = solidRecords[secondIndex];
                    if (!firstRecord.CanImproveDistance(secondRecord, evaluation.MinimumDistance)) continue;
                    var pairEvaluation = LinkageRouteClearanceEvaluator.EvaluatePair(
                        firstRecord.Solid, secondRecord.Solid, requiredInsulationClearance);
                    if (pairEvaluation.IsSameGroupExcluded || pairEvaluation.ActualDistance >= evaluation.MinimumDistance) continue;
                    evaluation.MinimumDistance = pairEvaluation.ActualDistance;
                    evaluation.FirstGroupKey = firstRecord.DisplayKey;
                    evaluation.SecondGroupKey = secondRecord.DisplayKey;
                    evaluation.IsClear = pairEvaluation.IsClear;
                    evaluation.FirstPieceIndex = firstRecord.BusbarIndex;
                    evaluation.SecondPieceIndex = secondRecord.BusbarIndex;
                    evaluation.FirstCorners = firstRecord.Corners();
                    evaluation.SecondCorners = secondRecord.Corners();
                }
            }
            return evaluation;
        }

        internal static System.Collections.IEnumerable ReferenceSolids(IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> selector, bool includeBends) =>
            CreateRouteLinkBarSolidRecords(busbars, selector, includeBends);

        // 长度约束与终检复用同一批直段、折弯扫描实体，避免另建简化几何漏算折弯。
        internal static IReadOnlyList<LongitudinalSolid> LongitudinalSolids(PlanarBendBusbarSetting bar)
            => CreateRouteLinkBarSolidRecords(new[] { bar }, ignored => bar.GroupKey, true)
                .Select(record => new LongitudinalSolid(record.Solid,
                    bar.PointList[record.BendPointIndex >= 0 ? record.BendPointIndex : record.Solid.SegmentIndex - 1],
                    bar.PointList[record.BendPointIndex >= 0 ? record.BendPointIndex : record.Solid.SegmentIndex])).ToArray();

        internal static IReadOnlyList<BridgeUiSolid> UiLongitudinalSolids(PlanarBendBusbarSetting bar)
        {
            if (bar?.PointList == null || bar.PointList.Count < 2 || bar.WidthVector == null ||
                bar.StartBusbarVector == null || bar.WidthVector.Norm <= GeometryTolerance ||
                bar.StartBusbarVector.Norm <= GeometryTolerance || bar.BusBarWidth <= GeometryTolerance ||
                bar.BusBarThickness <= GeometryTolerance || bar.PointList.Any(p => p == null))
                throw new InvalidOperationException("铜排缺少本地保守实体所需的路径、截面及方向。");
            var result = new List<BridgeUiSolid>();
            for (var index = 0; index < bar.PointList.Count - 1; index++)
            {
                if (bar.PointList[index].DistanceTo(bar.PointList[index + 1]) <= GeometryTolerance)
                    throw new InvalidOperationException("铜排存在无效路径段，不能执行本地净距检查。");
                var solid = CreateSegmentSolid(bar.PointList[index], bar.PointList[index + 1],
                    bar.WidthVector, bar.StartBusbarVector, bar.BusBarWidth, bar.BusBarThickness, bar.IsCenter);
                result.Add(ToUiSolid(solid, bar.GroupKey, index + 1));
            }
            var halfSpan = CalculateBendSweepHalfSpan(bar.BusBarThickness, bar.BendAllowance);
            for (var index = 1; index < bar.PointList.Count - 1; index++)
            {
                var incoming = new Vector3d(bar.PointList[index - 1], bar.PointList[index]);
                var outgoing = new Vector3d(bar.PointList[index], bar.PointList[index + 1]);
                if (incoming.Norm <= GeometryTolerance || outgoing.Norm <= GeometryTolerance ||
                    Math.Abs(incoming.Normalized.Dot(bar.WidthVector.Normalized)) > 1e-6d ||
                    Math.Abs(outgoing.Normalized.Dot(bar.WidthVector.Normalized)) > 1e-6d ||
                    incoming.Normalized.Cross(outgoing.Normalized).Norm <= GeometryTolerance) continue;
                result.Add(CreateUiBendSweepSolid(bar.GroupKey, bar.PointList[index], incoming, outgoing,
                    bar.WidthVector, halfSpan, bar.BusBarWidth, index));
            }
            return result.AsReadOnly();
        }

        internal sealed class LongitudinalSolid
        {
            internal LinkageRouteSolid Solid { get; }
            internal Point3d First { get; }
            internal Point3d Last { get; }
            internal LongitudinalSolid(LinkageRouteSolid solid, Point3d first, Point3d last)
            { Solid = solid; First = first; Last = last; }
        }

        private static List<RouteLinkBarSolidRecord> CreateRouteLinkBarSolidRecords(
            IReadOnlyList<PlanarBendBusbarSetting> busbars,
            Func<PlanarBendBusbarSetting, string> clearanceGroupSelector, bool includeBendSweeps)
        {
            var solidRecords = new List<RouteLinkBarSolidRecord>();
            for (var busbarIndex = 0;
                busbarIndex < busbars.Count;
                busbarIndex++)
            {
                var busbar = busbars[busbarIndex];
                var firstRecord = solidRecords.Count;
                var clearanceGroupKey = clearanceGroupSelector(busbar);
                if (busbar?.PointList == null ||
                    busbar.PointList.Count < 2)
                {
                    throw new InvalidOperationException(
                        "铜排“" + (busbar?.GroupKey ?? "未知") +
                        "”的数据不足，无法执行 RouteLinkBar 干涉检查。");
                }

                for (var segmentIndex = 0;
                    segmentIndex < busbar.PointList.Count - 1;
                    segmentIndex++)
                {
                    var segmentStart = busbar.PointList[segmentIndex];
                    var segmentEnd = busbar.PointList[segmentIndex + 1];
                    var routeSolid = CreateRouteLinkBarSegmentSolid(
                        busbar,
                        clearanceGroupKey,
                        segmentStart,
                        segmentEnd,
                        segmentIndex);
                    solidRecords.Add(new RouteLinkBarSolidRecord(routeSolid, (busbar.GroupKey ??
                        ("铜排" + (busbarIndex + 1))) + "-段" + (segmentIndex + 1)));
                }

                // RouteLinkBar 的净距扫描不仅检查直段，还会把每个折弯
                // 按“排厚/2 + 折弯余量”转换为折弯扫描实体。换相排的
                // 干涉通常发生在垂直搭接的转角处；只扫描直段会漏掉
                // 折弯包络，导致预览看似有距离、实际实体却相互侵入。
                if (includeBendSweeps && busbar.PointList.Count >= 3 &&
                    busbar.WidthVector != null &&
                    busbar.WidthVector.Norm > GeometryTolerance &&
                    busbar.BusBarWidth > GeometryTolerance)
                {
                    var bendSweepHalfSpan = CalculateBendSweepHalfSpan(
                        busbar.BusBarThickness, busbar.BendAllowance);
                    for (var bendPointIndex = 1;
                        bendPointIndex < busbar.PointList.Count - 1;
                        bendPointIndex++)
                    {
                        var bendCenter = busbar.PointList[bendPointIndex];
                        var incomingDirection = new Vector3d(
                            busbar.PointList[bendPointIndex - 1],
                            bendCenter);
                        var outgoingDirection = new Vector3d(
                            bendCenter,
                            busbar.PointList[bendPointIndex + 1]);
                        if (incomingDirection.Norm <= GeometryTolerance ||
                            outgoingDirection.Norm <= GeometryTolerance ||
                            Math.Abs(incomingDirection.Normalized.Dot(
                                busbar.WidthVector.Normalized)) > 1e-6d ||
                            Math.Abs(outgoingDirection.Normalized.Dot(
                                busbar.WidthVector.Normalized)) > 1e-6d ||
                            incomingDirection.Normalized.Cross(
                                outgoingDirection.Normalized).Norm <=
                            GeometryTolerance)
                        {
                            continue;
                        }

                        solidRecords.Add(new RouteLinkBarSolidRecord(
                            LinkageRouteSolidGeometry.CreateBendSweepSolid(
                                busbar.GroupKey ?? "未命名铜排",
                                bendCenter,
                                incomingDirection,
                                outgoingDirection,
                                busbar.WidthVector,
                                bendSweepHalfSpan,
                                busbar.BusBarWidth,
                                bendPointIndex,
                                clearanceGroupKey),
                            (busbar.GroupKey ?? ("铜排" + (busbarIndex + 1))) + "-折弯" + bendPointIndex, bendPointIndex));
                    }
                }
                for (var index = firstRecord; index < solidRecords.Count; index++)
                    solidRecords[index].BusbarIndex = busbarIndex;
            }

            return solidRecords;
        }

        /// <summary>
        /// 规划与终检共用的折弯扫描半跨度。规划叠排时传总包络厚度，
        /// 终检逐根扫描时传单根排厚；两者均包含相同的折弯余量。
        /// </summary>
        internal static double CalculateBendSweepHalfSpan(double thickness, double bendAllowance)
        {
            return thickness / 2d + Math.Max(0d, bendAllowance);
        }

        private static LinkageRouteSolid CreateRouteLinkBarSegmentSolid(
            PlanarBendBusbarSetting busbar,
            string clearanceGroupKey,
            Point3d segmentStart,
            Point3d segmentEnd,
            int segmentIndex)
        {
            if (segmentStart == null || segmentEnd == null ||
                busbar.WidthVector == null ||
                busbar.StartBusbarVector == null ||
                busbar.BusBarWidth <= GeometryTolerance ||
                busbar.BusBarThickness <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "铜排“" + (busbar?.GroupKey ?? "未知") +
                    "”的排宽、排厚或方向数据无效，无法执行 RouteLinkBar 干涉检查。");
            }

            var segmentDirection = new Vector3d(segmentStart, segmentEnd);
            if (segmentDirection.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "铜排“" + (busbar.GroupKey ?? "未知") +
                    "”存在无效路径段，无法执行 RouteLinkBar 干涉检查。");
            }

            var normalizedLengthDirection = segmentDirection.Normalized;
            var normalizedWidthDirection =
                busbar.WidthVector.Normalized;
            var widthProjection = normalizedWidthDirection.Dot(
                normalizedLengthDirection);
            normalizedWidthDirection = new Vector3d(
                normalizedWidthDirection.X -
                    normalizedLengthDirection.X * widthProjection,
                normalizedWidthDirection.Y -
                    normalizedLengthDirection.Y * widthProjection,
                normalizedWidthDirection.Z -
                    normalizedLengthDirection.Z * widthProjection);
            if (normalizedWidthDirection.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "铜排“" + (busbar.GroupKey ?? "未知") +
                    "”的路径方向与排宽方向平行，无法执行 RouteLinkBar 干涉检查。");
            }

            normalizedWidthDirection = normalizedWidthDirection.Normalized;
            var resolvedThicknessDirection = normalizedLengthDirection
                .Cross(normalizedWidthDirection).Normalized;
            if (resolvedThicknessDirection.Dot(
                    busbar.StartBusbarVector.Normalized) < 0d)
            {
                resolvedThicknessDirection = new Vector3d(
                    -resolvedThicknessDirection.X,
                    -resolvedThicknessDirection.Y,
                    -resolvedThicknessDirection.Z);
            }

            var routeStart = segmentStart;
            var routeEnd = segmentEnd;
            if (!busbar.IsCenter)
            {
                routeStart = Translate(
                    routeStart,
                    resolvedThicknessDirection,
                    busbar.BusBarThickness / 2d);
                routeEnd = Translate(
                    routeEnd,
                    resolvedThicknessDirection,
                    busbar.BusBarThickness / 2d);
            }

            return LinkageRouteSolidGeometry.CreateSegmentSolid(
                busbar.GroupKey ?? "未命名铜排",
                routeStart,
                routeEnd,
                busbar.WidthVector,
                busbar.StartBusbarVector,
                busbar.BusBarWidth,
                busbar.BusBarThickness,
                segmentIndex + 1,
                clearanceGroupKey);
        }

        private static IEnumerable<PlanarBusbarSolidRecord>
            CreateSolidRecords(
            PlanarBendBusbarSetting busbar,
            string clearanceGroupKey,
            int busbarIndex)
        {
            if (busbar?.PointList == null || busbar.PointList.Count < 2 ||
                busbar.WidthVector == null ||
                busbar.WidthVector.Norm <= GeometryTolerance ||
                busbar.StartBusbarVector == null ||
                busbar.StartBusbarVector.Norm <= GeometryTolerance ||
                busbar.BusBarWidth <= GeometryTolerance ||
                busbar.BusBarThickness <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "铜排“" + (busbar?.GroupKey ?? "未知") +
                    "”的数据不足，无法转换为绝缘距离检查长方体。");
            }

            var records = new List<PlanarBusbarSolidRecord>();
            for (var segmentIndex = 0;
                segmentIndex < busbar.PointList.Count - 1;
                segmentIndex++)
            {
                var segmentStart = busbar.PointList[segmentIndex];
                var segmentEnd = busbar.PointList[segmentIndex + 1];
                if (segmentStart == null || segmentEnd == null ||
                    segmentStart.DistanceTo(segmentEnd) <= GeometryTolerance)
                {
                    throw new InvalidOperationException(
                        "铜排“" + (busbar.GroupKey ?? "未知") +
                        "”第 " + (segmentIndex + 1) +
                        " 段长度无效，无法转换为绝缘距离检查长方体。");
                }

                records.Add(new PlanarBusbarSolidRecord
                {
                    ClearanceGroupKey = clearanceGroupKey,
                    DisplayKey = (busbar.GroupKey ??
                        ("铜排" + (busbarIndex + 1))) +
                        "-段" + (segmentIndex + 1),
                    Solid = CreateSegmentSolid(
                        segmentStart,
                        segmentEnd,
                        busbar.WidthVector,
                        busbar.StartBusbarVector,
                        busbar.BusBarWidth,
                        busbar.BusBarThickness,
                        busbar.IsCenter)
                });
            }

            return records;
        }

        private static PlanarBusbarOrientedSolid CreateSegmentSolid(
            Point3d segmentStart,
            Point3d segmentEnd,
            Vector3d sourceWidthDirection,
            Vector3d sourceThicknessDirection,
            double busbarWidth,
            double busbarThickness,
            bool usesCenterline)
        {
            var segmentVector = new Vector3d(segmentStart, segmentEnd);
            var lengthDirection = segmentVector.Normalized;
            var sourceNormalizedWidthDirection =
                sourceWidthDirection.Normalized;
            var widthLengthProjection = sourceNormalizedWidthDirection.Dot(
                lengthDirection);
            var orthogonalWidthDirection = new Vector3d(
                sourceNormalizedWidthDirection.X -
                    lengthDirection.X * widthLengthProjection,
                sourceNormalizedWidthDirection.Y -
                    lengthDirection.Y * widthLengthProjection,
                sourceNormalizedWidthDirection.Z -
                    lengthDirection.Z * widthLengthProjection);
            if (orthogonalWidthDirection.Norm <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "铜排路径方向与排宽方向平行，无法建立检查长方体。");
            }

            var widthDirection = orthogonalWidthDirection.Normalized;
            var thicknessDirection = lengthDirection.Cross(widthDirection)
                .Normalized;
            if (thicknessDirection.Dot(
                    sourceThicknessDirection.Normalized) < 0d)
            {
                thicknessDirection = Negate(thicknessDirection);
            }

            var solidCenter = new Point3d(
                (segmentStart.X + segmentEnd.X) / 2d,
                (segmentStart.Y + segmentEnd.Y) / 2d,
                (segmentStart.Z + segmentEnd.Z) / 2d);
            if (!usesCenterline)
            {
                solidCenter = Translate(
                    solidCenter,
                    thicknessDirection,
                    busbarThickness / 2d);
            }

            return new PlanarBusbarOrientedSolid
            {
                Center = solidCenter,
                LengthDirection = lengthDirection,
                WidthDirection = widthDirection,
                ThicknessDirection = thicknessDirection,
                HalfLength = segmentVector.Norm / 2d,
                HalfWidth = busbarWidth / 2d,
                HalfThickness = busbarThickness / 2d
            };
        }

        private static double GetMinimumDistance(
            PlanarBusbarOrientedSolid firstSolid,
            PlanarBusbarOrientedSolid secondSolid)
        {
            if (Intersects(firstSolid, secondSolid))
            {
                return 0d;
            }

            // 面对面换相的直段、竖直段和横向段均沿装配体三条正交轴
            // 布置。对于这种情况，两个实体是轴对齐长方体，AABB 间距
            // 就是实体表面的精确最短距离。继续做棱边到棱边的通用计算
            // 会把斜线参数错误地带入正交棱边，出现 101 mm 之类的假低
            // 值，从而把实际满足绝缘距离的换相方案误判为干涉。
            if (IsAxisAlignedSolid(firstSolid) &&
                IsAxisAlignedSolid(secondSolid))
            {
                return GetAxisAlignedBoundingBoxDistanceLowerBound(
                    firstSolid,
                    secondSolid);
            }

            var minimumDistance = double.PositiveInfinity;
            var firstVertices = firstSolid.GetVertices();
            var secondVertices = secondSolid.GetVertices();
            foreach (var firstVertex in firstVertices)
            {
                minimumDistance = Math.Min(
                    minimumDistance,
                    GetPointDistance(firstVertex, secondSolid));
            }
            foreach (var secondVertex in secondVertices)
            {
                minimumDistance = Math.Min(
                    minimumDistance,
                    GetPointDistance(secondVertex, firstSolid));
            }

            for (var firstEdgeIndex = 0;
                firstEdgeIndex < SolidEdgeVertexIndexPairs.GetLength(0);
                firstEdgeIndex++)
            {
                var firstEdgeStart = firstVertices[
                    SolidEdgeVertexIndexPairs[firstEdgeIndex, 0]];
                var firstEdgeEnd = firstVertices[
                    SolidEdgeVertexIndexPairs[firstEdgeIndex, 1]];
                for (var secondEdgeIndex = 0;
                    secondEdgeIndex < SolidEdgeVertexIndexPairs.GetLength(0);
                    secondEdgeIndex++)
                {
                    var secondEdgeStart = secondVertices[
                        SolidEdgeVertexIndexPairs[secondEdgeIndex, 0]];
                    var secondEdgeEnd = secondVertices[
                        SolidEdgeVertexIndexPairs[secondEdgeIndex, 1]];
                    if (GetSegmentPairAxisAlignedDistanceLowerBound(
                            firstEdgeStart,
                            firstEdgeEnd,
                            secondEdgeStart,
                            secondEdgeEnd) >= minimumDistance)
                    {
                        continue;
                    }

                    minimumDistance = Math.Min(
                        minimumDistance,
                        GetSegmentDistance(
                            firstEdgeStart,
                            firstEdgeEnd,
                            secondEdgeStart,
                            secondEdgeEnd));
                }
            }

            // 对非正交路径保留通用棱边算法，同时以轴向包围盒间距作为
            // 可靠下限，防止正交棱边参数计算出现偏小值，误报合法的
            // 换相净距（例如实际 130 mm 被算成 101 mm）。
            return Math.Max(
                minimumDistance,
                GetAxisAlignedBoundingBoxDistanceLowerBound(
                    firstSolid,
                    secondSolid));
        }

        private static bool IsAxisAlignedSolid(
            PlanarBusbarOrientedSolid solid)
        {
            return IsAxisAlignedVector(solid.LengthDirection) &&
                IsAxisAlignedVector(solid.WidthDirection) &&
                IsAxisAlignedVector(solid.ThicknessDirection);
        }

        private static bool IsAxisAlignedVector(Vector3d vector)
        {
            var normalizedVector = vector.Normalized;
            var nonZeroComponentCount = 0;
            if (Math.Abs(normalizedVector.X) > 1d - 1e-8d)
            {
                nonZeroComponentCount++;
            }
            if (Math.Abs(normalizedVector.Y) > 1d - 1e-8d)
            {
                nonZeroComponentCount++;
            }
            if (Math.Abs(normalizedVector.Z) > 1d - 1e-8d)
            {
                nonZeroComponentCount++;
            }

            return nonZeroComponentCount == 1;
        }

        private static bool Intersects(
            PlanarBusbarOrientedSolid firstSolid,
            PlanarBusbarOrientedSolid secondSolid)
        {
            var centerDifference = new Vector3d(
                firstSolid.Center,
                secondSolid.Center);
            foreach (var separationAxis in BuildSeparationAxes(
                firstSolid,
                secondSolid))
            {
                var centerDistance = Math.Abs(
                    centerDifference.Dot(separationAxis));
                var projectionRadius = GetProjectionRadius(
                        firstSolid,
                        separationAxis) +
                    GetProjectionRadius(secondSolid, separationAxis);
                if (centerDistance > projectionRadius + GeometryTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<Vector3d> BuildSeparationAxes(
            PlanarBusbarOrientedSolid firstSolid,
            PlanarBusbarOrientedSolid secondSolid)
        {
            var firstAxes = firstSolid.GetAxes();
            var secondAxes = secondSolid.GetAxes();
            var separationAxes = new List<Vector3d>();
            separationAxes.AddRange(firstAxes);
            separationAxes.AddRange(secondAxes);
            foreach (var firstAxis in firstAxes)
            {
                foreach (var secondAxis in secondAxes)
                {
                    var crossedAxis = firstAxis.Cross(secondAxis);
                    if (crossedAxis != null &&
                        crossedAxis.Norm > GeometryTolerance)
                    {
                        separationAxes.Add(crossedAxis.Normalized);
                    }
                }
            }

            return separationAxes;
        }

        private static double GetPointDistance(
            Point3d sourcePoint,
            PlanarBusbarOrientedSolid targetSolid)
        {
            var centerToPoint = new Vector3d(
                targetSolid.Center,
                sourcePoint);
            var lengthCoordinate = Clamp(
                centerToPoint.Dot(targetSolid.LengthDirection),
                -targetSolid.HalfLength,
                targetSolid.HalfLength);
            var widthCoordinate = Clamp(
                centerToPoint.Dot(targetSolid.WidthDirection),
                -targetSolid.HalfWidth,
                targetSolid.HalfWidth);
            var thicknessCoordinate = Clamp(
                centerToPoint.Dot(targetSolid.ThicknessDirection),
                -targetSolid.HalfThickness,
                targetSolid.HalfThickness);
            var closestPoint = Translate(
                Translate(
                    Translate(
                        targetSolid.Center,
                        targetSolid.LengthDirection,
                        lengthCoordinate),
                    targetSolid.WidthDirection,
                    widthCoordinate),
                targetSolid.ThicknessDirection,
                thicknessCoordinate);
            return sourcePoint.DistanceTo(closestPoint);
        }

        private static double GetSegmentDistance(
            Point3d firstStart,
            Point3d firstEnd,
            Point3d secondStart,
            Point3d secondEnd)
        {
            var firstDirection = new Vector3d(firstStart, firstEnd);
            var secondDirection = new Vector3d(secondStart, secondEnd);
            var secondToFirst = new Vector3d(secondStart, firstStart);
            var firstSquaredLength = firstDirection.Dot(firstDirection);
            var firstSecondDot = firstDirection.Dot(secondDirection);
            var secondSquaredLength = secondDirection.Dot(secondDirection);
            var firstOffsetDot = firstDirection.Dot(secondToFirst);
            var secondOffsetDot = secondDirection.Dot(secondToFirst);
            var denominator = firstSquaredLength * secondSquaredLength -
                firstSecondDot * firstSecondDot;
            if (firstSquaredLength <= GeometryTolerance * GeometryTolerance &&
                secondSquaredLength <= GeometryTolerance * GeometryTolerance)
            {
                return firstStart.DistanceTo(secondStart);
            }
            if (firstSquaredLength <= GeometryTolerance * GeometryTolerance)
            {
                return GetPointToSegmentDistance(
                    firstStart,
                    secondStart,
                    secondEnd,
                    secondSquaredLength);
            }
            if (secondSquaredLength <= GeometryTolerance * GeometryTolerance)
            {
                return GetPointToSegmentDistance(
                    secondStart,
                    firstStart,
                    firstEnd,
                    firstSquaredLength);
            }

            var firstParameterNumerator = 0d;
            var firstParameterDenominator = denominator;
            var secondParameterNumerator = 0d;
            var secondParameterDenominator = denominator;
            var denominatorTolerance = GeometryTolerance *
                firstSquaredLength * secondSquaredLength;
            if (denominator <= denominatorTolerance)
            {
                firstParameterDenominator = 1d;
                secondParameterNumerator = secondOffsetDot;
                secondParameterDenominator = secondSquaredLength;
            }
            else
            {
                firstParameterNumerator = firstSecondDot *
                    secondOffsetDot - secondSquaredLength * firstOffsetDot;
                secondParameterNumerator = firstSquaredLength *
                    secondOffsetDot - firstSecondDot * firstOffsetDot;
                if (firstParameterNumerator < 0d)
                {
                    firstParameterNumerator = 0d;
                    secondParameterNumerator = secondOffsetDot;
                    secondParameterDenominator = secondSquaredLength;
                }
                else if (firstParameterNumerator >
                    firstParameterDenominator)
                {
                    firstParameterNumerator = firstParameterDenominator;
                    secondParameterNumerator = secondOffsetDot +
                        firstSecondDot;
                    secondParameterDenominator = secondSquaredLength;
                }
            }

            if (secondParameterNumerator < 0d)
            {
                secondParameterNumerator = 0d;
                if (-firstOffsetDot < 0d)
                {
                    firstParameterNumerator = 0d;
                }
                else if (-firstOffsetDot > firstSquaredLength)
                {
                    firstParameterNumerator = firstParameterDenominator;
                }
                else
                {
                    firstParameterNumerator = -firstOffsetDot;
                    firstParameterDenominator = firstSquaredLength;
                }
            }
            else if (secondParameterNumerator > secondParameterDenominator)
            {
                secondParameterNumerator = secondParameterDenominator;
                var shiftedFirstOffset = -firstOffsetDot + firstSecondDot;
                if (shiftedFirstOffset < 0d)
                {
                    firstParameterNumerator = 0d;
                }
                else if (shiftedFirstOffset > firstSquaredLength)
                {
                    firstParameterNumerator = firstParameterDenominator;
                }
                else
                {
                    firstParameterNumerator = shiftedFirstOffset;
                    firstParameterDenominator = firstSquaredLength;
                }
            }

            var firstParameter = firstParameterNumerator /
                firstParameterDenominator;
            var secondParameter = secondParameterNumerator /
                secondParameterDenominator;
            return new Vector3d(
                secondToFirst.X + firstParameter * firstDirection.X -
                    secondParameter * secondDirection.X,
                secondToFirst.Y + firstParameter * firstDirection.Y -
                    secondParameter * secondDirection.Y,
                secondToFirst.Z + firstParameter * firstDirection.Z -
                    secondParameter * secondDirection.Z).Norm;
        }

        private static double GetPointToSegmentDistance(
            Point3d sourcePoint,
            Point3d segmentStart,
            Point3d segmentEnd,
            double segmentSquaredLength)
        {
            if (segmentSquaredLength <=
                GeometryTolerance * GeometryTolerance)
            {
                return sourcePoint.DistanceTo(segmentStart);
            }

            var segmentDirection = new Vector3d(segmentStart, segmentEnd);
            var segmentStartToPoint = new Vector3d(
                segmentStart,
                sourcePoint);
            var segmentParameter = Clamp(
                segmentStartToPoint.Dot(segmentDirection) /
                    segmentSquaredLength,
                0d,
                1d);
            return sourcePoint.DistanceTo(new Point3d(
                segmentStart.X + segmentDirection.X * segmentParameter,
                segmentStart.Y + segmentDirection.Y * segmentParameter,
                segmentStart.Z + segmentDirection.Z * segmentParameter));
        }

        private static double GetAxisAlignedBoundingBoxDistanceLowerBound(
            PlanarBusbarOrientedSolid firstSolid,
            PlanarBusbarOrientedSolid secondSolid)
        {
            var firstExtents = firstSolid.GetAxisAlignedExtents();
            var secondExtents = secondSolid.GetAxisAlignedExtents();
            var xGap = Math.Max(
                0d,
                Math.Abs(firstSolid.Center.X - secondSolid.Center.X) -
                firstExtents[0] - secondExtents[0]);
            var yGap = Math.Max(
                0d,
                Math.Abs(firstSolid.Center.Y - secondSolid.Center.Y) -
                firstExtents[1] - secondExtents[1]);
            var zGap = Math.Max(
                0d,
                Math.Abs(firstSolid.Center.Z - secondSolid.Center.Z) -
                firstExtents[2] - secondExtents[2]);
            return Math.Sqrt(xGap * xGap + yGap * yGap + zGap * zGap);
        }

        private static double GetSegmentPairAxisAlignedDistanceLowerBound(
            Point3d firstStart,
            Point3d firstEnd,
            Point3d secondStart,
            Point3d secondEnd)
        {
            var xGap = GetIntervalGap(
                Math.Min(firstStart.X, firstEnd.X),
                Math.Max(firstStart.X, firstEnd.X),
                Math.Min(secondStart.X, secondEnd.X),
                Math.Max(secondStart.X, secondEnd.X));
            var yGap = GetIntervalGap(
                Math.Min(firstStart.Y, firstEnd.Y),
                Math.Max(firstStart.Y, firstEnd.Y),
                Math.Min(secondStart.Y, secondEnd.Y),
                Math.Max(secondStart.Y, secondEnd.Y));
            var zGap = GetIntervalGap(
                Math.Min(firstStart.Z, firstEnd.Z),
                Math.Max(firstStart.Z, firstEnd.Z),
                Math.Min(secondStart.Z, secondEnd.Z),
                Math.Max(secondStart.Z, secondEnd.Z));
            return Math.Sqrt(xGap * xGap + yGap * yGap + zGap * zGap);
        }

        private static double GetProjectionRadius(
            PlanarBusbarOrientedSolid solid,
            Vector3d projectionAxis)
        {
            return Math.Abs(solid.LengthDirection.Dot(projectionAxis)) *
                    solid.HalfLength +
                Math.Abs(solid.WidthDirection.Dot(projectionAxis)) *
                    solid.HalfWidth +
                Math.Abs(solid.ThicknessDirection.Dot(projectionAxis)) *
                    solid.HalfThickness;
        }

        private static double GetIntervalGap(
            double firstMinimum,
            double firstMaximum,
            double secondMinimum,
            double secondMaximum)
        {
            if (firstMaximum < secondMinimum)
            {
                return secondMinimum - firstMaximum;
            }

            return secondMaximum < firstMinimum
                ? firstMinimum - secondMaximum
                : 0d;
        }

        private static Point3d Translate(
            Point3d sourcePoint,
            Vector3d direction,
            double distance)
        {
            return new Point3d(
                sourcePoint.X + direction.X * distance,
                sourcePoint.Y + direction.Y * distance,
                sourcePoint.Z + direction.Z * distance);
        }

        private static Vector3d Negate(Vector3d sourceVector)
        {
            return new Vector3d(
                -sourceVector.X,
                -sourceVector.Y,
                -sourceVector.Z);
        }

        private static double Clamp(
            double sourceValue,
            double minimumValue,
            double maximumValue)
        {
            return Math.Max(minimumValue, Math.Min(maximumValue, sourceValue));
        }

        private sealed class PlanarBusbarSolidRecord
        {
            internal string ClearanceGroupKey { get; set; }
            internal string DisplayKey { get; set; }
            internal PlanarBusbarOrientedSolid Solid { get; set; }
        }

        private sealed class RouteLinkBarSolidRecord
        {
            internal int BusbarIndex { get; set; }
            internal int BendPointIndex { get; }
            internal string DisplayKey { get; }
            internal LinkageRouteSolid Solid { get; }
            private readonly double _xSpan, _ySpan, _zSpan;

            internal RouteLinkBarSolidRecord(LinkageRouteSolid solid, string displayKey, int bendPointIndex = -1)
            {
                Solid = solid; DisplayKey = displayKey;
                BendPointIndex = bendPointIndex;
                _xSpan = Math.Abs(solid.LengthDirection.X) * solid.HalfLength +
                    Math.Abs(solid.WidthDirection.X) * solid.HalfWidth + Math.Abs(solid.ThicknessDirection.X) * solid.HalfThickness;
                _ySpan = Math.Abs(solid.LengthDirection.Y) * solid.HalfLength +
                    Math.Abs(solid.WidthDirection.Y) * solid.HalfWidth + Math.Abs(solid.ThicknessDirection.Y) * solid.HalfThickness;
                _zSpan = Math.Abs(solid.LengthDirection.Z) * solid.HalfLength +
                    Math.Abs(solid.WidthDirection.Z) * solid.HalfWidth + Math.Abs(solid.ThicknessDirection.Z) * solid.HalfThickness;
            }

            internal bool CanImproveDistance(RouteLinkBarSolidRecord other, double minimumDistance)
            {
                if (double.IsInfinity(minimumDistance) || double.IsNaN(minimumDistance)) return true;
                // 实际直段和折弯实体的投影给出严格距离下界；只跳过不可能刷新最小值的配对。
                // 留出数值容差后，近距及相交候选仍由原RouteLinkBar算法精确计算。
                var x = Math.Max(0d, Math.Abs(Solid.Center.X - other.Solid.Center.X) - _xSpan - other._xSpan);
                var y = Math.Max(0d, Math.Abs(Solid.Center.Y - other.Solid.Center.Y) - _ySpan - other._ySpan);
                var z = Math.Max(0d, Math.Abs(Solid.Center.Z - other.Solid.Center.Z) - _zSpan - other._zSpan);
                var threshold = minimumDistance + DistanceComparisonTolerance;
                var squared = x * x + y * y + z * z;
                return double.IsNaN(squared) || double.IsInfinity(squared) || squared < threshold * threshold;
            }

            internal double[][] Corners()
            {
                return new PlanarBusbarOrientedSolid {
                    Center = Solid.Center, LengthDirection = Solid.LengthDirection,
                    WidthDirection = Solid.WidthDirection, ThicknessDirection = Solid.ThicknessDirection,
                    HalfLength = Solid.HalfLength, HalfWidth = Solid.HalfWidth, HalfThickness = Solid.HalfThickness
                }.GetVertices().Select(p => new[] { p.X, p.Y, p.Z }).ToArray();
            }
        }

        private sealed class PlanarBusbarOrientedSolid
        {
            private IReadOnlyList<Point3d> cachedVertices;
            private double[] cachedAxisAlignedExtents;

            internal Point3d Center { get; set; }
            internal Vector3d LengthDirection { get; set; }
            internal Vector3d WidthDirection { get; set; }
            internal Vector3d ThicknessDirection { get; set; }
            internal double HalfLength { get; set; }
            internal double HalfWidth { get; set; }
            internal double HalfThickness { get; set; }

            internal IReadOnlyList<Vector3d> GetAxes()
            {
                return new[]
                {
                    LengthDirection,
                    WidthDirection,
                    ThicknessDirection
                };
            }

            internal IReadOnlyList<Point3d> GetVertices()
            {
                if (cachedVertices != null)
                {
                    return cachedVertices;
                }

                var vertices = new List<Point3d>(8);
                for (var lengthSign = -1;
                    lengthSign <= 1;
                    lengthSign += 2)
                {
                    for (var widthSign = -1;
                        widthSign <= 1;
                        widthSign += 2)
                    {
                        for (var thicknessSign = -1;
                            thicknessSign <= 1;
                            thicknessSign += 2)
                        {
                            vertices.Add(Translate(
                                Translate(
                                    Translate(
                                        Center,
                                        LengthDirection,
                                        lengthSign * HalfLength),
                                    WidthDirection,
                                    widthSign * HalfWidth),
                                ThicknessDirection,
                                thicknessSign * HalfThickness));
                        }
                    }
                }

                cachedVertices = vertices.AsReadOnly();
                return cachedVertices;
            }

            internal double[] GetAxisAlignedExtents()
            {
                if (cachedAxisAlignedExtents == null)
                {
                    cachedAxisAlignedExtents = new[]
                    {
                        Math.Abs(LengthDirection.X) * HalfLength +
                        Math.Abs(WidthDirection.X) * HalfWidth +
                        Math.Abs(ThicknessDirection.X) * HalfThickness,
                        Math.Abs(LengthDirection.Y) * HalfLength +
                        Math.Abs(WidthDirection.Y) * HalfWidth +
                        Math.Abs(ThicknessDirection.Y) * HalfThickness,
                        Math.Abs(LengthDirection.Z) * HalfLength +
                        Math.Abs(WidthDirection.Z) * HalfWidth +
                        Math.Abs(ThicknessDirection.Z) * HalfThickness
                    };
                }

                return cachedAxisAlignedExtents;
            }
        }
    }
}
