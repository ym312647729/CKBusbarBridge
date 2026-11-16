using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;

namespace CKBusbarBridgePreview
{
    public sealed class SideToSideCopperPlan
    {
        public MiddleBusbarPathPlan Path { get; }
        public IReadOnlyList<SideToSideCopperPiece> Pieces { get; }
        public SideToSideCopperClearanceProblem ClearanceProblem { get; internal set; }
        internal SideToSideCopperPlan(MiddleBusbarPathPlan path, IEnumerable<SideToSideCopperPiece> pieces)
        {
            Path = path;
            Pieces = Array.AsReadOnly(pieces.ToArray());
        }
    }

    /// <summary>同一次净距计算的失败对象和实际扫描实体，仅供诊断预览，不能用于绕过生成校验。</summary>
    public sealed class SideToSideCopperClearanceProblem
    {
        public string Message { get; internal set; }
        public string FirstLabel { get; internal set; }
        public string SecondLabel { get; internal set; }
        public int FirstPieceIndex { get; internal set; }
        public int SecondPieceIndex { get; internal set; }
        public double ActualDistance { get; internal set; }
        public double RequiredDistance { get; internal set; }
        public double[][] FirstCorners { get; internal set; }
        public double[][] SecondCorners { get; internal set; }
    }

    /// <summary>仅由柜端驱动尺寸和实际接口规划铜排；输入中不存在中间基架。</summary>
    public static class SideToSideIndependentCopperPlanner
    {
        private const double LengthToleranceMm = 1.0;
        private const double MaximumDevelopedLengthMm = 3000d;

        public static SideToSideCopperPlan Create(SideToSideCopperPlanningInput input, MiddleBusbarPathPlan editedPath = null)
        {
            ValidateInput(input);
            return input.FixedPhaseChange ? CreateFirstType(input, editedPath) : CreateSecondType(input, editedPath);
        }

        public static SideToSideCopperPlan CreateDiagnosticPreview(SideToSideCopperPlanningInput input)
        {
            ValidateInput(input);
            return input.FixedPhaseChange ? CreateFirstType(input, null, true) : CreateSecondType(input, null);
        }

        private static void ValidateInput(SideToSideCopperPlanningInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.A == null || input.B == null || input.RootCount < 2 || input.RootCount > 4 ||
                new[] { input.Width, input.Thickness, input.PhaseSpacing, input.Clearance }
                    .Any(v => double.IsNaN(v) || double.IsInfinity(v) || v <= 0d) ||
                double.IsNaN(input.CabinetEccentricity) || double.IsInfinity(input.CabinetEccentricity) ||
                input.Width <= input.Thickness)
                throw new ArgumentException("侧对侧铜排输入必须具有两柜、有效规格、2～4根叠排及正值净距。");
            foreach (var cabinet in new[] { input.A, input.B })
            {
                if (cabinet.Opening == null || new[] { cabinet.Opening.X, cabinet.Opening.Y, cabinet.Opening.Z,
                        cabinet.OpeningHeight, cabinet.BaseWidth }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                    cabinet.OpeningHeight <= 0d || cabinet.BaseWidth <= 0d || cabinet.Extensions == null ||
                    cabinet.Extensions.Any(e => e == null || e.Shape == null || e.TerminalStart == null || e.TerminalEnd == null) ||
                    new[] { BusbarPhase_e.A, BusbarPhase_e.B, BusbarPhase_e.C }.Any(p =>
                        cabinet.Extensions.Count(e => e.Phase == p) != input.RootCount - 1) ||
                    cabinet.OrdinaryConnections == null || cabinet.ElevatedConnections == null ||
                    cabinet.OrdinaryConnections.Concat(cabinet.ElevatedConnections).Any(c => c == null || c.Center == null ||
                        c.SolidCenter == null || c.Normal == null || c.Normal.Norm <= .001d || c.Radius <= 0d))
                    throw new ArgumentException("侧对侧柜端孔口、每相E=N−1根实际伸出排或连接几何不完整。");
            }
        }

        private static SideToSideCopperPlan CreateFirstType(SideToSideCopperPlanningInput input, MiddleBusbarPathPlan editedPath, bool diagnosticPreview = false)
        {
            var ends = FaceToFaceModelBusbarPlanner.CreateIndependentFixedPhaseEnds(input);
            var bGroups = "ABC".Select(p => ends.Where(e => e.Side == "B" && e.Phase == p.ToString())
                .OrderBy(e => e.Shape.PointList.Last().Z).ToArray()).ToArray();
            var aGroups = "ABC".Select(p => ends.Where(e => e.Side == "A" && e.Phase == p.ToString())
                .OrderBy(e => e.Shape.PointList.Last().Z).ToArray()).ToArray();
            var start = bGroups.SelectMany(g => g).Max(e => e.Shape.PointList.Last().X);
            var end = aGroups.SelectMany(g => g).Min(e => e.Shape.PointList.Last().X);
            var path = editedPath ?? MiddleBusbarPathPlanner.Create(new MiddleBusbarPathPlanningInput
            {
                StartX = start, EndX = end, StartY = input.B.Opening.Z, EndY = input.A.Opening.Z,
                OverlapLength = input.Width, DeferOffsetLengthLimitToPhysicalGeometry = true
            });
            if (Math.Abs(path.Points.First().X - start) > 1.0 || Math.Abs(path.Points.Last().X - end) > 1.0 ||
                path.Points.Any(p => Math.Abs(p.Y - input.B.Opening.Z) > 1.0))
                throw new InvalidOperationException("基座1柜端接口固定，编辑只能移动中间断点。");
            Func<MiddleBusbarPathPlan, IReadOnlyList<PlanarBendBusbarSetting>> createMiddle =
                candidate => CreateFirstTypeMiddle(input, candidate, bGroups, aGroups);
            var middle = CompleteBreakpoints(path, input.Width, createMiddle(path), createMiddle, editedPath == null, out path);
            var pieces = ends.Concat(CreateMiddlePieces(middle)).ToArray();
            SideToSideLapAdapter.ValidateSeparateMembers(middle);
            var clearance = PlanarBusbarClearanceValidator.Evaluate(pieces.Select(p => p.Shape).ToArray(), s => s.GroupKey.Substring(0, 1), input.Clearance);
            var clearanceMessage = clearance.IsClear ? null : FormattableString.Invariant(
                $"固定换相跨相净距不足：{clearance.FirstGroupKey}/{clearance.SecondGroupKey}，实测最小净距{clearance.MinimumDistance:0.0} mm，小于要求{input.Clearance:0.0} mm。");
            if (!clearance.IsClear && !diagnosticPreview)
                throw new InvalidOperationException(clearanceMessage);
            var floor = Math.Max(input.A.Opening.Y - input.A.OpeningHeight / 2d, input.B.Opening.Y - input.B.OpeningHeight / 2d);
            var top = Math.Min(input.A.Opening.Y + input.A.OpeningHeight / 2d, input.B.Opening.Y + input.B.OpeningHeight / 2d);
            var halfWidth = Math.Min(input.A.BaseWidth, input.B.BaseWidth) / 2d;
            if (middle.SelectMany(FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids).SelectMany(s => s)
                .Any(p => p[1] < floor - LengthToleranceMm || p[1] > top + LengthToleranceMm ||
                    Math.Abs(p[2] - input.B.Opening.Z) > halfWidth + LengthToleranceMm))
                throw new InvalidOperationException("固定换相铜排超出基座孔口可用范围。");
            return new SideToSideCopperPlan(path, pieces) { ClearanceProblem = clearance.IsClear ? null : new SideToSideCopperClearanceProblem {
                Message = clearanceMessage, FirstLabel = clearance.FirstGroupKey, SecondLabel = clearance.SecondGroupKey,
                FirstPieceIndex = clearance.FirstPieceIndex, SecondPieceIndex = clearance.SecondPieceIndex,
                ActualDistance = clearance.MinimumDistance, RequiredDistance = input.Clearance,
                FirstCorners = clearance.FirstCorners, SecondCorners = clearance.SecondCorners
            } };
        }

        private static IReadOnlyList<PlanarBendBusbarSetting> CreateFirstTypeMiddle(
            SideToSideCopperPlanningInput input, MiddleBusbarPathPlan path,
            SideToSideCopperPiece[][] bGroups, SideToSideCopperPiece[][] aGroups)
        {
            var middle = new List<PlanarBendBusbarSetting>();
            for (var phase = 0; phase < 3; phase++)
            {
                if (bGroups[phase].Length != input.RootCount || aGroups[phase].Length != input.RootCount)
                    throw new InvalidOperationException("两柜固定换相连接段叠排数量不一致。");
                var bs = bGroups[phase][0].Shape; var ass = aGroups[phase][0].Shape;
                var bp = BusbarGeometryUtility.Average(bGroups[phase].Select(e => e.Shape.PointList.Last()));
                var ap = BusbarGeometryUtility.Average(aGroups[phase].Select(e => e.Shape.PointList.Last()));
                var offsets = Enumerable.Range(0, input.RootCount)
                    .Select(i => (2d * i - input.RootCount + 1d) * input.Thickness).ToArray();
                var by = bp.Y - bs.BusBarWidth / 2d; var ay = ap.Y - ass.BusBarWidth / 2d;
                if (Math.Abs(by - ay) > .5d) throw new InvalidOperationException("固定换相两柜实际搭接高度不一致。");
                // 柜端排保持直头；中间排每段入口桥头抬高一个排厚，
                // 只有最后一段的A端也抬高一个排厚，不使用基架或候选绝缘子定位。
                var bContact = bp.Z + (bs.BusBarThickness + input.Thickness) / 2d;
                var layer = bContact - input.Thickness;
                foreach (var segment in path.Segments)
                {
                    var first = segment.Index == 0; var last = segment.Index == path.Segments.Count - 1;
                    var p1 = new Point3d(first ? bp.X - bs.BusBarWidth / 2d : segment.Start.X - input.Width / 2d, by, layer);
                    var p2 = new Point3d(last ? ap.X + ass.BusBarWidth / 2d : segment.End.X + input.Width / 2d, by, layer);
                    var aOffset = last ? input.Thickness : 0d;
                    var shape = new PlanarBendBusbarSetting
                    {
                        IsCenter = true, BusBarWidth = input.Width, BusBarThickness = input.Thickness,
                        StartBusbarVector = new Vector3d(0, 0, 1), WidthVector = new Vector3d(0, 1, 0),
                        BarBendingRadius = Math.Max(10d, input.Thickness), BendAllowance = 20d,
                        GroupKey = "ABC"[phase].ToString(), PlanningSegmentIndex = segment.Index,
                        StartLapWidth = input.Width, EndLapWidth = input.Width,
                        PointList = FaceToWallModelBusbarPlanner.CreateBridgeReferenceCenterlinePoints(
                            p1, p2, new Vector3d(1, 0, 0), new Vector3d(0, 0, 1), input.Width, input.Thickness,
                            input.Thickness, aOffset).ToList()
                    };
                    if (p2.X - p1.X <= 2d * (input.Width + 40d + 50d) + Math.Max(10d, input.Thickness))
                        throw new InvalidOperationException("固定换相中间段不足以容纳桥头及折弯余量。");
                    shape.RemoveInvalidPoints();
                    var members = FaceToWallModelBusbarPlanner.CreateBridgeStackMemberSettings(
                        shape, offsets, new Vector3d(0, 0, 1), phase);
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (first) SideToSideLapAdapter.SetTerminal(members[i], true,
                            bGroups[phase][i].Shape.PointList.Last().Z +
                            (bGroups[phase][i].Shape.BusBarThickness + input.Thickness) / 2d, true, true);
                        if (last) SideToSideLapAdapter.SetTerminal(members[i], false,
                            aGroups[phase][i].Shape.PointList.Last().Z +
                            (aGroups[phase][i].Shape.BusBarThickness + input.Thickness) / 2d, true, true);
                        SideToSideLapAdapter.RemoveCollinearPlanarPoints(members[i]);
                    }
                    middle.AddRange(members);
                }
            }
            return middle;
        }

        public static SideToSideCopperPlan CreateSecondType(SideToSideCopperPlanningInput input,
            MiddleBusbarPathPlan editedPath = null)
        {
            ValidateInput(input);
            if (input.FixedPhaseChange) throw new ArgumentException("此入口只接受基座2。");
            var ends = SideToSideSecondTypeFirstSegmentPlanner.CreateIndependentCabinetConnections(input);
            var b = Interfaces(ends, "B", input.Width, input.Thickness);
            var a = Interfaces(ends, "A", input.Width, input.Thickness);
            var startX = b.SelectMany(p => p).Max(p => p.X);
            var endX = a.SelectMany(p => p).Min(p => p.X);
            var startZ = b.SelectMany(p => p).Average(p => p.Z);
            var endZ = a.SelectMany(p => p).Average(p => p.Z);
            // 侧对侧的相序转换由柜端工艺完成，中间接口只能保留三相一致的柜间偏移。
            if (Enumerable.Range(0, 3).Any(p => Math.Abs(
                b[p].Average(q => q.Z) - startZ - a[p].Average(q => q.Z) + endZ) > 1.0))
                throw new InvalidOperationException("侧对侧换相应由柜端工艺完成；两端中间排接口相序不一致，请检查柜端排。");
            var nominalZeroOffset = Math.Abs(input.CabinetEccentricity) <= 1.0;
            if (nominalZeroOffset && Math.Abs(endZ - startZ) > 1.0)
                throw new InvalidOperationException("柜体驱动为零偏移，但两端实际搭接中心横向不对齐；请检查模型接口。");
            var initial = FaceToFaceUnifiedPlanner.Create(new FaceToFaceUnifiedPlanningInput
            {
                StartX = startX, EndX = endX, StartY = startZ, EndY = endZ,
                ACabinetBaseWidth = input.A.BaseWidth, BCabinetBaseWidth = input.B.BaseWidth,
                PhaseSpacing = input.PhaseSpacing, BusbarWidth = input.Width, BusbarThickness = input.Thickness,
                RequiresPhaseChange = false, DeferOffsetLengthLimitToPhysicalGeometry = true,
                PhaseChangeInsulationClearance = input.Clearance
            });
            var useLegacyZeroOffset = nominalZeroOffset && initial.MiddlePath.Segments.Count == 1;
            var path = editedPath ?? initial.MiddlePath;
            if (path.Segments.Any(s => (s.Role & MiddleBusbarSegmentRole.PhaseChange) != 0))
                throw new InvalidOperationException("侧对侧换相由柜端工艺完成，中间排不允许增加换相工艺段。");
            if (Math.Abs(path.Points.First().X - startX) > 1.0 || Math.Abs(path.Points.Last().X - endX) > 1.0 ||
                Math.Abs(path.Points.First().Y - startZ) > 1.0 || Math.Abs(path.Points.Last().Y - endZ) > 1.0)
                throw new InvalidOperationException("七点首段的柜端接口固定，编辑只能移动中间断点。");
            var floor = Math.Max(input.A.Opening.Y - input.A.OpeningHeight / 2d,
                input.B.Opening.Y - input.B.OpeningHeight / 2d);
            var top = Math.Min(input.A.Opening.Y + input.A.OpeningHeight / 2d,
                input.B.Opening.Y + input.B.OpeningHeight / 2d);
            var fixedConnections = ends.Select(e => e.Shape).ToArray();
            if (useLegacyZeroOffset)
                ValidateFixedCabinetLapRegion(ends, b, a, input.Width);
            Func<MiddleBusbarPathPlan, IReadOnlyList<PlanarBendBusbarSetting>> createMiddle = candidate => useLegacyZeroOffset
                ? CreateLegacyZeroOffsetMiddle(candidate, b, a, input.Width, input.Thickness,
                    floor + 41.5d, top, Math.Min(input.A.BaseWidth, input.B.BaseWidth) / 2d - 40d,
                    input.Clearance)
                : FaceToFaceModelBusbarPlanner.CreateSideToSideFixedInterfaceMiddleGeometry(candidate, b, a,
                    input.Width, input.Thickness, input.B.CalculateConnectionHeight() + input.Thickness / 2d,
                    false, floor + 41.5d, top,
                    Math.Min(input.A.BaseWidth, input.B.BaseWidth) / 2d - 40d,
                    input.Clearance, null, splitOrdinaryOverlength: false);
            var middle = editedPath == null
                ? FaceToFaceUnifiedPlanner.CreateWithAutomaticOffsetExpansion(path, input.Width, createMiddle, out path,
                    initial.RawSpecialFrameMinimumLength)
                : createMiddle(path);
            middle = CompleteBreakpoints(path, input.Width, middle, createMiddle, editedPath == null, out path);
            // 柜端连接排与搭接排不套用中间工艺区的相间绝缘净距。
            // 全组只阻断实际相穿；中间排自身仍在上方按输入净距校验。
            ValidateCabinetLapPhysicalNonIntersection(fixedConnections, middle);
            return new SideToSideCopperPlan(path, ends.Concat(CreateMiddlePieces(middle)));
        }

        private static IReadOnlyList<PlanarBendBusbarSetting> CompleteBreakpoints(MiddleBusbarPathPlan initial,
            double width, IReadOnlyList<PlanarBendBusbarSetting> pieces,
            Func<MiddleBusbarPathPlan, IReadOnlyList<PlanarBendBusbarSetting>> createMiddle,
            bool automatic, out MiddleBusbarPathPlan path)
        {
            path = initial;
            while (true)
            {
                var overlength = pieces.FirstOrDefault(IsOverlength);
                if (overlength == null) return pieces;

                var section = path.Segments[overlength.PlanningSegmentIndex];
                var detail = FormattableString.Invariant(
                    $"侧对侧第{section.Index + 1}段 {overlength.GroupKey} 展开长度为{overlength.GetDevelopedLength():0.0} mm，上限为{MaximumDevelopedLengthMm:0.0} mm。");
                if (!automatic || section.Role != MiddleBusbarSegmentRole.Ordinary)
                    throw new InvalidOperationException(detail + "请在三相均可分段的普通直段调整断点。");

                var members = pieces.Where(p => p.PlanningSegmentIndex == section.Index).ToArray();
                InvalidOperationException failure = null;
                MiddleBusbarPathPlan accepted = null;
                foreach (var x in CommonCutCandidates(section, members))
                {
                    var coordinates = path.Points.Select(p => p.X).ToList();
                    var indices = Enumerable.Range(0, coordinates.Count).ToList();
                    coordinates.Insert(section.Index + 1, x);
                    indices.Insert(section.Index + 1, -1);
                    try
                    {
                        var candidate = MiddleBusbarPathPlanner.RepartitionBreakpoints(path, coordinates, indices, width,
                            false, deferOrdinaryLengthLimitToPhysicalGeometry: true);
                        var proposed = createMiddle(candidate);
                        if (proposed.Any(p => p.PlanningSegmentIndex == section.Index && IsOverlength(p))) continue;
                        accepted = candidate;
                        pieces = proposed;
                        break;
                    }
                    catch (InvalidOperationException ex)
                    {
                        failure = ex;
                    }
                }
                if (accepted == null)
                    throw new InvalidOperationException(detail + "找不到三相所有叠排共用的可加工断点。", failure);
                path = accepted;
            }
        }

        private static bool IsOverlength(PlanarBendBusbarSetting piece) =>
            piece.GetDevelopedLength() > MaximumDevelopedLengthMm + LengthToleranceMm;

        private static IEnumerable<double> CommonCutCandidates(MiddleBusbarPathSegment section,
            IReadOnlyList<PlanarBendBusbarSetting> members)
        {
            var common = new List<(double Min, double Max)>
                { (section.Start.X, section.End.X) };
            foreach (var piece in members)
            {
                common = (from a in common
                          from b in CutIntervals(piece)
                          let min = Math.Max(a.Min, b.Min)
                          let max = Math.Min(a.Max, b.Max)
                          where min <= max
                          select (min, max)).ToList();
                if (common.Count == 0) return Array.Empty<double>();
            }
            var reference = members.Where(p => p.GroupKey.StartsWith("B", StringComparison.Ordinal)).ToArray();
            if (reference.Length == 0) throw new InvalidOperationException("侧对侧段排缺少B相加工几何。");
            var preferred = (reference.Min(p => p.PointList.First().X) + reference.Max(p => p.PointList.Last().X)) / 2d;
            return common.SelectMany(r => new[] { Math.Max(r.Min, Math.Min(r.Max, preferred)), r.Min, r.Max })
                .Distinct().OrderBy(x => Math.Abs(x - preferred));
        }

        private static IEnumerable<(double Min, double Max)> CutIntervals(PlanarBendBusbarSetting piece)
        {
            var prefix = 0d;
            var reserve = piece.BusBarWidth + 40d + 50d + Math.Max(piece.BarBendingRadius, piece.VerticalBendingRadius);
            for (var i = 0; i < piece.PointList.Count - 1; i++)
            {
                var start = piece.PointList[i];
                var end = piece.PointList[i + 1];
                var dx = end.X - start.X;
                var travel = start.DistanceTo(end);
                if (!piece.IsMilledBar && dx > 2d * reserve &&
                    start.DistanceTo(new Point3d(start.X, end.Y, end.Z)) <= LengthToleranceMm)
                {
                    var available = MaximumDevelopedLengthMm + LengthToleranceMm - prefix -
                        i * Math.Max(0d, piece.BendAllowance) - piece.BusBarWidth / 2d;
                    var max = start.X + Math.Min(dx - reserve, available * dx / travel);
                    if (max >= start.X + reserve) yield return (start.X + reserve, max);
                }
                prefix += travel;
            }
        }

        internal static IReadOnlyList<SideToSideCopperPiece> CreateMiddlePieces(
            IReadOnlyList<PlanarBendBusbarSetting> middle)
        {
            if (middle == null) throw new ArgumentNullException(nameof(middle));
            // 铣排三块共用一根正式加工排：入口桥头只出现在首块，
            // 但生成组的桥排身份必须由整根铣排决定，不能逐块按点数拆组。
            var milledBridgeGroups = middle.Where(s => s.IsMilledBar)
                .GroupBy(s => Tuple.Create(s.GroupKey, s.PlanningSegmentIndex))
                .ToDictionary(group => group.Key,
                    group => group.Any(s => s.PointList.Count > 2));
            return middle.Select(s =>
                new SideToSideCopperPiece("Middle", s.GroupKey.Substring(0, 1), s,
                    s.IsMilledBar
                        ? milledBridgeGroups[Tuple.Create(s.GroupKey, s.PlanningSegmentIndex)]
                        : s.PointList.Count > 2)).ToArray();
        }

        private static IReadOnlyList<PlanarBendBusbarSetting> CreateLegacyZeroOffsetMiddle(
            MiddleBusbarPathPlan path, IReadOnlyList<IReadOnlyList<Point3d>> b,
            IReadOnlyList<IReadOnlyList<Point3d>> a, double width, double thickness,
            double floor, double top, double halfWidth, double clearance)
        {
            if (path.Segments.Any(s => s.Role != MiddleBusbarSegmentRole.Ordinary) || path.Points.Any(p =>
                Math.Abs(p.Y - path.Points.First().Y) > LengthToleranceMm))
                throw new InvalidOperationException("零偏移直连型式只允许在桥向普通直段分节。");
            var bars = new List<PlanarBendBusbarSetting>();
            for (var phase = 0; phase < 3; phase++)
            {
                if (b[phase].Count != a[phase].Count)
                    throw new InvalidOperationException("两柜同相搭接成员数量不一致。");
                for (var member = 0; member < b[phase].Count; member++)
                {
                    var first = b[phase][member]; var last = a[phase][member];
                    if (first.DistanceTo(new Point3d(first.X, last.Y, last.Z)) > LengthToleranceMm)
                        throw new InvalidOperationException("两柜同相搭接中心不符合原零偏移直连型式：" +
                            "ABC"[phase] + "-" + (member + 1));
                    var start = new Point3d(first.X - width / 2d, first.Y, first.Z);
                    var end = new Point3d(last.X + width / 2d, last.Y, last.Z);
                    var travel = new Vector3d(start, end);
                    if (travel.X <= width)
                        throw new InvalidOperationException("同相中间直排缺少有效桥向长度。");
                    var key = "ABC"[phase] + "-" + (member + 1);
                    var thicknessDirection = new Vector3d(0d, 1d, 0d);
                    var widthDirection = new Vector3d(-travel.Z, 0d, travel.X).Normalized;
                    var radius = Math.Max(10d, thickness);
                    foreach (var section in path.Segments)
                    {
                        var firstSection = section.Index == 0;
                        var entry = firstSection ? start : AtX(section.Start.X - width / 2d);
                        var exit = section.Index == path.Segments.Count - 1 ? end : AtX(section.End.X + width / 2d);
                        var minimum = firstSection ? width : width + 40d + 50d + radius;
                        if (exit.X - entry.X <= minimum)
                            throw new InvalidOperationException("零偏移中间段不足以容纳搭接及折弯余量：" + key);
                        var points = firstSection ? new List<Point3d> { entry, exit } :
                            FaceToWallModelBusbarPlanner.CreateBridgeReferenceCenterlinePoints(
                                entry, exit, travel, thicknessDirection, width, thickness, thickness, 0d)
                                .Take(3).Concat(new[] { exit }).ToList();
                        bars.Add(new PlanarBendBusbarSetting
                        {
                            IsCenter = true, BusBarWidth = width, BusBarThickness = thickness,
                            StartBusbarVector = thicknessDirection, WidthVector = widthDirection,
                            BarBendingRadius = radius, BendAllowance = 20d,
                            GroupKey = key, PlanningSegmentIndex = section.Index,
                            StartLapWidth = width, EndLapWidth = width, PointList = points
                        });
                    }

                    Point3d AtX(double x)
                    {
                        var fraction = (x - start.X) / travel.X;
                        return new Point3d(x, start.Y + travel.Y * fraction, start.Z + travel.Z * fraction);
                    }
                }
            }
            foreach (var piece in bars)
            foreach (var corner in FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(piece).SelectMany(s => s))
                if (corner[1] < floor - LengthToleranceMm || corner[1] > top + LengthToleranceMm ||
                    Math.Abs(corner[2] - path.Points.First().Y) > halfWidth + LengthToleranceMm)
                    throw new InvalidOperationException("零偏移中间排超出基座孔口可用空间：" + piece.GroupKey);
            var evaluation = PlanarBusbarClearanceValidator.Evaluate(bars,
                s => s.GroupKey.Substring(0, 1), clearance);
            if (!evaluation.IsClear)
                throw new InvalidOperationException("中间直排跨相净距不足：" + evaluation.FirstGroupKey + "/" +
                    evaluation.SecondGroupKey + "，净距=" + evaluation.MinimumDistance.ToString("0.0",
                        System.Globalization.CultureInfo.InvariantCulture) + " mm。");
            SideToSideLapAdapter.ValidateSeparateMembers(bars);
            return bars;
        }

        internal static void ValidateCabinetLapPhysicalNonIntersection(
            IReadOnlyList<PlanarBendBusbarSetting> fixedConnections,
            IReadOnlyList<PlanarBendBusbarSetting> middle)
        {
            // 柜端排和搭接排的连接处不设绝缘距离门槛；只拒绝跨相实体相穿。
            var localClearance = PlanarBusbarClearanceValidator.Evaluate(
                fixedConnections.Concat(middle).ToArray(), s => s.GroupKey.Substring(0, 1), .01d);
            if (!localClearance.IsClear)
                throw new InvalidOperationException("柜端排与搭接排跨相实体相穿：" +
                    localClearance.FirstGroupKey + "/" + localClearance.SecondGroupKey + "。");
        }

        private static void ValidateFixedCabinetLapRegion(IReadOnlyList<SideToSideCopperPiece> ends,
            IReadOnlyList<IReadOnlyList<Point3d>> b, IReadOnlyList<IReadOnlyList<Point3d>> a, double width)
        {
            foreach (var side in new[] { "B", "A" })
            for (var phase = 0; phase < 3; phase++)
            {
                var members = ends.Where(e => e.Side == side && e.Phase == "ABC"[phase].ToString())
                    .OrderBy(e => e.Shape.GroupKey).ToArray();
                var contacts = side == "B" ? b[phase] : a[phase];
                if (members.Length != contacts.Count)
                    throw new InvalidOperationException("柜端固定搭接成员与实际接口数量不一致。");
                for (var member = 0; member < members.Length; member++)
                {
                    var points = members[member].Shape.PointList;
                    if (points == null ||
                    (points.Count != 5 && points.Count != 7 && points.Count != 9) ||
                        points.Any(p =>
                        Math.Abs(p.X - contacts[member].X) > width / 2d + .001d))
                        throw new InvalidOperationException("柜端铜排不在固定搭接区域：" +
                            members[member].Shape.GroupKey);
                }
            }
        }

        private static IReadOnlyList<IReadOnlyList<Point3d>> Interfaces(
            IReadOnlyList<SideToSideCopperPiece> ends, string side, double middleWidth, double middleThickness)
        {
            return "ABC".Select(phase => (IReadOnlyList<Point3d>)ends
                .Where(e => e.Side == side && e.Phase == phase.ToString()).OrderBy(e => e.Shape.GroupKey)
                .Select(e =>
                {
                    var s = e.Shape;
                    var direction = new Vector3d(s.PointList[s.PointList.Count - 2], s.PointList.Last());
                    if (direction.Norm <= .001d || Math.Abs(direction.Normalized.Z) < .999999d)
                        throw new InvalidOperationException("七点首段末端必须沿柜间偏心方向形成实际搭接直段。");
                    var lap = PlanarBendBusbarSetting.Translate(s.PointList.Last(), direction.Normalized, -s.BusBarWidth / 2d);
                    // 中间排端面跨过首段排宽；接口约定在中间排自身半排宽以内。
                    return new Point3d(lap.X + (side == "B" ? -1d : 1d) * (s.BusBarWidth - middleWidth) / 2d,
                        lap.Y - (s.BusBarThickness + middleThickness) / 2d, lap.Z);
                }).ToArray()).ToArray();
        }
    }
}
