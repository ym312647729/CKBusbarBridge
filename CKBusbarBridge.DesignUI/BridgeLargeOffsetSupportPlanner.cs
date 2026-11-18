using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Prototype
{
    // 原大偏移按物理连接架分别装三相支撑；相位错开量取实际中间排，不按相名固定分配。
    internal sealed class BridgeLargeOffsetSupportPlanner
    {
        private const double GeometryToleranceMm = 1d;
        internal static bool IsConnector(BridgeDesignObject frame) => frame?.Kind == DesignObjectKind.Frame &&
            frame.FrameTemplateKey == "Regular" && frame.Role.Contains("大偏移");
        private sealed class Carrier
        {
            internal string Phase;
            internal double Minimum, Maximum, Bottom;
        }
        private sealed class CopperRoute
        {
            internal BridgeDesignRoute Route;
            internal double[][] Points;
        }
        private sealed class PhaseAnchor
        {
            internal string Phase;
            internal double Transverse, Reference;
        }
        private static bool HasExpectedStackSpacing(IEnumerable<Carrier> members, double padThickness)
        {
            var levels = members.Select(member => member.Bottom).OrderBy(level => level).ToArray();
            return !levels.Skip(1).Where((level, index) => Math.Abs(level - levels[index] - 2d * padThickness) > GeometryToleranceMm).Any();
        }

        internal BridgeDesignObject[] Apply(BridgeDesignSample sample, IReadOnlyList<BridgeDesignObject> objects)
        {
            if (sample?.Coordinates == null || !sample.Source.IsModelDerived ||
                (sample.ScenarioKey != "FaceToFace" && sample.ScenarioKey != "SideToSideBase2")) return objects.ToArray();
            var connectors = objects.Where(IsConnector).ToArray();
            if (connectors.Length == 0) return objects.ToArray();
            var geometry = new BusbarBridgeDraftPreviewSceneFactory();
            var nodes = objects.ToDictionary(node => node.Id);
            var result = objects.ToList();
            foreach (var frame in connectors)
            {
                var angle = frame.ModelDirection * Math.PI / 180d; var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
                Func<double[], double[]> local = point => new[] {
                    (point[0] - frame.X) * cosine + (point[1] - frame.Y) * sine,
                    -(point[0] - frame.X) * sine + (point[1] - frame.Y) * cosine, point[2] - frame.Z };
                var routes = sample.Routes.Where(route => route.IsPhysicalGeometry &&
                        new[] { "A", "B", "C" }.Contains(route.Phase) && route.CopyNativePreview()?.IsCenter == true)
                    .Select(route => new CopperRoute { Route = route, Points = route.PointIds.Select(id => local(geometry.WorldPosition(nodes[id], objects))).ToArray() })
                    .Where(member => member.Points.Length >= 2 &&
                        Math.Abs(member.Points.Last()[0] - member.Points[0][0]) > GeometryToleranceMm &&
                        member.Points.All(point => Math.Abs(point[1] - member.Points[0][1]) <= GeometryToleranceMm) &&
                        Math.Abs(member.Points[0][1]) + InsulatorPreviewProfile.Diameter / 2d <= frame.Width / 2d - 20d).ToArray();
                if (new[] { "A", "B", "C" }.Any(phase => !routes.Any(member => member.Route.Phase == phase)))
                    throw new InvalidOperationException(frame.Name + "缺少完整三相大偏移中间排，不能保留普通三相横担。");
                var phases = routes.GroupBy(member => member.Route.Phase).Select(group => new PhaseAnchor {
                    Phase = group.Key, Transverse = group.Average(member => member.Points[0][1]),
                    Reference = (group.Min((CopperRoute member) => member.Points.Min((double[] point) => point[0])) +
                        group.Max((CopperRoute member) => member.Points.Max((double[] point) => point[0]))) / 2d }).ToArray();
                if (phases.Any(phase => routes.Where(member => member.Route.Phase == phase.Phase)
                    .Any(member => Math.Abs(member.Points[0][1] - phase.Transverse) > GeometryToleranceMm)))
                    throw new InvalidOperationException(frame.Name + "的同相中间排没有一致的横向支承位置。");
                var centerReference = phases.Average(phase => phase.Reference);
                var stagger = phases.ToDictionary(phase => phase.Phase, phase => phase.Reference - centerReference);
                if (phases.Any(phase => phases.Any(other => phase.Phase != other.Phase &&
                    Math.Abs(stagger[phase.Phase] - stagger[other.Phase]) <= GeometryToleranceMm)))
                    throw new InvalidOperationException(frame.Name + "缺少三相中间排的独立错开定位。");
                var transverse = phases.OrderByDescending(phase => phase.Transverse).ToArray();
                var carriers = new List<Carrier>();
                foreach (var member in routes)
                    for (var index = 1; index < member.Points.Length; index++)
                    {
                        var start = member.Points[index - 1]; var end = member.Points[index];
                        if (Math.Abs(start[1] - end[1]) > GeometryToleranceMm || Math.Abs(start[2] - end[2]) > GeometryToleranceMm) continue;
                        var minimum = Math.Max(Math.Min(start[0], end[0]) + BusbarBridgeFrameJointClearance.MinimumLapDistanceMm,
                            -frame.Length / 2d + InsulatorPreviewProfile.Diameter / 2d + 20d);
                        var maximum = Math.Min(Math.Max(start[0], end[0]) - BusbarBridgeFrameJointClearance.MinimumLapDistanceMm,
                            frame.Length / 2d - InsulatorPreviewProfile.Diameter / 2d - 20d);
                        if (minimum > maximum) continue;
                        carriers.Add(new Carrier { Phase = member.Route.Phase, Minimum = minimum, Maximum = maximum,
                            Bottom = (start[2] + end[2]) / 2d - member.Route.SectionThickness / 2d });
                    }
                var shifts = carriers.SelectMany(carrier => new[] { carrier.Minimum - stagger[carrier.Phase], carrier.Maximum - stagger[carrier.Phase] })
                    .Concat(new[] { 0d }).Distinct().OrderBy(Math.Abs).ThenBy(value => value);
                Dictionary<string, Carrier[]> selected = null; var selectedShift = 0d;
                foreach (var shift in shifts)
                {
                    var candidate = phases.ToDictionary(phase => phase.Phase, phase => carriers.Where(carrier => carrier.Phase == phase.Phase &&
                        shift + stagger[phase.Phase] >= carrier.Minimum - 1e-7 && shift + stagger[phase.Phase] <= carrier.Maximum + 1e-7).ToArray());
                    if (candidate.Values.Any(members => members.Length != frame.InsulatorPadCount + 1)) continue;
                    if (candidate.Values.Any(members => !HasExpectedStackSpacing(members, frame.InsulatorPadThickness))) continue;
                    selected = candidate; selectedShift = shift; break;
                }
                if (selected == null) throw new InvalidOperationException(frame.Name + "没有足够的实际直段安装三个错开的单相支架，请调整大偏移布局。");
                var supports = new List<BridgeDesignObject>(); var ordinal = 0;
                foreach (var phase in phases.OrderBy(phase => stagger[phase.Phase]))
                {
                    var floor = -frame.DriveValue(DesignField.Height) / 2d + BusbarBridgeDraftPreviewSceneFactory.FrameFloorThicknessMm;
                    var bodyHeight = selected[phase.Phase].Min(member => member.Bottom) - floor - InsulatorPreviewProfile.BracketHeight;
                    if (bodyHeight <= BridgeInsulatorMountPlan.MinimumBodyHeightMm)
                        throw new InvalidOperationException(frame.Name + "的" + phase.Phase + "相中间排没有足够的单相绝缘子安装高度。");
                    var active = Array.FindIndex(transverse, row => row.Phase == phase.Phase);
                    var heights = new double[3]; heights[active] = bodyHeight;
                    var names = new string[3]; names[active] = phase.Phase;
                    var mount = new BridgeInsulatorMountPlan(transverse.Select(row => row.Transverse), heights, names, frame.InsulatorAssemblyHeight, true);
                    for (var slot = 0; slot < 3; slot++)
                    {
                        var child = new PrototypeBusbarBridgeDesignProvider().CreateInsulator(frame, ++ordinal, mount.Transverse[slot]);
                        var values = child.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
                        values[DesignField.X] = selectedShift + stagger[phase.Phase];
                        child = child.WithValues(values).WithMountPlan(mount, slot);
                        supports.Add(frame.FramePositionIndex > 0 ? child.WithFramePosition(frame.FramePositionIndex) : child);
                    }
                }
                result.RemoveAll(node => node.ParentId == frame.Id && node.Kind == DesignObjectKind.Insulator);
                var frameValues = frame.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
                frameValues[DesignField.InsulatorCount] = 3d;
                result[result.FindIndex(node => node.Id == frame.Id)] = frame.WithValues(frameValues);
                result.AddRange(supports);
            }
            return result.ToArray();
        }
    }
}
