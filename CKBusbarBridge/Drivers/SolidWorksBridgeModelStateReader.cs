using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CKBusbarBridge.Prototype;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed class SolidWorksBridgeModelStateReader
    {
        private string _project;
        private BridgeModelState _baseline;
        private readonly Dictionary<string, string> _hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal BridgeModelState Read(IModelDoc2 assembly, IEnumerable<Component2> roots, BridgeModelState baseline,
            string assemblyHash, IReadOnlyDictionary<string, string> hashes, Func<string, Component2, bool> includeChild = null)
        {
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(
                assembly, assembly.ConfigurationManager.ActiveConfiguration.Name, "业务快照总装");
            _project = Path.GetDirectoryName(assembly.GetPathName());
            _baseline = baseline;
            _hashes.Clear();
            foreach (var item in hashes) _hashes[item.Key] = item.Value;
            var state = new BridgeModelState { AssemblyFileHash = assemblyHash };
            var rootUnchanged = new BridgeModelStateReadPolicy().CanReuse(baseline?.AssemblyFileHash,
                state.AssemblyFileHash);
            var components = roots.ToArray();
            foreach (var root in components) ReadComponent(root, root.Name2, null, state, !rootUnchanged, includeChild);
            if (rootUnchanged && baseline.FileHashes != null && state.FileHashes.Count == baseline.FileHashes.Count &&
                baseline.Connections.Values.All(c => c.Connection != null) &&
                state.FileHashes.All(p => baseline.FileHashes.TryGetValue(p.Key, out var hash) && hash == p.Value))
            { state.Connections = baseline.Connections; return state; }
            state.Connections = ReadConnections(assembly, components.Select(c => c.Name2));
            return state;
        }

        internal static Dictionary<string, BridgeModelDocumentState> ReadConnections(IModelDoc2 assembly, IEnumerable<string> rootInstances)
        {
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(assembly,
                assembly.ConfigurationManager.ActiveConfiguration.Name, "总装安装配合快照");
            var connections = new Dictionary<string, BridgeModelDocumentState>(StringComparer.Ordinal);
            var names = new HashSet<string>(rootInstances, StringComparer.OrdinalIgnoreCase);
            for (var feature = assembly.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
                for (var sub = feature.GetFirstSubFeature() as Feature; sub != null; sub = sub.GetNextSubFeature() as Feature)
                {
                    var mate = sub.GetSpecificFeature2() as Mate2;
                    if (mate == null) continue;
                    var entities = Enumerable.Range(0, mate.GetMateEntityCount()).Select(i => mate.MateEntity(i)).ToArray();
                    if (!entities.Any(e => names.Contains((e.ReferenceComponent?.Name2 ?? "").Split('/')[0]))) continue;
                    var connection = new BridgeModelDocumentState { Path = "总装配合/" + sub.Name,
                        Configuration = assembly.ConfigurationManager.ActiveConfiguration.Name };
                    var suppression = sub.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                        new[] { connection.Configuration }) as bool[];
                    if (suppression == null || suppression.Length != 1)
                        throw new InvalidOperationException("总装配合压缩状态无法核验：" + sub.Name);
                    bool warning;
                    connection.Connection = new BridgeModelConnectionState { Type = mate.Type, Alignment = mate.Alignment,
                        Suppressed = suppression[0], Error = sub.GetErrorCode2(out warning), Warning = warning };
                    connection.Properties["类型及对齐"] = mate.Type + "/" + mate.Alignment;
                    foreach (var entity in entities)
                    {
                        connection.Connection.Endpoints.Add(ReadConnectionEndpoint(assembly, entity));
                        var key = (entity.ReferenceComponent?.Name2 ?? "总装") + "/" + entity.ReferenceType;
                        var reference = entity.Reference as Feature;
                        if (reference != null) connection.Properties[key] = reference.Name + "/" + reference.GetTypeName2();
                        else if (entity.Reference is Face2 face)
                        {
                            var surface = face.GetSurface() as Surface;
                            if (surface?.IsPlane() != true) throw new InvalidOperationException("总装配合引用几何暂不支持完整核验：" + sub.Name);
                            Add(connection.Geometry, key, new BridgeAnalyticGeometry().Plane(surface.PlaneParams as double[]), 1e-8);
                        }
                        else throw new InvalidOperationException("总装配合引用无法核验：" + sub.Name + "/" + key);
                    }
                    connections.Add(sub.Name, connection);
                }
            return connections;
        }

        private static BridgeModelConnectionEndpoint ReadConnectionEndpoint(IModelDoc2 assembly, MateEntity2 entity)
        {
            var component = entity.ReferenceComponent;
            var isRoot = component == null || string.Equals(component.GetPathName(), assembly.GetPathName(), StringComparison.OrdinalIgnoreCase);
            var model = isRoot ? assembly : component.GetModelDoc2() as IModelDoc2;
            var configuration = isRoot ? assembly.ConfigurationManager.ActiveConfiguration.Name : component.ReferencedConfiguration;
            if (model == null || string.IsNullOrWhiteSpace(configuration))
                throw new InvalidOperationException("总装配合引用文档或配置无法核验。");
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(model, configuration, "总装配合引用");
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(model,
                model.ConfigurationManager.ActiveConfiguration.Name, "总装配合引用；实际活动配置");
            var reference = entity.Reference as Feature;
            var endpoint = new BridgeModelConnectionEndpoint { Instance = isRoot ? "" : component.Name2,
                Configuration = configuration, Kind = reference?.GetTypeName2() ?? entity.ReferenceType.ToString(),
                Reference = reference?.Name };
            if (endpoint.Kind == "CoordSys")
            {
                var transform = model.Extension.GetCoordinateSystemTransformByName(reference.Name) as MathTransform;
                if (transform == null || !isRoot && component.Transform2 == null)
                    throw new InvalidOperationException("总装配合坐标系变换无法核验：" + endpoint.Instance + "/" + reference.Name);
                if (!isRoot) transform = transform.Multiply(component.Transform2) as MathTransform;
                endpoint.WorldTransform = transform?.ArrayData as double[];
            }
            return endpoint;
        }

        private void ReadComponent(Component2 component, string root, MathTransform parent, BridgeModelState state, bool parentChanged,
            Func<string, Component2, bool> includeChild)
        {
            var path = component.GetPathName(); var configuration = component.ReferencedConfiguration;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || string.IsNullOrWhiteSpace(configuration))
                throw new InvalidOperationException("业务快照引用路径、文件或配置缺失：" + component.Name2);
            if (!BridgeGenerationFileBackup.Under(_project, path))
                throw new InvalidOperationException("业务快照引用超出当前项目范围：" + component.Name2 + "/" + path);
            var document = Path.GetFullPath(path).ToUpperInvariant() + "|" + configuration;
            var suppressed = component.IsSuppressed();
            var transform = component.Transform2;
            var relative = parent == null ? transform : transform?.Multiply(parent.Inverse()) as MathTransform;
            state.Instances.Add(component.Name2, new BridgeModelInstanceState { Root = root, Document = document,
                IsRoot = parent == null, Suppressed = suppressed, Transform = relative?.ArrayData as double[] });
            if (suppressed) return;
            var model = component.GetModelDoc2() as IModelDoc2;
            if (model == null)
                throw new InvalidOperationException("业务快照文档未解析：" + component.Name2 + "/" + path);
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(
                model, model.ConfigurationManager.ActiveConfiguration.Name, component.Name2 + "；实际活动配置");
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(model, configuration, component.Name2);
            var hash = ReadHash(path); state.FileHashes[path] = hash;
            string oldHash = null;
            _baseline?.FileHashes?.TryGetValue(path, out oldHash);
            BridgeModelDocumentState oldDocument = null;
            _baseline?.Documents?.TryGetValue(document, out oldDocument);
            var reusable = oldDocument != null && new BridgeModelStateReadPolicy().CanReuse(oldHash, hash);
            if (!state.Documents.ContainsKey(document)) state.Documents.Add(document, reusable ? oldDocument : ReadDocument(model, configuration));
            if (model is PartDoc)
            {
                BridgeModelInstanceState oldInstance = null;
                _baseline?.Instances?.TryGetValue(component.Name2, out oldInstance);
                if (reusable && !parentChanged && oldInstance?.Document == document && !oldInstance.Suppressed)
                    state.Instances[component.Name2].Geometry = oldInstance.Geometry;
                else
                {
                    // GetBodies3 包含装配体级切除；同一零件的两个实例可以具有不同实体，不能只缓存零件文档。
                    object info;
                    var bodies = (component.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[] ?? Array.Empty<object>()).OfType<Body2>().ToArray();
                    if (bodies.Length == 0) throw new InvalidOperationException("组件缺少实际装配实体：" + component.Name2);
                    foreach (var body in bodies) ReadBody(body, state.Instances[component.Name2].Geometry);
                }
            }
            foreach (var child in (component.GetChildren() as object[] ?? Array.Empty<object>()).OfType<Component2>())
                if (includeChild?.Invoke(root, child) != false)
                    ReadComponent(child, root, transform, state, parentChanged || !reusable, includeChild);
        }

        private string ReadHash(string path)
        {
            if (!_hashes.TryGetValue(path, out var hash)) _hashes.Add(path, hash = BridgeGenerationFileBackup.Hash(path));
            return hash;
        }

        private BridgeModelDocumentState ReadDocument(IModelDoc2 model, string configuration)
        {
            try
            {
                var result = new BridgeModelDocumentState { Path = Path.GetFullPath(model.GetPathName()), Configuration = configuration };
                ReadProperties(model, "", result); ReadProperties(model, configuration, result);
                var equations = model.GetEquationMgr() as EquationMgr;
                if (equations != null)
                    for (var i = 0; i < equations.GetCount(); i++)
                    {
                        var expression = equations.Equation[i];
                        var equals = expression.IndexOf('=');
                        if (equals > 0) result.Dimensions["方程式/" + expression.Substring(0, equals).Trim()] = equations.Value[i] * 1000;
                    }
                ReadFeatures(model.FirstFeature() as Feature, configuration, result);
                if (model is PartDoc part)
                {
                    var database = string.Empty;
                    result.Properties["实际材质"] = part.GetMaterialPropertyName2(configuration, out database) + "|" + database;
                    var bodies = (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? Array.Empty<object>()).OfType<Body2>().ToArray();
                    if (bodies.Length == 0) throw new InvalidOperationException("零件缺少实际实体：" + result.Path);
                    foreach (var body in bodies) ReadBody(body, result.Geometry);
                    Add(result.Geometry, "实体数", new[] { (double)bodies.Length }, 1e-9);
                }
                return result;
            }
            catch (COMException error)
            { throw new InvalidOperationException("无法读取模型业务状态：" + model.GetPathName() + "/" + configuration, error); }
        }

        private void ReadProperties(IModelDoc2 model, string configuration, BridgeModelDocumentState state)
        {
            var manager = model.Extension.CustomPropertyManager[configuration];
            foreach (var name in (manager.GetNames() as string[] ?? Array.Empty<string>()).Where(n =>
                n.Contains("规格") || n.Contains("材质") || n.Contains("图号") || n.Contains("相序") || n.Contains("根数") ||
                n.Contains("模型对照映射") || n.Contains("驱动参数") || n.Contains("零件类型")))
            {
                string raw, resolved; bool wasResolved, linked;
                manager.Get6(name, false, out raw, out resolved, out wasResolved, out linked);
                state.Properties[configuration + "/" + name] = wasResolved ? resolved : raw;
            }
        }

        private void ReadFeatures(Feature first, string configuration, BridgeModelDocumentState state)
        {
            for (var feature = first; feature != null; feature = feature.GetNextFeature() as Feature)
            {
                var name = feature.Name + "/" + feature.GetTypeName2();
                var suppressed = feature.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { configuration }) as bool[];
                if (suppressed == null || suppressed.Length != 1) throw new InvalidOperationException("特征压缩状态无法核验：" + state.Path + "/" + name);
                state.Properties["特征/" + name] = suppressed[0].ToString(CultureInfo.InvariantCulture);
                for (var display = feature.GetFirstDisplayDimension() as DisplayDimension; display != null;
                    display = feature.GetNextDisplayDimension(display) as DisplayDimension)
                {
                    var dimension = display.GetDimension2(0);
                    var values = dimension?.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration, new[] { configuration }) as double[];
                    if (values == null || values.Length != 1) throw new InvalidOperationException("原驱动尺寸无法核验：" + state.Path + "/" + name);
                    state.Dimensions[dimension.FullName] = values[0] * 1000;
                }
                for (var sub = feature.GetFirstSubFeature() as Feature; sub != null; sub = sub.GetNextSubFeature() as Feature)
                    ReadSingleSubFeature(sub, configuration, state);
            }
        }

        private void ReadSingleSubFeature(Feature feature, string configuration, BridgeModelDocumentState state)
        {
            // 配合不能只看名字或安装变换；同时保留类型、参与组件及实际配合尺寸。
            var mate = feature.GetSpecificFeature2() as Mate2;
            if (mate == null) return;
            var entities = Enumerable.Range(0, mate.GetMateEntityCount()).Select(i => mate.MateEntity(i))
                .Select(e => (e.ReferenceComponent?.Name2 ?? "总装") + "/" + e.ReferenceType).OrderBy(v => v, StringComparer.Ordinal);
            state.Properties["配合/" + feature.Name] = mate.Type + "/" + mate.Alignment + "/" + string.Join(";", entities);
        }

        private void ReadBody(Body2 body, List<BridgeModelGeometryValue> values)
        {
            var faces = (body.GetFaces() as object[] ?? Array.Empty<object>()).OfType<Face2>().ToArray();
            if (faces.Length == 0) throw new InvalidOperationException("实际实体缺少可读取曲面。");
            var bodyFaces = new List<BridgeModelGeometryValue>();
            foreach (var face in faces)
            {
                var faceValues = new List<BridgeModelGeometryValue>();
                var surface = face.GetSurface() as Surface;
                if (surface == null) throw new InvalidOperationException("实际曲面读取失败。");
                var kind = "面/" + surface.Identity();
                double[] parameters;
                if (surface.IsPlane()) parameters = new BridgeAnalyticGeometry().Plane(surface.PlaneParams as double[]);
                else if (surface.IsCylinder()) parameters = new BridgeAnalyticGeometry().Cylinder(surface.CylinderParams as double[]);
                else if (surface.IsCone()) parameters = (surface.ConeParams2 as double[])?.Take(8).ToArray();
                else if (surface.IsSphere()) parameters = surface.SphereParams as double[];
                else if (surface.IsTorus()) parameters = surface.TorusParams as double[];
                else { ReadSplineSurface(surface, faceValues); parameters = new[] { (double)surface.Identity() }; }
                Add(faceValues, kind, parameters, 1e-8);
                var edges = (face.GetEdges() as object[] ?? Array.Empty<object>()).OfType<Edge>().ToArray();
                Add(faceValues, kind + "/边数", new[] { (double)edges.Length }, 1e-9);
                foreach (var edge in edges) ReadEdge(edge, kind, faceValues);
                bodyFaces.Add(new BridgeModelGeometryValue { Kind = "实际面", Values = new[] { face.GetArea() }, Tolerance = 1e-10, Children = faceValues });
            }
            values.Add(new BridgeModelGeometryValue { Kind = "实际实体", Values = new[] { (double)faces.Length }, Tolerance = 1e-9, Children = bodyFaces });
        }

        private void ReadSplineSurface(Surface surface, List<BridgeModelGeometryValue> values)
        {
            bool sense;
            var spline = surface.GetBSurfParams3(false, false, surface.Parameterization2(), 1e-8, out sense);
            if (spline == null || spline.ControlPointRowCount < 1 || spline.ControlPointColumnCount < 1)
                throw new InvalidOperationException("无法完整读取实际曲面控制点：" + surface.Identity());
            Add(values, "样条面/阶数", new[] { (double)spline.UOrder, spline.VOrder, spline.ControlPointDimension,
                spline.ControlPointRowCount, spline.ControlPointColumnCount, spline.UPeriodicity ? 1 : 0, spline.VPeriodicity ? 1 : 0 }, 1e-9);
            Add(values, "样条面/U节点", spline.UKnots as double[], 1e-8);
            Add(values, "样条面/V节点", spline.VKnots as double[], 1e-8);
            var points = new List<double>();
            for (var row = 1; row <= spline.ControlPointRowCount; row++)
                for (var column = 1; column <= spline.ControlPointColumnCount; column++)
                {
                    var point = spline.GetControlPoints(row, column) as double[];
                    if (point == null || point.Length != spline.ControlPointDimension) throw new InvalidOperationException("实际曲面控制点缺失。");
                    points.AddRange(point);
                }
            Add(values, "样条面/控制网", points.ToArray(), 1e-8);
        }

        private void ReadEdge(Edge edge, string face, List<BridgeModelGeometryValue> values)
        {
            var curve = edge.GetCurve() as Curve; var bounds = edge.GetCurveParams3();
            if (curve == null || bounds == null) throw new InvalidOperationException("实际边界曲线读取失败。");
            var kind = face + "/边/" + curve.Identity();
            var start = bounds.StartPoint as double[]; var end = bounds.EndPoint as double[];
            // 起终点组成无序集合；圆弧长度、圆心半径和轴向共同区分长弧、短弧及整圆。
            var length = curve.GetLength3(bounds.UMinValue, bounds.UMaxValue);
            var circle = curve.IsCircle() ? curve.CircleParams as double[] : null;
            var fullCircle = circle?.Length == 7 && Math.Abs(length - 2 * Math.PI * circle[6]) < 1e-8;
            if (!fullCircle) { Add(values, kind + "/端点", start, 1e-8); Add(values, kind + "/端点", end, 1e-8); }
            Add(values, kind + "/长度", new[] { length }, 1e-8);
            if (curve.IsLine()) return; // 直线已由两个实际端点完整确定，避免无限直线的任意参数原点。
            if (curve.IsCircle())
            {
                if (circle == null || circle.Length != 7) throw new InvalidOperationException("实际圆边界参数缺失。");
                Add(values, kind + "/圆", circle.Take(3).Concat(new BridgeAnalyticGeometry().Axis(circle.Skip(3).Take(3).ToArray())).Concat(new[] { circle[6] }).ToArray(), 1e-8);
                return;
            }
            if (curve.IsEllipse()) { Add(values, kind + "/椭圆", curve.GetEllipseParams() as double[], 1e-8); return; }
            if (curve.IsBcurve())
            {
                var spline = curve.GetBCurveParams5(false, false, false, false);
                object points, knots;
                if (spline == null || !spline.GetControlPoints(out points) || !spline.GetKnotPoints(out knots))
                    throw new InvalidOperationException("实际边界样条控制点或节点缺失。");
                Add(values, kind + "/阶数", new[] { (double)spline.Order, spline.Dimension, spline.Periodic }, 1e-9);
                Add(values, kind + "/控制点", points as double[], 1e-8); Add(values, kind + "/节点", knots as double[], 1e-8);
                return;
            }
            throw new InvalidOperationException("暂不支持实际边界曲线类型 " + curve.Identity() + " 的完整核验。");
        }

        private static void Add(List<BridgeModelGeometryValue> values, string kind, double[] data, double tolerance)
        {
            if (data == null || data.Length == 0 || data.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new InvalidOperationException("实际几何数据缺失或无效：" + kind);
            values.Add(new BridgeModelGeometryValue { Kind = kind, Values = data, Tolerance = tolerance });
        }
    }
}
