using System;
using System.Collections.Generic;
using System.Linq;
using CKBusbarBridge.Drivers;
using CKBusbarBridgePreview;
using GeometRi;

namespace CKBusbarBridge.Prototype
{
    // 固定换相三相柜端本来就错位；业务固定接口不能由三相接触面的同一截面反推。
    internal sealed class SideToSideZeroOffsetPhaseChangeReadback
    {
        private const double OffsetTolerance = 1.0;

        internal bool AppliesToPhysical(BridgeDesignSample sample)
        {
            if (sample?.IsSideToSide != true) return false;
            var frames = sample.Objects.Where(o => o.Kind == DesignObjectKind.Frame).ToArray();
            var count = frames.Count(o => o.FrameTemplateKey == "SideToSidePhaseChange029");
            if (count != 0 && count != frames.Length)
                throw new InvalidOperationException("侧对侧实物不能混装029与普通偏移基架。");
            return frames.Length == 0 ? sample.ScenarioKey == "SideToSideBase1" : count > 0;
        }

        internal bool AppliesTo(BridgeUpstreamSnapshot upstream)
        {
            var input = upstream?.SideToSide?.Copper;
            return upstream?.FaceToWall == null && input?.FixedPhaseChange == true &&
                input.A?.Opening != null && input.B?.Opening != null &&
                Math.Abs(input.CabinetEccentricity) <= OffsetTolerance &&
                Math.Abs(input.A.Opening.Z - input.B.Opening.Z) <= OffsetTolerance;
        }

        internal BridgeDesignSample BindRegisteredCopper(BridgeDesignSample saved,
            IReadOnlyList<BridgeReadbackCopper> actual, IReadOnlyDictionary<string, BridgeCopperPartBinding> registered,
            string parentInstance, bool replaceBindings = false)
        {
            if (registered == null) return saved;
            var routes = saved.Routes.ToDictionary(r => r.Id);
            foreach (var unit in AlgorithmBusbarGenerationService.DescribeBusinessParts(saved))
            {
                if (!replaceBindings && unit.Routes.All(r => r.ComponentBinding != null)) continue;
                if (!registered.TryGetValue(unit.Key, out var old)) continue;
                var matches = actual.Where(c => c.Binding.Instance == parentInstance + "/" + old.Instance &&
                    string.Equals(c.Binding.Path, old.Path, StringComparison.OrdinalIgnoreCase) &&
                    c.Binding.Configuration == old.Configuration).ToArray();
                if (matches.Length == 0) continue; // 删除的实例交给实际贯通检查，不按文件恢复。
                if (matches.Length != 1)
                    throw new InvalidOperationException("侧对侧不偏移换相铜排登记实例重复：" + old.Instance);
                var binding = new BridgeComponentBinding { Instance = matches[0].Binding.Instance,
                    Path = old.Path, Configuration = old.Configuration,
                    Transform = TotalTransform(old), SourcePath = matches[0].Binding.SourcePath };
                foreach (var route in unit.Routes.Where(r => replaceBindings || r.ComponentBinding == null))
                    routes[route.Id] = route.WithComponentBinding(binding);
            }
            return saved.WithGeometry(saved.Objects, saved.Routes.Select(r => routes[r.Id]));
        }

        private double[] TotalTransform(BridgeCopperPartBinding binding)
        {
            var local = binding.Transform; var parent = binding.ParentTransform;
            if (local?.Length != 16 || parent?.Length != 16 ||
                local.Concat(parent).Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                Math.Abs(local[12]) < 1e-8 || Math.Abs(parent[12]) < 1e-8)
                throw new InvalidOperationException("侧对侧不偏移换相铜排缺少有效登记变换：" + binding.Instance);
            var total = new double[16];
            for (var row = 0; row < 3; row++) for (var axis = 0; axis < 3; axis++)
                total[row * 3 + axis] = Enumerable.Range(0, 3).Sum(k => local[row * 3 + k] * parent[k * 3 + axis]);
            for (var axis = 0; axis < 3; axis++)
                total[9 + axis] = Enumerable.Range(0, 3).Sum(k => local[9 + k] * parent[k * 3 + axis]) * parent[12] + parent[9 + axis];
            total[12] = local[12] * parent[12];
            return total;
        }

        internal BridgeDesignSample RestoreControls(BridgeDesignSample saved, BridgeDesignSample recovered,
            IReadOnlyList<BridgeReadbackCopper> actual, bool allowDisconnected)
        {
            RequireRoles(recovered);
            var byInstance = actual.ToDictionary(c => c.Binding.Instance, StringComparer.Ordinal);
            var values = recovered.Objects.ToDictionary(o => o.Id);
            foreach (var phase in new[] { "A", "B", "C" })
            {
                var joints = saved.Objects.Where(o => o.Kind == DesignObjectKind.Breakpoint && o.IsJointGroup && o.Phase == phase)
                    .OrderBy(o => o.X).ToArray();
                for (var index = 0; index < joints.Length; index++)
                {
                    var before = recovered.Routes.Where(r => r.Phase == phase && r.CopperSectionId == BridgeCopperSection.IdFor(index + 1)).ToArray();
                    var after = recovered.Routes.Where(r => r.Phase == phase && r.CopperSectionId == BridgeCopperSection.IdFor(index + 2)).ToArray();
                    var shifts = new List<double>();
                    foreach (var left in before) foreach (var right in after)
                    {
                        if (!byInstance.TryGetValue(left.ComponentBinding.Instance, out var a) ||
                            !byInstance.TryGetValue(right.ComponentBinding.Instance, out var b) || !Touches(a, b, saved.Coordinates)) continue;
                        var oldLeft = saved.Routes.SingleOrDefault(r => r.Id == left.Id);
                        var oldRight = saved.Routes.SingleOrDefault(r => r.Id == right.Id);
                        if (oldLeft == null || oldRight == null) continue;
                        // 由原分段两侧的加工端点恢复位移，保留排厚/桥头相对业务断点的原偏置。
                        shifts.Add(Extreme(a.Path, saved.Coordinates, true) - Extreme(BridgeCopperBusinessRecovery.Preview(oldLeft.CopyNativePreview()), saved.Coordinates, true));
                        shifts.Add(Extreme(b.Path, saved.Coordinates, false) - Extreme(BridgeCopperBusinessRecovery.Preview(oldRight.CopyNativePreview()), saved.Coordinates, false));
                    }
                    if (shifts.Count == 0 || shifts.Max() - shifts.Min() > .03)
                    {
                        if (allowDisconnected) continue;
                        throw new InvalidOperationException("侧对侧不偏移换相分段接缝不能唯一恢复：" + joints[index].Id);
                    }
                    var fields = joints[index].Values.ToDictionary(p => p.Key, p => p.Value);
                    fields[DesignField.X] += shifts.Average();
                    values[joints[index].Id] = joints[index].WithValues(fields);
                }
            }
            // 柜端固定业务基准原样保留；实际接触、缺排和跨相接触由专用贯通检查核验。
            return recovered.WithGeometry(recovered.Objects.Select(o => values[o.Id]), recovered.Routes);
        }

        internal void RequireRoles(BridgeDesignSample sample)
        {
            foreach (var route in sample.Routes)
                if ((route.Phase != "A" && route.Phase != "B" && route.Phase != "C") ||
                    !((route.GenerationGroup == "A柜连接段" || route.GenerationGroup == "B柜连接段") && route.CopperSectionId == null ||
                        route.GenerationGroup == "中间排" && route.CopperSectionId != null))
                    throw new InvalidOperationException("侧对侧不偏移换相铜排缺少明确柜端/中间段归属：" + route.Id);
        }

        internal bool Touches(BridgeReadbackCopper a, BridgeReadbackCopper b, BridgeCoordinateFrame coordinates)
        {
            for (var axis = 0; axis < 3; axis++)
                if (a.Path.Max(p => p[axis]) + a.Width + b.Width < b.Path.Min(p => p[axis]) ||
                    b.Path.Max(p => p[axis]) + a.Width + b.Width < a.Path.Min(p => p[axis])) return false;
            a.Faces = a.Faces ?? a.ReadFaces(); b.Faces = b.Faces ?? b.ReadFaces();
            return a.Faces.Any(f => b.Faces.Any(g => BridgeBusinessGeometry.Contact(f, g, coordinates) != null));
        }

        private double Extreme(double[][] path, BridgeCoordinateFrame coordinates, bool last)
        {
            var positions = path.Select(p => coordinates.ToDraft(p)[0]);
            return last ? positions.Max() : positions.Min();
        }

        internal BridgeDesignSample Edit(BridgeDesignSample baseline, IReadOnlyList<BridgeDesignObject> proposed)
        {
            var before = baseline.Objects.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
            foreach (var fixedJoint in before.Values.Where(o => o.Kind == DesignObjectKind.FixedJoint))
                if (!proposed.Any(o => o.Id == fixedJoint.Id && o.Values.Count == fixedJoint.Values.Count &&
                    o.Values.All(p => Math.Abs(p.Value - fixedJoint.Value(p.Key)) <= .001)))
                    throw new InvalidOperationException("侧对侧不偏移换相柜端固定接口不能作为普通断点修改：" + fixedJoint.Id);
            var oldIds = before.Values.Where(o => o.Kind == DesignObjectKind.Breakpoint).Select(o => o.Id).OrderBy(id => id).ToArray();
            var newIds = proposed.Where(o => o.Kind == DesignObjectKind.Breakpoint).Select(o => o.Id).OrderBy(id => id).ToArray();
            if (!oldIds.SequenceEqual(newIds))
            {
                if (baseline.ReplanCopper == null || baseline.Routes.Any(r => r.ComponentBinding?.GeometryChanged == true))
                    throw new InvalidOperationException("实际换相铜排已改变分段，请在模型中完成分段增删后保存反读。");
                var planned = baseline.ReplanCopper(proposed);
                if (planned.Issues.Any(i => i.BlocksGeneration)) throw new InvalidOperationException(planned.Issues.First(i => i.BlocksGeneration).Message);
                Func<BridgeDesignObject, bool> support = o => o.Kind == DesignObjectKind.Frame || o.Kind == DesignObjectKind.Insulator;
                return baseline.WithGeometry(planned.Objects.Where(o => !support(o)).Concat(proposed.Where(support)), planned.Routes)
                    .WithCopperEditing(planned.CopperEditRoutes, baseline.ReplanCopper);
            }
            var changes = proposed.Where(o => o.Kind == DesignObjectKind.Breakpoint && before.ContainsKey(o.Id) &&
                Math.Abs(o.X - before[o.Id].X) > .001).ToArray();
            if (changes.Length == 0) return baseline.WithGeometry(proposed, baseline.Routes);
            var routes = baseline.Routes.Select(route =>
            {
                if (route.GenerationGroup != "中间排") return route;
                var editingRoute = route.ForRaisedLapEditing(baseline.Coordinates);
                var native = editingRoute.CopyNativePreview(); var changed = false;
                var joints = before.Values.Where(o => o.Phase == route.Phase && o.Kind == DesignObjectKind.Breakpoint).OrderBy(o => o.X).ToArray();
                foreach (var joint in changes.Where(o => o.Phase == route.Phase))
                {
                    var boundary = Array.FindIndex(joints, o => o.Id == joint.Id) + 1;
                    var left = route.CopperSectionId == BridgeCopperSection.IdFor(boundary);
                    var right = route.CopperSectionId == BridgeCopperSection.IdFor(boundary + 1);
                    if (!left && !right) continue;
                    var edge = Extreme(BridgeCopperBusinessRecovery.Preview(native), baseline.Coordinates, left);
                    var sectionEdges = baseline.Routes.Where(r => r.Phase == route.Phase && r.CopperSectionId == route.CopperSectionId)
                        .Select(r => Extreme(BridgeCopperBusinessRecovery.Preview(r.CopyUnraisedNativePreview()), baseline.Coordinates, left)).ToArray();
                    var sectionEdge = left ? sectionEdges.Max() : sectionEdges.Min();
                    if (Math.Abs(edge - sectionEdge) > .03) continue; // 超长分件内部接缝不属于这个业务断点。
                    var delta = joint.X - before[joint.Id].X;
                    native.PointList = native.PointList.Select(p =>
                    {
                        var q = baseline.Coordinates.ToDraft(new[] { p.X, p.Y, p.Z });
                        if (left ? q[0] < edge - baseline.Parameters.OverlapLengthMm - .15 : q[0] > edge + baseline.Parameters.OverlapLengthMm + .15) return p;
                        q[0] += delta; changed = true;
                        var world = baseline.Coordinates.ToAssembly(q); return new Point3d(world[0], world[1], world[2]);
                    }).ToList();
                }
                if (!changed) return route;
                var local = baseline.Coordinates.ToPlanning(native);
                return editingRoute.WithPhysicalGeometry(native.BusBarWidth, native.BusBarThickness,
                    FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(local).Select(s => s.Select(p => new[] { p[0], p[2], p[1] }).ToArray()))
                    .WithNativePreview(local, baseline.Coordinates);
            }).ToArray();
            return baseline.WithGeometry(proposed, routes);
        }
    }
}
