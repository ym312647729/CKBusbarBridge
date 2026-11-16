using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Helpers;
using CKBusbarBridgePreview;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    // 一次反读会话只缓存模板参数和草图，不扫描柜体实体或历史文件摘要。
    internal sealed class SolidWorksBridgeBusinessReader
    {
        private readonly Dictionary<string, BridgeReferencedModelParameters> _parameters = new Dictionary<string, BridgeReferencedModelParameters>();
        private readonly Dictionary<string, double[][]> _paths = new Dictionary<string, double[][]>();
        private readonly Dictionary<string, int> _rootCounts = new Dictionary<string, int>();
        private readonly MathUtility _math;
        private readonly ISldWorks _app;
        private readonly Func<string, bool> _allowUnsavedDocument;
        internal SolidWorksBridgeBusinessReader(ISldWorks app, Func<string, bool> allowUnsavedDocument = null)
        {
            _app = app;
            _math = (MathUtility)app.GetMathUtility();
            _allowUnsavedDocument = allowUnsavedDocument;
        }

        internal BridgeBusinessObservation Read(IModelDoc2 root, FaceToFaceFrameReadResult chain, bool wall,Component2[] inventory=null,
            BridgeUpstreamSnapshot upstream = null)
        {
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(root, root.ConfigurationManager.ActiveConfiguration.Name, "反读总装");
            new BridgeBusinessReadPolicy(_allowUnsavedDocument).RequireSaved(root.GetSaveFlag(), root.GetPathName());
            var all = inventory ?? (((AssemblyDoc)root).GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>().ToArray();
            var result = new BridgeBusinessObservation();
            foreach (var physical in chain.Frames)
            {
                var component = all.Single(c => !c.IsSuppressed() && c.Name2 == physical.ComponentInstanceId);
                var parameters = Parameters(component); var binding = Binding(component);
                binding.Inlet = physical.InletCoordinateName;
                binding.Outlet = physical.OutletCoordinateName ?? PairedOutlet(binding.Inlet);
                var item = new BridgeReadbackFrame { Physical = physical, Binding = binding,
                    Inlet = Coordinate(component, binding.Inlet), Outlet = Coordinate(component, binding.Outlet) };
                if (wall)
                {
                    var upper = physical.FrameTemplateKind == FaceToWallFrameTemplateKind.Large1 ? parameters.Global("上侧封板宽度") : 0;
                    var lower = physical.FrameTemplateKind == FaceToWallFrameTemplateKind.Large1 ? parameters.Global("下侧封板宽度") : 0;
                    item.Wall = new BridgeWallFramePreview(physical.Width - upper - lower,
                        physical.Height - (physical.SideCoverPlateWidth ?? 0), (upper - lower) / 2);
                }
                if (physical.InsulatorCount > 0) ReadInsulators(component, item);
                var key = physical.TemplateIdentity == FrameTemplateIdentity.SideToSidePhaseChange029 ? "SideToSidePhaseChange029" :
                    physical.IsSmallOffsetFrame ? "SmallOffset" : physical.FrameTemplateKind == FaceToWallFrameTemplateKind.Large1 ? "FaceToWallLarge1" :
                    physical.FrameTemplateKind == FaceToWallFrameTemplateKind.Large2 ? "FaceToWallLarge2" :
                    physical.FrameTemplateKind == FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner ? "LargeOffsetCorner" : "Regular";
                var notchFrame = new BridgeDesignObject("notch-read", component.Name2, DesignObjectKind.Frame,
                    new Dictionary<DesignField, double> { [DesignField.Length] = physical.Length,
                        [DesignField.Width] = physical.Width, [DesignField.Height] = physical.Height }, Array.Empty<DesignField>(), "豁口反读")
                    .WithFrameTemplate(key);
                if (physical.SmallOffsetFrame != null)
                    notchFrame = notchFrame.WithSmallOffsetPreview(new BridgeSmallOffsetPreview(physical.SmallOffsetFrame.InletOffsetDirection,
                        physical.SmallOffsetFrame.InletOffsetSize, physical.SmallOffsetFrame.OutletOffsetDirection, physical.SmallOffsetFrame.OutletOffsetSize));
                item.AngleNotches = new SolidWorksFaceToFaceFrameModelOperations(_app).ReadTemplateAngleNotches(component, notchFrame);
                item.SuppressTemplateAngleNotches = item.AngleNotches?.Suppress;
                result.Frames.Add(item);
            }
            var parents = all.Where(c => !c.IsSuppressed() && GeneratedBusbarSubassemblyIdentity.IsGeneratedSubassembly(c)).ToArray();
            if (parents.Length > 1) throw new InvalidOperationException("母线桥铜排总成必须能唯一定位。");
            foreach(var parent in parents) Parameters(parent);
            foreach (var component in parents.SelectMany(Children))
            {
                if (component.IsSuppressed()) { result.Suppressed.Add(component.Name2); continue; }
                if (component.GetModelDoc2() is AssemblyDoc) continue;
                var parameters = Parameters(component);
                if (parameters.Property("零件类型") != "铜排") continue;
                var binding = Binding(component);
                if (!_paths.TryGetValue(binding.DocumentKey, out var path))
                    _paths[binding.DocumentKey] = path = SketchPath(component);
                binding.SourcePath = path;
                var transform = component.GetTotalTransform(false);
                var milled=parameters.Property("铜排形状")=="铣排";
                var item=new BridgeReadbackCopper { Binding = binding,
                    Path = path.Select(p => TransformPoint(p, transform)).ToArray(),
                    ReadFaces = () => ContactFaces(component) };
                var vertical=parameters.HasFeature("原始草图") && !parameters.HasFeature("形状草图");
                new BridgeCopperParameterReadback().Read(parameters, item, vertical, milled,
                    BridgeCopperBusinessRecovery.Compact(path).Length > 2, component.Name2);
                result.Copper.Add(item);
            }
            foreach (var component in all.Where(c => c.IsSuppressed() && c.GetConfiguredRdSystemPartType() == SystemPartType_e.Frame))
                result.Suppressed.Add(component.Name2);
            if (new SideToSideZeroOffsetPhaseChangeReadback().AppliesTo(upstream)) ReadSpecialCabinetCopper(upstream, all, result);
            result.Signature = BridgeBusinessObservation.Sign(result,
                new BridgeGenerationRecordPaths(root.GetPathName()).RelativePath);
            return result;
        }

        private void ReadSpecialCabinetCopper(BridgeUpstreamSnapshot upstream, Component2[] inventory, BridgeBusinessObservation result)
        {
            var side = upstream.SideToSide;
            foreach (var source in new[] { new { Name = "B", Geometry = side.Copper.B }, new { Name = "A", Geometry = side.Copper.A } })
                foreach (var extension in source.Geometry.Extensions)
                {
                    var matches = inventory.Where(c => !c.IsSuppressed() && c.Name2 == extension.Instance).ToArray();
                    if (string.IsNullOrWhiteSpace(extension.Instance) || matches.Length != 1)
                        throw new InvalidOperationException("侧对侧不偏移换相柜端伸出排不能唯一定位：" + source.Name + "/" + extension.Instance);
                    var component = matches[0]; Parameters(component);
                    var binding = Binding(component);
                    var path = extension.Shape.PointList.Select(p => side.Coordinates.ToAssembly(new[] { p.X, p.Z, p.Y })).ToArray();
                    binding.SourcePath = path;
                    result.CabinetCopper.Add(new BridgeReadbackCabinetCopper { Side = source.Name, Phase = extension.Phase.ToString(),
                        Copper = new BridgeReadbackCopper { Binding = binding, Path = path,
                            Width = extension.Shape.BusBarWidth, Thickness = extension.Shape.BusBarThickness,
                            ReadFaces = () => ContactFaces(component) } });
                }
        }

        private void ReadInsulators(Component2 frame, BridgeReadbackFrame item)
        {
            var sets = (frame.GetChildren() as object[] ?? Array.Empty<object>()).OfType<Component2>()
                .Where(c => !c.IsSuppressed() && c.GetConfiguredRdSystemPartType() == SystemPartType_e.Insulator).ToArray();
            var indexed = sets.Select(c => new { Component = c,
                Index = FaceToFaceFrameInsulatorDriver.InstallationIndex(frame, c, item.Physical.InsulatorCount) }).ToArray();
            if (indexed.Length != item.Physical.InsulatorCount || indexed.Select(c => c.Index).Distinct().Count() != indexed.Length)
                throw new InvalidOperationException("基架绝缘子实际安装位重复或缺失：" + frame.Name2);
            sets = indexed.OrderBy(c => c.Index).Select(c => c.Component).ToArray();
            var mounts = sets.Select(FaceToFaceFrameInsulatorDriver.ReadMountPlan).ToArray();
            item.Mounts = mounts.Any(m => m != null) ? mounts : null;
            var offsets = sets.Select(set =>
            {
                var parameters = Parameters(set);
                var reader = ReferencedModelValueReader.FromComponent(set);
                if (reader.TryGlobal(UniversalInsulatorLayout.DistanceNames[0], out var firstDistance))
                    return reader.TryProperty(InsulatorDriver.UniversalOffsetProperty) == "1"
                        ? (double?)InsulatorDriver.ReadUniversalLayout(reader).Offset : null;
                if (!reader.TryGlobal("偏移方向", out var direction))
                {
                    if (System.IO.Path.GetFileName(set.GetPathName()).StartsWith("绝缘子+垫片+支架（3垫块）-P", StringComparison.Ordinal))
                        throw new InvalidOperationException("可偏移绝缘子缺少可读取的原驱动参数：" + set.Name2);
                    if (reader.TryGlobal("偏移尺寸", out var unused)) throw new InvalidOperationException("可偏移绝缘子缺少偏移方向：" + set.Name2);
                    return (double?)null;
                }
                var size = parameters.Global("偏移尺寸");
                if (Math.Abs(direction) > .001 && Math.Abs(direction - 1) > .001 || size < 0 ||
                    double.IsNaN(size) || double.IsInfinity(size) || double.IsNaN(direction) || double.IsInfinity(direction))
                    throw new InvalidOperationException("可偏移绝缘子原驱动参数无效：" + set.Name2);
                return (double?)((direction > .5 ? 1 : -1) * size);
            }).ToArray();
            item.TransverseOffsets = ResolveTransverseOffsets(frame.Name2, offsets, mounts);
            if (item.TransverseOffsets != null)
                foreach (var set in sets) FaceToFaceFrameInsulatorDriver.InstallationIndex(frame, set, item.Physical.InsulatorCount, requireAlignedAxes: true);
            var values = sets.Select(set =>
            {
                var bracket = Parameters(set);
                var mount = FaceToFaceFrameInsulatorDriver.ReadMountPlan(set);
                if (mount != null)
                {
                    var bodyValues = ReferencedModelValueReader.FromComponent(FaceToFaceFrameInsulatorDriver.MountedGroups(
                        bracket.Model).First(c => !c.IsSuppressed()));
                    var spacingText = ReferencedModelValueReader.FromComponent(set).TryProperty(FaceToFaceFrameInsulatorDriver.HangingSpacingProperty);
                    if (!double.TryParse(spacingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var spacing) ||
                        double.IsNaN(spacing) || double.IsInfinity(spacing) || spacing <= 0)
                        throw new InvalidOperationException("吊装绝缘子原相间距无法读取：" + set.Name2);
                    return new[] { spacing, mount.NominalAssemblyHeight, bodyValues.Global("排厚") };
                }
                var bodies = Children(set).Where(c => !c.IsSuppressed() &&
                    (System.IO.Path.GetFileNameWithoutExtension(c.GetPathName()).StartsWith("绝缘子垫块", StringComparison.Ordinal) ||
                    c.GetConfiguredRdSystemPartType() == SystemPartType_e.Insulator)).ToArray();
                // 传统一体模板的原变量在本装配，L型模板的原变量在绝缘子垫块子装配。
                var body = bodies.Length == 0 ? bracket : Parameters(bodies[0]);
                return new[] { bracket.Global("相间距"), body.Global("绝缘子高度") + 25, body.Global("排厚") };
            }).ToArray();
            if (values.Skip(1).Any(v => v.Where((x, i) => Math.Abs(x - values[0][i]) > .001).Any()))
                throw new InvalidOperationException("同一基架内的绝缘子规格无法用一个业务参数组表达：" + frame.Name2);
            item.PhaseSpacing = values[0][0]; item.AssemblyHeight = values[0][1]; item.PadThickness = values[0][2];
            var rootCounts = sets.Select(set =>
            {
                var bodyModel = Parameters(set).Model;
                if (FaceToFaceFrameInsulatorDriver.ReadMountPlan(set) != null)
                {
                    var groups = FaceToFaceFrameInsulatorDriver.MountedGroups(bodyModel).Where(c => !c.IsSuppressed()).ToArray();
                    var counts = groups.Select(c => ReferencedModelValueReader.FromComponent(c)
                        .InsulatorGroupRootCount()).Distinct().ToArray();
                    if (counts.Length != 1) throw new InvalidOperationException("吊装各相垫块根数不一致：" + set.Name2);
                    return counts[0];
                }
                var key = bodyModel.GetPathName().ToUpperInvariant() + "|" + set.ReferencedConfiguration;
                if (!_rootCounts.TryGetValue(key, out var roots))
                    _rootCounts[key] = roots = InsulatorDriver.ReadSinglePhaseRootCount(bodyModel, set.ReferencedConfiguration);
                return roots;
            }).Distinct().ToArray();
            if (rootCounts.Length != 1) throw new InvalidOperationException("同一基架内各安装位绝缘子垫块根数不一致：" + frame.Name2);
            item.PadCount = rootCounts[0] - 1;
            for (var i = 0; i < mounts.Length; i++)
                if (mounts[i] != null)
                {
                    FaceToFaceFrameInsulatorDriver.InstallationIndex(frame, sets[i], sets.Length, requireAlignedAxes: true);
                    FaceToFaceFrameInsulatorDriver.ValidateMountedInsulator(sets[i], mounts[i], rootCounts[0], item.PadThickness);
                }
        }

        internal static double[] ResolveTransverseOffsets(string frameName, IReadOnlyList<double?> offsets,
            IReadOnlyList<BridgeInsulatorMountPlan> mounts)
        {
            if (offsets == null || mounts == null || offsets.Count != mounts.Count)
                throw new InvalidOperationException("实际横移与逐相安装计划数量不一致：基架=" + frameName +
                    "；横移计划数量=" + (offsets?.Count ?? 0) + "；逐相安装计划数量=" + (mounts?.Count ?? 0));
            for (var index = 0; index < offsets.Count; index++)
            {
                if (!offsets[index].HasValue) continue;
                var context = "：基架=" + frameName + "；安装位=" + (index + 1) +
                    "；横移量(mm)=" + offsets[index].Value.ToString("R", CultureInfo.InvariantCulture);
                if (double.IsNaN(offsets[index].Value) || double.IsInfinity(offsets[index].Value))
                    throw new InvalidOperationException("实际安装位的横移量必须为有限数值" + context);
                if (mounts[index] != null)
                    throw new InvalidOperationException("实际安装位的普通横移与逐相定位冲突" + context);
            }
            var floor = Enumerable.Range(0, offsets.Count).Where(index => mounts[index] == null).ToArray();
            if (!floor.Any(index => offsets[index].HasValue)) return null;
            var missing = floor.Where(index => !offsets[index].HasValue).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("同一基架普通落地位混用了普通与可偏移绝缘子模板：基架=" + frameName +
                    "；缺少横移参数的安装位=" + string.Join(",", missing.Select(index => index + 1)));
            // 逐相位置从原MountPlan反读；零仅为该位置不使用普通横移的占位值。
            return offsets.Select((value, index) => mounts[index] == null ? value.Value : 0d).ToArray();
        }

        private BridgeReferencedModelParameters Parameters(Component2 component)
        {
            SolidWorksFaceToFaceFrameModelOperations.RequireDefaultReference(component.GetModelDoc2() as IModelDoc2, component.ReferencedConfiguration, component.Name2);
            var key = component.GetPathName().ToUpperInvariant() + "|" + component.ReferencedConfiguration;
            if (!_parameters.TryGetValue(key, out var value))
                _parameters[key] = value = new BridgeReferencedModelParameters(component, _allowUnsavedDocument);
            return value;
        }
        private BridgeComponentBinding Binding(Component2 component) => new BridgeComponentBinding {
            Instance = component.Name2, Path = component.GetPathName(), Configuration = component.ReferencedConfiguration,
            Transform = (double[])component.GetTotalTransform(false).ArrayData };
        private string PairedOutlet(string inlet)
        {
            if (inlet != "坐标系-进1" && inlet != "坐标系-进2")
                throw new InvalidOperationException("末段入口不是已核验模板接口：" + inlet);
            return inlet.Replace("进", "出");
        }
        private double[] Coordinate(Component2 component, string name)
        {
            var definition = component.FeatureByName(name)?.GetDefinition() as CoordinateSystemFeatureData;
            var data = definition?.Transform?.ArrayData as double[];
            if (data == null || data.Length != 16) throw new InvalidOperationException("实际连接坐标系无法读取：" + component.Name2 + "/" + name);
            return TransformPoint(new[] { data[9], data[10], data[11] }, component.GetTotalTransform(false));
        }
        private double[][] SketchPath(Component2 component)
        {
            var sketch = (component.FeatureByName("形状草图") ?? component.FeatureByName("原始草图"))?.GetSpecificFeature2() as Sketch;
            if (sketch == null) throw new InvalidOperationException("铜排缺少原驱动形状草图：" + component.Name2);
            var inverse = (MathTransform)((MathTransform)sketch.ModelToSketchTransform).Inverse();
            var segments = (sketch.GetSketchSegments() as object[] ?? Array.Empty<object>()).OfType<SketchSegment>()
                .Where(s => !s.ConstructionGeometry).ToArray();
            var edges = new List<double[][]>();
            foreach (var segment in segments)
            {
                var line = segment as SketchLine;
                if (line == null) throw new InvalidOperationException("铜排形状草图包含当前业务无法表达的非直线路径：" + component.Name2);
                var a = (SketchPoint)line.GetStartPoint2(); var b = (SketchPoint)line.GetEndPoint2();
                edges.Add(new[] { Local(new[] { a.X, a.Y, a.Z }, inverse), Local(new[] { b.X, b.Y, b.Z }, inverse) });
            }
            return BridgeBusinessGeometry.OrderPath(edges, .000001);
        }
        private double[] Local(double[] point, MathTransform transform) => (double[])((MathPoint)((MathPoint)_math.CreatePoint(point)).MultiplyTransform(transform)).ArrayData;
        private double[] TransformPoint(double[] point, MathTransform transform) => Local(point, transform).Select(v => v * 1000).ToArray();
        private List<BridgeContactFace> ContactFaces(Component2 component)
        {
            object information;
            var bodies = (component.GetBodies3((int)swBodyType_e.swSolidBody, out information) as object[] ?? Array.Empty<object>()).OfType<Body2>();
            var result = new List<BridgeContactFace>(); var transform = component.GetTotalTransform(false);
            var matrix = (double[])transform.ArrayData;
            var sampling = new BridgeContactBoundarySampling();
            var faceIndex = 0;
            foreach (var face in bodies.SelectMany(b => (b.GetFaces() as object[] ?? Array.Empty<object>()).OfType<Face2>()))
            {
                faceIndex++;
                var surface = face.GetSurface() as Surface;
                if (surface?.IsPlane() != true) continue;
                var normal = face.Normal as double[];
                if (normal == null) continue;
                var loops = (face.GetLoops() as object[] ?? Array.Empty<object>()).OfType<Loop2>().OrderByDescending(l => l.IsOuter()).ToArray();
                var boundaries = new List<double[][]>();
                var loopIndex = 0;
                foreach (var loop in loops)
                {
                    loopIndex++;
                    var context = component.Name2 + "；文件=" + component.GetPathName() + "；面=" + faceIndex +
                        "；" + (loop.IsOuter() ? "外环" : "内环") + "=" + loopIndex;
                    var edges = new List<double[][]>();
                    var edgeIndex = 0;
                    foreach (var edge in (loop.GetEdges() as object[] ?? Array.Empty<object>()).OfType<Edge>())
                    {
                        edgeIndex++;
                        var edgeContext = context + "；边=" + edgeIndex;
                        var start = edge.GetStartVertex() as Vertex; var end = edge.GetEndVertex() as Vertex;
                        var curve=(Curve)edge.GetCurve();
                        if(curve.IsLine() && start!=null && end!=null)
                            edges.Add(new[] { BridgeBusinessGeometry.TransformModelPoint((double[])start.GetPoint(), matrix),
                                BridgeBusinessGeometry.TransformModelPoint((double[])end.GetPoint(), matrix) });
                        else if(curve.IsCircle())
                        {
                            var range=edge.GetCurveParams3() as CurveParamData;
                            var circle=curve.CircleParams as double[];
                            if(range==null || circle==null || circle.Length<7)
                                throw new InvalidOperationException("实际接触面的圆弧边界无法读取："+edgeContext);
                            // GetCurveParams3 的参数属于边；反向边与底层 Curve 的参数化可能不同。
                            var points = sampling.SampleArc(range.UMinValue, range.UMaxValue, circle[6] * 1000,
                                parameter => BridgeBusinessGeometry.TransformModelPoint(sampling.ReadPoint(edge.Evaluate2(parameter, 0) as double[], edgeContext), matrix),
                                start == null ? null : BridgeBusinessGeometry.TransformModelPoint((double[])start.GetPoint(), matrix),
                                end == null ? null : BridgeBusinessGeometry.TransformModelPoint((double[])end.GetPoint(), matrix), edgeContext);
                            for(var i=0;i<points.Length-1;i++) edges.Add(new[]{points[i],points[i+1]});
                        }
                        else throw new InvalidOperationException("实际接触面包含当前业务不支持的曲线边界："+edgeContext);
                    }
                    if (edges.Count >= 3) boundaries.Add(BridgeBusinessGeometry.OrderPath(edges, .001, true, context));
                    else if (loop.IsOuter()) break;
                }
                if (boundaries.Count == 0) continue;
                result.Add(new BridgeContactFace { Loops = boundaries.ToArray(),
                    Normal = BridgeBusinessGeometry.Unit(Enumerable.Range(0,3).Select(axis => Enumerable.Range(0,3).Sum(k => normal[k]*matrix[k*3+axis])).ToArray()) });
            }
            return result;
        }
        private IEnumerable<Component2> Children(Component2 component)
        {
            foreach (var child in (component.GetChildren() as object[] ?? Array.Empty<object>()).OfType<Component2>())
            {
                yield return child;
                if (!child.IsSuppressed()) foreach (var nested in Children(child)) yield return nested;
            }
        }
    }
}
