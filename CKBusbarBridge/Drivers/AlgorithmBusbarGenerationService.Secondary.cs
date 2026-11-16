using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CKBusbarBridge.Prototype;
using CKBusbarBridge.Services;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.viewmodels;
using CKBusbarBridgePreview;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.Drivers
{
    internal sealed class BridgeCopperPartBinding
    {
        public string Instance { get; set; }
        public string Path { get; set; }
        public string Configuration { get; set; }
        public string Shape { get; set; }
        public double[] Anchor { get; set; }
        public double[] Transform { get; set; }
        public double[] ParentTransform { get; set; }
    }

    internal sealed class BridgeCopperShapeIdentity
    {
        internal double[] CurrentAnchor(BridgeCopperPartBinding old, double[] currentParent)
        {
            var parent = old.ParentTransform;
            if (parent?.Length != 16 || currentParent?.Length != 16 || old.Anchor?.Length != 3 ||
                parent.Concat(currentParent).Concat(old.Anchor).Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                Math.Abs(parent[12]) < 1e-8)
                throw new InvalidOperationException("铜排缺少已登记的总装安装基准。");
            var relative = old.Anchor.Select((a, i) => a / 1000d - parent[9 + i]).ToArray();
            var local = Enumerable.Range(0, 3).Select(axis => Enumerable.Range(0, 3)
                .Sum(i => parent[axis * 3 + i] * relative[i]) / parent[12]).ToArray();
            return Enumerable.Range(0, 3).Select(axis => (Enumerable.Range(0, 3)
                .Sum(i => currentParent[i * 3 + axis] * local[i]) * currentParent[12] + currentParent[9 + axis]) * 1000d).ToArray();
        }

        internal double[] Translate(double[] component, double[] parent, double[] delta)
        {
            if (component?.Length != 16 || parent?.Length != 16 || delta?.Length != 3 ||
                component.Concat(parent).Concat(delta).Any(v => double.IsNaN(v) || double.IsInfinity(v)) || Math.Abs(parent[12]) < 1e-8)
                throw new InvalidOperationException("铜排安装变换无效。");
            var target = component.ToArray();
            for (var axis = 0; axis < 3; axis++)
                target[9 + axis] += Enumerable.Range(0, 3).Sum(i => parent[axis * 3 + i] * delta[i]) / parent[12] / 1000d;
            return target;
        }

        internal string Signature(PlanarBendBusbarSetting setting)
        {
            if (setting?.PointList == null || setting.PointList.Count < 2)
                throw new InvalidOperationException("铜排缺少可核验的原生加工参数。");
            var copy = BridgeNativePreviewCopy.Copy(setting);
            var anchor = copy.PointList[0].Copy();
            copy.PointList = copy.PointList.Select(p => new GeometRi.Point3d(
                Math.Round(p.X - anchor.X, 6), Math.Round(p.Y - anchor.Y, 6), Math.Round(p.Z - anchor.Z, 6))).ToList();
            // 保留截面朝向、立弯、铣削、搭接等加工参数；只消除整体平移。
            var data = JObject.FromObject(copy, JsonSerializer.Create(BridgeWallPlanningCodec.CreateSettings()));
            data.Remove("GroupKey");
            return new BridgeGenerationRecipeCodec().Hash(data.ToString(Formatting.None));
        }
    }

    internal sealed partial class AlgorithmBusbarGenerationService
    {
        internal static IReadOnlyList<BridgeCopperReadUnit> DescribeBusinessParts(BridgeDesignSample sample)
        {
            var groups = new BridgeDraftGenerationPlanFactory().CopperGroups(sample, validateForGeneration: false);
            var items = BuildGenerationWorkGroups(groups).SelectMany(g => g.GenerationItems).ToArray();
            var result = new List<BridgeCopperReadUnit>(); var available = sample.Routes.ToList();
            var identity = new BridgeCopperShapeIdentity();
            var nativeById = sample.Routes.ToDictionary(r => r.Id, r => r.CopyNativePreview());
            var signatures = nativeById.ToDictionary(p => p.Key, p => identity.Signature(p.Value));
            foreach (var item in items)
            {
                var source = item.BridgeSetting;
                var signature = identity.Signature(source);
                var routes = available.Where(r => {
                    var native = nativeById[r.Id];
                    return source.IsMilledBar ? native.IsMilledBar && native.GroupKey == source.GroupKey :
                        !native.IsMilledBar && signatures[r.Id] == signature &&
                        native.PointList[0].DistanceTo(source.PointList[0]) < .001;
                }).ToArray();
                if (routes.Length != (source.IsMilledBar ? 3 : 1))
                    throw new InvalidOperationException("铜排生成单元与业务路线不能唯一绑定：" + item.BridgeKey);
                foreach (var route in routes) available.Remove(route);
                result.Add(new BridgeCopperReadUnit { Key = item.BridgeKey, Routes = routes, Native = source,
                    EffectiveVerticalRadius = item.VerticalBendSetting?.BusBarVerticalRadius,
                    Path = item.ShapePointGather.Select(p => new[] { p.X, p.Y, p.Z }).ToArray() });
            }
            if (available.Count != 0) throw new InvalidOperationException("业务路线存在未绑定的铜排生成单元。");
            return result;
        }
        private IReadOnlyDictionary<string, BridgeCopperPartBinding> _preservedCopper;
        private HashSet<string> _preservedCopperPaths;

        internal void PreserveRegisteredParts(IReadOnlyDictionary<string, BridgeCopperPartBinding> parts)
        {
            _preservedCopper = parts;
            _preservedCopperPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal AlgorithmBusbarGenerationResult TryUpdateRegisteredPlacements(
            IReadOnlyList<AlgorithmBusbarGenerationGroup> groups, Component2 parent)
        {
            if (_preservedCopper == null) return null;
            var items = BuildGenerationWorkGroups(groups).SelectMany(g => g.GenerationItems).ToArray();
            var identity = new BridgeCopperShapeIdentity();
            if (items.Length != _preservedCopper.Count || items.Any(i => !_preservedCopper.TryGetValue(i.BridgeKey, out var p) ||
                p.Shape != identity.Signature(i.BridgeSetting))) return null;
            var model = parent.GetModelDoc2() as ModelDoc2;
            if (model == null) throw new InvalidOperationException("已登记铜排总成未解析。");
            var components = (((AssemblyDoc)model).GetComponents(true) as object[] ?? Array.Empty<object>())
                .OfType<Component2>().ToDictionary(c => c.Name2, StringComparer.Ordinal);
            var result = new AlgorithmBusbarGenerationResult { RequestedBusbarCount = items.Length, RequestedAssemblyCount = 1 };
            var changed = false;
            new SolidWorksDriver(_solidWorksApplication).OpenOrActivateDocument(model.GetPathName());
            foreach (var item in items)
            {
                var old = _preservedCopper[item.BridgeKey];
                var point = item.BridgeSetting.PointList[0];
                var actualAnchor = identity.CurrentAnchor(old, (double[])parent.Transform2.ArrayData);
                var delta = new[] { point.X - actualAnchor[0], point.Y - actualAnchor[1], point.Z - actualAnchor[2] };
                var component = components[old.Instance];
                if (delta.Any(d => Math.Abs(d) >= .01))
                {
                    model.ClearSelection2(true);
                    if (!component.Select(false)) throw new InvalidOperationException("铜排安装调整无法选中：" + old.Instance);
                    ((AssemblyDoc)model).UnfixComponent();
                    var expected = identity.Translate(old.Transform, (double[])parent.Transform2.ArrayData, delta);
                    component.Transform2 = ((MathUtility)_solidWorksApplication.GetMathUtility()).CreateTransform(expected) as MathTransform;
                    ((AssemblyDoc)model).FixComponent();
                    if (!((double[])component.Transform2.ArrayData).Zip(expected, (a, b) => Math.Abs(a - b)).All(d => d < 1e-8))
                        throw new InvalidOperationException("铜排实际安装位置未达到同步目标：" + old.Instance);
                    changed = true;
                }
                result.Parts[item.BridgeKey] = BindPart(item, component);
                result.Parts[item.BridgeKey].ParentTransform = (double[])parent.Transform2.ArrayData;
                result.CreatedPartPaths.Add(old.Path);
            }
            if (changed)
            {
                RebuildGeneratedModel(model, "已登记铜排安装调整后");
                foreach (var binding in result.Parts.Values)
                    if (!((double[])components[binding.Instance].Transform2.ArrayData).Zip(binding.Transform, (a, b) => Math.Abs(a - b)).All(d => d < 1e-8))
                        throw new InvalidOperationException("重建后铜排安装位置偏离同步目标：" + binding.Instance);
                var errors = 0; var warnings = 0;
                var saved = model.Save3((int)SolidWorks.Interop.swconst.swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
                CheckGenerationSave(saved, errors, warnings, "铜排安装调整：" + model.GetPathName());
            }
            foreach (var component in components.Values.Where(c => !c.IsSuppressed()))
                RequireCopperComponent(component, "已登记铜排安装调整核验");
            result.CreatedAssemblyPaths.Add(model.GetPathName());
            new SolidWorksDriver(_solidWorksApplication).OpenOrActivateDocument(_bridgeContext.AssemblyPath);
            return result;
        }

        private BridgeCopperPartBinding BindPart(AlgorithmBusbarGenerationItem item, Component2 part)
        {
            var point = item.BridgeSetting.PointList[0];
            return new BridgeCopperPartBinding { Instance = part.Name2, Path = part.GetPathName(),
                Configuration = part.ReferencedConfiguration, Shape = new BridgeCopperShapeIdentity().Signature(item.BridgeSetting),
                Anchor = new[] { point.X, point.Y, point.Z }, Transform = (double[])part.Transform2.ArrayData,
                ParentTransform = new[] { 1d, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 } };
        }

        private Component2 ReuseOrCreateBusbar(ModelDoc2 model, AssemblyDoc assembly, string assemblyPath,
            string outputDirectory, string templates, AlgorithmBusbarGenerationItem item,
            DrawingNumberMetadataSnapshot metadata, ICollection<string> ownedPaths)
        {
            if (_preservedCopper == null || !_preservedCopper.TryGetValue(item.BridgeKey, out var old) ||
                old.Shape != new BridgeCopperShapeIdentity().Signature(item.BridgeSetting))
                return CreateAndInsertBusbar(model, assembly, assemblyPath, outputDirectory, templates, item, metadata, ownedPaths);
            if (!File.Exists(old.Path) || old.Transform?.Length != 16 || old.Anchor?.Length != 3)
                throw new InvalidOperationException("已登记铜排的文件或安装变换缺失：" + old.Instance);
            var component = assembly.AddComponent5(old.Path, 0, "", false, old.Configuration, 0, 0, 0);
            if (component == null) throw new InvalidOperationException("复用铜排插入失败：" + old.Instance);
            var utility = (MathUtility)_solidWorksApplication.GetMathUtility();
            if (old.ParentTransform?.Length != 16) throw new InvalidOperationException("铜排复用缺少总装安装基准。");
            var relative = utility.CreateTransform(old.Transform) as MathTransform;
            var registeredParent = utility.CreateTransform(old.ParentTransform) as MathTransform;
            var transform = (double[])((MathTransform)relative.Multiply(registeredParent)).ArrayData;
            var anchor = item.BridgeSetting.PointList[0];
            transform[9] += (anchor.X - old.Anchor[0]) / 1000d;
            transform[10] += (anchor.Y - old.Anchor[1]) / 1000d;
            transform[11] += (anchor.Z - old.Anchor[2]) / 1000d;
            component.Transform2 = ((MathUtility)_solidWorksApplication.GetMathUtility()).CreateTransform(transform) as MathTransform;
            model.ClearSelection2(true);
            if (!component.Select(false)) throw new InvalidOperationException("复用铜排无法固定：" + old.Instance);
            assembly.FixComponent();
            if (!((double[])component.Transform2.ArrayData).Zip(transform, (a, b) => Math.Abs(a - b)).All(d => d < 1e-8))
                throw new InvalidOperationException("复用铜排安装位置与目标不一致：" + old.Instance);
            _preservedCopperPaths.Add(System.IO.Path.GetFullPath(old.Path));
            item.TargetPartBaseName = System.IO.Path.GetFileNameWithoutExtension(old.Path);
            return component;
        }
    }
}
