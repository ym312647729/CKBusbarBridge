using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CKBusbarBridgePreview;
using GeometRi;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Services;

namespace CKBusbarBridge.Prototype
{
    internal sealed class BridgeCopperReadUnit
    {
        internal string Key;
        internal BridgeDesignRoute[] Routes;
        internal PlanarBendBusbarSetting Native;
        internal double? EffectiveVerticalRadius;
        internal double[][] Path;
    }
    internal sealed class BridgeCopperBusinessRecovery
    {
        internal const string SideToSideBreakpointSource = BridgeSideToSideCopperEditor.BreakpointIssueSource;
        private readonly BridgeCopperJointGeometryEditor _jointGeometry = new BridgeCopperJointGeometryEditor();
        private sealed class Part
        {
            internal BridgeReadbackCopper Actual;
            internal BridgeDesignRoute Previous, Route;
            internal PlanarBendBusbarSetting Native;
            internal bool Changed;
        }
        internal BridgeDesignSample Recover(BridgeDesignSample saved, IReadOnlyList<BridgeReadbackCopper> actual, bool allowDisconnected = false,
            BridgeUpstreamSnapshot upstream = null)
        {
            var side = new SideToSideZeroOffsetPhaseChangeReadback();
            if (!side.AppliesToPhysical(saved)) side = null;
            if(actual.Count==0)
                return AttachEditing(saved.WithGeometry(saved.Objects.Where(o=>!o.IsJointGroup && o.Kind!=DesignObjectKind.FixedRoutePoint),
                    Array.Empty<BridgeDesignRoute>()).WithCopperEditing(Array.Empty<BridgeDesignRoute>(),null), side, upstream);
            var units=AlgorithmBusbarGenerationService.DescribeBusinessParts(saved);
            var collapsed=saved.WithGeometry(saved.Objects,units.Select(u=>u.Routes.Length==1 ? u.Routes[0] :
                u.Routes[0].WithPhysicalGeometry(u.Native.BusBarWidth,u.Native.BusBarThickness)
                    .WithNativePreview(saved.Coordinates.ToPlanning(u.Native),saved.Coordinates)));
            var recovered=RecoverParts(collapsed,actual,allowDisconnected,side,upstream?.SideToSide?.Copper);
            var routes=new List<BridgeDesignRoute>();
            foreach(var route in recovered.Routes)
            {
                var unit=units.SingleOrDefault(u=>u.Routes[0].Id==route.Id);
                if(unit==null || unit.Routes.Length==1) { routes.Add(route); continue; }
                var native=route.CopyNativePreview();
                Func<double,double,bool> same=(a,b)=>Math.Abs(a-b)<.001;
                if(BridgeBusinessGeometry.SamePath(Preview(native),unit.Path) &&
                    same(native.BusBarWidth,unit.Native.BusBarWidth) && same(native.BusBarThickness,unit.Native.BusBarThickness) &&
                    same(native.BarBendingRadius,unit.Native.BarBendingRadius) && same(native.MilledBarOriginalWidth,unit.Native.MilledBarOriginalWidth) &&
                    same(native.MilledBarAdditionalWidth,unit.Native.MilledBarAdditionalWidth) && same(native.MilledBarStartLength,unit.Native.MilledBarStartLength) &&
                    native.MilledBarFeatureSide==unit.Native.MilledBarFeatureSide &&
                    (native.WidthVector-unit.Native.WidthVector).Norm<1e-8 && (native.StartBusbarVector-unit.Native.StartBusbarVector).Norm<1e-8)
                    routes.AddRange(unit.Routes.Select(r=>r.WithComponentBinding(route.ComponentBinding)));
                else routes.AddRange(ExpandMilled(unit,route,native,saved.Coordinates));
            }
            var result=recovered.WithGeometry(recovered.Objects,routes);
            return AttachEditing(SynchronizePathPoints(result,routes.Where(r=>r.ComponentBinding?.GeometryChanged==true).Select(r=>r.Id)),side,upstream);
        }
        private BridgeDesignSample RecoverParts(BridgeDesignSample saved, IReadOnlyList<BridgeReadbackCopper> actual, bool allowDisconnected,
            SideToSideZeroOffsetPhaseChangeReadback side, SideToSideCopperPlanningInput sideInput)
        {
            var available = saved.Routes.ToList(); var parts = new List<Part>();
            foreach (var item in actual)
            {
                var route = available.SingleOrDefault(r => r.ComponentBinding?.Instance == item.Binding.Instance);
                if (route == null)
                {
                    var matches = available.Where(r => BridgeBusinessGeometry.SamePath(Compact(Preview(r.CopyNativePreview())), Compact(item.Path)) &&
                        Math.Abs(r.SectionWidth-item.Width)<.001 && Math.Abs(r.SectionThickness-item.Thickness)<.001).ToArray();
                    if (matches.Length == 1) route = matches[0];
                    else if (matches.Length > 1) throw new InvalidOperationException("铜排实际实例与保存路线存在重叠歧义：" + item.Binding.Instance);
                }
                if (route != null) available.Remove(route);
                parts.Add(new Part { Actual = item, Previous = route });
            }
            foreach (var part in parts.Where(p => p.Previous != null))
            {
                var original = part.Previous.CopyNativePreview();
                var verticalRadius = new BusbarGenerationIntegrity().ResolveVerticalBend(original)?.BusBarVerticalRadius ?? original.VerticalBendingRadius;
                var unchangedPart = BridgeBusinessGeometry.SamePath(Compact(Preview(original)), Compact(part.Actual.Path)) &&
                    (part.Previous.ComponentBinding?.Transform==null || part.Actual.Binding.Transform==null ||
                        part.Previous.ComponentBinding.Transform.Take(9).Zip(part.Actual.Binding.Transform.Take(9),(a,b)=>Math.Abs(a-b)).All(d=>d<1e-8)) &&
                    Math.Abs(original.BusBarWidth-part.Actual.Width)<.001 && Math.Abs(original.BusBarThickness-part.Actual.Thickness)<.001 &&
                    (!part.Actual.Radius.HasValue || Math.Abs(original.BarBendingRadius-part.Actual.Radius.Value)<.001) &&
                    (!part.Actual.VerticalRadius.HasValue || Math.Abs(verticalRadius-part.Actual.VerticalRadius.Value)<.001) &&
                    (part.Actual.Milling==null || original.IsMilledBar && Math.Abs(original.MilledBarOriginalWidth-part.Actual.Milling[0])<.001 &&
                        Math.Abs(original.MilledBarAdditionalWidth-part.Actual.Milling[1])<.001 &&
                        Math.Abs(original.MilledBarStartLength-part.Actual.Milling[2])<.001 && original.MilledBarFeatureSide==part.Actual.MilledSide);
                if (unchangedPart)
                    part.Native = original;
                else
                {
                    if (IsSpecial(saved, part.Previous)) throw new InvalidOperationException("面对墙特殊铜排与已保存方案不一致，请恢复特殊铜排或明确重新规划：" + part.Actual.Binding.Instance);
                    part.Native = RestoreNative(original, part.Previous.ComponentBinding, part.Actual);
                }
                part.Actual.Binding.GeometryChanged=!unchangedPart || part.Previous.ComponentBinding?.GeometryChanged==true;
                part.Changed=!unchangedPart;
                part.Route = unchangedPart ? part.Previous.WithComponentBinding(part.Actual.Binding) :
                    Physical(part.Previous, part.Native, part.Actual.Binding, saved.Coordinates);
            }
            // 新实例的相位只由实际电气接触向已绑定路线传播；同文件的相位属性不能作为依据。
            var pending = parts.Where(p => p.Previous == null).ToList();
            while (pending.Count > 0)
            {
                var progress = false;
                foreach (var part in pending.ToArray())
                {
                    var contacts = parts.Where(p => p.Route != null && Contact(part.Actual, p.Actual, saved.Coordinates) != null).ToArray();
                    var phases = contacts.Select(p => p.Route.Phase).Distinct().ToArray();
                    if (phases.Length == 0) continue;
                    if (phases.Length != 1) throw new InvalidOperationException("新增铜排同时连接不同相位，无法恢复三相业务方案：" + part.Actual.Binding.Instance);
                    var donors = parts.Where(p => p.Route != null && p.Actual.Binding.DocumentKey == part.Actual.Binding.DocumentKey).ToArray();
                    if(donors.Length==0 && !string.IsNullOrWhiteSpace(part.Actual.Kind))
                        donors=parts.Where(p=>p.Route!=null && p.Actual.Kind==part.Actual.Kind &&
                            (p.Actual.Milling!=null)==(part.Actual.Milling!=null)).ToArray();
                    if (donors.Length == 0) throw new InvalidOperationException("新增铜排缺少已核验的模板加工参数：" + part.Actual.Binding.Instance);
                    var donor = donors[0];
                    part.Native = RestoreNative(donor.Native, donor.Actual.Binding, part.Actual);
                    part.Changed=true;
                    part.Actual.Binding.GeometryChanged=true;
                    part.Native.GroupKey = phases[0] + "-实际中间排";
                    var id = "actual-copper-" + new BridgeGenerationRecipeCodec().Hash(part.Actual.Binding.Instance).Substring(0,16);
                    var prototype = new BridgeDesignRoute(id, phases[0], Array.Empty<string>()).WithPhysicalGeometry(part.Actual.Width, part.Actual.Thickness)
                        .WithGenerationGroup("中间排", true);
                    part.Route = Physical(prototype, part.Native, part.Actual.Binding, saved.Coordinates);
                    pending.Remove(part); progress = true;
                }
                if (!progress) throw new InvalidOperationException("新增铜排无法从实际接触唯一绑定到业务相位：" + string.Join("、", pending.Select(p => p.Actual.Binding.Instance)));
            }
            var unchanged = available.Count == 0 && parts.Count == saved.Routes.Count && parts.All(p => p.Previous != null && !p.Changed);
            if(available.Count>0 || parts.Any(p=>p.Previous==null))
                foreach(var part in parts) part.Actual.Binding.GeometryChanged=true;
            var result = saved.WithGeometry(saved.Objects, parts.Select(p => p.Route));
            // 原实例拓扑未变时，断开不意味着人工删除断点；保留已核验的业务绑定给修复规划。
            side?.RequireRoles(result);
            if (saved.IsSideToSide && sideInput != null && parts.Any(p => p.Route.GenerationGroup == "中间排" &&
                p.Native.StartLapWidth > 0d && p.Native.EndLapWidth > 0d))
                return RestoreSideToSideJoints(result, parts, sideInput.RootCount);
            if (side != null)
            {
                if (!unchanged) result = side.RestoreControls(saved, result, actual, allowDisconnected);
            }
            else if (!unchanged) result = RestoreJoints(result, parts,
                preserveDisconnected: allowDisconnected && available.Count == 0 && parts.All(p => p.Previous != null));
            return result;
        }

        private BridgeDesignSample RestoreSideToSideJoints(BridgeDesignSample sample, IReadOnlyList<Part> parts, int rootCount)
        {
            var tolerance = BridgeDesignPrecision.LengthToleranceMm;
            var overlap = sample.Parameters.OverlapLengthMm;
            var members = parts.Where(p => p.Route.GenerationGroup == "中间排").Select(p =>
            {
                var x = p.Actual.Path.Select(point => sample.Coordinates.ToDraft(point)[0]).ToArray();
                return new { p.Route.Phase, p.Route.CopperSectionId, p.Native.GroupKey, Start = x.Min(), End = x.Max(),
                    p.Native.StartLapWidth, p.Native.EndLapWidth };
            }).ToArray();
            var problems = new List<string>();
            var oldCuts = members.Where(p => sample.SpecialCopperSectionIds?.Contains(p.CopperSectionId) != true).SelectMany(p =>
            {
                var labels = (p.GroupKey ?? "").Split('/');
                var positions = new List<double>();
                if (labels.Contains("split")) positions.Add(p.End - p.EndLapWidth / 2d);
                if (labels.Contains("tail")) positions.Add(p.Start + p.StartLapWidth / 2d);
                return positions.Select(x => (p.Phase, X: x));
            }).GroupBy(p => p.Phase).OrderBy(g => g.Key).Select(g =>
                g.Key + "相 " + string.Join("、", g.Select(p => p.X).Distinct()
                    .OrderBy(x => x).Select(x => FormattableString.Invariant($"{x:0.0} mm")))).ToArray();
            if (oldCuts.Length > 0)
                problems.Add("旧方案有未登记为三相共用断点的分件，实际搭接中心为" + string.Join("，", oldCuts) + "。");

            var sections = members.ToLookup(p => (p.Phase, p.CopperSectionId));
            var controls = sample.Objects.ToDictionary(o => o.Id);
            var editRoutes = sample.CopperEditRoutes.ToLookup(r => r.Phase);
            var reference = editRoutes["B"].Count() == 1 ? editRoutes["B"].Single() : null;
            if (reference == null || reference.PointIds.Count < 2 ||
                new[] { "A", "B", "C" }.Any(phase => editRoutes[phase].Count() != 1 ||
                    editRoutes[phase].Single().PointIds.Count != reference.PointIds.Count ||
                    editRoutes[phase].Single().PointIds.Any(id => !controls.ContainsKey(id))))
            {
                problems.Add("实际铜排缺少完整的三相断点与段排对应关系。");
            }
            else
            {
                var sectionCount = reference.PointIds.Count - 1;
                foreach (var phase in new[] { "A", "B", "C" })
                {
                    var ids = editRoutes[phase].Single().PointIds;
                    var first = sections[(phase, BridgeCopperSection.IdFor(1))].ToArray();
                    var last = sections[(phase, BridgeCopperSection.IdFor(sectionCount))].ToArray();
                    if (first.Length == 0 || last.Length == 0)
                        problems.Add(phase + "相缺少可核验的柜端排件。");
                    if (first.Length > 0) SetPosition(ids[0], first.Min(p => p.Start) + overlap);
                    if (last.Length > 0) SetPosition(ids[sectionCount], last.Max(p => p.End));
                }
                for (var index = 1; index < sectionCount; index++)
                {
                    var leftId = BridgeCopperSection.IdFor(index);
                    var rightId = BridgeCopperSection.IdFor(index + 1);
                    if (sample.SpecialCopperSectionIds?.Contains(leftId) == true ||
                        sample.SpecialCopperSectionIds?.Contains(rightId) == true) continue;
                    var rear = new List<double>();
                    var front = new List<double>();
                    var centers = new List<double>();
                    var phaseCenters = new Dictionary<string, double>();
                    foreach (var phase in new[] { "A", "B", "C" })
                    {
                        var left = sections[(phase, leftId)].OrderByDescending(p => p.End).ToArray();
                        var right = sections[(phase, rightId)].ToArray();
                        if (left.Length > 0)
                        {
                            SetPosition(editRoutes[phase].Single().PointIds[index], left[0].End);
                            phaseCenters.Add(phase, left[0].End - left[0].EndLapWidth / 2d);
                        }
                        if (left.Length != rootCount || right.Length != rootCount)
                            problems.Add($"第{index}个断点的{phase}相实际叠排分段数量不一致。");
                        rear.AddRange(left.Select(p => p.End));
                        front.AddRange(right.Select(p => p.Start + p.StartLapWidth));
                        centers.AddRange(left.Select(p => p.End - p.EndLapWidth / 2d));
                        centers.AddRange(right.Select(p => p.Start + p.StartLapWidth / 2d));
                    }
                    var b = controls[reference.PointIds[index]].X;
                    if (rear.Concat(front).Any(x => Math.Abs(x - b) > tolerance) ||
                        phaseCenters.TryGetValue("B", out var bCenter) && centers.Any(x => Math.Abs(x - bCenter) > tolerance))
                    {
                        var positions = phaseCenters.OrderBy(p => p.Key).Select(p =>
                            FormattableString.Invariant($"{p.Key}相 {p.Value:0.0} mm"));
                        problems.Add($"第{index}个断点实际搭接与B相不一致，搭接中心为" + string.Join("，", positions) + "。");
                    }
                }
                var groupProblem = BridgeSideToSideCopperEditor.JointGroupProblem(controls.Values.Where(o => o.IsJointGroup).ToArray());
                if (groupProblem != null) problems.Add(groupProblem);
            }
            var issues = sample.Issues.Where(i => i.Source != SideToSideBreakpointSource);
            return sample.WithGeometry(sample.Objects.Select(o => controls[o.Id]), sample.Routes,
                issues.Concat(problems.Select(message => new BridgeDesignIssue(message, null, SideToSideBreakpointSource, true))));

            void SetPosition(string id, double x)
            {
                var node = controls[id];
                var values = node.Values.ToDictionary(p => p.Key, p => p.Value);
                values[DesignField.X] = x;
                controls[id] = node.WithValues(values);
            }
        }

        private bool IsSpecial(BridgeDesignSample saved, BridgeDesignRoute route) => saved.ScenarioKey == "FaceToWall" &&
            route.CopperSectionId != null && saved.SpecialCopperSectionIds?.Contains(route.CopperSectionId) == true;
        internal static double[][] Preview(PlanarBendBusbarSetting native)
        {
            var vertical = new BusbarGenerationIntegrity().ResolveVerticalBend(native);
            return (vertical != null ? vertical.PointList : PlanarBendPreviewAdapter.Create(native)?.PointGather)
                .Select(p => new[] { p.X, p.Y, p.Z }).ToArray();
        }
        internal static double[][] Compact(double[][] path)
        {
            if (path == null) return null;
            var points = path.ToList();
            for (var i=1; i<points.Count-1;)
            {
                var a=BridgeBusinessGeometry.Sub(points[i],points[i-1]); var b=BridgeBusinessGeometry.Sub(points[i+1],points[i]);
                if (BridgeBusinessGeometry.Distance(points[i],points[i-1])<.001 ||
                    BridgeBusinessGeometry.Dot(a,b)>0 && Math.Sqrt(BridgeBusinessGeometry.Dot(BridgeBusinessGeometry.Cross(a,b),BridgeBusinessGeometry.Cross(a,b)))<.001)
                    points.RemoveAt(i); else i++;
            }
            return points.ToArray();
        }
        private PlanarBendBusbarSetting RestoreNative(PlanarBendBusbarSetting original, BridgeComponentBinding old, BridgeReadbackCopper actual)
        {
            if (old?.Transform == null) throw new InvalidOperationException("修改后的铜排缺少可绑定的原实例基准：" + actual.Binding.Instance);
            var copy = original.Copy();
            Func<Vector3d,Vector3d> rotate = vector =>
            {
                var source = new[] { vector.X, vector.Y, vector.Z };
                var local = Enumerable.Range(0,3).Select(axis => Enumerable.Range(0,3).Sum(k => old.Transform[axis*3+k]*source[k])).ToArray();
                var world = Enumerable.Range(0,3).Select(axis => Enumerable.Range(0,3).Sum(k => actual.Binding.Transform[k*3+axis]*local[k])).ToArray();
                return new Vector3d(world[0],world[1],world[2]);
            };
            copy.WidthVector = rotate(copy.WidthVector); copy.StartBusbarVector = rotate(copy.StartBusbarVector);
            copy.BusBarWidth=actual.Width; copy.BusBarThickness=actual.Thickness; copy.BarBendingRadius=actual.Radius??original.BarBendingRadius;
            copy.VerticalBendingRadius=actual.VerticalRadius??original.VerticalBendingRadius;
            var path=copy.IsMilledBar ? actual.Path.Select(p=>p.ToArray()).ToArray() : Compact(actual.Path);
            if (actual.Binding.SourcePath != null && BridgeBusinessGeometry.Distance(actual.Binding.SourcePath.Last(),new double[3]) <
                BridgeBusinessGeometry.Distance(actual.Binding.SourcePath.First(),new double[3])) Array.Reverse(path);
            if (copy.IsMilledBar && path.Length != Preview(original).Length)
                throw new InvalidOperationException("铣排的实际加工路径无法由保存的模板参数表达：" + actual.Binding.Instance);
            if(actual.Milling!=null)
            {
                copy.MilledBarOriginalWidth=actual.Milling[0]; copy.MilledBarAdditionalWidth=actual.Milling[1];
                copy.MilledBarStartLength=actual.Milling[2]; copy.MilledBarFeatureSide=actual.MilledSide;
            }
            if(!copy.IsVerticalBend) copy.PreviewBendPointKinds=Enumerable.Repeat(BusbarPreviewBendPointKind_e.Planar,path.Length).ToList();
            copy.PointList=path.Select(p=>new Point3d(p[0],p[1],p[2])).ToList();
            // 逆解原适配器的厚度偏移，随后用同一适配器核验，避免把草图边线当中心线。
            for (var pass=0; pass<3; pass++)
            {
                var projected=Preview(copy);
                if (projected.Length!=path.Length) throw new InvalidOperationException("铜排路径拓扑无法由当前业务表达：" + actual.Binding.Instance);
                copy.PointList=copy.PointList.Select((p,i)=>new Point3d(p.X+path[i][0]-projected[i][0],p.Y+path[i][1]-projected[i][1],p.Z+path[i][2]-projected[i][2])).ToList();
            }
            if (!BridgeBusinessGeometry.SamePath(Preview(copy),path,.001)) throw new InvalidOperationException("铜排中心线逆解未收敛：" + actual.Binding.Instance);
            return copy;
        }
        private IEnumerable<BridgeDesignRoute> ExpandMilled(BridgeCopperReadUnit unit,BridgeDesignRoute actual,
            PlanarBendBusbarSetting native,BridgeCoordinateFrame coordinates)
        {
            var originals=unit.Routes.Select(r=>r.CopyNativePreview()).ToArray();
            var count=originals[0].PointList.Count;
            if(native.PointList.Count!=count+originals[2].PointList.Count)
                throw new InvalidOperationException("铣排实际路径不能恢复原三段结构："+actual.ComponentBinding.Instance);
            var sign=Math.Sign(new Vector3d(originals[0].PointList.Last(),originals[2].PointList.First()).Dot(originals[0].WidthVector));
            var direction=native.WidthVector.Normalized;
            var along=new Vector3d(native.PointList[0],native.PointList[1]).Normalized;
            if(native.MilledBarFeatureSide!=MilledBarFeatureSide_e.None)
            {
                var reversed=along.Cross(direction).Normalized.Dot(native.StartBusbarVector.Normalized)>0;
                sign=(native.MilledBarFeatureSide==MilledBarFeatureSide_e.Left?-1:1)*(reversed?-1:1);
            }
            var delta=native.MilledBarStartLength-unit.Native.MilledBarStartLength;
            var boundary=native.PointList[count-1];
            native.PointList[count-1]=new Point3d(boundary.X+along.X*delta,boundary.Y+along.Y*delta,boundary.Z+along.Z*delta);
            if(originals[2].PointList.Count==2)
                native.MilledBarEndLength=native.PointList[count].DistanceTo(native.PointList.Last());
            for(var i=0;i<3;i++)
            {
                var copy=native.Copy();
                var range=i==0?native.PointList.Take(count):i==1?native.PointList.Skip(count-1).Take(2):native.PointList.Skip(count);
                var shift=i==0?-sign*native.MilledBarAdditionalWidth/2:i==2?sign*native.MilledBarAdditionalWidth/2:0;
                copy.PointList=range.Select(p=>new Point3d(p.X+direction.X*shift,p.Y+direction.Y*shift,p.Z+direction.Z*shift)).ToList();
                copy.BusBarWidth=i==1?native.BusBarWidth:native.MilledBarOriginalWidth;
                copy.PreviewBendPointKinds=Enumerable.Repeat(BusbarPreviewBendPointKind_e.Planar,copy.PointList.Count).ToList();
                yield return Physical(unit.Routes[i],copy,actual.ComponentBinding,coordinates);
            }
        }
        private BridgeDesignRoute Physical(BridgeDesignRoute route, PlanarBendBusbarSetting native, BridgeComponentBinding binding, BridgeCoordinateFrame coordinates)
        {
            var local=coordinates.ToPlanning(native);
            return route.WithPhysicalGeometry(native.BusBarWidth,native.BusBarThickness,
                FaceToFaceModelBusbarPlanner.CreateFixedInterfacePreviewSolids(local).Select(s=>s.Select(p=>new[]{p[0],p[2],p[1]}).ToArray()))
                .WithNativePreview(local,coordinates).WithComponentBinding(binding);
        }
        private BridgeDesignSample SynchronizePathPoints(BridgeDesignSample sample, IEnumerable<string> changedRouteIds)
        {
            var changed=new HashSet<string>(changedRouteIds);
            if(changed.Count==0) return sample;
            var objects=sample.Objects.ToList();
            var routes=sample.Routes.Select(route=> {
                if(!changed.Contains(route.Id)) return route;
                var native=route.CopyNativePreview();
                var ids=new List<string>();
                for(var i=0;i<native.PointList.Count;i++)
                {
                    var point=native.PointList[i]; var draft=sample.Coordinates.ToDraft(new[]{point.X,point.Y,point.Z});
                    var id=route.Id+"/actual-point/"+i;
                    objects.RemoveAll(o=>o.Id==id);
                    objects.Add(new BridgeDesignObject(id,route.Phase+"相实际加工点",DesignObjectKind.FixedRoutePoint,
                        new Dictionary<DesignField,double>{{DesignField.X,draft[0]},{DesignField.Y,draft[1]},{DesignField.Z,draft[2]}},
                        Array.Empty<DesignField>(),"已保存铜排加工路径",phase:route.Phase));
                    ids.Add(id);
                }
                return route.WithPointIds(ids);
            }).ToArray();
            // 未变的抬高排件仍需原始加工点，后续撤去抬高不能引用已被清理的节点。
            var used=new HashSet<string>(routes.SelectMany(r=>r.PointIds)
                .Concat(routes.Where(r=>r.HasRaisedLapBaseline).SelectMany(r=>r.RaisedLapBaselinePointIds))
                .Concat(sample.CopperEditRoutes.SelectMany(r=>r.PointIds)));
            return sample.WithGeometry(objects.Where(o=>o.Kind!=DesignObjectKind.FixedRoutePoint || used.Contains(o.Id)),routes);
        }
        private double[] Contact(BridgeReadbackCopper a, BridgeReadbackCopper b, BridgeCoordinateFrame coordinates)
        {
            var first=a.Path.Select(coordinates.ToDraft).ToArray(); var second=b.Path.Select(coordinates.ToDraft).ToArray();
            if (first.Max(p=>p[0])+a.Width<second.Min(p=>p[0]) || second.Max(p=>p[0])+b.Width<first.Min(p=>p[0])) return null;
            a.Faces=a.Faces??a.ReadFaces(); b.Faces=b.Faces??b.ReadFaces();
            var contacts=a.Faces.SelectMany(f=>b.Faces.Select(g=>BridgeBusinessGeometry.Contact(f,g,coordinates))).Where(c=>c!=null).ToArray();
            if (contacts.Length==0) return null;
            return new[]{contacts.Min(c=>c[0]),contacts.Max(c=>c[1])};
        }
        private BridgeDesignSample RestoreJoints(BridgeDesignSample saved, IReadOnlyList<Part> parts, bool preserveDisconnected = false)
        {
            var ordinary=parts.ToArray();
            var rear=new Dictionary<string,List<double>> { ["A"]=new List<double>(), ["B"]=new List<double>(), ["C"]=new List<double>() };
            var fixedGroups=saved.Objects.Where(o=>o.Kind==DesignObjectKind.FixedJoint && o.IsJointGroup)
                .GroupBy(o=>o.JointGroupId).OrderBy(g=>g.First().X).ToArray();
            var fixedRear=new Dictionary<string,Dictionary<string,List<double>>>();
            for (var i=0;i<ordinary.Length;i++) for(var j=i+1;j<ordinary.Length;j++)
            {
                var a=ordinary[i]; var b=ordinary[j];
                if (a.Route.Phase!=b.Route.Phase || a.Previous!=null && b.Previous!=null && a.Previous.CopperSectionId==b.Previous.CopperSectionId) continue;
                if(IsSpecial(saved,a.Route) && IsSpecial(saved,b.Route)) continue;
                var contact=Contact(a.Actual,b.Actual,saved.Coordinates);
                if(contact==null) continue;
                string fixedKey=null;
                if(fixedGroups.Length>0)
                {
                    var ends=new[]{a,b}.Where(p=>p.Route.CopperSectionId==null || IsSpecial(saved,p.Route)).ToArray();
                    if(ends.Length>0)
                    {
                        var end=ends[0];
                        var last=IsSpecial(saved,end.Route) || (end.Native.GroupKey??"").EndsWith("A基座L排",StringComparison.Ordinal);
                        fixedKey=(last?fixedGroups.Last():fixedGroups.First()).Key;
                    }
                }
                if(fixedKey!=null)
                {
                    if(!fixedRear.TryGetValue(fixedKey,out var phases))
                        fixedRear[fixedKey]=phases=new Dictionary<string,List<double>>{{"A",new List<double>()},{"B",new List<double>()},{"C",new List<double>()}};
                    if(!phases[a.Route.Phase].Any(x=>Math.Abs(x-contact[1])<.03)) phases[a.Route.Phase].Add(contact[1]);
                }
                else if(!rear[a.Route.Phase].Any(x=>Math.Abs(x-contact[1])<.03)) rear[a.Route.Phase].Add(contact[1]);
            }
            foreach(var phase in rear.Keys.ToArray()) rear[phase]=rear[phase].OrderBy(x=>x).ToList();
            if (preserveDisconnected && (rear.Any(p => p.Value.Count != saved.Objects.Count(o =>
                o.Kind == DesignObjectKind.Breakpoint && o.IsJointGroup && o.Phase == p.Key)) ||
                fixedGroups.Any(g => !fixedRear.ContainsKey(g.Key)) ||
                fixedRear.Values.Any(g => g.Values.Any(v => v.Count != 1))))
                return saved; // 实例仍唯一绑定但接缝断开，不把接触缺失当成人工删除断点。
            if(rear["A"].Count!=rear["B"].Count || rear["B"].Count!=rear["C"].Count ||
                rear["A"].Where((x,i)=>Math.Abs(x-rear["B"][i])>.03 || Math.Abs(x-rear["C"][i])>.03).Any())
                throw new InvalidOperationException("实际接缝不能唯一恢复为ABC联动断点，请检查三相分段和接缝。");
            foreach(var group in fixedRear.Values)
                if(group.Values.Any(v=>v.Count!=1) || Math.Abs(group["A"][0]-group["B"][0])>.03 || Math.Abs(group["A"][0]-group["C"][0])>.03)
                    throw new InvalidOperationException("实际端部接缝不能唯一恢复为ABC联动固定断点。");
            var objects=saved.Objects.Where(o=>o.Kind!=DesignObjectKind.Breakpoint).Select(o=> {
                if(o.JointGroupId==null || !fixedRear.TryGetValue(o.JointGroupId,out var phases)) return o;
                var values=o.Values.ToDictionary(p=>p.Key,p=>p.Value); values[DesignField.X]=phases[o.Phase][0]; return o.WithValues(values);
            }).ToList();
            var editRoutes=new List<BridgeDesignRoute>();
            foreach(var phase in new[]{"A","B","C"})
            {
                var old=saved.Objects.Where(o=>o.IsJointGroup && o.Phase==phase).OrderBy(o=>o.X).ToArray();
                var ids=new List<string>();
                foreach(var x in rear[phase])
                {
                    var id=phase+"-actual-joint-"+ids.Count; var prototype=old.First();
                    objects.Add(new BridgeDesignObject(id,"断点"+(ids.Count+1),DesignObjectKind.Breakpoint,
                        new Dictionary<DesignField,double>{{DesignField.X,x},{DesignField.Y,prototype.Y},{DesignField.Z,prototype.Z}},
                        new[]{DesignField.X},"实际搭接区后截面；ABC联动",phase:phase).WithJointGroup("actual-joint-"+ids.Count)); ids.Add(id);
                }
                var all=objects.Where(o=>o.IsJointGroup && o.Phase==phase).OrderBy(o=>o.X).Select(o=>o.Id).ToArray();
                editRoutes.Add(new BridgeDesignRoute("edit-route-"+phase,phase,all));
            }
            var routes=parts.Select(p=>
            {
                if(p.Route.CopperSectionId==null && p.Previous!=null || IsSpecial(saved,p.Route)) return p.Route;
                var midpoint=p.Native.PointList.Select(v=>saved.Coordinates.ToDraft(new[]{v.X,v.Y,v.Z})[0]).Average();
                return p.Route.WithCopperSection(rear[p.Route.Phase].Count(x=>x<midpoint)+1);
            }).ToArray();
            return saved.WithGeometry(objects,routes).WithCopperEditing(editRoutes,null);
        }
        private BridgeDesignSample AttachEditing(BridgeDesignSample baseline, SideToSideZeroOffsetPhaseChangeReadback side = null,
            BridgeUpstreamSnapshot upstream = null)
        {
            if (upstream?.FaceToWall != null)
            {
                var problem = new FaceToWallWallEndRule().FindProblem(upstream.FaceToWall, baseline);
                var issues = baseline.Issues.Where(issue => issue.Source != "墙端预留").ToList();
                if (problem != null) issues.Add(new BridgeDesignIssue(problem, null, "墙端预留"));
                baseline = baseline.WithGeometry(baseline.Objects, baseline.Routes, issues);
            }
            var interactive = upstream?.SideToSide == null ? null :
                BridgeSideToSideCopperEditor.Create(baseline, upstream.SideToSide.Copper);
            Func<IReadOnlyList<BridgeDesignObject>, BridgeDesignSample> replan = null;
            replan = proposed =>
            {
                if (side != null)
                {
                    var edited = side.Edit(baseline, proposed);
                    BridgeSideToSideCopperEditor.RequireReadbackEditClearance(baseline, edited);
                    return AttachEditing(SynchronizePathPoints(edited, edited.Routes.Where(r =>
                        !baseline.Routes.Any(old => old.Id == r.Id && BridgeBusinessGeometry.SamePath(Preview(old.CopyNativePreview()), Preview(r.CopyNativePreview()))))
                        .Select(r => r.Id)), side, upstream);
                }
                if (interactive != null && interactive.TryReplan(proposed, out var editedCopper))
                    return editedCopper.WithCopperEditing(baseline.CopperEditRoutes, replan);
                var before=baseline.Objects.Where(o=>o.IsJointGroup).ToDictionary(o=>o.Id);
                if(proposed.Where(o=>o.IsJointGroup).Any(o=>!before.ContainsKey(o.Id) || Math.Abs(before[o.Id].X-o.X)>.001) ||
                    before.Keys.Any(id=>!proposed.Any(o=>o.Id==id)))
                {
                    var updated = MoveJoints(baseline, proposed, before, upstream);
                    BridgeSideToSideCopperEditor.RequireReadbackEditClearance(baseline, updated);
                    return updated;
                }
                return baseline.WithGeometry(proposed,baseline.Routes);
            };
            return baseline.WithCopperEditing(baseline.CopperEditRoutes, replan);
        }

        private BridgeDesignSample MoveJoints(BridgeDesignSample baseline, IReadOnlyList<BridgeDesignObject> proposed,
            IReadOnlyDictionary<string,BridgeDesignObject> before, BridgeUpstreamSnapshot upstream)
        {
            // 显式修改断点时才规划铜排；基架/绝缘子编辑不会重算实际铜排。
            var originalJoints = before.Values.Where(o=>o.Kind==DesignObjectKind.Breakpoint).Select(o=>o.Id).OrderBy(id=>id).ToArray();
            var currentJoints = proposed.Where(o=>o.Kind==DesignObjectKind.Breakpoint).Select(o=>o.Id).OrderBy(id=>id).ToArray();
            if (!originalJoints.SequenceEqual(currentJoints))
            {
                if(baseline.ReplanCopper==null || baseline.Routes.Any(r=>r.ComponentBinding?.GeometryChanged==true))
                    throw new InvalidOperationException("实际铜排已改变原分段路径，请在模型中完成分段增删后保存反读；现有断点可调整位置。");
                var copper=baseline.ReplanCopper(proposed);
                if(copper.Issues.Any(issue=>issue.BlocksGeneration)) throw new InvalidOperationException(copper.Issues.First(issue=>issue.BlocksGeneration).Message);
                Func<BridgeDesignObject,bool> support=o=>o.Kind==DesignObjectKind.Frame || o.Kind==DesignObjectKind.Insulator;
                return AttachEditing(baseline.WithGeometry(copper.Objects.Where(o=>!support(o)).Concat(proposed.Where(support)),copper.Routes)
                    .WithCopperEditing(copper.CopperEditRoutes,baseline.ReplanCopper)
                    .WithSpecialCopperSectionIds(copper.SpecialCopperSectionIds), upstream: upstream);
            }
            ValidateOffsetCopperSpan(baseline, proposed, before, upstream);
            var offsetChange = BridgeOffsetCopperChange.Create(baseline, proposed);
            var phaseChanges = BridgePhaseChangeCopperChange.Create(baseline, proposed);
            var changes=proposed.Where(o=>o.IsJointGroup && before.ContainsKey(o.Id) && Math.Abs(o.X-before[o.Id].X)>.001).ToArray();
            var changedIds=new List<string>();
            var routes=baseline.Routes.Select(route=>
            {
                if(IsSpecial(baseline,route)) return route;
                var phaseChange = phaseChanges.SingleOrDefault(change => change.SectionId == route.CopperSectionId);
                if (phaseChange != null)
                {
                    changedIds.Add(route.Id);
                    return Physical(route.ForRaisedLapEditing(baseline.Coordinates), phaseChange.Move(route),
                        route.ComponentBinding, baseline.Coordinates);
                }
                var editingRoute = route.ForRaisedLapEditing(baseline.Coordinates);
                var native=editingRoute.CopyNativePreview();
                var changed = _jointGeometry.MoveRouteJointPoints(baseline, editingRoute, native, before, changes, offsetChange);
                if(changed) changedIds.Add(route.Id);
                return changed ? Physical(editingRoute,native,route.ComponentBinding,baseline.Coordinates) : route;
            }).ToArray();
            foreach (var phaseChange in phaseChanges)
                FaceToFaceModelBusbarPlanner.ValidateEditedPhaseChangeClearance(routes.Where(r => r.CopperSectionId == phaseChange.SectionId)
                    .Select(r => r.CopyNativePreview()).ToArray(), ReadInsulationClearance(upstream));
            var result = baseline.WithGeometry(proposed, routes);
            if (offsetChange != null) result = result.WithFrameLengthEditing(offsetChange.PreserveFrameBaseline(baseline))
                .WithOffsetCopperSafetyShift(baseline.OffsetCopperSafetyShift + offsetChange.Shift);
            return AttachEditing(SynchronizePathPoints(result,changedIds), upstream: upstream);
        }

        private void ValidateOffsetCopperSpan(BridgeDesignSample baseline, IReadOnlyList<BridgeDesignObject> proposed,
            IReadOnlyDictionary<string, BridgeDesignObject> before, BridgeUpstreamSnapshot upstream)
        {
            if (!BridgeOffsetCopperChange.SupportsMiddleEditing(baseline)) return;
            var current = proposed.Where(o => o.IsJointGroup).ToDictionary(o => o.Id);
            var clearance = ReadInsulationClearance(upstream);
            // 与断点重规划一致，使用B相代表路径；A/C相的相序换位不是柜间偏移。
            foreach (var route in baseline.CopperEditRoutes.Where(r => r.Phase == "B"))
            for (var i = 1; i < route.PointIds.Count; i++)
            {
                var start = before[route.PointIds[i - 1]]; var end = before[route.PointIds[i]];
                var offset = Math.Abs(end.Y - start.Y);
                if (offset < .001d || (Math.Abs(current[start.Id].X - start.X) < .001d &&
                    Math.Abs(current[end.Id].X - end.X) < .001d)) continue;
                // 使用已核验的三相断点与原排件规格；只校验，不重铺实际铜排或基架。
                var phases = before.Values.Where(o => o.JointGroupId == start.JointGroupId)
                    .OrderBy(o => o.Y).ToArray();
                if (phases.Length != 3 || phases.Select(o => o.Phase).Distinct().Count() != 3)
                    throw new InvalidOperationException("偏移段缺少完整三相接口，无法核验最小长度。");
                var phaseSpacing = Math.Min(phases[1].Y - phases[0].Y, phases[2].Y - phases[1].Y);
                if (phaseSpacing <= .001d)
                {
                    // 侧对侧编辑点表示公共路径，相间距来自已核验的基架叠排参数，不能由三个同位节点补猜。
                    var spacings = baseline.FrameLengthBaseline.Select(f => f.InsulatorPhaseSpacing).Where(s => s > .001d).ToArray();
                    if (spacings.Length == 0 || spacings.Any(s => Math.Abs(s - spacings[0]) > .1d))
                        throw new InvalidOperationException("偏移段缺少一致且已核验的三相间距，无法调整长度。");
                    phaseSpacing = spacings[0];
                }
                var members = baseline.Routes.Where(r => r.CopperSectionId == BridgeCopperSection.IdFor(i)).ToArray();
                if (members.Length == 0)
                    throw new InvalidOperationException("偏移段缺少原铜排规格，无法核验最小长度。");
                var minimumSpan = FaceToFaceUnifiedPlanner.CalculateMinimumOffsetCopperSpan(offset, phaseSpacing,
                    members.Max(r => r.SectionWidth), members.Max(r => r.SectionThickness), clearance,
                    baseline.Objects.Any(o => o.Kind == DesignObjectKind.Frame && o.FrameTemplateKey == "LargeOffsetCorner"));
                if (current[end.Id].X - current[start.Id].X < minimumSpan - .001d)
                    throw new InvalidOperationException("偏移段排最小长度为" +
                        (minimumSpan + baseline.Parameters.OverlapLengthMm).ToString("0.0", CultureInfo.InvariantCulture) +
                        " mm（含搭接），本次联动会使偏移段过短，未提交修改。");
            }
        }

        private static double ReadInsulationClearance(BridgeUpstreamSnapshot upstream)
        {
            var text = upstream?.Fields.FirstOrDefault(f => f.Name == InsulationDistanceMetadata.PropertyName)?.Value;
            return InsulationDistanceMetadata.ParseOrDefault(
                Regex.Match(text ?? string.Empty, @"[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?").Value, out var usedDefault);
        }
    }
}
