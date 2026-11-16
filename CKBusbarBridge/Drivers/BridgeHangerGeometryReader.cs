using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CKBusbarBridge.Prototype;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed class BridgeHangerGeometryReader
    {
        private readonly ISldWorks _app;
        private readonly Func<string, bool> _allowUnsaved;
        internal BridgeHangerGeometryReader(ISldWorks app, Func<string, bool> allowUnsaved = null)
        { _app = app; _allowUnsaved = allowUnsaved; }

        internal Component2 RoofComponent(IModelDoc2 root)
        {
            var components = Components(root).ToArray();
            var name = SelectRoofComponentName(components.Select(c => c.Name2));
            return components.Single(c => c.Name2 == name);
        }

        internal string SelectRoofComponentName(IEnumerable<string> componentNames)
        {
            var names = componentNames.ToArray();
            var label = "横梁";
            var matches = names.Where(n => Regex.IsMatch(n.Split('/').Last(), "^横梁(?:-[0-9]+)?$", RegexOptions.CultureInvariant)).ToArray();
            // 只有未找到横梁时才使用屋顶，不能以另一名称绕过重复组件的歧义。
            if (matches.Length == 0)
            {
                label = "屋顶";
                matches = names.Where(n => Regex.IsMatch(n.Split('/').Last(), "^屋顶(?:-[0-9]+)?$", RegexOptions.CultureInvariant)).ToArray();
            }
            if (matches.Length == 0) throw new InvalidOperationException("总装未找到名为“横梁”或“屋顶”的有效屋顶组件。");
            if (matches.Length != 1) throw new InvalidOperationException("总装必须唯一包含名为“" + label + "”的屋顶组件，当前匹配数量：" + matches.Length);
            return matches[0];
        }

        internal string RoofStamp(IModelDoc2 root)
        {
            var roof = RoofComponent(root);
            return string.Join("\n", Descendants(roof).OrderBy(c => c.Name2, StringComparer.Ordinal).Select(c =>
            {
                var model = c.GetModelDoc2() as IModelDoc2 ?? throw new InvalidOperationException("横梁组件未解析：" + c.Name2);
                var path = c.GetPathName();
                return c.Name2 + "|" + path + "|" + c.ReferencedConfiguration + "|" + model.GetUpdateStamp() + "|" +
                    (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) : "virtual") + "|" +
                    string.Join(",", Data(c.GetTotalTransform(false)).Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            }));
        }

        internal BridgeHangerRoof ReadRoof(IModelDoc2 root)
        {
            var roof = RoofComponent(root); var faces = new List<BridgeHangerRoofFace>();
            foreach (var component in Descendants(roof).OrderBy(c => c.Name2, StringComparer.Ordinal))
            {
                if (!(component.GetModelDoc2() is IPartDoc)) continue;
                object bodyInfo;
                var bodies = component.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) as object[];
                if (bodies == null) throw new InvalidOperationException("横梁缺少引用配置的实际实体：" + component.Name2);
                var transform = component.GetTotalTransform(false);
                foreach (var body in bodies.OfType<IBody2>())
                    foreach (var face in (body.GetFaces() as object[] ?? Array.Empty<object>()).OfType<IFace2>())
                    {
                        var surface = face.GetSurface() as ISurface;
                        if (surface?.IsPlane() != true) continue;
                        var p = surface.PlaneParams as double[];
                        if (p == null || p.Length != 6)
                            throw new InvalidOperationException("横梁实际平面不可读取：" + component.Name2);
                        var normal = new[] { p[0], p[1], p[2] };
                        // FaceInSurfaceSense=true表示实体面的外法向与底层曲面法向相反。
                        if (face.FaceInSurfaceSense()) normal = normal.Select(v => -v).ToArray();
                        faces.Add(new BridgeHangerRoofFace(Point(p.Skip(3).ToArray(), transform), Direction(normal, transform)));
                    }
            }
            return new BridgeHangerRoof(RoofStamp(root), faces);
        }

        internal BridgeHangerPose ReadPose(Component2 frame, string id)
        {
            var parameters = new BridgeReferencedModelParameters(frame, _allowUnsaved);
            var local = (frame.FeatureByName(BridgeHangerGeometry.CoordinateName)?.GetDefinition() as ICoordinateSystemFeatureData)?.Transform;
            if (local == null) throw new InvalidOperationException("基架缺少“吊杆配合坐标系”：" + frame.Name2);
            var width = parameters.Global("基架宽度");
            return new BridgeHangerGeometry().FromTransform(id, width, Data(local), Data(frame.GetTotalTransform(false)));
        }

        internal double[] Coordinate(Component2 component)
        {
            var reader = new BridgeReferencedModelParameters(component, _allowUnsaved);
            var local = (component.FeatureByName(BridgeHangerGeometry.CoordinateName)?.GetDefinition() as ICoordinateSystemFeatureData)?.Transform;
            if (local == null) throw new InvalidOperationException("缺少吊杆安装坐标系：" + component.Name2);
            return Data(local.Multiply(component.GetTotalTransform(false)) as MathTransform);
        }

        internal double[] EndPlanes(Component2 component, double[] axis)
        {
            object info;
            var bodies = component.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[] ?? Array.Empty<object>();
            var positions = new List<double>(); var transform = component.GetTotalTransform(false);
            foreach (var body in bodies.OfType<IBody2>())
                foreach (var face in (body.GetFaces() as object[] ?? Array.Empty<object>()).OfType<IFace2>())
                {
                    var surface = face.GetSurface() as ISurface;
                    if (surface?.IsPlane() != true) continue;
                    var p = (double[])surface.PlaneParams;
                    var normal = Direction(p.Take(3).ToArray(), transform);
                    if (Math.Abs(normal.Zip(axis, (a,b) => a*b).Sum()) < 1-1e-6) continue;
                    positions.Add(Point(p.Skip(3).ToArray(), transform).Zip(axis, (a,b)=>a*b).Sum());
                }
            if (positions.Count < 2) throw new InvalidOperationException("无法读取吊杆零件的实际端面：" + component.Name2);
            return new[] { positions.Min(), positions.Max() };
        }

        internal void ValidateInterference(IModelDoc2 root, IReadOnlyDictionary<string, string> hangers)
        {
            if (hangers.Count == 0) return;
            var modeler = _app.IGetModeler();
            if (modeler == null) throw new InvalidOperationException("未取得吊架实体干涉检查接口。");
            var bodies = new List<InstalledBody>();
            var representedHangers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in Components(root))
            {
                if (!(component.GetModelDoc2() is IPartDoc)) continue;
                var name = component.Name2;
                var separator = name.IndexOf('/');
                var top = separator >= 0 ? name.Substring(0, separator) : name;
                hangers.TryGetValue(top, out var hangerLabel);
                object bodyInfo;
                var native = component.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) as object[];
                if (native == null || native.Length == 0)
                {
                    if (hangerLabel != null) throw new InvalidOperationException(hangerLabel + "缺少实际零件实体：" + name);
                    continue;
                }
                var transform = component.GetTotalTransform(false);
                foreach (var item in native.OfType<IBody2>())
                {
                    var copy = item.Copy() as IBody2;
                    if (copy == null || transform == null || !copy.ApplyTransform(transform))
                        throw new InvalidOperationException("无法核验吊架干涉，零件实体无法转换到总装：" + name);
                    if (hangerLabel != null) representedHangers.Add(top);
                    bodies.Add(new InstalledBody { Name = name, RootName = top, HangerLabel = hangerLabel, Body = copy,
                        Extents = AxisExtents(copy, name) });
                }
            }
            if (!representedHangers.SetEquals(hangers.Keys))
                throw new InvalidOperationException("部分吊架缺少可核验的装配实体：" +
                    string.Join("、", hangers.Keys.Where(name => !representedHangers.Contains(name))));

            const double cubicMetresToCubicMillimetres = 1000000000.0;
            var ordered = bodies.OrderBy(body => body.Extents[0]).ToArray();
            var problems = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < ordered.Length; index++)
                for (var next = index + 1; next < ordered.Length; next++)
                {
                    var first = ordered[index];
                    var second = ordered[next];
                    if (second.Extents[0] > first.Extents[1]) break;
                    if (first.HangerLabel == null && second.HangerLabel == null || first.RootName == second.RootName ||
                        Enumerable.Range(1, 2).Any(axis => Math.Min(first.Extents[2 * axis + 1], second.Extents[2 * axis + 1]) <=
                            Math.Max(first.Extents[2 * axis], second.Extents[2 * axis]))) continue;
                    var hanger = first.HangerLabel == null ? second : first;
                    var other = ReferenceEquals(hanger, first) ? second : first;
                    if (!modeler.CheckInterferenceBetweenTwoBodies(first.Body, second.Body, false,
                        out _, out _, out var intersection))
                        throw new InvalidOperationException("无法检查" + hanger.HangerLabel + "与“" + other.Name + "”的实际实体干涉。");
                    var intersectedBodies = (intersection as object[] ?? Array.Empty<object>()).OfType<IBody2>().ToArray();
                    var volume = 0.0;
                    foreach (var body in intersectedBodies)
                    {
                        var properties = body.GetMassProperties(1.0) as double[];
                        if (properties == null || properties.Length <= 3 || !HangerVectors.Finite(properties[3]))
                            throw new InvalidOperationException("无法核验干涉体积：" + hanger.HangerLabel + " / " + other.Name);
                        volume += properties[3] * cubicMetresToCubicMillimetres;
                    }
                    if (volume > BridgeHangerGeometry.Tolerance)
                        problems.Add(hanger.HangerLabel + "与“" + other.Name + "”实体干涉（" +
                            volume.ToString("0.0", CultureInfo.InvariantCulture) + " mm³）");
                }
            if (problems.Count > 0)
                throw new InvalidOperationException(string.Join("；", problems.OrderBy(problem => problem, StringComparer.Ordinal)));
        }

        private static double[] AxisExtents(IBody2 body, string name)
        {
            var extents = new double[6];
            for (var axis = 0; axis < 3; axis++)
                for (var side = 0; side < 2; side++)
                {
                    var direction = new double[3];
                    direction[axis] = side == 0 ? -1.0 : 1.0;
                    if (!body.GetExtremePoint(direction[0], direction[1], direction[2], out var x, out var y, out var z))
                        throw new InvalidOperationException("无法从实际实体读取吊架干涉检查范围：" + name);
                    var value = axis == 0 ? x : axis == 1 ? y : z;
                    if (!HangerVectors.Finite(value)) throw new InvalidOperationException("实体极值无效：" + name);
                    extents[2 * axis + side] = value;
                }
            return extents;
        }

        private sealed class InstalledBody
        {
            internal string Name, RootName, HangerLabel;
            internal IBody2 Body;
            internal double[] Extents;
        }

        private double[] Point(double[] p, MathTransform transform)
        {
            var result = (_app.IGetMathUtility().CreatePoint(p) as MathPoint).MultiplyTransform(transform) as MathPoint;
            return ((double[])result.ArrayData).Select(v => v * 1000).ToArray();
        }
        private double[] Direction(double[] p, MathTransform transform)
        {
            var result = (_app.IGetMathUtility().CreateVector(p) as MathVector).MultiplyTransform(transform) as MathVector;
            return (double[])result.ArrayData;
        }
        internal double[] Data(MathTransform t)
        {
            var data = t?.ArrayData as double[];
            if (data == null || data.Length != 16 || data.Any(v => double.IsNaN(v) || double.IsInfinity(v)) || Math.Abs(data[12]-1)>1e-8)
                throw new InvalidOperationException("吊杆或横梁的组件变换缺失、不支持缩放或包含无效值。");
            return data;
        }
        private IEnumerable<Component2> Components(IModelDoc2 root) =>
            (((AssemblyDoc)root).GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>().Where(c => !c.IsSuppressed());
        private IEnumerable<Component2> Descendants(Component2 component)
        {
            if (component.IsSuppressed()) yield break;
            yield return component;
            foreach (var child in (component.GetChildren() as object[] ?? Array.Empty<object>()).OfType<Component2>())
                foreach (var item in Descendants(child)) yield return item;
        }
    }
}
