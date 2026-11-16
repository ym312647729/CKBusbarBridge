using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.DesignGeometry;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeRaisedLapPlan
    {
        internal BridgeDesignSample Sample { get; set; }
        internal IReadOnlyList<BridgeDesignIssue> Issues { get; set; }
        internal bool HasChanges { get; set; }
    }

    internal sealed class BridgeFastenerEnvelope
    {
        internal BridgeUiSolid Solid;
        internal string Phase;
        internal double AnchorX;
    }

    // 本次只增加局部成形；柜端接口、基架安装和未抬高基线都由既有规划器拥有。
    internal sealed class BridgeRaisedLapPlanner
    {
        internal const string IssueSource = "搭接紧固件净距";
        private const double Clearance = BridgeRaisedLapGeometry.RequiredClearanceMm;
        private const double ClearanceToleranceMm = BridgeDesignPrecision.LengthToleranceMm;
        private const double Projection = BridgeRaisedLapGeometry.FastenerDownwardProjectionMm;
        private const double Tolerance = 1e-6d;
        private const string PointMarker = "/raised-lap/";
        private readonly BridgeRaisedLapGeometry _forming = new BridgeRaisedLapGeometry();
        private readonly BusbarBridgeDraftPreviewSceneFactory _scene = new BusbarBridgeDraftPreviewSceneFactory();

        private sealed class Part
        {
            internal BridgeDesignRoute Route;
            internal PlanarBendBusbarSetting Shape;
            internal PlanarBendBusbarSetting Baseline;
        }
        private sealed class Terminal
        {
            internal Part Part;
            internal bool Start;
            internal Point3d Center;
            internal Vector3d Inward;
            internal Vector3d Width;
            internal double Length;
            internal double Bottom;
            internal double Top;
            internal double[][] Polygon;
        }
        private sealed class Lap
        {
            internal Terminal First, Second;
            internal double[][] Polygon;
            internal double Bottom, Top;
            internal string Joint;
            internal string Phase => First.Part.Route.Phase;
        }
        private sealed class Metal
        {
            internal string Frame;
            internal string Name;
            internal int? AngleNotchSlot;
            internal double[][] Polygon;
            internal double Bottom, Top;
            internal BridgeUiSolid Solid;
        }

        internal IReadOnlyList<BridgeFastenerEnvelope> CreateWorkerFasteners(IEnumerable<PlanarBendBusbarSetting> bars)
        {
            var parts = bars.Select((bar, index) =>
            {
                var shape = BridgeNativePreviewCopy.Copy(bar);
                shape.PointList = bar.PointList.Select(p => new Point3d(p.X, p.Z, p.Y)).ToList();
                shape.WidthVector = new Vector3d(bar.WidthVector.X, bar.WidthVector.Z, bar.WidthVector.Y);
                shape.StartBusbarVector = new Vector3d(bar.StartBusbarVector.X, bar.StartBusbarVector.Z, bar.StartBusbarVector.Y);
                return new Part { Shape = shape, Route = new BridgeDesignRoute("worker-part-" + index,
                    bar.GroupKey, Array.Empty<string>()).WithPhysicalGeometry(bar.BusBarWidth, bar.BusBarThickness).WithGenerationGroup("中间排", false) };
            }).ToArray();
            var pairs = NonHorizontalFasteners(parts).ToList();
            pairs.AddRange(FindLaps(parts, Array.Empty<BridgeDesignObject>())
                .Select(lap => new KeyValuePair<Lap, BridgeUiSolid>(lap, HorizontalFastener(lap))));
            return pairs.Select(pair => new BridgeFastenerEnvelope { Solid = pair.Value, Phase = pair.Key.Phase,
                AnchorX = pair.Key.First.Center.X }).ToArray();
        }


        internal BridgeRaisedLapPlan Apply(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects,
            Func<string, bool> isLocked = null, bool allowLift = true, bool keepNotchPlan = false)
        {
            if (sample == null || objects == null) throw new ArgumentNullException(sample == null ? nameof(sample) : nameof(objects));
            var issues = new List<BridgeDesignIssue>();
            var result = new BridgeRaisedLapPlan { Sample = sample, Issues = issues.AsReadOnly() };
            if (!sample.Source.IsModelDerived || !sample.Routes.Any(r => r.IsPhysicalGeometry)) return result;
            if (sample.Coordinates == null) return Failed(result, issues, "缺少实际装配坐标系，无法核验搭接紧固件净距。", null);
            try
            {
                if (!objects.Any(o => o.Kind == DesignObjectKind.Frame)) return result;
                var parts = sample.Routes.Where(r => r.IsPhysicalGeometry).Select(r =>
                {
                    // 先验证原生参数，不能让基线恢复早于校验而解引用残缺几何。
                    var shape = ToDraft(allowLift ? r.CopyUnraisedNativePreview() : r.CopyNativePreview(), sample.Coordinates);
                    return new Part { Route = allowLift ? r.ForRaisedLapEditing(sample.Coordinates) : r, Shape = shape };
                }).ToArray();
                foreach (var part in parts) part.Baseline = BridgeNativePreviewCopy.Copy(part.Shape);
                var laps = FindLaps(parts, objects);
                var baselineSolids = StructureSolids(parts);
                if (!keepNotchPlan) objects = SelectTemplateNotches(objects, parts, laps, baselineSolids);
                var metals = ReadMetals(objects);
                if (allowLift)
                {
                    foreach (var group in laps.GroupBy(l => l.Joint).OrderBy(g => g.Min(l => l.First.Center.X)).ThenBy(g => g.Key, StringComparer.Ordinal))
                    {
                        var lift = RequiredLift(group, metals, out var maximum);
                        if (lift <= Tolerance) continue;
                        if (lift > maximum + Tolerance)
                            return Failed(result, issues, FormattableString.Invariant($"当前断点统一抬高至少需要{lift:0.###} mm，上方只允许{maximum:0.###} mm，差{lift - maximum:0.###} mm；请调整搭接位置或基架净空。"), group.Key);
                        var members = group.SelectMany(l => new[] { l.First, l.Second }).Distinct().ToArray();
                        if (!group.Select(l => l.Phase).Distinct().OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(new[] { "A", "B", "C" }) ||
                            group.GroupBy(l => l.Phase).Select(g => g.Count()).Distinct().Count() != 1 ||
                            group.Key.StartsWith("unmapped:", StringComparison.Ordinal) || members.Any(t => !CanForm(t)))
                            return Failed(result, issues, "该搭接需要抬高，但缺少完整ABC联动身份，或涉及固定接口及不支持的工艺；当前草稿保留。", group.Key);
                        foreach (var terminal in members)
                        {
                            var lap = group.First(l => l.First == terminal || l.Second == terminal);
                            if (lap.First.Inward.Dot(lap.Second.Inward) > -1d + .000001d)
                                return Failed(result, issues, "垂直转向搭接不支持套用水平直头抬高成形，请调整工艺布置。", group.Key);
                            var lapLength = group.Where(l => l.First == terminal || l.Second == terminal)
                                .Max(l => Span(l.Polygon, terminal.Inward));
                            var formed = _forming.RaiseEnd(terminal.Part.Shape, terminal.Start, new Vector3d(0, 0, 1), lift, lapLength);
                            if (!formed.Success) return Failed(result, issues, formed.FailureReason, terminal.Part.Route.Id);
                            if (formed.DevelopedLengthMm > 3000d + Tolerance)
                                return Failed(result, issues, "局部抬高后铜排展开长度超过3000 mm，请先调整断点分节。", terminal.Part.Route.Id);
                            terminal.Part.Shape = formed.Setting;
                        }
                    }
                }
                var lockProblem = FindLockProblem(sample, parts, laps, objects, isLocked);
                if (lockProblem != null) return Failed(result, issues, lockProblem, null);
                // 相间使用实际直段和圆角扫描实体；不能因为新增形状而放宽固定接口接触规则。
                var changed = parts.Where(p => !SamePath(p.Shape, p.Baseline)).ToArray();
                var solids = changed.Length == 0 ? baselineSolids : StructureSolids(parts);
                if (changed.Length > 0)
                {
                    var phaseProblem = FindChangedPhaseProblem(parts, changed, solids, sample.Parameters.InsulationClearanceMm);
                    if (phaseProblem != null) return Failed(result, issues, phaseProblem, null);
                    foreach (var phase in parts.GroupBy(p => p.Route.Phase))
                    {
                        var shapes = phase.Select(p => p.Shape).ToArray();
                        var identities = phase.ToDictionary(p => p.Shape, p => p.Route.Id);
                        var separated = BridgeCopperClearance.EvaluateMemberStraightSegments(shapes, s => identities[s]);
                        if (!separated.IsClear) return Failed(result, issues, "搭接抬高后同相叠排实体相穿：" + separated.FirstGroupKey + " / " + separated.SecondGroupKey + "。", null);
                    }
                }
                var finalLaps = FindLaps(parts, objects);
                // 成形后的最终铜排重新从完整角钢判断，预览、保存和驱动共用最终决策。
                if (changed.Length > 0 && !keepNotchPlan)
                {
                    objects = SelectTemplateNotches(objects, parts, finalLaps, solids);
                    metals = ReadMetals(objects);
                }
                result.Sample = sample.WithGeometry(objects, sample.Routes);
                CheckGeometry(parts, finalLaps, metals, solids, issues, sample.Parameters.InsulationClearanceMm);
                if (issues.Count > 0) return result;
                if (!allowLift) return result;
                PublishRaisedRoutes(sample, objects, parts, result);
                return result;
            }
            catch (ArgumentException ex) { return Failed(result, issues, "搭接净距数据无效：" + ex.Message, null); }
            catch (InvalidOperationException ex) { return Failed(result, issues, "搭接净距校验未完成：" + ex.Message, null); }
        }

        private void PublishRaisedRoutes(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects,
            Part[] parts, BridgeRaisedLapPlan result)
        {
            var routes = sample.Routes.ToDictionary(r => r.Id, StringComparer.Ordinal);
            var plannedObjects = objects.Where(o => !o.Id.Contains(PointMarker)).ToList();
            foreach (var part in parts)
            {
                var original = routes[part.Route.Id];
                if (SamePath(part.Shape, part.Baseline))
                {
                    routes[part.Route.Id] = part.Route;
                    EnsurePathPoints(plannedObjects, part.Route, part.Shape);
                    if (original.HasRaisedLapBaseline) result.HasChanges = true;
                    continue;
                }
                var ids = part.Shape.PointList.Select((p, i) => part.Route.Id + PointMarker + i.ToString(CultureInfo.InvariantCulture)).ToArray();
                for (var i = 0; i < ids.Length; i++)
                    plannedObjects.Add(new BridgeDesignObject(ids[i], part.Route.Phase + "相抬高搭接加工点", DesignObjectKind.FixedRoutePoint,
                        new Dictionary<DesignField, double> { [DesignField.X] = part.Shape.PointList[i].X,
                            [DesignField.Y] = part.Shape.PointList[i].Y, [DesignField.Z] = part.Shape.PointList[i].Z },
                        Array.Empty<DesignField>(), "抬高搭接原生加工点", phase: part.Route.Phase));
                var assembly = ToAssembly(part.Shape, sample.Coordinates);
                routes[part.Route.Id] = part.Route.WithPointIds(ids)
                    .WithPhysicalGeometry(part.Shape.BusBarWidth, part.Shape.BusBarThickness, BridgeCopperClearance.CreateFixedInterfacePreviewSolids(part.Shape))
                    .WithNativePreview(sample.Coordinates.ToPlanning(assembly), sample.Coordinates)
                    .WithRaisedLapBaseline(part.Route.CopyNativePreview(), part.Route.PointIds);
                if (!SamePath(ToDraft(original.CopyNativePreview(), sample.Coordinates), part.Shape)) result.HasChanges = true;
            }
            result.Sample = sample.WithGeometry(plannedObjects, sample.Routes.Select(r => routes[r.Id]));
        }

        private Dictionary<Part, IReadOnlyList<BridgeUiSolid>> StructureSolids(Part[] parts) =>
            parts.ToDictionary(p => p, p => _forming.CreateStructureSolids(p.Shape, new Vector3d(0, 0, 1)) ??
                BridgeCopperClearance.UiLongitudinalSolids(p.Shape));

        private IReadOnlyList<BridgeDesignObject> SelectTemplateNotches(IReadOnlyList<BridgeDesignObject> objects,
            Part[] parts, List<Lap> laps, Dictionary<Part, IReadOnlyList<BridgeUiSolid>> solids)
        {
            var ordered = BusbarBridgeDraftEditor.FramesInInstallationOrder(objects.Where(o => o.Kind == DesignObjectKind.Frame));
            ValidateNotchGroups(ordered);
            var nonHorizontal = NonHorizontalFasteners(parts);
            return objects.Select(frame =>
            {
                if (frame.CornerPreview == null && !BusbarBridgeDraftPreviewSceneFactory.HasTemplateAngleNotches(frame))
                    return frame;

                var seals = BusbarBridgeDraftPreviewSceneFactory.SmallOffsetSeals(frame);
                var depths = new double[4];
                var pending = new List<int>(4);
                for (var slot = 0; slot < depths.Length; slot++)
                {
                    var special = BusbarBridgeDraftPreviewSceneFactory.SmallOffsetAngle(seals, slot);
                    if (special == null) pending.Add(slot);
                    else depths[slot] = special.SuppressNotch ? 0d : special.NotchWidth.Value;
                }
                var candidates = frame.CornerPreview != null || IsNotchConnector(frame) || frame.FrameTemplateKey == "SideToSidePhaseChange029"
                    ? BridgeAngleNotchPlan.FourDepthsMm
                    : frame.FrameTemplateKey == "Regular" ? BridgeAngleNotchPlan.RegularDepthsMm
                    : new[] { 0d, 22d };
                var issues = new List<BridgeDesignIssue>();
                foreach (var depth in candidates)
                {
                    if (pending.Count == 0) break;
                    foreach (var slot in pending) depths[slot] = depth;
                    var candidate = frame.WithAngleNotches(new BridgeAngleNotchPlan(depths));
                    var metals = ReadMetals(objects, new[] { candidate }, anglesOnly: true).ToLookup(m => m.AngleNotchSlot);
                    pending.RemoveAll(slot =>
                    {
                        if (!metals.Contains(slot))
                            throw new InvalidOperationException("基架缺少对应位置的角钢豁口实体：" + frame.DisplayName);
                        issues.Clear();
                        CheckFrameGeometry(parts, laps, metals[slot].ToList(), solids, issues, nonHorizontal);
                        return issues.Count == 0;
                    });
                }
                // 最后一档仍冲突时保留定位几何，交给整架校验给出阻断原因。
                return frame.WithAngleNotches(new BridgeAngleNotchPlan(depths));
            }).ToArray();
        }

        private void ValidateNotchGroups(BridgeDesignObject[] ordered)
        {
            var remaining = ordered.Count(frame => frame.CornerPreview != null || IsNotchConnector(frame));
            if (!ordered.Any(IsNotchConnector) && remaining <= 1) return;
            for (var i = 0; i < ordered.Length; i++)
            {
                if (ordered[i].CornerPreview?.ClosedEnd != 1) continue;
                var end = i + 1;
                while (end < ordered.Length && IsNotchConnector(ordered[end])) end++;
                if (end == i + 1 || end == ordered.Length || ordered[end].CornerPreview?.ClosedEnd != -1 ||
                    Enumerable.Range(i, end - i).Any(j => !PortsTouch(ordered[j], ordered[j + 1])))
                    throw new InvalidOperationException("大偏移豁口计划无法沿实际接口唯一识别两个拐角及连接架。");
                remaining -= end - i + 1;
                i = end;
            }
            if (remaining != 0) throw new InvalidOperationException("存在未归入大偏移豁口组的拐角或连接架。");
        }

        private static bool IsNotchConnector(BridgeDesignObject frame) => frame.FrameTemplateKey == "Regular" && frame.Role.Contains("大偏移");

        private bool PortsTouch(BridgeDesignObject first, BridgeDesignObject second) =>
            BridgeDesignPrecision.PositionDistance(_scene.FrameInterfacePoints(first)[1], _scene.FrameInterfacePoints(second)[0]) <=
                BridgeDesignPrecision.LengthToleranceMm;

        private BridgeRaisedLapPlan Failed(BridgeRaisedLapPlan result, List<BridgeDesignIssue> issues, string message, string target)
        {
            issues.Add(new BridgeDesignIssue(message, target, IssueSource));
            return result;
        }
        private bool CanForm(Terminal terminal)
        {
            var part = terminal.Part;
            return part.Route.GenerationGroup == "中间排" && !part.Shape.IsMilledBar && !part.Shape.IsVerticalBend && part.Route.LSegmentStartIds.Count == 0;
        }

        private string FindChangedPhaseProblem(Part[] parts, Part[] changed,
            Dictionary<Part, IReadOnlyList<BridgeUiSolid>> solids, double phaseClearance)
        {
            for (var i = 0; i < parts.Length - 1; i++)
            for (var j = i + 1; j < parts.Length; j++)
            {
                var a = parts[i]; var b = parts[j];
                if (a.Route.Phase == b.Route.Phase || !changed.Contains(a) && !changed.Contains(b)) continue;
                var required = a.Route.GenerationGroup == "中间排" && b.Route.GenerationGroup == "中间排" ? phaseClearance : .01d;
                foreach (var first in solids[a])
                foreach (var second in solids[b])
                    if (NeedsExactDistance(first, second, required) && BridgeCopperClearance.UiPairDistance(first, second) + Tolerance < required)
                        return "搭接抬高后铜排实体净距不足：" + a.Route.Id + " / " + b.Route.Id + "。";
            }
            return null;
        }

        private string FindLockProblem(BridgeDesignSample sample, Part[] parts, List<Lap> laps,
            IReadOnlyList<BridgeDesignObject> objects, Func<string, bool> locked)
        {
            if (locked == null) return null;
            var current = sample.Routes.ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (var part in parts)
            {
                var original = current[part.Route.Id];
                if (SamePath(ToDraft(original.CopyNativePreview(), sample.Coordinates), part.Shape)) continue;
                if (locked(original.Id) || original.PointIds.Concat(original.RaisedLapBaselinePointIds).Any(locked))
                    return "搭接重算将改变已锁定铜排，当前草稿保留：" + original.Id + "。";
            }
            foreach (var lap in laps)
            {
                if (!objects.Any(o => o.JointGroupId == lap.Joint && locked(o.Id))) continue;
                foreach (var terminal in new[] { lap.First, lap.Second })
                {
                    var original = ToDraft(current[terminal.Part.Route.Id].CopyNativePreview(), sample.Coordinates);
                    var before = terminal.Start ? original.PointList.First() : original.PointList.Last();
                    var after = terminal.Start ? terminal.Part.Shape.PointList.First() : terminal.Part.Shape.PointList.Last();
                    if (before.DistanceTo(after) > Tolerance)
                        return "搭接重算将改变已锁定断点，当前草稿保留：" + lap.Joint + "。";
                }
            }
            return null;
        }

        private void EnsurePathPoints(List<BridgeDesignObject> objects, BridgeDesignRoute route, PlanarBendBusbarSetting shape)
        {
            for (var i = 0; i < route.PointIds.Count; i++)
            {
                if (objects.Any(o => o.Id == route.PointIds[i])) continue;
                var p = shape.PointList[i];
                objects.Add(new BridgeDesignObject(route.PointIds[i], route.Phase + "相原始加工点", DesignObjectKind.FixedRoutePoint,
                    new Dictionary<DesignField, double> { [DesignField.X] = p.X, [DesignField.Y] = p.Y, [DesignField.Z] = p.Z },
                    Array.Empty<DesignField>(), "抬高前原生加工点", phase: route.Phase));
            }
        }

        private sealed class ContactFace
        {
            internal Part Part;
            internal Point3d Center;
            internal Vector3d Along, Width, Normal;
            internal double[][] Corners;
            internal bool Horizontal;
        }
        private IReadOnlyList<KeyValuePair<Lap, BridgeUiSolid>> NonHorizontalFasteners(Part[] parts)
        {
            var result = new List<KeyValuePair<Lap, BridgeUiSolid>>();
            var faces = parts.SelectMany(p => new[] { Contact(p, true), Contact(p, false) }).ToArray();
            for (var i = 0; i < faces.Length - 1; i++)
            for (var j = i + 1; j < faces.Length; j++)
            {
                var a = faces[i]; var b = faces[j];
                if (a.Part == b.Part || a.Part.Route.Phase != b.Part.Route.Phase || a.Horizontal && b.Horizontal ||
                    Math.Abs(a.Normal.Dot(b.Normal)) < 1d - Tolerance) continue;
                var delta = new Vector3d(a.Center, b.Center);
                if (Math.Abs(Math.Abs(delta.Dot(a.Normal)) - (a.Part.Shape.BusBarThickness + b.Part.Shape.BusBarThickness) / 2) > .01) continue;
                Func<double[], double[]> project = p =>
                {
                    var v = new Vector3d(a.Center, Point(p)); return new[] { v.Dot(a.Along), v.Dot(a.Width) };
                };
                var polygon = Intersect(Hull(a.Corners.Select(project)), Hull(b.Corners.Select(project)));
                if (polygon.Length < 3 || Math.Abs(Area(polygon)) < .001 ||
                    !Contains(polygon, project(Values(a.Center))) || !Contains(polygon, project(Values(b.Center)))) continue;
                var lo = polygon.Min(p => p[0]); var hi = polygon.Max(p => p[0]);
                var sideLo = polygon.Min(p => p[1]); var sideHi = polygon.Max(p => p[1]);
                var bottom = Math.Min(a.Center.ToVector.Dot(a.Normal) - a.Part.Shape.BusBarThickness / 2d,
                    b.Center.ToVector.Dot(a.Normal) - b.Part.Shape.BusBarThickness / 2d);
                var top = Math.Max(a.Center.ToVector.Dot(a.Normal) + a.Part.Shape.BusBarThickness / 2d,
                    b.Center.ToVector.Dot(a.Normal) + b.Part.Shape.BusBarThickness / 2d);
                var center = Shift(Shift(a.Center, a.Width, (sideLo + sideHi) / 2d), a.Normal,
                    (bottom + top) / 2d - a.Center.ToVector.Dot(a.Normal));
                // 非水平搭接未指定螺栓朝向，两侧各保留35mm，完整覆盖沿排面法向的紧固件。
                var hardware = BridgeCopperClearance.CreateUiSegmentSolid("非水平搭接紧固件",
                    Shift(center, a.Along, lo), Shift(center, a.Along, hi), a.Width, a.Normal,
                    sideHi - sideLo, top - bottom + 2d * Projection, 1, a.Part.Route.Phase);
                var lap = new Lap { First = new Terminal { Part = a.Part, Center = a.Center }, Second = new Terminal { Part = b.Part, Center = b.Center },
                    Joint = "oriented:" + a.Part.Route.Id + ":" + b.Part.Route.Id };
                result.Add(new KeyValuePair<Lap, BridgeUiSolid>(lap, hardware));
            }
            return result.AsReadOnly();
        }
        private ContactFace Contact(Part part, bool start)
        {
            var shape = part.Shape;
            if (shape?.PointList == null || shape.PointList.Count < 2 || shape.WidthVector == null || shape.StartBusbarVector == null)
                throw new InvalidOperationException("铜排缺少实际端面参数：" + part.Route.Id);
            var index = start ? 0 : shape.PointList.Count - 1;
            var point = shape.PointList[index]; var inward = new Vector3d(point, shape.PointList[start ? 1 : index - 1]);
            if (inward.Norm <= Tolerance || shape.WidthVector.Norm <= Tolerance) throw new InvalidOperationException("铜排端面方向无效：" + part.Route.Id);
            var along = inward.Normalized; var width = shape.WidthVector.Normalized;
            // 换相斜段保留全排参考宽度；端面须与实体净距计算使用相同的逐段正交截面。
            var projection = along.Dot(width);
            width = new Vector3d(width.X - along.X * projection,
                width.Y - along.Y * projection, width.Z - along.Z * projection);
            if (width.Norm <= Tolerance) throw new InvalidOperationException("铜排端面排宽与路径平行：" + part.Route.Id);
            width = width.Normalized;
            var normal = along.Cross(width).Normalized;
            if (normal.Dot(shape.StartBusbarVector) < 0) normal = new Vector3d(-normal.X, -normal.Y, -normal.Z);
            var center = shape.IsCenter ? point : Shift(point, normal, shape.BusBarThickness / 2);
            var end = Shift(center, along, inward.Norm);
            return new ContactFace { Part = part, Center = center, Along = along, Width = width, Normal = normal,
                Horizontal = Math.Abs(along.Z) <= Tolerance && Math.Abs(width.Z) <= Tolerance,
                Corners = new[] { Values(Shift(center, width, -shape.BusBarWidth / 2)), Values(Shift(center, width, shape.BusBarWidth / 2)),
                    Values(Shift(end, width, shape.BusBarWidth / 2)), Values(Shift(end, width, -shape.BusBarWidth / 2)) } };
        }

        private List<Metal> ReadMetals(IReadOnlyList<BridgeDesignObject> objects, IEnumerable<BridgeDesignObject> frames = null,
            bool anglesOnly = false)
        {
            var result = new List<Metal>();
            foreach (var frame in (frames ?? objects.Where(o => o.Kind == DesignObjectKind.Frame)).OrderBy(o => o.Id, StringComparer.Ordinal))
            {
                if (frame.Height <= 0 || frame.Width <= 0 || frame.Length <= 0 ||
                    !new[] { "Regular", "SmallOffset", "LargeOffsetCorner",
                        "SideToSidePhaseChange029", "FaceToWallLarge1", "FaceToWallLarge2" }.Contains(frame.FrameTemplateKey))
                    throw new InvalidOperationException("基架缺少已支持的模板或实际驱动尺寸：" + frame.Name);
                foreach (var solid in _scene.Solids3D(frame, objects))
                {
                    if (anglesOnly && !solid.AngleNotchSlot.HasValue) continue;
                    var corners = solid.Corners;
                    var label = solid.AngleNotchSlot.HasValue
                        ? "角钢豁口位置 " + (solid.AngleNotchSlot.Value + 1).ToString(CultureInfo.InvariantCulture) : solid.Material;
                    result.Add(new Metal { Frame = frame.Id, Name = frame.DisplayName + " / " + label, AngleNotchSlot = solid.AngleNotchSlot,
                        Polygon = Hull(corners), Bottom = corners.Min(p => p[2]), Top = corners.Max(p => p[2]),
                        Solid = MetalSolid(corners, frame.Id + ":" + result.Count) });
                }
            }
            return result;
        }

        private BridgeUiSolid MetalSolid(double[][] corners, string name)
        {
            if (corners == null || corners.Length != 8) throw new InvalidOperationException("基架金属板件缺少八个已核验角点。");
            var origin = Point(corners[0]);
            var vectors = corners.Skip(1).Select(p => new Vector3d(origin, Point(p))).OrderBy(v => v.Norm).ToArray();
            var x = vectors.First(v => v.Norm > Tolerance);
            var y = vectors.FirstOrDefault(v => Math.Abs(v.Normalized.Dot(x.Normalized)) < Tolerance);
            var z = y == null ? null : vectors.FirstOrDefault(v => Math.Abs(v.Normalized.Dot(x.Normalized)) < Tolerance && Math.Abs(v.Normalized.Dot(y.Normalized)) < Tolerance);
            if (y == null || z == null || corners.Any(p => !OnEdgeGrid(new Vector3d(origin, Point(p)), x, y, z)))
                throw new InvalidOperationException("基架板件不是已支持的正交薄板，不能回退到Box。");
            var center = new Point3d(corners.Average(p => p[0]), corners.Average(p => p[1]), corners.Average(p => p[2]));
            var u = x.Normalized;
            return BridgeCopperClearance.CreateUiSegmentSolid(name, Shift(center, u, -x.Norm / 2), Shift(center, u, x.Norm / 2),
                y.Normalized, z.Normalized, y.Norm, z.Norm, 0, "frame:" + name);
        }
        private bool OnEdgeGrid(Vector3d p, Vector3d x, Vector3d y, Vector3d z) => new[] { x, y, z }.All(v =>
        {
            var d = p.Dot(v.Normalized); return Math.Abs(d) <= Tolerance || Math.Abs(d - v.Norm) <= Tolerance;
        });

        private List<Lap> FindLaps(Part[] parts, IReadOnlyList<BridgeDesignObject> objects)
        {
            var ends = parts.SelectMany(p => new[] { End(p, true), End(p, false) }).Where(t => t != null).ToArray();
            var result = new List<Lap>();
            for (var i = 0; i < ends.Length - 1; i++)
            for (var j = i + 1; j < ends.Length; j++)
            {
                var a = ends[i]; var b = ends[j];
                if (a.Part == b.Part || a.Part.Route.Phase != b.Part.Route.Phase ||
                    Math.Abs(Math.Abs(a.Center.Z - b.Center.Z) - (a.Part.Shape.BusBarThickness + b.Part.Shape.BusBarThickness) / 2) > .01) continue;
                var polygon = Intersect(a.Polygon, b.Polygon);
                if (polygon.Length < 3 || Math.Abs(Area(polygon)) < .001) continue;
                // 纯两点排的首尾共用一个直段，只有位于实际重叠区的加工端才是接头。
                if (!Contains(polygon, Values(a.Center)) || !Contains(polygon, Values(b.Center))) continue;
                var controls = objects.Where(o => o.IsJointGroup && o.Phase == a.Part.Route.Phase &&
                    o.X >= polygon.Min(p => p[0]) - .2 && o.X <= polygon.Max(p => p[0]) + .2 &&
                    o.Y >= polygon.Min(p => p[1]) - .2 && o.Y <= polygon.Max(p => p[1]) + .2).ToArray();
                result.Add(new Lap { First = a, Second = b, Polygon = polygon,
                    Bottom = Math.Min(a.Bottom, b.Bottom), Top = Math.Max(a.Top, b.Top),
                    Joint = controls.Length == 1 ? controls[0].JointGroupId : "unmapped:" + a.Part.Route.Id + ":" + b.Part.Route.Id });
            }
            return result;
        }


        private Terminal End(Part part, bool start)
        {
            var shape = part.Shape;
            if (shape?.PointList == null || shape.PointList.Count < 2 || shape.WidthVector == null || shape.StartBusbarVector == null)
                throw new InvalidOperationException("铜排缺少可核验的截面及原生路径：" + part.Route.Id);
            var index = start ? 0 : shape.PointList.Count - 1;
            var point = shape.PointList[index]; var inner = shape.PointList[start ? 1 : index - 1];
            var inward = new Vector3d(point, inner);
            if (inward.Norm <= Tolerance || Math.Abs(inward.Normalized.Z) > Tolerance || Math.Abs(shape.WidthVector.Normalized.Z) > Tolerance) return null;
            var width = shape.WidthVector.Normalized;
            if (Math.Abs(width.Dot(inward.Normalized)) > Tolerance) return null;
            var normal = inward.Normalized.Cross(width).Normalized;
            if (normal.Dot(shape.StartBusbarVector) < 0) normal = new Vector3d(-normal.X, -normal.Y, -normal.Z);
            var center = shape.IsCenter ? point : Shift(point, normal, shape.BusBarThickness / 2);
            var endCenter = Shift(center, inward.Normalized, inward.Norm);
            var polygon = Hull(new[] { Values(Shift(center, width, -shape.BusBarWidth / 2)), Values(Shift(center, width, shape.BusBarWidth / 2)),
                Values(Shift(endCenter, width, -shape.BusBarWidth / 2)), Values(Shift(endCenter, width, shape.BusBarWidth / 2)) });
            return new Terminal { Part = part, Start = start, Center = center, Width = width, Inward = inward.Normalized,
                Length = inward.Norm, Bottom = center.Z - shape.BusBarThickness / 2, Top = center.Z + shape.BusBarThickness / 2, Polygon = polygon };
        }

        private double RequiredLift(IEnumerable<Lap> laps, List<Metal> metals, out double maximum)
        {
            var forbidden = new List<double[]>(); maximum = double.PositiveInfinity;
            foreach (var lap in laps)
            foreach (var metal in metals)
            {
                var horizontal = PlanarDistance(lap.Polygon, metal.Polygon);
                if (horizontal >= Clearance) continue;
                var vertical = Math.Sqrt(Clearance * Clearance - horizontal * horizontal);
                var low = metal.Bottom - vertical - lap.Top;
                var high = metal.Top + vertical - (lap.Bottom - Projection);
                // 不能穿过上部金属再把“越过屋顶”当成另一个可行层位。
                if (metal.Bottom >= lap.Top - Tolerance) maximum = Math.Min(maximum, low);
                forbidden.Add(new[] { low, high });
            }
            var lift = 0d;
            foreach (var interval in forbidden.OrderBy(p => p[0]).ThenBy(p => p[1]))
                if (lift > interval[0] + Tolerance && lift < interval[1] - Tolerance)
                    lift = Math.Ceiling((interval[1] - Tolerance) * 10d) / 10d;
            return lift;
        }

        private void CheckFrameGeometry(Part[] parts, List<Lap> laps, List<Metal> metals,
            Dictionary<Part, IReadOnlyList<BridgeUiSolid>> solids, List<BridgeDesignIssue> issues,
            IReadOnlyList<KeyValuePair<Lap, BridgeUiSolid>> nonHorizontal)
        {
            foreach (var lap in laps)
            foreach (var metal in metals)
            {
                var vertical = Math.Max(0, Math.Max(metal.Bottom - lap.Top, lap.Bottom - Projection - metal.Top));
                var planar = PlanarDistance(lap.Polygon, metal.Polygon);
                var distance = Math.Sqrt(vertical * vertical + planar * planar);
                if (distance + ClearanceToleranceMm >= Clearance) continue;
                issues.Add(new BridgeDesignIssue(FormattableString.Invariant($"{lap.Phase}相搭接下凸35.0 mm的保守净距为{distance:0.0} mm，小于125.0 mm。对象：{metal.Name}。"), lap.Joint, IssueSource));
                return;
            }
            foreach (var part in parts)
            foreach (var copper in solids[part])
            foreach (var metal in metals)
            {
                if (!NeedsExactDistance(copper, metal.Solid, Clearance)) continue;
                var distance = BridgeCopperClearance.UiPairDistance(copper, metal.Solid);
                if (distance + ClearanceToleranceMm >= Clearance) continue;
                issues.Add(new BridgeDesignIssue(FormattableString.Invariant(
                    $"铜排{part.Route.Id}（含折弯保守包络）与{metal.Name}的净距为{distance:0.0} mm，小于要求值{Clearance:0.0} mm。"),
                    part.Route.Id, "铜排与基架净距不足").WithLocation(
                        new BridgeIssueLocation(metal.Frame, distance, Clearance,
                            IssueCorners(copper), IssueCorners(metal.Solid))));
                return;
            }
            foreach (var item in nonHorizontal)
            {
                foreach (var metal in metals)
                    if (NeedsExactDistance(item.Value, metal.Solid, Clearance) &&
                        BridgeCopperClearance.UiPairDistance(item.Value, metal.Solid) + ClearanceToleranceMm < Clearance)
                    {
                        issues.Add(new BridgeDesignIssue("非水平搭接紧固件与基架的125mm保守净距未通过：" + metal.Name, item.Key.Joint, IssueSource));
                        return;
                    }
            }
        }

        private double[][] IssueCorners(BridgeUiSolid solid)
        {
            var points = new List<double[]>();
            foreach (var lengthSign in new[] { -1d, 1d })
            foreach (var widthSign in new[] { -1d, 1d })
            foreach (var thicknessSign in new[] { -1d, 1d })
            {
                var point = Shift(solid.Center, solid.LengthDirection, lengthSign * solid.HalfLength);
                point = Shift(point, solid.WidthDirection, widthSign * solid.HalfWidth);
                point = Shift(point, solid.ThicknessDirection, thicknessSign * solid.HalfThickness);
                points.Add(Values(point));
            }
            return points.ToArray();
        }

        private void CheckGeometry(Part[] parts, List<Lap> laps, List<Metal> metals,
            Dictionary<Part, IReadOnlyList<BridgeUiSolid>> solids, List<BridgeDesignIssue> issues, double phaseClearance)
        {
            var nonHorizontal = NonHorizontalFasteners(parts);
            CheckFrameGeometry(parts, laps, metals, solids, issues, nonHorizontal);
            if (issues.Count > 0) return;
            // 紧固件的覆盖轮廓是保守实体；相交只能说明未通过保守校核。
            var hardwareSolids = laps.Select(lap => new KeyValuePair<Lap, BridgeUiSolid>(lap, HorizontalFastener(lap)))
                .Concat(nonHorizontal).ToList();
            foreach (var item in hardwareSolids)
            {
                var lap = item.Key; var hardware = item.Value;
                foreach (var part in parts.Where(p => p.Route.Phase != lap.Phase))
                foreach (var copper in solids[part])
                {
                    var required = MiddleLap(lap) && part.Route.GenerationGroup == "中间排" ? phaseClearance : .01d;
                    if (!NeedsExactDistance(hardware, copper, required)) continue;
                    if (BridgeCopperClearance.UiPairDistance(hardware, copper) + Tolerance >= required) continue;
                    issues.Add(new BridgeDesignIssue("搭接紧固件对其他相铜排的保守净距未通过：" + lap.Phase + " / " + part.Route.Id + "。", lap.Joint, IssueSource));
                    return;
                }
            }
            for (var i = 0; i < hardwareSolids.Count - 1; i++)
            for (var j = i + 1; j < hardwareSolids.Count; j++)
            {
                var a = hardwareSolids[i]; var b = hardwareSolids[j];
                if (a.Key.Phase == b.Key.Phase) continue;
                var required = MiddleLap(a.Key) && MiddleLap(b.Key) ? phaseClearance : .01d;
                if (!NeedsExactDistance(a.Value, b.Value, required)) continue;
                if (BridgeCopperClearance.UiPairDistance(a.Value, b.Value) + Tolerance >= required) continue;
                issues.Add(new BridgeDesignIssue("不同相搭接紧固件之间的保守净距未通过。", a.Key.Joint, IssueSource));
                return;
            }
        }

        private bool MiddleLap(Lap lap) => lap.First.Part.Route.GenerationGroup == "中间排" && lap.Second.Part.Route.GenerationGroup == "中间排";

        private BridgeUiSolid HorizontalFastener(Lap lap)
        {
            var along = lap.First.Inward; var width = lap.First.Width;
            var lo = lap.Polygon.Min(p => p[0] * along.X + p[1] * along.Y);
            var hi = lap.Polygon.Max(p => p[0] * along.X + p[1] * along.Y);
            var sideLo = lap.Polygon.Min(p => p[0] * width.X + p[1] * width.Y);
            var sideHi = lap.Polygon.Max(p => p[0] * width.X + p[1] * width.Y);
            var side = (sideLo + sideHi) / 2d;
            var a = new Point3d(along.X * lo + width.X * side, along.Y * lo + width.Y * side, lap.Bottom);
            var b = new Point3d(along.X * hi + width.X * side, along.Y * hi + width.Y * side, lap.Bottom);
            return _forming.CreateFastenerSolid(a, b, width, new Vector3d(0, 0, 1), sideHi - sideLo, lap.Phase, lap.Top - lap.Bottom);
        }

        // 只用已构造实体的投影给出距离下界，远距离对不进入精确扫描；不读取组件Box。
        private bool NeedsExactDistance(BridgeUiSolid a, BridgeUiSolid b, double required)
        {
            var x = Math.Max(0, Math.Abs(a.Center.X - b.Center.X) - AxisSpan(a, 0) - AxisSpan(b, 0));
            var y = Math.Max(0, Math.Abs(a.Center.Y - b.Center.Y) - AxisSpan(a, 1) - AxisSpan(b, 1));
            var z = Math.Max(0, Math.Abs(a.Center.Z - b.Center.Z) - AxisSpan(a, 2) - AxisSpan(b, 2));
            return x * x + y * y + z * z < (required + Tolerance) * (required + Tolerance);
        }
        private double AxisSpan(BridgeUiSolid solid, int axis) =>
            Math.Abs(Component(solid.LengthDirection, axis)) * solid.HalfLength +
            Math.Abs(Component(solid.WidthDirection, axis)) * solid.HalfWidth +
            Math.Abs(Component(solid.ThicknessDirection, axis)) * solid.HalfThickness;
        private double Component(Vector3d vector, int axis) => axis == 0 ? vector.X : axis == 1 ? vector.Y : vector.Z;

        private PlanarBendBusbarSetting ToDraft(PlanarBendBusbarSetting native, BridgeCoordinateFrame coordinates)
        {
            if (native == null) throw new InvalidOperationException("铜排缺少原生加工参数。");
            if (native.PointList == null || native.PointList.Count < 2 || native.PointList.Any(p => p == null || !Finite(Values(p))) ||
                native.WidthVector == null || native.StartBusbarVector == null ||
                !Finite(Values(native.WidthVector)) || !Finite(Values(native.StartBusbarVector)) ||
                native.WidthVector.Norm <= Tolerance || native.StartBusbarVector.Norm <= Tolerance ||
                !Finite(new[] { native.BusBarWidth, native.BusBarThickness }) || native.BusBarWidth <= 0 || native.BusBarThickness <= 0)
                throw new InvalidOperationException("铜排缺少有效的原生路径、截面或方向参数。");
            var shape = BridgeNativePreviewCopy.Copy(native);
            shape.PointList = native.PointList.Select(p => Point(coordinates.ToDraft(Values(p)))).ToList();
            shape.WidthVector = Direction(coordinates.ToDraftDirection(Values(native.WidthVector)));
            shape.StartBusbarVector = Direction(coordinates.ToDraftDirection(Values(native.StartBusbarVector)));
            return shape;
        }
        private bool Finite(IEnumerable<double> values) => values.All(v => !double.IsNaN(v) && !double.IsInfinity(v));
        private PlanarBendBusbarSetting ToAssembly(PlanarBendBusbarSetting shape, BridgeCoordinateFrame coordinates)
        {
            var native = BridgeNativePreviewCopy.Copy(shape);
            native.PointList = shape.PointList.Select(p => Point(coordinates.ToAssembly(Values(p)))).ToList();
            native.WidthVector = Direction(coordinates.ToAssemblyDirection(Values(shape.WidthVector)));
            native.StartBusbarVector = Direction(coordinates.ToAssemblyDirection(Values(shape.StartBusbarVector)));
            return native;
        }
        private bool SamePath(PlanarBendBusbarSetting a, PlanarBendBusbarSetting b) => a != null && b != null &&
            a.PointList.Count == b.PointList.Count && a.PointList.Zip(b.PointList, (x, y) => x.DistanceTo(y)).All(d => d <= Tolerance);
        private double[] Values(Point3d p) => new[] { p.X, p.Y, p.Z };
        private double[] Values(Vector3d v) => new[] { v.X, v.Y, v.Z };
        private Point3d Point(IReadOnlyList<double> p) => new Point3d(p[0], p[1], p[2]);
        private Vector3d Direction(IReadOnlyList<double> p) => new Vector3d(p[0], p[1], p[2]);
        private Point3d Shift(Point3d p, Vector3d v, double d) => new Point3d(p.X + v.X * d, p.Y + v.Y * d, p.Z + v.Z * d);
        private double Span(double[][] polygon, Vector3d v) => polygon.Max(p => p[0] * v.X + p[1] * v.Y) - polygon.Min(p => p[0] * v.X + p[1] * v.Y);
        private double Cross(double[] a, double[] b, double[] c) => (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
        private double Area(double[][] p) => Enumerable.Range(0, p.Length).Sum(i => p[i][0] * p[(i + 1) % p.Length][1] - p[i][1] * p[(i + 1) % p.Length][0]) / 2;
        private bool Contains(double[][] polygon, double[] point) => Enumerable.Range(0, polygon.Length)
            .All(i => Cross(polygon[i], polygon[(i + 1) % polygon.Length], point) >= -.01d);
        private double[][] Hull(IEnumerable<double[]> points)
        {
            var sorted = points.Select(p => new[] { p[0], p[1] }).OrderBy(p => p[0]).ThenBy(p => p[1]).ToArray();
            var unique = new List<double[]>();
            foreach (var p in sorted) if (unique.Count == 0 || Math.Abs(p[0] - unique.Last()[0]) > Tolerance || Math.Abs(p[1] - unique.Last()[1]) > Tolerance) unique.Add(p);
            if (unique.Count < 3) throw new InvalidOperationException("板件投影缺少有效平面轮廓。");
            var lower = new List<double[]>(); var upper = new List<double[]>();
            foreach (var p in unique) { while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower.Last(), p) <= Tolerance) lower.RemoveAt(lower.Count - 1); lower.Add(p); }
            foreach (var p in unique.AsEnumerable().Reverse()) { while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper.Last(), p) <= Tolerance) upper.RemoveAt(upper.Count - 1); upper.Add(p); }
            return lower.Take(lower.Count - 1).Concat(upper.Take(upper.Count - 1)).ToArray();
        }
        private double[][] Intersect(double[][] subject, double[][] clip)
        {
            var output = subject.ToList();
            for (var i = 0; i < clip.Length && output.Count > 0; i++)
            {
                var a = clip[i]; var b = clip[(i + 1) % clip.Length]; var input = output; output = new List<double[]>();
                var previous = input.Last(); var previousSide = Cross(a, b, previous);
                foreach (var p in input)
                {
                    var side = Cross(a, b, p);
                    if ((side >= -Tolerance) != (previousSide >= -Tolerance))
                    {
                        var t = previousSide / (previousSide - side);
                        output.Add(new[] { previous[0] + (p[0] - previous[0]) * t, previous[1] + (p[1] - previous[1]) * t });
                    }
                    if (side >= -Tolerance) output.Add(p);
                    previous = p; previousSide = side;
                }
            }
            return output.ToArray();
        }
        private double PlanarDistance(double[][] a, double[][] b)
        {
            if (Intersect(a, b).Length >= 3) return 0;
            var distance = double.PositiveInfinity;
            for (var i = 0; i < a.Length; i++)
            for (var j = 0; j < b.Length; j++)
            {
                distance = Math.Min(distance, PointSegment(a[i], b[j], b[(j + 1) % b.Length]));
                distance = Math.Min(distance, PointSegment(b[j], a[i], a[(i + 1) % a.Length]));
            }
            return distance;
        }
        private double PointSegment(double[] p, double[] a, double[] b)
        {
            var x = b[0] - a[0]; var y = b[1] - a[1]; var length = x * x + y * y;
            var t = length <= Tolerance * Tolerance ? 0 : Math.Max(0, Math.Min(1, ((p[0] - a[0]) * x + (p[1] - a[1]) * y) / length));
            var dx = p[0] - a[0] - x * t; var dy = p[1] - a[1] - y * t;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
