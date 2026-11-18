using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    // 中间搭接排拥有支撑位置；支架仍使用基架原落底坐标系，只有一个模板组启用。
    internal sealed class BridgeSmallOffsetSupportPlanner
    {
        private const double Tolerance = 1.0;
        internal static bool IsSupport(BridgeDesignObject item) => item?.MountPlan?.IsFloorStanding == true;
        internal static bool IsSupportFrame(BridgeDesignObject frame) => frame?.FrameTemplateKey == "SmallOffset" || BridgeLargeOffsetSupportPlanner.IsConnector(frame);

        internal BridgeDesignObject[] Apply(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (sample?.Coordinates == null || !sample.Source.IsModelDerived ||
                (sample.ScenarioKey != "FaceToFace" && sample.ScenarioKey != "SideToSideBase2"))
                return objects.ToArray();
            var frames = objects.Where(o => o.Kind == DesignObjectKind.Frame && o.FrameTemplateKey == "SmallOffset").ToArray();
            if (frames.Length == 0) return new BridgeLargeOffsetSupportPlanner().Apply(sample, objects);
            var nodes = objects.ToDictionary(o => o.Id);
            var geometry = new BusbarBridgeDraftPreviewSceneFactory();
            var middle = sample.Routes.Where(r => r.IsPhysicalGeometry &&
                r.CopyUnraisedNativePreview()?.GroupKey?.EndsWith("-中间", StringComparison.Ordinal) == true)
                .OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
            var result = objects.ToList();
            foreach (var frame in frames)
            {
                var angle = frame.ModelDirection * Math.PI / 180; var c = Math.Cos(angle); var s = Math.Sin(angle);
                Func<double[], double[]> local = p => new[] { (p[0] - frame.X) * c + (p[1] - frame.Y) * s,
                    -(p[0] - frame.X) * s + (p[1] - frame.Y) * c, p[2] - frame.Z };
                var candidates = middle.Select(r => new { Route = r, Native = r.CopyNativePreview(),
                    Points = r.PointIds.Select(id => local(geometry.WorldPosition(nodes[id], objects))).ToArray() })
                    .Where(m => m.Points.Length >= 2 && m.Points.All(p => Math.Abs(p[0]) <= frame.Length / 2 + Tolerance)).ToArray();
                if (candidates.Length == 0)
                {
                    if (objects.Any(o => o.ParentId == frame.Id && IsSupport(o)))
                        throw new InvalidOperationException(frame.Name + "的中间偏移搭接排缺失或已移出所属基架，不能保留旧支撑位置。");
                    continue;
                }
                if (new[] { "A", "B", "C" }.Any(p => candidates.Count(m => m.Route.Phase == p) != frame.InsulatorPadCount + 1))
                    throw new InvalidOperationException(frame.Name + "的三相中间偏移搭接排缺失或重复。");
                var transverse = candidates.GroupBy(m => m.Route.Phase).Select(g => new { Phase = g.Key,
                    Y = (g.First().Points[0][1] + g.First().Points[1][1]) / 2 }).OrderByDescending(p => p.Y).ToArray();
                var supports = new List<BridgeDesignObject>();
                var ordinal = 0;
                foreach (var phase in new[] { "A", "B", "C" }.OrderBy(p => candidates.Where(m => m.Route.Phase == p)
                    .Select(m => m.Points[0][0]).DefaultIfEmpty(double.PositiveInfinity).Min()))
                {
                    var members = candidates.Where(m => m.Route.Phase == phase).ToArray();
                    if (members.Length != frame.InsulatorPadCount + 1 || members.Any(m => m.Points.Length != 2 ||
                        Math.Abs(m.Points[0][0] - m.Points[1][0]) > Tolerance ||
                        Math.Abs(m.Points[0][2] - m.Points[1][2]) > Tolerance))
                        throw new InvalidOperationException(frame.Name + "的" + phase + "相中间偏移搭接排缺失、重复或不是可承托的横向直排。");
                    var centers = members.Select(m => new[] { (m.Points[0][0] + m.Points[1][0]) / 2,
                        (m.Points[0][1] + m.Points[1][1]) / 2, m.Points[0][2] - m.Route.SectionThickness / 2 }).ToArray();
                    if (centers.Any(p => Math.Abs(p[0] - centers[0][0]) > Tolerance || Math.Abs(p[1] - centers[0][1]) > Tolerance) ||
                        members.Any(m => !m.Native.IsCenter))
                        throw new InvalidOperationException(frame.Name + "的" + phase + "相中间叠排缺少一致的实际中心线。");
                    var levels = centers.Select(p => p[2]).OrderBy(z => z).ToArray();
                    if (levels.Skip(1).Where((z, i) => Math.Abs(z - levels[i] - 2 * frame.InsulatorPadThickness) > Tolerance).Any())
                        throw new InvalidOperationException(frame.Name + "的" + phase + "相中间叠排间距与原垫块排厚不一致。");
                    var floor = -frame.DriveValue(DesignField.Height) / 2 + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm;
                    var body = centers.Min(p => p[2]) - floor - InsulatorPreviewProfile.BracketHeight;
                    var x = centers[0][0]; var y = centers[0][1];
                    if (body <= BridgeInsulatorMountPlan.MinimumBodyHeightMm || Math.Abs(y) + InsulatorPreviewProfile.Diameter / 2 > frame.Width / 2 - 20 ||
                        Math.Abs(x) + InsulatorPreviewProfile.Diameter / 2 > frame.Length / 2 - 20)
                        throw new InvalidOperationException(frame.Name + "的" + phase + "相搭接排中心没有足够的绝缘子安装空间。");
                    // 原模板三组的距离配合保持三相实际排列，只启用本相所在组，不能把不同组驱动到重合位置。
                    var active = Array.FindIndex(transverse, p => p.Phase == phase);
                    var heights = new double[3]; heights[active] = body;
                    var phases = new string[3]; phases[active] = phase;
                    var mount = new BridgeInsulatorMountPlan(transverse.Select(p => p.Y), heights,
                        phases, frame.InsulatorAssemblyHeight, true);
                    for (var slot = 0; slot < 3; slot++)
                    {
                        var child = new PrototypeBusbarBridgeDesignProvider().CreateInsulator(frame, ++ordinal, mount.Transverse[slot]);
                        var values = child.Values.ToDictionary(p => p.Key, p => p.Value);
                        values[DesignField.X] = x;
                        child = child.WithValues(values).WithMountPlan(mount, slot);
                        supports.Add(frame.FramePositionIndex > 0 ? child.WithFramePosition(frame.FramePositionIndex) : child);
                    }
                }
                result.RemoveAll(o => o.ParentId == frame.Id && o.Kind == DesignObjectKind.Insulator);
                var driven = frame.Values.ToDictionary(p => p.Key, p => p.Value);
                driven[DesignField.InsulatorCount] = 3;
                result[result.FindIndex(o => o.Id == frame.Id)] = frame.WithValues(driven);
                result.AddRange(supports);
            }
            return new BridgeLargeOffsetSupportPlanner().Apply(sample, result);
        }

        internal IReadOnlyList<BridgeDesignIssue> Validate(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            var expected = Apply(sample, objects);
            var issues = new List<BridgeDesignIssue>();
            var geometry = new BusbarBridgeDraftPreviewSceneFactory();
            foreach (var frame in expected.Where(o => o.Kind == DesignObjectKind.Frame))
            {
                var required = expected.Where(o => o.ParentId == frame.Id && IsSupport(o) && !o.IsSuppressedInsulator).ToArray();
                if (required.Length == 0) continue;
                var actual = objects.Where(o => o.ParentId == frame.Id && o.Kind == DesignObjectKind.Insulator && !o.IsSuppressedInsulator).ToArray();
                if (actual.Length != 3 || actual.Any(o => !IsSupport(o)) ||
                    actual.Select(o => o.InsulatorGroupId).Distinct().Count() != 3)
                {
                    issues.Add(new BridgeDesignIssue(frame.Name + "的偏移搭接需要三组支架各安装一个绝缘子，请重新规划支撑。", frame.Id, "偏移单相支撑"));
                    continue;
                }
                foreach (var target in required)
                {
                    var phase = target.MountPlan.Phases[target.MountGroupIndex];
                    var matches = actual.Where(o => o.MountPlan.Phases[o.MountGroupIndex] == phase).ToArray();
                    var position = geometry.WorldPosition(target, expected);
                    if (matches.Length != 1 || Math.Sqrt(geometry.WorldPosition(matches[0], objects)
                        .Select((v, i) => (v - position[i]) * (v - position[i])).Sum()) > Tolerance ||
                        Math.Abs(matches[0].MountPlan.BodyHeights[matches[0].MountGroupIndex] - target.MountPlan.BodyHeights[target.MountGroupIndex]) > Tolerance)
                        issues.Add(new BridgeDesignIssue(frame.Name + "的" + phase + "相绝缘子未对准中间偏移搭接排中心或承托面。", frame.Id, "偏移单相支撑"));
                }
            }
            return issues;
        }
    }
}
