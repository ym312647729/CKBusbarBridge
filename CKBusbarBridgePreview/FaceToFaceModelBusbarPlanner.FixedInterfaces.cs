using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;

namespace CKBusbarBridgePreview
{
    public static partial class FaceToFaceModelBusbarPlanner
    {
        /// <summary>在真实直段增加搭接断点，保持原端点和原折弯，不拆铣排组合体。</summary>
        public static IReadOnlyList<PlanarBendBusbarSetting> SplitFixedInterfaceOverlengthPieces(
            IReadOnlyList<PlanarBendBusbarSetting> pieces)
        {
            return SplitFixedInterfaceOverlengthPieces(pieces, candidate => true);
        }

        private static IReadOnlyList<PlanarBendBusbarSetting> SplitFixedInterfaceOverlengthPieces(
            IReadOnlyList<PlanarBendBusbarSetting> pieces,
            Func<IReadOnlyList<PlanarBendBusbarSetting>, bool> accepts)
        {
            if (pieces == null) throw new ArgumentNullException(nameof(pieces));
            foreach (var piece in pieces)
            {
                if (piece == null || piece.PointList == null || piece.PointList.Count < 2 ||
                    piece.PointList.Any(p => p == null || double.IsNaN(p.X) || double.IsInfinity(p.X) ||
                        double.IsNaN(p.Y) || double.IsInfinity(p.Y) || double.IsNaN(p.Z) || double.IsInfinity(p.Z)))
                    throw new ArgumentException("自动分段需要有限且完整的排件路径。", nameof(pieces));
                ValidatePositiveFinite(piece.BusBarWidth, nameof(piece.BusBarWidth));
                ValidatePositiveFinite(piece.BusBarThickness, nameof(piece.BusBarThickness));
                ValidateNonNegativeFinite(piece.BendAllowance, nameof(piece.BendAllowance));
            }
            var result = pieces.Select(p => p.Copy()).ToList();
            for (var index = 0; index < result.Count; index++)
            {
                var source = result[index];
                if (source.GetDevelopedLength() <= 3000d + GeometryTolerance) continue;
                PlanarBendBusbarSetting first = null, tail = null;
                if (!source.IsMilledBar)
                {
                    var prefix = 0d;
                    var candidates = new List<Tuple<int, double, double>>();
                    var reserve = source.BusBarWidth + 40d + 50d + Math.Max(source.BarBendingRadius, source.VerticalBendingRadius);
                    for (var leg = 0; leg < source.PointList.Count - 1; leg++)
                    {
                        var travel = new Vector3d(source.PointList[leg], source.PointList[leg + 1]);
                        var length = travel.Norm;
                        // 新断点必须能沿+Y抬高一个排厚。竖段、斜段及立放截面不能
                        // 靠翻转桥头方向凑出空间；继续寻找其它水平直段。
                        if (length <= GeometryTolerance || source.WidthVector == null ||
                            source.WidthVector.Norm <= GeometryTolerance ||
                            Math.Abs(travel.Normalized.Y) > GeometryTolerance ||
                            Math.Abs(source.WidthVector.Normalized.Y) > GeometryTolerance ||
                            Math.Abs(travel.Normalized.Dot(source.WidthVector.Normalized)) > GeometryTolerance)
                        {
                            prefix += length;
                            continue;
                        }
                        var low = reserve;
                        var high = Math.Min(length - reserve, 3000d - prefix - leg * Math.Max(0d, source.BendAllowance) - source.BusBarWidth / 2d);
                        if (high >= low)
                        {
                            foreach (var distance in new[] { Math.Max(low, Math.Min(high, 2000d - prefix)), low, high }.Distinct())
                                candidates.Add(Tuple.Create(leg, distance, Math.Abs(prefix + distance - 2000d)));
                        }
                        prefix += length;
                    }
                    foreach (var candidate in candidates.OrderBy(c => c.Item3).ThenBy(c => c.Item1).ThenBy(c => c.Item2))
                    {
                        SplitAtFixedStraight(source, candidate.Item1, candidate.Item2, out var left, out var right);
                        if (left.GetDevelopedLength() > 3000d + GeometryTolerance ||
                            right.GetDevelopedLength() >= source.GetDevelopedLength() - GeometryTolerance) continue;
                        var proposed = result.Take(index).Concat(new[] { left, right }).Concat(result.Skip(index + 1)).ToArray();
                        if (!accepts(proposed)) continue;
                        first = left; tail = right; break;
                    }
                }
                if (first == null)
                    throw new InvalidOperationException("铜排超长且找不到避开折弯/铣排并满足空间及净距的直段断点：" + source.GroupKey);
                result[index] = first;
                result.Insert(index + 1, tail);
            }
            return result.AsReadOnly();
        }

        private static void SplitAtFixedStraight(PlanarBendBusbarSetting source, int leg, double distance,
            out PlanarBendBusbarSetting first, out PlanarBendBusbarSetting tail)
        {
            var direction = new Vector3d(source.PointList[leg], source.PointList[leg + 1]).Normalized;
            var thicknessDirection = new Vector3d(0d, 1d, 0d);
            var cut = PlanarBendBusbarSetting.Translate(source.PointList[leg], direction, distance);
            var halfLap = source.BusBarWidth / 2d;
            first = source.Copy();
            first.GroupKey = source.GroupKey + "/split";
            first.EndLapWidth = source.BusBarWidth;
            first.PointList = source.PointList.Take(leg + 1).Select(p => p.Copy()).Concat(new[]
                { PlanarBendBusbarSetting.Translate(cut, direction, halfLap) }).ToList();
            tail = source.Copy();
            tail.GroupKey = source.GroupKey + "/tail";
            tail.StartBusbarVector = thicknessDirection.Copy();
            tail.StartLapWidth = source.BusBarWidth;
            // 复用正式桥头的排宽+40直段、50投影过渡；只取入口半桥，随后回到原层。
            var bridge = FaceToWallModelBusbarPlanner.CreateBridgeReferenceCenterlinePoints(
                PlanarBendBusbarSetting.Translate(cut, direction, -halfLap), source.PointList[leg + 1],
                direction, thicknessDirection, source.BusBarWidth, source.BusBarThickness,
                source.BusBarThickness, 0d);
            tail.PointList = bridge.Take(3).Select(p => p.Copy())
                .Concat(source.PointList.Skip(leg + 1).Select(p => p.Copy())).ToList();
            // 原立弯类型跟随原点，新增桥头为平弯，防止混合路径被整条解释成立弯。
            foreach (var piece in new[] { first, tail })
                piece.PreviewBendPointKinds = piece.PointList.Select(point =>
                {
                    var original = source.PointList.FindIndex(p => p.DistanceTo(point) < GeometryTolerance);
                    return original >= 0
                        ? source.PreviewBendPointKinds != null && source.PreviewBendPointKinds.Count == source.PointList.Count
                            ? source.PreviewBendPointKinds[original]
                            : source.IsVerticalBend ? BusbarPreviewBendPointKind_e.Vertical : BusbarPreviewBendPointKind_e.Planar
                        : BusbarPreviewBendPointKind_e.Planar;
                }).ToList();
        }

        /// <summary>保留正式截面方向的直段外形；仅用于预览，不包含孔及折弯圆角。</summary>
        public static IReadOnlyList<double[][]> CreateFixedInterfacePreviewSolids(PlanarBendBusbarSetting piece)
        {
            // 兼容入口；预览与交互使用同一份实际截面几何源码。
            return CKBusbarBridge.DesignGeometry.BridgeCopperClearance.CreateFixedInterfacePreviewSolids(piece);
        }

        /// <summary>
        /// 固定接口的中间排几何适配。坐标为X桥向/Y高度/Z横向；输入不含基架组件。
        /// 这里只组合既有正式几何模板，禁止另画四分点折线冒充立弯、铣排或换相。
        /// </summary>
        public static IReadOnlyList<PlanarBendBusbarSetting> CreateFixedInterfaceMiddleGeometry(
            MiddleBusbarPathPlan path, IReadOnlyList<IReadOnlyList<Point3d>> bInterfaces,
            IReadOnlyList<IReadOnlyList<Point3d>> aInterfaces, double width, double thickness,
            bool requiresPhaseChange, double availableHeightMinimum, double availableHeightMaximum,
            double availableHalfWidth, double clearance = 125d,
            IReadOnlyList<PlanarBendBusbarSetting> fixedConnections = null)
        {
            return CreateFixedInterfaceMiddleGeometryCore(path, bInterfaces, aInterfaces, width, thickness,
                requiresPhaseChange, availableHeightMinimum, availableHeightMaximum, availableHalfWidth,
                clearance, fixedConnections, false, null);
        }

        public static IReadOnlyList<PlanarBendBusbarSetting> CreateFixedInterfaceMiddleGeometry(
            MiddleBusbarPathPlan path, IReadOnlyList<IReadOnlyList<Point3d>> bInterfaces,
            IReadOnlyList<IReadOnlyList<Point3d>> aInterfaces, double width, double thickness,
            bool requiresPhaseChange, double availableHeightMinimum, double availableHeightMaximum,
            double availableHalfWidth, double clearance,
            IReadOnlyList<PlanarBendBusbarSetting> fixedConnections, bool preservePhaseTerminalPositions)
        {
            return CreateFixedInterfaceMiddleGeometryCore(path, bInterfaces, aInterfaces, width, thickness,
                requiresPhaseChange, availableHeightMinimum, availableHeightMaximum, availableHalfWidth,
                clearance, fixedConnections, preservePhaseTerminalPositions, null);
        }

        /// <summary>柜端实际接触中心保持固定，中间排按自身排厚叠排，在柜端完成层距过渡。</summary>
        public static IReadOnlyList<PlanarBendBusbarSetting> CreateSideToSideFixedInterfaceMiddleGeometry(
            MiddleBusbarPathPlan path, IReadOnlyList<IReadOnlyList<Point3d>> bContacts,
            IReadOnlyList<IReadOnlyList<Point3d>> aContacts, double width, double thickness,
            double lowestMiddleCenterHeight, bool requiresPhaseChange, double availableHeightMinimum,
            double availableHeightMaximum, double availableHalfWidth, double clearance,
            IReadOnlyList<PlanarBendBusbarSetting> fixedConnections)
        {
            return CreateSideToSideFixedInterfaceMiddleGeometry(path, bContacts, aContacts, width, thickness,
                lowestMiddleCenterHeight, requiresPhaseChange, availableHeightMinimum, availableHeightMaximum,
                availableHalfWidth, clearance, fixedConnections, true);
        }

        internal static IReadOnlyList<PlanarBendBusbarSetting> CreateSideToSideFixedInterfaceMiddleGeometry(
            MiddleBusbarPathPlan path, IReadOnlyList<IReadOnlyList<Point3d>> bContacts,
            IReadOnlyList<IReadOnlyList<Point3d>> aContacts, double width, double thickness,
            double lowestMiddleCenterHeight, bool requiresPhaseChange, double availableHeightMinimum,
            double availableHeightMaximum, double availableHalfWidth, double clearance,
            IReadOnlyList<PlanarBendBusbarSetting> fixedConnections, bool splitOrdinaryOverlength)
        {
            // 先校验真实接口，不能通过名义叠排参数掩盖缺失或不共面的柜端几何。
            CreateFixedInterfaceContext(bContacts, width, thickness);
            CreateFixedInterfaceContext(aContacts, width, thickness);
            if (double.IsNaN(lowestMiddleCenterHeight) || double.IsInfinity(lowestMiddleCenterHeight))
                throw new ArgumentException("中间排层位必须为有限值。", nameof(lowestMiddleCenterHeight));
            for (var phase = 0; phase < PhaseCount; phase++)
                if (bContacts[phase].Count != aContacts[phase].Count)
                    throw new InvalidOperationException("两柜实际搭接成员数量不一致。");
            Func<IReadOnlyList<IReadOnlyList<Point3d>>, IReadOnlyList<IReadOnlyList<Point3d>>> nominal = contacts =>
                contacts.Select(points => (IReadOnlyList<Point3d>)points.OrderBy(p => p.Y)
                    .Select((p, i) => new Point3d(p.X, lowestMiddleCenterHeight + i * 2d * thickness, p.Z))
                    .ToArray()).ToArray();
            return CreateFixedInterfaceMiddleGeometryCore(path, nominal(bContacts), nominal(aContacts), width,
                thickness, requiresPhaseChange, availableHeightMinimum, availableHeightMaximum, availableHalfWidth,
                clearance, fixedConnections, true, (pieces, first, last) =>
                    SideToSideLapAdapter.AdaptHorizontal(pieces, bContacts, aContacts, first, last), splitOrdinaryOverlength);
        }

        private static IReadOnlyList<PlanarBendBusbarSetting> CreateFixedInterfaceMiddleGeometryCore(
            MiddleBusbarPathPlan path, IReadOnlyList<IReadOnlyList<Point3d>> bInterfaces,
            IReadOnlyList<IReadOnlyList<Point3d>> aInterfaces, double width, double thickness,
            bool requiresPhaseChange, double availableHeightMinimum, double availableHeightMaximum,
            double availableHalfWidth, double clearance,
            IReadOnlyList<PlanarBendBusbarSetting> fixedConnections, bool preservePhaseTerminalPositions,
            Action<PlanarBendBusbarSetting[], bool, bool> adaptTerminals, bool splitOrdinaryOverlength = true)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            ValidatePositiveFinite(width, nameof(width));
            ValidatePositiveFinite(thickness, nameof(thickness));
            ValidatePositiveFinite(availableHalfWidth, nameof(availableHalfWidth));
            ValidateNonNegativeFinite(clearance, nameof(clearance));
            if (clearance <= 0d)
                throw new ArgumentOutOfRangeException(nameof(clearance), "固定接口铜排绝缘距离必须大于0 mm。");
            if (double.IsNaN(availableHeightMinimum) || double.IsInfinity(availableHeightMinimum) ||
                double.IsNaN(availableHeightMaximum) || double.IsInfinity(availableHeightMaximum) ||
                availableHeightMaximum <= availableHeightMinimum)
                throw new ArgumentException("孔口可用高度范围无效。");
            if (path.Segments.Count == 0 || path.Points.Count < 2)
                throw new ArgumentException("中间路径缺少有效分段。", nameof(path));
            var bSource = CreateFixedInterfaceContext(bInterfaces, width, thickness);
            var aSource = CreateFixedInterfaceContext(aInterfaces, width, thickness);
            for (var phase = 0; phase < PhaseCount; phase++)
            {
                EnsureSmallOffsetStackOffsetsMatch(bSource.PhaseRouteContexts[phase], aSource.PhaseRouteContexts[phase], phase);
                if (Math.Abs(bSource.PhaseRouteContexts[phase].OrdinaryRouteReferencePoint.Y -
                    aSource.PhaseRouteContexts[phase].OrdinaryRouteReferencePoint.Y) > ModelAlignmentTolerance)
                    throw new InvalidOperationException("两端高度不同，不规划竖向过渡。");
            }
            var axis = new BusbarBridgeAxisSetting(BusbarBridgeAxisDirection_e.PositiveX,
                BusbarBridgeAxisDirection_e.PositiveY, BusbarBridgeAxisDirection_e.PositiveZ);
            var result = new List<PlanarBendBusbarSetting>();
            for (var index = 0; index < path.Segments.Count; index++)
            {
                var firstPiece = result.Count;
                var segment = path.Segments[index];
                var left = PositionFixedContext(index == 0 ? bSource : aSource,
                    index == 0 ? path.Points.First().Y : path.Points.Last().Y, segment.Start.X, segment.Start.Y);
                var right = PositionFixedContext(aSource, path.Points.Last().Y, segment.End.X, segment.End.Y);
                var region = new FaceToFaceFrameBox
                {
                    ComponentName = "固定接口计算空间（非基架反读）",
                    LengthMinimum = segment.Start.X, LengthMaximum = segment.End.X,
                    WidthMinimum = Math.Min(segment.Start.Y, segment.End.Y) - availableHalfWidth,
                    WidthMaximum = Math.Max(segment.Start.Y, segment.End.Y) + availableHalfWidth,
                    HeightMinimum = availableHeightMinimum, HeightMaximum = availableHeightMaximum
                };
                if (index == 0 && requiresPhaseChange)
                {
                    if ((segment.Role & MiddleBusbarSegmentRole.Offset) != 0)
                        throw new InvalidOperationException("换相与偏移不能共用当前首段，请增加断点分离两个工艺区域。");
                    var phaseRegion = new FaceToFacePhaseChangeRegionPlan
                    {
                        MiddleFrame = region, LeftFixedBreakpointCoordinate = segment.Start.X,
                        RightFixedBreakpointCoordinate = segment.End.X, SuppressedBreakpointIntervalIndex = index,
                        InsulationClearance = clearance, RoutingClearance = clearance,
                        BusbarWidth = width, BusbarThickness = thickness, BendAllowance = 20d
                    };
                    result.AddRange(CreatePhaseChangeBusbarGroupWithClearance(phaseRegion, left, right,
                        axis, clearance + PhaseChangePreferredClearanceAddition, false, frontBridgeOnly: true).Busbars);
                }
                else if ((segment.Role & MiddleBusbarSegmentRole.Offset) != 0)
                {
                    if (path.IsLargeOffset == true)
                    {
                        result.AddRange(CreateFixedLargeOffsetGeometry(left, right, axis, segment.Start.X,
                            segment.End.X, index));
                    }
                    else
                    {
                    var isMiddleParallel = ResolveSmallOffsetTransitionKind(
                        Math.Abs(segment.End.Y - segment.Start.Y), width) ==
                        FaceToFaceSmallOffsetTransitionKind_e.MiddleParallelConnection;
                    // 搭接排按横向降序输入，使偏移方向最靠外的一相先过渡；
                    // 模板内按偏移正负镜像错开，输出再还原真实电气相别。
                    var order = Enumerable.Range(0, PhaseCount).OrderBy(p =>
                        (isMiddleParallel ? -1d : 1d) *
                        left.PhaseRouteContexts[p].OrdinaryRouteReferencePoint.Z).ToArray();
                    var orderedLeft = ReorderFixedContext(left, order);
                    var orderedRight = ReorderFixedContext(right, order);
                    var settings = CreateSmallOffsetTransitionPreviewBusbars(new FaceToFaceSmallOffsetRegionPlan
                    {
                        SpecialFrame = region, SuppressedBreakpointIntervalIndex = index,
                        TransitionLeftBreakpointCoordinate = segment.Start.X,
                        TransitionRightBreakpointCoordinate = segment.End.X
                    }, orderedLeft, orderedRight, axis, clearance, false, null, frontBridgeOnly: true,
                        scenarioName: adaptTerminals == null ? "面对面" : "侧对侧");
                    foreach (var setting in settings)
                    {
                        var physical = "ABC".IndexOf(setting.GroupKey[0]);
                        if (physical < 0) throw new InvalidOperationException("过渡排缺少相别。");
                        setting.GroupKey = "ABC"[order[physical]] + setting.GroupKey.Substring(1);
                    }
                    result.AddRange(settings);
                    }
                }
                else
                    result.AddRange(CreateFixedOrdinarySegment(left, right, axis));
                var unrefined = result.Skip(firstPiece).ToArray();
                if (preservePhaseTerminalPositions)
                    ExtendFixedPhaseTerminals(unrefined, bSource, aSource,
                        index == 0 ? (double?)segment.Start.X : null,
                        index == path.Segments.Count - 1 ? (double?)segment.End.X : null);
                adaptTerminals?.Invoke(unrefined, index == 0, index == path.Segments.Count - 1);
                foreach (var piece in unrefined) piece.PlanningSegmentIndex = index;
                // 偏移等工艺段包含沿横向的加工拼接，由工艺几何处理；桥向普通段的共用断点由侧对侧路径规划。
                if (splitOrdinaryOverlength || segment.Role != MiddleBusbarSegmentRole.Ordinary)
                {
                    result.RemoveRange(firstPiece, result.Count - firstPiece);
                    result.AddRange(SplitFixedInterfaceOverlengthPieces(unrefined, candidate =>
                        candidate.All(piece => CreateFixedInterfacePreviewSolids(piece).SelectMany(s => s).All(corner =>
                            corner[1] >= availableHeightMinimum - ModelAlignmentTolerance &&
                            corner[1] <= availableHeightMaximum + ModelAlignmentTolerance &&
                            corner[2] >= region.WidthMinimum - ModelAlignmentTolerance &&
                            corner[2] <= region.WidthMaximum + ModelAlignmentTolerance)) &&
                        PlanarBusbarClearanceValidator.Evaluate(result.Concat(candidate)
                            .Concat(fixedConnections ?? Array.Empty<PlanarBendBusbarSetting>()).ToArray(),
                            GetPhaseChangeClearanceGroupKey, clearance).IsClear));
                }
                foreach (var piece in result.Skip(firstPiece))
                foreach (var corner in CreateFixedInterfacePreviewSolids(piece).SelectMany(s => s))
                    if (corner[1] < availableHeightMinimum - ModelAlignmentTolerance ||
                        corner[1] > availableHeightMaximum + ModelAlignmentTolerance ||
                        corner[2] < region.WidthMinimum - ModelAlignmentTolerance ||
                        corner[2] > region.WidthMaximum + ModelAlignmentTolerance)
                        throw new InvalidOperationException("铜排实体截面超出固定接口的可用空间：" + piece.GroupKey);
            }
            foreach (var piece in result)
            {
                if (splitOrdinaryOverlength && piece.GetDevelopedLength() > 3000d + GeometryTolerance)
                    throw new InvalidOperationException("自动分段后铜排展开长度仍超过3000 mm；排件=" + piece.GroupKey);
                if (piece.PointList.Any(p => p.Y - piece.BusBarThickness / 2d < availableHeightMinimum - ModelAlignmentTolerance ||
                    p.Y + piece.BusBarThickness / 2d > availableHeightMaximum + ModelAlignmentTolerance))
                    throw new InvalidOperationException("实际铜排超出孔口可用高度：" + piece.GroupKey);
            }
            var evaluation = PlanarBusbarClearanceValidator.Evaluate(
                result.Concat(fixedConnections ?? Array.Empty<PlanarBendBusbarSetting>()).ToArray(),
                GetPhaseChangeClearanceGroupKey, clearance);
            if (!evaluation.IsClear)
                throw new InvalidOperationException("中间排跨相实体净距不足：" + evaluation.FirstGroupKey + "/" +
                    evaluation.SecondGroupKey + "，净距=" + evaluation.MinimumDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm。");
            if (adaptTerminals != null) SideToSideLapAdapter.ValidateSeparateMembers(result);
            return result.AsReadOnly();
        }

        /// <summary>人工改长后直接校验原换相实体，不重新生成或压缩工艺。</summary>
        public static void ValidateEditedPhaseChangeClearance(IReadOnlyList<PlanarBendBusbarSetting> busbars, double clearance)
        {
            if (busbars == null) throw new ArgumentNullException(nameof(busbars));
            ValidateNonNegativeFinite(clearance, nameof(clearance));
            var evaluation = PlanarBusbarClearanceValidator.Evaluate(busbars, GetPhaseChangeClearanceGroupKey, clearance);
            if (!evaluation.IsClear)
                throw new InvalidOperationException("换相段排长度不足以满足原实体净距：" + evaluation.FirstGroupKey + "/" +
                    evaluation.SecondGroupKey + "，净距=" + evaluation.MinimumDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                    " mm，要求=" + clearance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " mm；未提交修改。");
        }

        // 侧对侧七点首段的三相端面不同。只延长柜端直段，保留工艺区、原折弯和叠排偏移。
        private static void ExtendFixedPhaseTerminals(PlanarBendBusbarSetting[] pieces,
            FaceToFaceSidePlanningContext b, FaceToFaceSidePlanningContext a,
            double? startX, double? endX)
        {
            foreach (var group in pieces.GroupBy(p => string.Join("-", p.GroupKey.Split('-').Take(2))))
            {
                var members = group.ToArray();
                var phase = "ABC".IndexOf(group.Key[0]);
                if (phase < 0) throw new InvalidOperationException("侧对侧中间排缺少电气相别。");
                if (startX.HasValue)
                {
                    var delta = b.PhaseRouteContexts[phase].OrdinaryRouteReferencePoint.X - startX.Value;
                    var first = members.First();
                    if (delta > GeometryTolerance || first.PointList.Count < 4)
                        throw new InvalidOperationException("柜端前桥头必须向柜内延长，不能侵入中间工艺区。");
                    for (var i = 0; i < 3; i++) first.PointList[i].X += delta;
                }
                if (endX.HasValue)
                {
                    var delta = a.PhaseRouteContexts[phase].OrdinaryRouteReferencePoint.X - endX.Value;
                    if (delta < -GeometryTolerance)
                        throw new InvalidOperationException("柜端出口直段不能缩入中间工艺区。");
                    members.Last().PointList.Last().X += delta;
                }
                var milled = members.Where(p => p.IsMilledBar).ToArray();
                if (milled.Length != 0)
                {
                    if (milled.Length != 3) throw new InvalidOperationException("铣排组合体必须保留三块实体。");
                    foreach (var piece in milled)
                    {
                        piece.MilledBarStartLength = milled.First().GetDevelopedLength();
                        piece.MilledBarEndLength = milled.Last().GetDevelopedLength();
                    }
                }
            }
        }

        private static IReadOnlyList<PlanarBendBusbarSetting> CreateFixedLargeOffsetGeometry(
            FaceToFaceSidePlanningContext left, FaceToFaceSidePlanningContext right,
            BusbarBridgeAxisSetting axis, double startX, double endX, int routeIndex)
        {
            var widths = left.PhaseRouteContexts.Select(p => p.OrdinaryRouteReferencePoint.Z).ToArray();
            var offsets = Enumerable.Range(0, PhaseCount).Select(p =>
                right.PhaseRouteContexts[p].OrdinaryRouteReferencePoint.Z - widths[p]).ToArray();
            var offset = offsets.Average();
            if (Math.Abs(offset) <= GeometryTolerance ||
                offsets.Any(v => Math.Abs(v - offset) > ModelAlignmentTolerance))
                throw new InvalidOperationException("大偏移三相接口必须具有一致的有符号横向偏移。");
            var width = left.PhaseRouteContexts[0].BusbarWidth;
            var span = widths.Max() - widths.Min();
            if (endX - startX < span + width - GeometryTolerance)
                throw new InvalidOperationException("大偏移转角空间不足以容纳三相中心跨度及排宽。");
            // 沿偏移方向最靠内的一相先转弯。正偏移按Z降序，负偏移按Z升序；
            // 沿用正式大偏移的实际相间距错开，不按ABC名称或W+净距强行分配。
            var order = Enumerable.Range(0, PhaseCount).OrderByDescending(p => Math.Sign(offset) * widths[p]).ToArray();
            var firstTurn = startX + (endX - startX - span) / 2d;
            var region = new FaceToFaceLargeOffsetRegionPlan { SignedWidthOffset = offset };
            for (var phase = 0; phase < PhaseCount; phase++)
            {
                var turn = firstTurn + Math.Abs(widths[phase] - widths[order[0]]);
                region.PhaseRoutes.Add(new FaceToFaceLargeOffsetPhaseRoute
                {
                    Phase = GetPhase(phase), TurnOrder = Array.IndexOf(order, phase), TurnLengthCoordinate = turn,
                    BSideWidthCoordinate = widths[phase], ASideWidthCoordinate = widths[phase] + offset,
                    BSideLengthStartCoordinate = startX - width / 2d,
                    BSideLengthEndCoordinate = turn + width / 2d,
                    CrosswiseStartWidthCoordinate = widths[phase] - Math.Sign(offset) * width / 2d,
                    CrosswiseEndWidthCoordinate = widths[phase] + offset + Math.Sign(offset) * width / 2d,
                    ASideLengthStartCoordinate = turn - width / 2d,
                    ASideLengthEndCoordinate = endX + width / 2d
                });
            }
            // 沿B→A的三个物理段都使用入口桥头、出口直头，与段号奇偶无关。
            var definitions = new[]
            {
                CreateLargeOffsetLengthDefinition(routeIndex, region, left, axis, true),
                CreateLargeOffsetCrosswiseDefinition(routeIndex + 1, region, left, axis, 0),
                CreateLargeOffsetLengthDefinition(routeIndex + 2, region, right, axis, false)
            };
            return definitions.SelectMany(d => CreateFixedFrontBridgeGroup(d,
                d == definitions[2] ? right : left, axis)).ToArray();
        }

        private static FaceToFaceSidePlanningContext CreateFixedInterfaceContext(
            IReadOnlyList<IReadOnlyList<Point3d>> interfaces, double width, double thickness)
        {
            if (interfaces == null || interfaces.Count != PhaseCount || interfaces.Any(p => p == null || p.Count == 0))
                throw new ArgumentException("固定接口必须包含完整三相及成员坐标。");
            var result = new FaceToFaceSidePlanningContext();
            for (var phase = 0; phase < PhaseCount; phase++)
            {
                var points = interfaces[phase];
                if (points.Any(p => p == null || double.IsNaN(p.X) || double.IsInfinity(p.X) ||
                    double.IsNaN(p.Y) || double.IsInfinity(p.Y) || double.IsNaN(p.Z) || double.IsInfinity(p.Z)))
                    throw new ArgumentException("固定接口坐标无效。");
                var center = BusbarGeometryUtility.Average(points);
                if (points.Any(p => Math.Abs(p.X - center.X) > ModelAlignmentTolerance || Math.Abs(p.Z - center.Z) > ModelAlignmentTolerance))
                    throw new InvalidOperationException("固定接口同相成员必须位于同一端面及横向位置。");
                result.PhaseRouteContexts.Add(new FaceToWallPhaseRouteContext
                {
                    PhaseIndex = phase, OrdinaryRouteReferencePoint = center,
                    RouteWidthDirection = new Vector3d(0, 0, 1), RouteThicknessDirection = new Vector3d(0, 1, 0),
                    FrameOutwardNormal = new Vector3d(0, 1, 0), BusbarWidth = width, BusbarThickness = thickness,
                    BendingRadius = Math.Max(10d, thickness), BendAllowance = 20d,
                    StackMemberOffsets = points.Select(p => p.Y - center.Y).OrderBy(v => v).ToArray()
                });
            }
            return result;
        }

        private static FaceToFaceSidePlanningContext PositionFixedContext(
            FaceToFaceSidePlanningContext source, double referenceWidth, double x, double z)
        {
            var result = new FaceToFaceSidePlanningContext();
            foreach (var context in source.PhaseRouteContexts)
            {
                var copy = CreateStackEnvelopeRouteContext(context, context.BusbarThickness, context.StackMemberOffsets);
                copy.OrdinaryRouteReferencePoint = new Point3d(x, context.OrdinaryRouteReferencePoint.Y,
                    z + context.OrdinaryRouteReferencePoint.Z - referenceWidth);
                result.PhaseRouteContexts.Add(copy);
            }
            return result;
        }

        private static FaceToFaceSidePlanningContext ReorderFixedContext(FaceToFaceSidePlanningContext source, int[] order)
        {
            var result = new FaceToFaceSidePlanningContext();
            result.PhaseRouteContexts.AddRange(order.Select(i => source.PhaseRouteContexts[i]));
            return result;
        }

        private static IEnumerable<PlanarBendBusbarSetting> CreateFixedOrdinarySegment(
            FaceToFaceSidePlanningContext left, FaceToFaceSidePlanningContext right,
            BusbarBridgeAxisSetting axis)
        {
            for (var phase = 0; phase < PhaseCount; phase++)
            {
                var context = left.PhaseRouteContexts[phase];
                var start = context.OrdinaryRouteReferencePoint.Copy();
                var end = right.PhaseRouteContexts[phase].OrdinaryRouteReferencePoint.Copy();
                start.X -= context.BusbarWidth / 2d; end.X += context.BusbarWidth / 2d;
                if (Math.Abs(start.Z - end.Z) > ModelAlignmentTolerance)
                    throw new InvalidOperationException("普通段的相位接口发生横移，但未规划换相或偏移段。");
                var definition = new FaceToFaceRouteGroupDefinition { DisplayName = "固定接口普通段" };
                var phaseSegment = new FaceToFacePhaseRouteSegment
                {
                    PhaseIndex = phase, StartPoint = start, EndPoint = end, RouteDirection = axis.LengthVector,
                    ThicknessDirection = axis.HeightVector, WidthDirection = axis.WidthVector
                };
                definition.PhaseSegments.Add(phaseSegment);
                foreach (var member in CreateFixedFrontBridgeGroup(definition, left, axis))
                    yield return member;
            }
        }

        private static IEnumerable<PlanarBendBusbarSetting> CreateFixedFrontBridgeGroup(
            FaceToFaceRouteGroupDefinition definition, FaceToFaceSidePlanningContext context,
            BusbarBridgeAxisSetting axis)
        {
            definition.IsBridge = true;
            foreach (var segment in definition.PhaseSegments)
            {
                var phase = context.PhaseRouteContexts[segment.PhaseIndex];
                segment.BridgeStartOffsetDistance = phase.BusbarThickness;
                segment.BridgeEndOffsetDistance = 0d;
                var minimumLength = phase.BusbarWidth + FaceToWallModelBusbarPlanner.BridgeLowestEdgeStraightAdditionalLength +
                    FaceToWallModelBusbarPlanner.BridgeInnermostTransitionProjectionLength + phase.BendingRadius;
                if (new Vector3d(segment.StartPoint, segment.EndPoint).Dot(segment.RouteDirection.Normalized) <= minimumLength)
                    throw new InvalidOperationException("中间排长度不足以放置前桥头和出口直段：" + definition.DisplayName);
                var bridgePoints = FaceToWallModelBusbarPlanner.CreateBridgeReferenceCenterlinePoints(
                    segment.StartPoint, segment.EndPoint, segment.RouteDirection, segment.ThicknessDirection,
                    phase.BusbarWidth, phase.BusbarThickness, phase.BusbarThickness, 0d);
                var reference = new PlanarBendBusbarSetting
                {
                    IsCenter = true, StartBusbarVector = segment.ThicknessDirection.Copy(),
                    WidthVector = segment.WidthDirection.Copy(), BusBarWidth = phase.BusbarWidth,
                    BusBarThickness = phase.BusbarThickness, BarBendingRadius = phase.BendingRadius,
                    BendAllowance = phase.BendAllowance, StartLapWidth = phase.BusbarWidth, EndLapWidth = phase.BusbarWidth,
                    PointList = bridgePoints.Take(3).Concat(new[] { bridgePoints.Last() }).Select(p => p.Copy()).ToList()
                };
                // 从最内侧排作平行偏移；单桥头只保留四点，不沿用双桥头的两倍长度下限。
                // 不能平移整根铜排，否则经过多个搭接点后会累计抬高。
                var innerOffset = segment.ThicknessDirection.Dot(phase.FrameOutwardNormal) > 0d
                    ? phase.StackMemberOffsets.Min() : phase.StackMemberOffsets.Max();
                reference.PointList = reference.PointList.Select(p => PlanarBendBusbarSetting.Translate(
                    p, segment.ThicknessDirection.Normalized, innerOffset)).ToList();
                for (var i = 0; i < phase.StackMemberOffsets.Count; i++)
                {
                    var member = reference.Copy();
                    member.GroupKey = FaceToWallModelBusbarPlanner.GetPhaseName(segment.PhaseIndex) + "-" + (i + 1);
                    member.PointList = GetOffsetCenterlinePreservingPointCount(reference, phase.StackMemberOffsets[i] - innerOffset);
                    if (Enumerable.Range(1, member.PointList.Count - 1).Any(p =>
                        new Vector3d(member.PointList[p - 1], member.PointList[p]).Dot(segment.RouteDirection.Normalized) <= GeometryTolerance))
                        throw new InvalidOperationException("前桥头叠排平行偏移后发生反向或重合：" + member.GroupKey);
                    member.PreviewBendPointKinds = member.PointList
                        .Select(p => BusbarPreviewBendPointKind_e.Planar).ToList();
                    yield return member;
                }
            }
        }

        private static IReadOnlyList<PlanarBendBusbarSetting> CreateFixedSpecialFrontBridges(
            IReadOnlyList<PlanarBendBusbarSetting> source, Vector3d heightVector = null,
            bool firstPiecePerGroupOnly = false)
        {
            var bridgeHeightDirection = (heightVector ?? new Vector3d(0, 1, 0)).Normalized;
            var result = new List<PlanarBendBusbarSetting>();
            var bridgedGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var original in source)
            {
                var piece = original.Copy();
                // 换相子排按 B→A 排列，同一相同一叠排根号只有首段加桥头；
                // 横向搭接短排及出口子排保留直头和原有层位。
                if (firstPiecePerGroupOnly && !bridgedGroups.Add(piece.GroupKey))
                {
                    result.Add(piece);
                    continue;
                }
                // 铣排三块预览实体属于同一根加工排，只有组合体入口加桥头，
                // 不能在铣排内部拼接处再加桥头或拆断原铣排特征。
                // 模板和正式生成消费者均按B→A输出/读取三块，沿用此顺序，不能按全局X重排。
                if (piece.IsMilledBar && !ReferenceEquals(original,
                    source.First(p => p.IsMilledBar && p.GroupKey == piece.GroupKey)))
                {
                    result.Add(piece);
                    continue;
                }
                var points = piece.PointList;
                var direction = new Vector3d(points[0], points[1]).Normalized;
                if (Math.Abs(direction.Dot(bridgeHeightDirection)) > GeometryTolerance)
                    throw new InvalidOperationException("特殊排入口没有水平直段，不能安置前桥头：" + piece.GroupKey);
                var reserve = piece.BusBarWidth + FaceToWallModelBusbarPlanner.BridgeLowestEdgeStraightAdditionalLength +
                    FaceToWallModelBusbarPlanner.BridgeInnermostTransitionProjectionLength;
                if (points[0].DistanceTo(points[1]) <= reserve + Math.Max(piece.BarBendingRadius, piece.VerticalBendingRadius))
                    throw new InvalidOperationException("特殊排入口直段不足以放置前桥头并避让原折弯，请扩大工艺区：" + piece.GroupKey);
                var bridge = FaceToWallModelBusbarPlanner.CreateBridgeReferenceCenterlinePoints(
                    points[0], points[1], direction, bridgeHeightDirection,
                    piece.BusBarWidth, piece.BusBarThickness, piece.BusBarThickness, 0d);
                var oldKinds = piece.PreviewBendPointKinds != null && piece.PreviewBendPointKinds.Count == points.Count
                    ? piece.PreviewBendPointKinds.ToList()
                    : points.Select(p => piece.IsVerticalBend ? BusbarPreviewBendPointKind_e.Vertical : BusbarPreviewBendPointKind_e.Planar).ToList();
                piece.PointList = bridge.Take(3).Concat(points.Skip(1)).Select(p => p.Copy()).ToList();
                piece.PreviewBendPointKinds = Enumerable.Repeat(BusbarPreviewBendPointKind_e.Planar, 3)
                    .Concat(oldKinds.Skip(1)).ToList();
                result.Add(piece);
            }
            foreach (var group in result.Where(p => p.IsMilledBar).GroupBy(p => p.GroupKey))
            {
                var ordered = group.ToArray();
                if (ordered.Length != 3)
                    throw new InvalidOperationException("铣排组合体必须按B→A包含三块实体：" + group.Key);
                var startLength = ordered.First().GetDevelopedLength();
                var endLength = ordered.Last().GetDevelopedLength();
                foreach (var piece in ordered)
                {
                    piece.MilledBarStartLength = startLength;
                    piece.MilledBarEndLength = endLength;
                }
                if (ordered.Sum(p => p.GetDevelopedLength()) > 3000d + GeometryTolerance)
                    throw new InvalidOperationException("铣排组合体增加前桥头后展开长度超过3000 mm，不能在铣排内部拆分：" + group.Key);
            }
            return result.AsReadOnly();
        }
    }
}
