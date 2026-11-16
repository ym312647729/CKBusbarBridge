using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RD.WebPreview.Contracts;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    /// <summary>预览用板件/简化实体，两种投影与三维共享；不作为制造模板尺寸。</summary>
    public sealed class BridgePreviewSolid
    {
        internal int? AngleNotchSlot { get; set; }
        public string Material { get; }
        public double[][] Corners { get; }
        public DraftRoundSection Round { get; }
        public bool IsHiddenIn2D { get; internal set; }
        public BridgePreviewSolid(string material, double[][] corners) { Material = material; Corners = corners; }
        public BridgePreviewSolid(string material, DraftRoundSection round)
        { Material = material; Round = round; Corners = new DesignGeometry.BridgeSupportPreviewGeometry().Vertices(round); }
    }

    /// <summary>同一草稿投影到2D/3D；架内绝缘子坐标相对所属基架，基座不归属柜体。</summary>
    public sealed class BusbarBridgeDraftPreviewSceneFactory
    {
        private delegate void FramePlate(string material, double x, double y, double z,
            double sx, double sy, double sz, int? notchSlot = null);
        private readonly DesignGeometry.BridgeSupportPreviewGeometry _supportGeometry = new DesignGeometry.BridgeSupportPreviewGeometry();
        // 仅用于内置示意：统一底板/绝缘子尺寸，避免Provider定位和二维/三维板件各用一套高度。
        public const double FrameHeightMm = 500;
        // 共用安装高度基准：已核验常规基架绝缘子支架坐标系距基架底面41.5mm。
        public const double FrameFloorThicknessMm = 41.5;
        public const double DemoCopperThicknessMm = 10;
        public const double InsulatorWidthMm = 100;
        // 保留旧常量名供调用者兼容；现在表示已核验折边支架的总安装高度。
        public const double DemoInsulatorBracketThicknessMm = InsulatorPreviewProfile.BracketHeight;
        public const double FrameFloorTopZ = -FrameHeightMm / 2 + FrameFloorThicknessMm;
        public const double VerticalInsulatorHeightMm = -FrameFloorTopZ - DemoCopperThicknessMm / 2;
        public const double VerticalInsulatorCenterZ = FrameFloorTopZ + VerticalInsulatorHeightMm / 2;
        public double[] WorldPosition(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
        {
            var hanging = HangingInsulatorPose(node, objects);
            if (hanging != null) return Enumerable.Range(0, 3).Select(i =>
                hanging.Connection[i] - hanging.Axis[i] * hanging.Height / 2).ToArray();
            var parent = objects.FirstOrDefault(o => o.Id == node.ParentId);
            if (parent == null) return new[] { node.X, node.Y, node.Z };
            var localY = node.Y; var localZ = node.Z;
            if (node.Kind == DesignObjectKind.Insulator && parent.Kind == DesignObjectKind.Frame)
            {
                var vertical = parent.FrameTemplateKey == "SideToSidePhaseChange029";
                var height = parent.InsulatorAssemblyHeight > 0d ? parent.InsulatorAssemblyHeight : node.Height;
                var seat = -(vertical ? parent.Width : parent.Height) / 2d + FrameFloorThicknessMm +
                    height / 2d;
                if (vertical) localY = RestoreRoundedDimension(localY, seat);
                else localZ = RestoreRoundedDimension(localZ, seat);
            }
            var a = parent.ModelDirection * Math.PI / 180;
            return new[] { parent.X + node.X * Math.Cos(a) - localY * Math.Sin(a),
                parent.Y + node.X * Math.Sin(a) + localY * Math.Cos(a), parent.Z + localZ };
        }

        // 草稿轴序的模板接口供编辑、正式生成和反读共用，墙端进孔可偏心或低于出口。
        internal double[][] FrameInterfacePoints(BridgeDesignObject frame, bool useDrivenPosition = false)
        {
            if (frame.WallFramePreview == null)
                return FacingFrameInterfacePoints(frame, useDrivenPosition).Select(p => new[] { p[0], p[2], p[1] }).ToArray();
            var inlet = frame.WallFramePreview;
            var angle = frame.DriveValue(DesignField.Direction) * Math.PI / 180;
            var center = FrameInterfaceCenter(frame, useDrivenPosition);
            Func<double, double, double, double[]> point = (x, y, z) => new[] {
                center[0] + x * Math.Cos(angle) - y * Math.Sin(angle),
                center[1] + x * Math.Sin(angle) + y * Math.Cos(angle), center[2] + z };
            return new[] { point(-frame.DriveValue(DesignField.Length) / 2, inlet.InletOffset,
                    -(frame.DriveValue(DesignField.Height) - inlet.InletHeight) / 2),
                point(frame.DriveValue(DesignField.Length) / 2, 0, 0) };
        }

        // 面对面接口使用原生规划轴序(X桥向、Y高度、Z横向)，与正式生成、反读共用模板坐标关系。
        internal double[][] FacingFrameInterfacePoints(BridgeDesignObject frame, bool useDrivenPosition = false)
        {
            double x1, y1, x2, y2;
            if (frame.CornerPreview != null)
            {
                var c = frame.CornerPreview;
                var end = -c.ClosedEnd * frame.DriveValue(DesignField.Length) / 2;
                var side = -c.ClosedSide * frame.DriveValue(DesignField.Width) / 2;
                x1 = c.ClosedEnd > 0 ? end : 0; y1 = c.ClosedEnd > 0 ? 0 : side;
                x2 = c.ClosedEnd > 0 ? 0 : end; y2 = c.ClosedEnd > 0 ? side : 0;
            }
            else
            {
                if (frame.FrameTemplateKey == "SmallOffset" && frame.SmallOffsetPreview == null)
                    throw new InvalidOperationException("小偏移基架缺少进出孔接口参数：" + frame.Name);
                x1 = -frame.DriveValue(DesignField.Length) / 2; x2 = frame.DriveValue(DesignField.Length) / 2;
                y1 = frame.SmallOffsetPreview?.InstalledPortOffset(0, frame.IsReversed) ?? 0d;
                y2 = frame.SmallOffsetPreview?.InstalledPortOffset(1, frame.IsReversed) ?? 0d;
            }
            var angle = frame.DriveValue(DesignField.Direction) * Math.PI / 180;
            var center = FrameInterfaceCenter(frame, useDrivenPosition);
            Func<double, double, double[]> world = (x, y) => new[]
            {
                center[0] + x * Math.Cos(angle) - y * Math.Sin(angle), center[2],
                center[1] + x * Math.Sin(angle) + y * Math.Cos(angle)
            };
            return new[] { world(x1, y1), world(x2, y2) };
        }

        private static double[] FrameInterfaceCenter(BridgeDesignObject frame, bool useDrivenPosition) =>
            useDrivenPosition ? new[] { frame.DriveValue(DesignField.X), frame.DriveValue(DesignField.Y), frame.DriveValue(DesignField.Z) } :
                new[] { frame.X, frame.Y, frame.Z };

        // 草稿字段保留0.1mm；预览安装面使用未量化驱动值，避免支架与本体之间出现0.05mm缝隙。
        // 超出量化范围的冲突保持可见，交给生成校验拒绝，不能吸附到名义位置掩盖错误。
        private double RestoreRoundedDimension(double draft, double driven) =>
            Math.Abs(draft - driven) <= .05d + 1e-7d ? driven : draft;

        public IReadOnlyList<BridgeCopperSegment> CopperSegments(BridgeDesignRoute route, IReadOnlyList<BridgeDesignObject> objects)
        {
            var points = route.PointIds.Select(id => objects.FirstOrDefault(o => o.Id == id) ??
                throw new InvalidOperationException("铜排路径缺少节点：" + id)).ToArray();
            if (route.IsPhysicalGeometry)
                return new[] { new BridgeCopperSegment(route.Id, points, false)
                    .WithPhysicalGeometry(route.SectionWidth, route.SectionThickness) };
            var result = new List<BridgeCopperSegment>(); var start = 0;
            for (var end = 1; end < points.Length; end++)
            {
                if (end != points.Length - 1 && points[end].Kind != DesignObjectKind.Lap && points[end].Kind != DesignObjectKind.Breakpoint && points[end].Kind != DesignObjectKind.FixedJoint) continue;
                result.Add(new BridgeCopperSegment(route.Id + "/" + points[start].Id + "--" + points[end].Id,
                    points.Skip(start).Take(end - start + 1), route.LSegmentStartIds.Contains(points[start].Id), route.StraightLapStartIds.Contains(points[start].Id)));
                start = end;
            }
            if (route.LSegmentStartIds.Any(id => result.All(s => s.Points[0].Id != id)))
                throw new InvalidOperationException("L排标识落在控制点而非段起点，不能套用端头规则。");
            return result.AsReadOnly();
        }

        public BridgeJointMeasurements MeasureJoint(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> objects, string jointId)
        {
            if (sample == null || objects == null || string.IsNullOrWhiteSpace(jointId)) return null;
            var joint = objects.FirstOrDefault(o => o.Id == jointId);
            if (joint == null) return null;
            // 基座L排也会引用桥端固定断点；断点参数只取桥内主路径，避免把基座L排误作前后段排。
            var route = (sample.CopperEditRoutes.Count > 0 ? sample.CopperEditRoutes : sample.Routes)
                .FirstOrDefault(r => r.LSegmentStartIds.Count == 0 && r.PointIds.Contains(jointId));
            if (route == null) return null;
            var segments = CopperSegments(route, objects);
            var previous = segments.FirstOrDefault(s => s.Points[s.Points.Count - 1].Id == jointId);
            var next = segments.FirstOrDefault(s => s.Points[0].Id == jointId);
            if (previous == null || next == null) return null;
            var previousJoint = previous.Points[0];
            // 段排长度按左右端点X向坐标差加独立搭接长度；不按桥头斜段外形计算展开长度。
            var overlap = sample.Parameters.OverlapLengthMm;
            return new BridgeJointMeasurements(
                Math.Abs(joint.X - previousJoint.X),
                sample.CopperSectionLength(previous.Points[0], previous.Points[previous.Points.Count - 1]),
                sample.CopperSectionLength(next.Points[0], next.Points[next.Points.Count - 1]), sample.IsSideToSide);
        }

        public IReadOnlyList<BridgeCopperSection> CopperSections(BridgeDesignSample sample,
            IReadOnlyList<BridgeDesignObject> objects)
        {
            if (sample == null || objects == null) return Array.Empty<BridgeCopperSection>();
            // 物理排只有显式的编辑路径和重算入口时才能调整，不能直接拉伸缓存实体。
            if (sample.Routes.Any(r => r.IsPhysicalGeometry) && sample.CopperEditRoutes.Count == 0)
                return Array.Empty<BridgeCopperSection>();
            // 三相同位置段排共用一个选择项；优先取中间B相作为稳定代表。
            var routes = (sample.CopperEditRoutes.Count > 0 ? sample.CopperEditRoutes : sample.Routes)
                .Where(r => r.LSegmentStartIds.Count == 0);
            var reference = sample.IsSideToSide ? routes.Where(r => r.Phase == "B").Take(2).ToArray() : null;
            var route = reference != null ? reference.Length == 1 ? reference[0] : null :
                routes.OrderBy(r => r.Phase == "B" ? 0 : 1).ThenBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
            if (route == null) return Array.Empty<BridgeCopperSection>();
            return Array.AsReadOnly(CopperSegments(route, objects).Select((segment, index) =>
            {
                var start = segment.Points[0];
                var end = segment.Points[segment.Points.Count - 1];
                var adjustable = end.IsJointGroup && end.IsEditable &&
                    (end.Kind == DesignObjectKind.Lap || end.Kind == DesignObjectKind.Breakpoint);
                return new BridgeCopperSection(index + 1, start.Id, end.Id,
                    sample.CopperSectionLength(start, end), adjustable, sample.IsSideToSide);
            }).ToArray());
        }

        [Obsolete("改用 CopperSegmentPath(segment, objects, width, thickness, overlapLengthMm)，显式传入规格和独立搭接长度。")]
        public double[][] CopperSegmentPath(BridgeCopperSegment segment, IReadOnlyList<BridgeDesignObject> objects, double thickness)
        {
            throw new InvalidOperationException("缺少铜排规格及搭接参数，不能生成搭接预览；请使用完整参数重载。");
        }

        // 兼容旧调用的暂定默认值；二维/三维正式入口均显式传入独立搭接参数。
        public double[][] CopperSegmentPath(BridgeCopperSegment segment, IReadOnlyList<BridgeDesignObject> objects, double width, double thickness)
            => CopperSegmentPath(segment, objects, width, thickness, width);

        public double[][] CopperSegmentPath(BridgeCopperSegment segment, IReadOnlyList<BridgeDesignObject> objects, double width, double thickness, double overlapLengthMm)
        {
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "预览排宽必须为有限正数。");
            if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= 0)
                throw new ArgumentOutOfRangeException(nameof(thickness), "预览排厚必须为有限正数。");
            if (double.IsNaN(overlapLengthMm) || double.IsInfinity(overlapLengthMm) || overlapLengthMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(overlapLengthMm), "预览搭接长度必须为有限正数。");
            var points = segment.Points.Select(p => WorldPosition(p, objects)).ToArray();
            if (points.Length < 2) return points;
            if (segment.IsPhysicalGeometry) return points; // 正式几何已含搭接及桥头，不能再叠加原型8:4:5端头。
            if (segment.IsL) return segment.StraightLapAtStart ? StraightLapPath(points, thickness, overlapLengthMm) : points;
            var origin = points[0];
            var first = 1;
            while (first < points.Length && Distance(origin, points[first]) < .01) first++;
            if (first == points.Length) return points;
            var length = Distance(origin, points[first]);
            var direction = Enumerable.Range(0, 3).Select(i => (points[first][i] - origin[i]) / length).ToArray();
            var xy = Math.Sqrt(direction[0] * direction[0] + direction[1] * direction[1]);
            var normal = xy > .000001 ? new[] { -direction[2] * direction[0] / xy, -direction[2] * direction[1] / xy, xy } : new[] { 1d, 0d, 0d };
            // 用户确认的预览近似比例：搭接区 : 平直余量 : 斜过渡水平长度 = 8 : 4 : 5。
            // 基准是独立搭接参数，不随排宽或段长重算；短段容纳性仍待正式校验。
            var overlap = overlapLengthMm;
            var lead = overlap * 4 / 8;
            var ramp = overlap * 5 / 8;
            Func<double, double, double[]> at = (along, lift) => Enumerable.Range(0, 3)
                .Select(i => origin[i] + direction[i] * along + normal[i] * lift).ToArray();
            return new[] { at(-overlap, thickness), at(lead, thickness), at(lead + ramp, 0) }.Concat(points.Skip(first)).ToArray();
        }
        private double Distance(double[] a, double[] b) => Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i])));

        private double[][] StraightLapPath(double[][] points, double thickness, double overlapLengthMm)
        {
            var dx = points[1][0] - points[0][0]; var dy = points[1][1] - points[0][1];
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < .01 || Math.Abs(points[1][2] - points[0][2]) > .01)
                throw new InvalidOperationException("直直搭接预览需要L排的水平直头段，不能自动套用桥头过渡。");
            // 原型暂将L排水平腿放在进基座直排上方一排厚，伸出独立搭接长度；不增加台阶或斜过渡。
            // 圆弯随水平腿升高，竖腿仍在原X/Y位置，底部固定接口不动；不代表已确认模板成形尺寸。
            for (var i = 0; i < points.Length - 1; i++) points[i][2] += thickness;
            points[0][0] -= overlapLengthMm * dx / length;
            points[0][1] -= overlapLengthMm * dy / length;
            return points;
        }

        public IReadOnlyList<BridgePreviewSolid> Solids(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (node.IsSuppressedInsulator) return Array.Empty<BridgePreviewSolid>();
            var hanging = HangingInsulatorPose(node, objects);
            if (hanging != null) return BaseInsulatorSolids(hanging);
            if (node.Kind == DesignObjectKind.Frame) return Solids3D(node, objects);
            if (node.InsulatorPlacement != null) return BaseInsulatorSolids(node.InsulatorPlacement);
            var phaseChangePose = PhaseChangeInsulatorPose(node, objects);
            if (phaseChangePose != null) return BaseInsulatorSolids(phaseChangePose);
            if (node.ModelMesh != null) return Array.Empty<BridgePreviewSolid>();
            var result = new List<BridgePreviewSolid>();
            var p = WorldPosition(node, objects);
            var parent = objects.FirstOrDefault(o => o.Id == node.ParentId);
            var angle = (node.ModelDirection + (parent?.ModelDirection ?? 0)) * Math.PI / 180;
            Action<string, double, double, double, double, double, double> box = (material, x, y, z, sx, sy, sz) =>
            {
                var corners = new List<double[]>();
                foreach (var dz in new[] { -1d, 1d })
                foreach (var xy in new[] { new[] { -1d, -1d }, new[] { 1d, -1d }, new[] { 1d, 1d }, new[] { -1d, 1d } })
                {
                    var lx = x + xy[0] * sx / 2; var ly = y + xy[1] * sy / 2;
                    corners.Add(new[] { p[0] + lx * Math.Cos(angle) - ly * Math.Sin(angle),
                        p[1] + lx * Math.Sin(angle) + ly * Math.Cos(angle), p[2] + z + dz * sz / 2 });
                }
                result.Add(new BridgePreviewSolid(material, corners.ToArray()));
            };
            if (node.Kind == DesignObjectKind.Cabinet)
            {
                box("cabinet", 0, 0, 0, node.Length, node.Width, node.Height);
            }
            else if (node.Kind == DesignObjectKind.Frame || node.Kind == DesignObjectKind.Base)
            {
                var frame = node.Kind == DesignObjectKind.Frame;
                var along = node.Length;
                var across = node.Width;
                var height = frame ? (node.Height > 0d ? node.Height : FrameHeightMm) : node.Height;
                // 开口槽形结构：两侧板、底板、横向支撑、端部翻边；各节独立，删节后真实留空。
                box("floor", 0, 0, -height / 2 + FrameFloorThicknessMm / 2, along, across, FrameFloorThicknessMm);
                foreach (var sign in new[] { -1d, 1d })
                {
                    box("steel", 0, sign * Math.Max(0, across / 2 - 10), 0, along, Math.Min(20, across), height);
                    box("rim", sign * Math.Max(0, along / 2 - 12), 0, 0, Math.Min(24, along), across, height);
                    box("rim", 0, sign * Math.Max(0, across / 2 - 30), height / 2 - 10, along, Math.Min(60, across), 20);
                }
            }
            else if (node.Kind == DesignObjectKind.Insulator || node.Kind == DesignObjectKind.FixedInsulator)
            {
                if (node.Kind == DesignObjectKind.FixedInsulator)
                    return BaseInsulatorSolids(new BridgeInsulatorPose(
                        new[] { p[0] + Math.Cos(angle) * node.Length / 2, p[1] + Math.Sin(angle) * node.Length / 2, p[2] },
                        new[] { Math.Cos(angle), Math.Sin(angle), 0d }, node.Length));
                var frameHeight = parent?.Kind == DesignObjectKind.Frame && parent.Height > 0d ? parent.Height : FrameHeightMm;
                var height = node.Height > 0d ? node.Height : parent?.Kind == DesignObjectKind.Frame
                    ? frameHeight / 2 - FrameFloorThicknessMm - DemoCopperThicknessMm / 2 : VerticalInsulatorHeightMm;
                if (parent?.Kind == DesignObjectKind.Frame && parent.InsulatorAssemblyHeight > 0d)
                    height = RestoreRoundedDimension(height, parent.InsulatorAssemblyHeight);
                var bracketHeight = node.IsInsulatorGroup && parent?.Kind == DesignObjectKind.Frame ? DemoInsulatorBracketThicknessMm : 0;
                var bottom = p[2] - height / 2 + bracketHeight;
                var profile = _supportGeometry.Meridian(height - bracketHeight);
                for (var i = 1; i < profile.Length; i++)
                {
                    if (profile[i][0] - profile[i - 1][0] < .000001) continue;
                    result.Add(new BridgePreviewSolid("insulator", new DraftRoundSection(
                        new[] { p[0], p[1], bottom + profile[i - 1][0] },
                        new[] { p[0], p[1], bottom + profile[i][0] }, profile[i - 1][1], profile[i][1])));
                }
                // 第一根铜排直接压在本体顶面；之后每两根之间有一层等排厚垫片。
                for (var i = 0; i < (parent?.InsulatorPadCount ?? 0); i++)
                    box("pad", 0, 0, height / 2 + (2 * i + 1.5) * parent.InsulatorPadThickness,
                        InsulatorPreviewProfile.PadWidth, InsulatorPreviewProfile.PadWidth, parent.InsulatorPadThickness);
            }
            else box("handle", 0, 0, 0, 12, 12, 12);
            return result.AsReadOnly();
        }
        internal BridgeInsulatorPose HoleConnection(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
        {
            var placement = HangingInsulatorPose(node, objects) ?? node.InsulatorPlacement ?? PhaseChangeInsulatorPose(node, objects);
            if (placement != null) return placement;
            var frame = objects.SingleOrDefault(item => item.Id == node.ParentId && item.Kind == DesignObjectKind.Frame);
            if (node.Kind != DesignObjectKind.Insulator || frame == null || node.IsSuppressedInsulator) return null;
            var center = WorldPosition(node, objects);
            var height = frame.InsulatorAssemblyHeight > 0 ? frame.InsulatorAssemblyHeight : node.Height;
            return new BridgeInsulatorPose(new[] { center[0], center[1], center[2] + height / 2 }, new[] { 0d, 0d, 1d },
                height - InsulatorPreviewProfile.BracketHeight);
        }

        private IReadOnlyList<BridgePreviewSolid> BaseInsulatorSolids(BridgeInsulatorPose placement)
        {
            // 复用架内绝缘子的同一造型分支，再将局部Z轴对齐实际配合面朝外轴向。
            var template = new BridgeDesignObject("insulator-preview-template", "绝缘子", DesignObjectKind.Insulator,
                new Dictionary<DesignField, double> { [DesignField.Height] = placement.Height },
                Array.Empty<DesignField>(), "共用绝缘子预览模板");
            var transform = BaseInsulatorTransform(placement);
            var result = Solids(template, Array.Empty<BridgeDesignObject>()).Select(solid => solid.Round != null
                ? new BridgePreviewSolid(solid.Material, solid.Round.Transform(transform))
                : new BridgePreviewSolid(solid.Material, solid.Corners.Select(transform).ToArray())).ToList();
            for (var i = 0; i < placement.PadCount; i++)
                result.Add(PlateSolid("pad", 0, 0, placement.Height / 2 + (2 * i + 1.5) * placement.PadThickness,
                    InsulatorPreviewProfile.PadWidth, InsulatorPreviewProfile.PadWidth, placement.PadThickness, transform));
            return result.AsReadOnly();
        }

        private void AddCornerOutline(BridgeDesignObject node, FramePlate plate)
        {
            var facing = node.CornerPreview ?? throw new InvalidOperationException("大偏移拐角缺少通道朝向：" + node.Name);
            var length = node.DriveValue(DesignField.Length); var width = node.DriveValue(DesignField.Width);
            var height = node.DriveValue(DesignField.Height);
            const double sheet = 1.5;
            // 028模板：051.024位于上下表面，055.025位于外侧；不能套用直通架的两块侧墙。
            foreach (var top in new[] { false, true })
            {
                var z = top ? height : 0d;
                var inward = top ? -1d : 1d;
                plate(top ? "roof" : "floor", 0, 0, z + inward * sheet / 2, length, width, sheet);
                foreach (var side in new[] { -1d, 1d })
                {
                    var flange = side == facing.ClosedSide ? 46d : 35d;
                    plate("steel", 0, side * (width / 2 - sheet / 2), z + inward * flange / 2,
                        length, sheet, flange);
                    // 139/146纵向角钢，40×4；忽略孔槽和局部端部切口。
                    plate("rim", 0, side * (width / 2 - 20), z + inward * (sheet + 2), length, 40, 4);
                    plate("rim", 0, side * (width / 2 - 2), z + inward * (sheet + 22), length, 4, 36);
                }
                foreach (var end in new[] { -1d, 1d })
                {
                    // 142～145横向角钢宽W-45.5，中心朝开口侧偏18.75。
                    plate("rim", end * (length / 2 - 20), -facing.ClosedSide * 18.75,
                        z + inward * (sheet + 2), 40, width - 45.5, 4);
                    plate("rim", end * (length / 2 - 2), -facing.ClosedSide * 18.75,
                        z + inward * (sheet + 22), 4, width - 45.5, 36);
                }
            }
            plate("corner-wall", 0, facing.ClosedSide * (width / 2 - sheet / 2), height / 2,
                length, sheet, height - 92);
            foreach (var end in new[] { -1d, 1d })
                foreach (var side in new[] { -1d, 1d })
                {
                    // 028按加工配置：140切X翼，141切两翼，147不切，148切Y翼。
                    // 草图9/10保留长度(H-83)-2×33.5。两个开口分别使用自己的深度。
                    var outerSide = width / 2 - (side == facing.ClosedSide ? sheet : 0d);
                    int? outerSlot = end == facing.ClosedEnd ? (int?)null : side == facing.ClosedSide ? 3 : 2;
                    int? connectionSlot = side == facing.ClosedSide ? (int?)null : end == facing.ClosedEnd ? 0 : 1;
                    var ordinaryDepth = outerSlot.HasValue ? node.AngleNotches?.CornerDepth(outerSlot.Value) ?? 10d : 0d;
                    var connectionDepth = connectionSlot.HasValue ? node.AngleNotches?.CornerDepth(connectionSlot.Value) ?? 10d : 0d;
                    AddVerticalAngleWing(plate, end * (length / 2 - 2), side * (outerSide - 20), 4, 40,
                        41.5, height - 41.5, 75, height - 75, 0, side * ordinaryDepth, outerSlot);
                    AddVerticalAngleWing(plate, end * (length / 2 - 22), side * (outerSide - 2), 36, 4,
                        41.5, height - 41.5, 75, height - 75, end * connectionDepth, 0, connectionSlot);
                }
            // 017.229是竖向端盖，W-2/H-2、厚2、四边向架外翻至12mm，并非顶部盖板。
            plate("corner-cover", facing.ClosedEnd * (length / 2 + 1), 0, height / 2, 2, width - 2, height - 2);
            foreach (var sign in new[] { -1d, 1d })
            {
                plate("corner-cover", facing.ClosedEnd * (length / 2 + 7), sign * (width / 2 - 2),
                    height / 2, 10, 2, height - 2);
                plate("corner-cover", facing.ClosedEnd * (length / 2 + 7), 0,
                    sign < 0 ? 2 : height - 2, 10, width - 2, 2);
            }
        }

        private void AddVerticalAngleWing(FramePlate plate,
            double x, double y, double sx, double sy, double bottom, double top,
            double notchBottom, double notchTop, double cutX, double cutY, int? notchSlot)
        {
            var low = Math.Max(bottom, notchBottom); var high = Math.Min(top, notchTop);
            if (Math.Abs(cutX) + Math.Abs(cutY) < .000001 || high - low < .000001)
            {
                plate("rim", x, y, (bottom + top) / 2, sx, sy, top - bottom, notchSlot);
                return;
            }
            // 只切中央让位区，上下未切端部仍参与铜排和紧固件净距校验。
            if (low - bottom > .000001) plate("rim", x, y, (bottom + low) / 2, sx, sy, low - bottom, notchSlot);
            var remainingX = sx - Math.Abs(cutX); var remainingY = sy - Math.Abs(cutY);
            if (remainingX < -.000001 || remainingY < -.000001)
                throw new InvalidOperationException("角钢豁口深度超过翼板宽度，无法生成已核验的结构几何。");
            if (remainingX > .000001 && remainingY > .000001)
                plate("rim", x + cutX / 2, y + cutY / 2, (low + high) / 2,
                    remainingX, remainingY, high - low, notchSlot);
            if (top - high > .000001) plate("rim", x, y, (high + top) / 2, sx, sy, top - high, notchSlot);
        }

        /// <summary>024/026/027/028/029/032共用二维、三维和净距板件；保留结构翻边与豁口，省略孔槽、冲压小细节、焊缝及小圆角。</summary>
        public IReadOnlyList<BridgePreviewSolid> Solids3D(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
            => FrameSolids(node, objects, false);

        private IReadOnlyList<BridgePreviewSolid> FrameSolids(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects, bool sideMountedSection)
        {
            if (node.Kind != DesignObjectKind.Frame) return Solids(node, objects);
            if (node.FrameTemplateKey == "SideToSidePhaseChange029") return PhaseChangeFrameSolids(node, objects);
            var length = node.DriveValue(DesignField.Length); var width = node.DriveValue(DesignField.Width);
            var height = node.DriveValue(DesignField.Height);
            if (new[] { length, width, height }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new InvalidOperationException("基架缺少有限的实际驱动尺寸：" + node.Name);
            if (length <= 80 || width <= 100 || height <= 80)
                throw new InvalidOperationException("基架尺寸不足以容纳已核验的40×4角钢外形：" + node.Name);
            var center = WorldPosition(node, objects);
            var parent = objects.FirstOrDefault(o => o.Id == node.ParentId);
            var angle = (node.ModelDirection + (parent?.ModelDirection ?? 0)) * Math.PI / 180;
            Func<double[], double[]> transform = p => new[] {
                center[0] + p[0] * Math.Cos(angle) - p[1] * Math.Sin(angle),
                center[1] + p[0] * Math.Sin(angle) + p[1] * Math.Cos(angle), center[2] - height / 2 + p[2] };
            var result = new List<BridgePreviewSolid>();
            FramePlate plate = (m, x, y, z, sx, sy, sz, notchSlot) =>
            {
                var solid = PlateSolid(m == "cover-support" ? "rim" : m, x, y, z, sx, sy, sz, transform);
                solid.IsHiddenIn2D = m == "cover-support";
                solid.AngleNotchSlot = notchSlot;
                result.Add(solid);
            };
            if (node.FrameTemplateKey == "LargeOffsetCorner")
            {
                AddCornerOutline(node, plate);
                return result;
            }
            var seals = SmallOffsetSeals(node);
            AddStraightFrameStructure(node, plate, seals, sideMountedSection, length, width, height);
            AddStraightFrameCoverAndSeals(node, plate, seals, length, width, height);
            return result.AsReadOnly();
        }

        private void AddStraightFrameStructure(BridgeDesignObject node,
            FramePlate plate,
            Drivers.FaceToFaceSmallOffsetSealPlateDrivePlan seals, bool sideMountedSection, double length, double width, double height)
        {
            const double sheet = 1.5, leg = 40, angleSheet = 4;
            // 8GX.055.001底板W-92；8GX.051.022侧板H×L及46mm底翻边。
            // 主板平面均为0～1.5；029负5仅为局部冲压凸出，不能用全体顶点极值平移整块板。
            // 024/026/027/032翻边上沿35，029的055.026翻边上沿33.5。
            const double floorBottom = 0d;
            var floorLipTop = sideMountedSection ? 33.5d : 35d;
            plate("floor", 0, 0, floorBottom + sheet / 2, length, width - 92, sheet);
            foreach (var side in new[] { -1d, 1d })
                plate("floor", 0, side * (width / 2 - 46 - sheet / 2),
                    (floorBottom + sheet + floorLipTop) / 2, length - 84, sheet, floorLipTop - floorBottom - sheet);
            foreach (var side in new[] { -1d, 1d })
            {
                plate("steel", 0, side * (width / 2 - sheet / 2), height / 2, length, sheet, height);
                plate("floor", 0, side * (width / 2 - 23), sheet / 2, length, 46, sheet);
                // 上下纵向40×4角钢：两条薄板构成L形，非实心40mm方梁。
                foreach (var top in new[] { false, true })
                {
                    var z = top ? height : sheet;
                    var direction = top ? -1d : 1d;
                    plate("rim", 0, side * (width / 2 - sheet - leg / 2), z + direction * angleSheet / 2,
                        length, leg, angleSheet);
                    plate("rim", 0, side * (width / 2 - sheet - angleSheet / 2), z + direction * (leg + angleSheet) / 2,
                        length, angleSheet, leg - angleSheet);
                }
            }
            // 两端为开口边框：横角钢跨W-83，立角钢长H-45.5，不用整块端板封口。
            foreach (var end in new[] { -1d, 1d })
            {
                foreach (var top in new[] { false, true })
                {
                    var z = top ? height : sheet;
                    var direction = top ? -1d : 1d;
                    plate("rim", end * (length / 2 - leg / 2), 0, z + direction * angleSheet / 2,
                        leg, width - 83, angleSheet);
                    plate("rim", end * (length / 2 - angleSheet / 2), 0, z + direction * (leg + angleSheet) / 2,
                        angleSheet, width - 83, leg - angleSheet);
                }
                foreach (var side in new[] { -1d, 1d })
                {
                    var vertical = height - 45.5;
                    // 024/032的001/003草图4：底端留25，长度350+(H-45.5-454.5)=H-150。
                    // 032按已核验的实例位置区分001/003，宽度和豁口压缩状态与正式驱动共用计划。
                    var slot = BridgeAngleNotchPlan.Slot(end, side);
                    var anglePlan = SmallOffsetAngle(seals, slot);
                    var endAngleWidth = anglePlan?.Width ?? leg;
                    var notchDepth = VerticalAngleNotchDepth(node, anglePlan, slot);
                    var notchTop = node.FrameTemplateKey == "FaceToWallLarge2" && end < 0
                        ? (node.WallFramePreview ?? throw new InvalidOperationException("027缺少入口开孔高度。")).InletHeight - 83.5 : height - 83.5;
                    AddVerticalAngleWing(plate, end * (length / 2 - angleSheet / 2), side * (width / 2 - sheet - endAngleWidth / 2),
                        angleSheet, endAngleWidth, 41.5, height - 4, 66.5, notchTop,
                        0, side * notchDepth, slot);
                    plate("rim", end * (length / 2 - (leg + angleSheet) / 2), side * (width / 2 - sheet - angleSheet / 2),
                        41.5 + vertical / 2, leg - angleSheet, angleSheet, vertical, slot);
                }
            }
        }

        private void AddStraightFrameCoverAndSeals(BridgeDesignObject node,
            FramePlate plate,
            Drivers.FaceToFaceSmallOffsetSealPlateDrivePlan seals, double length, double width, double height)
        {
            const double leg = 40;
            // 017.222实模安装面在H，外侧翻边朝上至H+12，不能反向占用架内净空。
            if (node.FrameTemplateKey == "FaceToWallLarge1" || node.FrameTemplateKey == "FaceToWallLarge2")
            {
                AddWallFrameCoverAndSeals(node, plate);
                return;
            }
            var splitSmallOffsetCovers = node.FrameTemplateKey == "SmallOffset" &&
                Drivers.FaceToFaceSmallOffsetFramePlanner.CreateCoverPlatePlans(width, length)
                    .Any(p => p.ComponentPrefix == "8GX.017.227");
            if (!splitSmallOffsetCovers) AddUpturnedCover(plate, 0, 0, height, length - 2, width - 2);
            else foreach (var sign in new[] { -1d, 1d })
            {
                AddUpturnedCover(plate, 0, sign * width / 4, height, length - 2, width / 2 - 2);
                plate("cover-support", 0, sign * 20, height - 2, length - 80, 40, 4);
                plate("cover-support", 0, sign * 2, height - 22, length - 80, 4, 36);
            }
            if (seals != null)
            {
                // 封板与附加角钢只使用各自所在端的偏移参数。
                foreach (var seal in seals.Folders.Where(f => !f.Suppress))
                {
                    var inlet = seal.PlateComponentPrefix == "8GX.017.236";
                    var end = inlet ? -1d : 1d;
                    var side = seal.FolderName.Contains("左") ? -1d : 1d;
                    // 236/237封板厚2，留边后宽=封板驱动宽-2、高H-2；跨相边缘各留1mm。
                    plate("seal", end * (length / 2 + 1), side * (width - seal.PlateWidth) / 2,
                        height / 2 - (inlet ? 1 : 0), 2, seal.PlateWidth - 2, height - 2);
                    if (seal.SuppressAngleSteel) continue;
                    // 167附加立角钢H-10，Y=6～H-4；用两板表示40×4外形，省略端部切口。
                    var edge = width / 2 - seal.PlateWidth;
                    plate("seal-rim", end * (length / 2 - 2), side * (edge + leg / 2),
                        height / 2 + 1, 4, leg, height - 10);
                    plate("seal-rim", end * (length / 2 - (leg + 4) / 2), side * (edge + 2),
                        height / 2 + 1, leg - 4, 4, height - 10);
                }
            }
        }

        private void AddUpturnedCover(FramePlate plate,
            double x, double y, double top, double length, double width)
        {
            plate("cover", x, y, top + 1, length, width, 2);
            foreach (var sign in new[] { -1d, 1d })
            {
                plate("cover", x + sign * (length / 2 - 1), y, top + 7, 2, width, 10);
                plate("cover", x, y + sign * (width / 2 - 1), top + 7, length, 2, 10);
            }
        }

        internal static Drivers.FaceToFaceSmallOffsetSealPlateDrivePlan SmallOffsetSeals(BridgeDesignObject frame)
        {
            if (frame.FrameTemplateKey != "SmallOffset") return null;
            var offset = frame.SmallOffsetPreview ?? throw new InvalidOperationException("小偏移基架缺少进出孔参数：" + frame.Name);
            return Drivers.FaceToFaceSmallOffsetSealPlatePlanner.CreatePlan(
                new Drivers.FaceToFaceSmallOffsetFrameDrivePlan(frame.DriveValue(DesignField.Width), offset.InletDirection,
                    offset.InletSize, offset.OutletDirection, offset.OutletSize), frame.DriveValue(DesignField.Height));
        }

        internal static Drivers.FaceToFaceSmallOffsetAnglePlan SmallOffsetAngle(
            Drivers.FaceToFaceSmallOffsetSealPlateDrivePlan plan, int slot)
        {
            if (plan == null) return null;
            // 032：进左/出右是003，进右/出左是001。相同型号的进出实例也独立驱动。
            var end = slot < 2 ? -1 : 1;
            var prefix = slot == 0 || slot == 3 ? "8GX.160.003" : "8GX.160.001";
            return plan.Angles.SingleOrDefault(item => item.ComponentPrefix == prefix && item.End == end);
        }

        private double VerticalAngleNotchDepth(BridgeDesignObject frame, Drivers.FaceToFaceSmallOffsetAnglePlan angle, int slot)
        {
            if (frame.AngleNotches?.InstanceDepthsMm != null) return frame.AngleNotches.InstanceDepthsMm[slot];
            if (angle != null)
                return angle.SuppressNotch ? 0d : angle.NotchWidth ??
                    throw new InvalidOperationException("小偏移角钢缺少豁口宽度：" + angle.ComponentPrefix);
            return HasTemplateAngleNotches(frame) ? frame.AngleNotches?.StraightDepth(slot) ?? 22d : 0d;
        }

        internal static bool HasTemplateAngleNotches(BridgeDesignObject frame) => frame.Kind == DesignObjectKind.Frame &&
            (frame.FrameTemplateKey == "Regular" || frame.FrameTemplateKey == "SmallOffset" || frame.FrameTemplateKey == "SideToSidePhaseChange029" ||
                frame.FrameTemplateKey == "FaceToWallLarge1" || frame.FrameTemplateKey == "FaceToWallLarge2");

        internal static string[] TemplateAngleComponentPrefixes(string templateKey) =>
            templateKey == "FaceToWallLarge1" ? new[] { "8GX.160.130", "8GX.160.129", "8GX.160.129", "8GX.160.130" } :
            templateKey == "FaceToWallLarge2" ? new[] { "8GX.160.136", "8GX.160.137", "8GX.160.129", "8GX.160.130" } :
            templateKey == "LargeOffsetCorner" ? new[] { "8GX.160.140", "8GX.160.147", "8GX.160.141", "8GX.160.148" } :
            new[] { "8GX.160.003", "8GX.160.001", "8GX.160.001", "8GX.160.003" };

        private void AddWallFrameCoverAndSeals(BridgeDesignObject node, FramePlate plate)
        {
            var profile = node.WallFramePreview ?? throw new InvalidOperationException("026/027缺少已核验的孔口参数。");
            var l = node.DriveValue(DesignField.Length); var w = node.DriveValue(DesignField.Width); var h = node.DriveValue(DesignField.Height);
            if (l < 300 || w < 100 || h < 80 || node.IsReversed)
                throw new InvalidOperationException("面对墙大基架长度至少300mm，且只支持正装。");
            // 026/027实模：017.224短顶板198，017.227两块分盖L-202×(W/2-2)。
            const double strip = 198, sheet = 2;
            // 017.224短板是平板；只有017.227长盖有向上的翻边。
            plate("cover", l / 2 - 100, 0, h + 1, strip, w - 2, sheet);
            foreach (var side in new[] { -1d, 1d }) AddUpturnedCover(plate, -100, side * w / 4, h, l - 202, w / 2 - 2);
            // 两片长盖中心接缝的40×4角钢，长度L-198-2-44；短盖内缘的横向支承W-12。
            foreach (var side in new[] { -1d, 1d })
            {
                // 160.134/135在模板总装X=-81mm，孔口链名义中心X=1mm，因此局部中心为-82mm。
                plate("cover-support", -82, side * 20, h - 2, l - 244, 40, 4);
                plate("cover-support", -82, side * 2, h - 22, l - 244, 4, 36);
                // 两根133跨完整W-12，在接缝两侧各伸40，不能当成左右半宽的平板。
                plate("cover-support", l / 2 - 200 + side * 20, -.5, h - 2, 40, w - 12, 4);
                plate("cover-support", l / 2 - 200 + side * 2, -.5, h - 22, 4, w - 12, 36);
            }
            if (node.FrameTemplateKey == "FaceToWallLarge1")
            {
                // 与正式026驱动同一封板公式；入口偏心相对墙端中心，不能变成028转角。
                var widths = new[] { (w - profile.InletWidth) / 2 + profile.InletOffset,
                    (w - profile.InletWidth) / 2 - profile.InletOffset };
                for (var i = 0; i < 2; i++)
                {
                    var size = widths[i]; var sign = i == 0 ? -1d : 1d;
                    if (size < 2) throw new InvalidOperationException("026入口封板不足2mm，请回柜体驱动调整孔口宽度或偏心后重读。");
                    plate("seal", -l / 2 - 1, sign * (w - size) / 2, h / 2, 2, size - 2, h - 2);
                    // 160.132封板开口竖角钢H-10。
                    plate("seal-rim", -l / 2 + 2, sign * (w / 2 - size + 5), h / 2 + 1, 4, 40, h - 10);
                    plate("seal-rim", -l / 2 + 22, sign * (w / 2 - size - 13), h / 2 + 1, 36, 4, h - 10);
                }
            }
            else
            {
                // 027入口与出口共底面，017.228仅封入口上方，封板高H-基座开孔高。
                var sealHeight = h - profile.InletHeight;
                if (sealHeight < -.01) throw new InvalidOperationException("027入口开孔高超过基架高。");
                if (sealHeight > .01) plate("seal", -l / 2 - 1, 0, h - sealHeight / 2, 2, w, sealHeight);
                // 027入口上沿有138/131背靠背横角钢，均须参与入口铜排净距。
                foreach (var sign in new[] { -1d, 1d })
                {
                    plate("rim", -l / 2 + 20, 0, profile.InletHeight + sign * 2, 40, w - 83, 4);
                    plate("rim", -l / 2 + 2, 0, profile.InletHeight + sign * 22, 4, w - 83, 36);
                }
            }
        }

        private BridgeInsulatorPose HangingInsulatorPose(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (node.MountPlan == null || node.IsSuppressedInsulator) return null;
            var frame = objects.Single(o => o.Id == node.ParentId);
            var plan = node.MountPlan; var index = node.MountGroupIndex;
            var angle = frame.ModelDirection * Math.PI / 180;
            var y = plan.Transverse[index]; var h = plan.BodyHeights[index];
            var floor = frame.Z - frame.DriveValue(DesignField.Height) / 2 + FrameFloorThicknessMm;
            var point = new[] { frame.X + node.X * Math.Cos(angle) - y * Math.Sin(angle),
                frame.Y + node.X * Math.Sin(angle) + y * Math.Cos(angle),
                plan.IsFloorStanding ? floor + InsulatorPreviewProfile.BracketHeight + h :
                frame.Z + frame.DriveValue(DesignField.Height) / 2 - BridgePhaseChangeSupportPlanner.CeilingInsetMm - InsulatorPreviewProfile.BracketHeight - h };
            return new BridgeInsulatorPose(point, new[] { 0d, 0d, plan.IsFloorStanding ? 1d : -1d }, h,
                frame.InsulatorPadCount + 1, frame.InsulatorPadThickness,
                new[] { -Math.Sin(angle), Math.Cos(angle), 0d },
                index == Enumerable.Range(0, 3).First(i => plan.BodyHeights[i] > 0) ? frame.DriveValue(DesignField.Width) - 20 : 0);
        }

        private BridgeInsulatorPose PhaseChangeInsulatorPose(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (node.Kind != DesignObjectKind.Insulator) return null;
            var frame = objects.SingleOrDefault(o => o.Id == node.ParentId && o.Kind == DesignObjectKind.Frame);
            if (frame?.FrameTemplateKey != "SideToSidePhaseChange029") return null;
            var center = WorldPosition(node, objects);
            var angle = frame.ModelDirection * Math.PI / 180d;
            var axis = new[] { -Math.Sin(angle), Math.Cos(angle), 0d };
            var members = objects.Where(o => o.ParentId == frame.Id && o.InsulatorGroupId == node.InsulatorGroupId)
                .OrderBy(o => o.Z).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
            var owner = members.FirstOrDefault(o => o.Phase == "B") ?? members[members.Length / 2];
            var height = RestoreRoundedDimension(node.Height, frame.InsulatorAssemblyHeight);
            return new BridgeInsulatorPose(Enumerable.Range(0, 3).Select(i => center[i] + axis[i] * height / 2).ToArray(),
                axis, frame.InsulatorAssemblyHeight - InsulatorPreviewProfile.BracketHeight,
                frame.InsulatorPadCount + 1, frame.InsulatorPadThickness, new[] { 0d, 0d, 1d },
                owner.Id == node.Id ? frame.Height - 20d : 0d);
        }

        private IReadOnlyList<BridgePreviewSolid> PhaseChangeFrameSolids(BridgeDesignObject frame, IReadOnlyList<BridgeDesignObject> objects)
        {
            // 029截面侧向安装：支架跨高度方向，本体轴沿横向；不能复用024落底姿态。
            var section = new BridgeDesignObject("029-section", "029截面", DesignObjectKind.Frame,
                new Dictionary<DesignField, double> { [DesignField.Length] = frame.DriveValue(DesignField.Length),
                    [DesignField.Width] = frame.DriveValue(DesignField.Height), [DesignField.Height] = frame.DriveValue(DesignField.Width) },
                Array.Empty<DesignField>(), "029已核验侧向截面").WithAngleNotches(frame.AngleNotches);
            var center = WorldPosition(frame, objects);
            var angle = frame.ModelDirection * Math.PI / 180d;
            // 029 装到侧对侧总装后，模板局部 +Z 朝下，模板豁口位置随这个变换转到上下两侧。
            Func<double[], double[]> transform = p => new[] {
                center[0] + p[0] * Math.Cos(angle) - p[2] * Math.Sin(angle),
                center[1] + p[0] * Math.Sin(angle) + p[2] * Math.Cos(angle), center[2] - p[1] };
            return FrameSolids(section, Array.Empty<BridgeDesignObject>(), true).Select(s =>
                new BridgePreviewSolid(s.Material, s.Corners.Select(transform).ToArray()) { AngleNotchSlot = s.AngleNotchSlot }).ToArray();
        }

        private Func<double[], double[]> BaseInsulatorTransform(BridgeInsulatorPose placement)
        {
            var axis = placement.Axis.ToArray();
            var reference = Math.Abs(axis[2]) < .9d ? new[] { 0d, 0d, 1d } : new[] { 0d, 1d, 0d };
            Func<double[], double[], double[]> cross = (a, b) => new[]
            {
                a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]
            };
            var x = cross(reference, axis);
            var length = Math.Sqrt(x.Sum(v => v * v));
            x = x.Select(v => v / length).ToArray();
            var y = cross(axis, x);
            if (placement.SpanAxis != null)
            {
                y = placement.SpanAxis.ToArray();
                x = cross(y, axis);
                var norm = Math.Sqrt(x.Sum(v => v * v));
                x = x.Select(v => v / norm).ToArray();
                y = cross(axis, x);
            }
            return p => Enumerable.Range(0, 3).Select(i =>
                    placement.Connection[i] + x[i] * p[0] + y[i] * p[1] +
                    axis[i] * (p[2] - placement.Height / 2d)).ToArray();
        }

        /// <summary>每套仅由中间绝缘子承载一份横向支架图形，不新增业务对象或数量。</summary>
        public IReadOnlyList<BridgePreviewSolid> InsulatorBracketSolids(BridgeDesignObject node, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (node.IsSuppressedInsulator) return Array.Empty<BridgePreviewSolid>();
            var placement = HangingInsulatorPose(node, objects) ?? node.InsulatorPlacement ?? PhaseChangeInsulatorPose(node, objects);
            if (placement?.BracketSpan > 0)
            {
                // 基座三相实际配合圆确定跨相方向，支架长度来自原宿主驱动变量。
                var transform = BaseInsulatorTransform(placement);
                return BracketSolids(placement.BracketSpan, p => transform(new[] { p[0],
                    p[1] - (node.MountPlan?.Transverse[node.MountGroupIndex] ?? 0),
                    p[2] - placement.Height / 2 - InsulatorPreviewProfile.BracketHeight }));
            }
            if (placement != null) return Array.Empty<BridgePreviewSolid>();
            var frame = objects.FirstOrDefault(o => o.Id == node.ParentId && o.Kind == DesignObjectKind.Frame);
            if (!node.IsInsulatorGroup || frame == null) return Array.Empty<BridgePreviewSolid>();
            var members = objects.Where(o => o.ParentId == frame.Id && o.IsInsulatorGroup && o.InsulatorGroupId == node.InsulatorGroupId)
                .OrderBy(o => o.Y).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
            var owner = members.FirstOrDefault(o => o.Phase == "B") ?? members[members.Length / 2];
            if (owner.Id != node.Id) return Array.Empty<BridgePreviewSolid>();
            var center = WorldPosition(frame, objects);
            var angle = frame.ModelDirection * Math.PI / 180;
            var x = members.Average(o => o.X); var y = frame.UseOffsetInsulators ? 0d : members.Average(o => o.Y);
            var span = frame.Width - 20; // 与InsulatorDriver支架长度驱动公式一致。
            if (span < InsulatorWidthMm) throw new InvalidOperationException("基架宽度不足以安装绝缘子折边支架。");
            var frameHeight = frame.Height > 0d ? frame.Height : FrameHeightMm;
            var floorTop = -frameHeight / 2 + FrameFloorThicknessMm;
            return BracketSolids(span, p => new[] { center[0] + (x + p[0]) * Math.Cos(angle) - (y + p[1]) * Math.Sin(angle),
                center[1] + (x + p[0]) * Math.Sin(angle) + (y + p[1]) * Math.Cos(angle), center[2] + floorTop + p[2] });
        }

        private BridgePreviewSolid PlateSolid(string material, double cx, double cy, double cz,
            double sx, double sy, double sz, Func<double[], double[]> transform)
        {
            var corners = new List<double[]>();
            foreach (var dz in new[] { -1d, 1d })
            foreach (var sign in new[] { new[] { -1d, -1d }, new[] { 1d, -1d }, new[] { 1d, 1d }, new[] { -1d, 1d } })
                corners.Add(transform(new[] { cx + sign[0] * sx / 2, cy + sign[1] * sy / 2, cz + dz * sz / 2 }));
            return new BridgePreviewSolid(material, corners.ToArray());
        }

        private IReadOnlyList<BridgePreviewSolid> BracketSolids(double span, Func<double[], double[]> transform)
        {
            var solids = new List<BridgePreviewSolid>();
            Action<double, double, double, double, double, double> plate = (cx, cy, cz, sx, sy, sz) =>
                solids.Add(PlateSolid("bracket", cx, cy, cz, sx, sy, sz, transform));
            const double sheet = InsulatorPreviewProfile.BracketSheetThickness;
            var h = DemoInsulatorBracketThicknessMm;
            plate(0, 0, h - sheet / 2, InsulatorWidthMm, span, sheet);
            foreach (var sign in new[] { -1d, 1d })
            {
                plate(sign * (InsulatorWidthMm - sheet) / 2, 0, (h - sheet) / 2, sheet, span, h - sheet);
                plate(0, sign * (span - sheet) / 2, (h - sheet) / 2, InsulatorWidthMm - 2 * sheet, sheet, h - sheet);
            }
            return solids.AsReadOnly();
        }

        public WebPreviewTriangleMeshDto SolidMesh(IReadOnlyList<BridgePreviewSolid> solids)
        {
            var vertices = new List<double>(); var indices = new List<int>();
            foreach (var solid in solids)
            {
                if (solid.Round == null) AppendBox(vertices, indices, solid.Corners);
                else
                {
                    var start = vertices.Count / 3;
                    vertices.AddRange(solid.Corners.SelectMany(p => p));
                    indices.AddRange(_supportGeometry.TriangleIndices().Select(i => start + i));
                }
            }
            return new WebPreviewTriangleMeshDto { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
        }

        public WebPreviewTriangleMeshDto RouteMesh(double[][] points, double width, double thickness)
        {
            var vertices = new List<double>(); var indices = new List<int>();
            for (var i = 1; i < points.Length; i++)
            {
                var a = points[i - 1]; var b = points[i];
                var dx = b[0] - a[0]; var dy = b[1] - a[1]; var dz = b[2] - a[2];
                var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (length < .01) continue;
                var xy = Math.Sqrt(dx * dx + dy * dy);
                var u = xy > .01 ? new[] { -dy / xy, dx / xy, 0d } : new[] { 0d, 1d, 0d };
                var v = new[] { -dz * u[1] / length, dz * u[0] / length, (dx * u[1] - dy * u[0]) / length };
                var corners = new List<double[]>();
                foreach (var end in new[] { a, b })
                foreach (var st in new[] { new[] { -1d, -1d }, new[] { 1d, -1d }, new[] { 1d, 1d }, new[] { -1d, 1d } })
                    corners.Add(Enumerable.Range(0, 3).Select(k => end[k] + st[0] * u[k] * width / 2 + st[1] * v[k] * thickness / 2).ToArray());
                AppendBox(vertices, indices, corners.ToArray());
            }
            return new WebPreviewTriangleMeshDto { Vertices = vertices.ToArray(), Indices = indices.ToArray() };
        }

        private void AppendBox(List<double> vertices, List<int> indices, double[][] corners)
        {
            var start = vertices.Count / 3;
            vertices.AddRange(corners.SelectMany(p => p));
            indices.AddRange(new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 }.Select(i => i + start));
        }

        public WebPreviewTriangleMeshDto ModelPartMesh(BridgeDesignObject node)
        {
            if (node.ModelMesh == null) throw new ArgumentException("对象缺少原模型显示网格。", nameof(node));
            // 总装XYZ转换为预览XZY后反转绕序，保持表面朝外。
            return new WebPreviewTriangleMeshDto
            {
                Vertices = node.ModelMesh.Vertices.ToArray(),
                Indices = Enumerable.Range(0, node.ModelMesh.Vertices.Count / 9)
                    .SelectMany(i => new[] { i * 3, i * 3 + 2, i * 3 + 1 }).ToArray()
            };
        }

        public WebPreviewSceneDto Create(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects, long revision)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            var scene = new WebPreviewSceneDto
            {
                SceneId = (sample.Source.IsModelDerived ? "planned/" : "prototype/") + sample.Id + "/" + revision.ToString(CultureInfo.InvariantCulture),
                Title = "母线桥设计 · " + (sample.Source.IsModelDerived ? "模型方案" : "演示数据") + " · 只读三维 · 版本 " + revision,
                ShowCenterLines = true
            };
            scene.Metadata["mode"] = sample.Source.IsModelDerived ? "PlannedPreview" : "Prototype";
            scene.Metadata["revision"] = revision.ToString(CultureInfo.InvariantCulture);
            scene.Metadata["coordinateSystem"] = "mm; right-handed; X=span Y=lateral Z=up";
            scene.Metadata["routeDirection"] = objects.Any(n => n.Id == "base-b") ? "B cabinet (left) -> A cabinet (right)" : "A cabinet -> wall";
            scene.Metadata["warning"] = sample.Source.IsModelDerived
                ? "规划结果预览，尚未写入SolidWorks；正式生成前仍需完成工程可行性联合校验。"
                : "草稿示意，非制造几何，工程可行性未校验。搭接长度为独立暂定参数；桥头其余尺寸、方向及孔位待确认。";
            scene.Metadata["overlapLengthMm"] = sample.Parameters.OverlapLengthMm.ToString("0.###", CultureInfo.InvariantCulture);
            foreach (var route in sample.Routes)
            {
                var points = route.PointIds.Select(id => objects.FirstOrDefault(o => o.Id == id)).Where(o => o != null).ToArray();
                var world = points.Select(p => WorldPosition(p, objects)).ToArray();
                var segments = CopperSegments(route, objects);
                var meshes = route.PhysicalSolids.Count > 0
                    ? new[] { SolidMesh(route.PhysicalSolids.Select(s => new BridgePreviewSolid("copper", s)).ToArray()) }
                    : segments.Select(s => RouteMesh(CopperSegmentPath(s, objects, sample.Source.BusbarWidth, sample.Source.BusbarThickness, sample.Parameters.OverlapLengthMm),
                    s.IsPhysicalGeometry ? s.SectionWidth : sample.Source.BusbarWidth,
                    s.IsPhysicalGeometry ? s.SectionThickness : sample.Source.BusbarThickness)).ToArray();
                var vertices = new List<double>(); var indices = new List<int>();
                foreach (var mesh in meshes)
                {
                    var vertexOffset = vertices.Count / 3;
                    vertices.AddRange(mesh.Vertices); indices.AddRange(mesh.Indices.Select(i => i + vertexOffset));
                }
                foreach (var segment in segments)
                {
                    scene.Metadata[segment.Id + "/heads"] = segment.StartHead + " -> " + segment.EndHead;
                    if (segment.StraightLapAtStart) scene.Metadata[segment.Id + "/startJoint"] = "StraightToStraight";
                }
                // 矩形截面只用于外观；转折、搭接不代表真实成形/孔位计算。
                scene.Objects.Add(new WebPreviewObjectDto
                {
                    ObjectId = route.Id, DisplayName = route.Phase + "相铜排（示意）", ObjectKind = WebPreviewObjectKinds.Mesh,
                    Color = new WebPreviewColorDto(155, 83, 59), BusinessCategory = "PrototypeRoute",
                    Mesh = new WebPreviewTriangleMeshDto { Vertices = vertices.ToArray(), Indices = indices.ToArray() },
                    Polyline = new WebPreviewPolylineDto { Points = world.Select(p => new WebPreviewCoordinateDto(p[0], p[1], p[2])).ToList(), Color = Color(route.Phase), LineWidth = 1 }
                });
            }
            foreach (var node in objects)
            {
                if (node.IsSuppressedInsulator) continue;
                // 路径编辑点只在二维用句柄表达，不能生成独立搭接板或断点实体。
                if (node.Kind == DesignObjectKind.Lap || node.Kind == DesignObjectKind.Breakpoint || node.Kind == DesignObjectKind.ControlPoint ||
                    node.Kind == DesignObjectKind.FixedJoint || node.Kind == DesignObjectKind.FixedRoutePoint) continue;
                var dto = new WebPreviewObjectDto
                {
                    ObjectId = node.Id, ParentObjectId = node.ParentId, DisplayName = node.DisplayName + " · " + node.Role,
                    BusinessCategory = node.Kind.ToString(), Color = Color(node.Phase), ObjectKind = WebPreviewObjectKinds.Mesh,
                    Mesh = node.ModelMesh != null ? ModelPartMesh(node) : SolidMesh(Solids3D(node, objects))
                };
                if (node.Kind == DesignObjectKind.Frame || node.Kind == DesignObjectKind.Base)
                {
                    dto.Color = new WebPreviewColorDto(107, 105, 100);
                    if (node.Kind == DesignObjectKind.Frame)
                    {
                        scene.Metadata[node.Id + "/sourceName"] = node.SourceName;
                        scene.Metadata[node.Id + "/position"] = node.PositionLabel;
                        scene.Metadata[node.Id + "/insulatorAssemblyCount"] = node.Value(DesignField.InsulatorCount).ToString(CultureInfo.InvariantCulture);
                    }
                }
                else
                {
                    if (node.Kind == DesignObjectKind.Cabinet) dto.Color = new WebPreviewColorDto(192, 195, 198);
                    if (node.Kind == DesignObjectKind.Wall) dto.Color = new WebPreviewColorDto(130, 134, 140);
                    if (node.Kind == DesignObjectKind.WallBushing) dto.Color = new WebPreviewColorDto(241, 239, 229);
                    if (node.Kind == DesignObjectKind.Insulator || node.Kind == DesignObjectKind.FixedInsulator) dto.Color = new WebPreviewColorDto(230, 229, 222);
                    if (node.Kind == DesignObjectKind.Insulator) scene.Metadata[node.Id + "/insulatorAssemblyId"] = node.InsulatorGroupId;
                    if (node.Kind == DesignObjectKind.Lap) dto.Color = new WebPreviewColorDto(169, 93, 66);
                    if (node.Kind == DesignObjectKind.FixedBusbar) dto.Color = new WebPreviewColorDto(155, 83, 59);
                }
                if (sample.Source.IsModelDerived && node.Kind == DesignObjectKind.Cabinet) dto.Opacity = .18d;
                if (node.Kind == DesignObjectKind.Wall) dto.Opacity = .25d;
                if (sample.Source.IsModelDerived && node.Kind == DesignObjectKind.Base) dto.Opacity = .35d;
                if (node.Kind == DesignObjectKind.Frame || node.Kind == DesignObjectKind.Insulator || node.Kind == DesignObjectKind.FixedInsulator)
                    dto.Opacity = 1 - DraftSupportSolid.PreviewTransparency;
                scene.Objects.Add(dto);
                var bracket = InsulatorBracketSolids(node, objects);
                if (bracket.Count > 0)
                {
                    var bracketGroup = node.InsulatorPlacement != null ? node.ParentId + "/insulators" : node.InsulatorGroupId;
                    var bracketId = bracketGroup + "/bracket";
                    scene.Objects.Add(new WebPreviewObjectDto
                    {
                        ObjectId = bracketId, ParentObjectId = node.Id, DisplayName = node.DisplayName + " · 配套横向支架（示意）",
                        BusinessCategory = "InsulatorBracket", Color = new WebPreviewColorDto(145, 146, 142),
                        ObjectKind = WebPreviewObjectKinds.Mesh, Mesh = SolidMesh(bracket), Opacity = 1 - DraftSupportSolid.PreviewTransparency
                    });
                    scene.Metadata[bracketId + "/insulatorAssemblyId"] = bracketGroup;
                }
            }
            return scene;
        }

        private WebPreviewColorDto Color(string phase)
        {
            if (phase == "A") return new WebPreviewColorDto(196, 145, 22);
            if (phase == "B") return new WebPreviewColorDto(39, 145, 103);
            if (phase == "C") return new WebPreviewColorDto(200, 67, 70);
            return new WebPreviewColorDto(94, 117, 143);
        }
    }
}
