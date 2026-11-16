using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed partial class SolidWorksFaceToFaceFrameModelOperations
    {
        internal BridgeAngleNotchPlan ReadTemplateAngleNotches(IComponent2 component, BridgeDesignObject frame)
        {
            if (frame.FrameTemplateKey != "LargeOffsetCorner" && !BusbarBridgeDraftPreviewSceneFactory.HasTemplateAngleNotches(frame)) return null;
            var model = component.GetModelDoc2() as IModelDoc2;
            RequireDefaultReference(model, component.ReferencedConfiguration, component.Name2);
            return ReadTemplateAngleNotches(model, frame);
        }

        internal BridgeAngleNotchPlan ReadTemplateAngleNotches(IModelDoc2 model, BridgeDesignObject frame)
        {
            var corner = frame.FrameTemplateKey == "LargeOffsetCorner";
            if (!corner && !BusbarBridgeDraftPreviewSceneFactory.HasTemplateAngleNotches(frame)) return null;
            RequireDefaultReference(model, model.ConfigurationManager.ActiveConfiguration.Name, model.GetPathName());
            var targets = corner ? CornerNotchTargets(model) : StraightNotchTargets(model, frame.FrameTemplateKey).Targets;
            var seals = BusbarBridgeDraftPreviewSceneFactory.SmallOffsetSeals(frame);
            var depths = new double[4];
            foreach (var target in targets)
            {
                var special = BusbarBridgeDraftPreviewSceneFactory.SmallOffsetAngle(seals, target.Slot);
                var depth = ReadNotchDepth(target, special != null ? 40d : BridgeAngleNotchPlan.MaximumDepthMm);
                if (special != null)
                {
                    if (!NotchDepthMatches(depth, special.SuppressNotch ? 0d : special.NotchWidth.Value))
                        throw new InvalidOperationException("032角钢实际豁口与该安装端的偏移专用计划不符：" + target.Component.Name2);
                }
                else if (!corner)
                    RequireStraightNotchGrade(frame.FrameTemplateKey, depth, target.Component.Name2);
                depths[target.Slot] = depth;
            }
            return new BridgeAngleNotchPlan(depths);
        }

        public void ApplyTemplateAngleNotches(string assemblyPath, FaceToFaceFrameModelPlan plan, IDrawingNumberGenerator numbers)
        {
            if (plan.IsSmallOffsetFrame || !plan.SuppressTemplateAngleNotches.HasValue) return;
            var driver = new SolidWorksDriver(_swApp);
            var root = driver.OpenOrActivateDocument(assemblyPath, rebuildAfterResolve: false);
            UseDefaultModelConfiguration(root);
            if (plan.TemplateIdentity == FrameTemplateIdentity.FaceToFaceLargeOffsetCorner028)
            {
                ApplyCornerNotches(root, plan.AngleNotches);
            }
            else
            {
                var key = plan.TemplateIdentity == FrameTemplateIdentity.Regular024 ? "Regular" : plan.TemplateIdentity.ToString();
                var located = StraightNotchTargets(root, key);
                var notches = plan.AngleNotches ?? new BridgeAngleNotchPlan(plan.SuppressTemplateAngleNotches.Value);
                var targets = located.Targets.Select(target =>
                {
                    var component = target.Component;
                    var path = component.GetPathName();
                    RequireAngleInTransaction(root, path);
                    RequireDefaultReference(component.GetModelDoc2() as IModelDoc2, component.ReferencedConfiguration, component.Name2);
                    var depth = notches.StraightDepth(target.Slot);
                    RequireStraightNotchGrade(key, depth, component.Name2);
                    return new FrameAngleFileTarget(target.Prefix, target.Slot < 2 ? -1 : 1,
                        path, component.GetID(), depth);
                }).ToArray();
                RequireAngleInTransaction(root, located.Assembly.GetPathName());
                var operations = DriveAngleFiles(root, located.Assembly, targets, numbers);
                if (operations.ReferencesChanged && !BridgeGenerationFileBackup.Same(located.Assembly.GetPathName(), root.GetPathName()))
                {
                    driver.OpenOrActivateDocument(located.Assembly.GetPathName(), rebuildAfterResolve: false);
                    EnsureSmallOffsetSealPlateRebuildSucceeded(located.Assembly.EditRebuild3(), located.Assembly.GetPathName());
                    SaveAngleModel(located.Assembly);
                    _swApp.CloseDoc(located.Assembly.GetTitle());
                }
            }
            SWHelper.EnsureAssemblyEditingContext(_swApp, root);
            EnsureSmallOffsetSealPlateRebuildSucceeded(root.EditRebuild3(), assemblyPath);
            SaveAngleModel(root);
        }

        private static void RequireStraightNotchGrade(string template, double depth, string identity)
        {
            if (template == "Regular") return;
            var grades = template == "SideToSidePhaseChange029" ? BridgeAngleNotchPlan.FourDepthsMm : new[] { 0d, 22d };
            if (!grades.Any(grade => NotchDepthMatches(depth, grade)))
                throw new InvalidOperationException(FormattableString.Invariant($"角钢豁口不符合模板档位：{identity}，实际{depth:0.0} mm。"));
        }

        private void ApplyCornerNotches(IModelDoc2 root, BridgeAngleNotchPlan plan)
        {
            if (plan?.CornerDepth(0) == null)
                throw new InvalidOperationException("028基架缺少明确的角钢豁口目标：" + root.GetPathName());
            var targets = CornerNotchTargets(root);
            var groups = targets.GroupBy(t => t.Component.GetPathName(), StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var group in groups)
            {
                RequireAngleInTransaction(root, group.Key);
                if (group.Select(t => t.Component.GetID()).Distinct().Count() != 1)
                    throw new InvalidOperationException("028不同型号的立角钢引用了同一个文件：" + group.Key);
                foreach (var target in group)
                {
                    var depth = plan.CornerDepth(target.Slot);
                    if (!BridgeAngleNotchPlan.Valid(depth))
                        throw new InvalidOperationException("028角钢豁口深度超出制造范围：" + target.Component.Name2);
                    RequireDefaultReference(target.Component.GetModelDoc2() as IModelDoc2,
                        target.Component.ReferencedConfiguration, target.Component.Name2);
                }
            }
            using (var context = new AngleAssemblyContext(_swApp, root))
            {
                try
                {
                    foreach (var group in groups)
                    {
                        var model = OpenAnglePart(group.Key);
                        var writes = group.Where(t => plan.CornerDepth(t.Slot).HasValue).ToArray();
                        foreach (var target in writes)
                            WriteNotchDepth(model, target.Feature, target.Dimension, plan.CornerDepth(target.Slot).Value);
                        EnsureSmallOffsetSealPlateRebuildSucceeded(model.EditRebuild3(), group.Key);
                        foreach (var target in writes)
                            RequireNotchDepth(ReadNotchDepth(target), plan.CornerDepth(target.Slot).Value, target.Component.Name2);
                        SaveAngleModel(model);
                    }
                }
                catch (Exception error) { context.Failure = error; throw; }
            }
        }

        private static void RequireAngleInTransaction(IModelDoc2 root, string path)
        {
            if (!BridgeGenerationFileBackup.Under(System.IO.Path.GetDirectoryName(root.GetPathName()), path))
                throw new InvalidOperationException("角钢或所属装配体位于基架事务目录外：" + path);
        }

        internal static void WriteNotchDepth(IModelDoc2 model, string feature, string dimension, double depth)
        {
            var path = model.GetPathName();
            SetSmallOffsetNotchSuppression(NotchFeature(model, feature, path), depth == 0d, path);
            if (depth > 0d)
            {
                var current = model.Parameter(dimension) as Dimension;
                if (current == null || !NotchDepthMatches(current.SystemValue * 1000d, depth))
                    SetDefaultAngleDimension(model, dimension, depth);
            }
        }

        internal static void WriteBaseBridgeAngleNotch(IModelDoc2 model, string feature, string depthDimension,
            BaseBridgeAngleNotchTarget target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            RequireDefaultReference(model, model.ConfigurationManager.ActiveConfiguration.Name, model.GetPathName());
            var cut = NotchFeature(model, feature, model.GetPathName());
            // 原草图尺寸在切除压缩时也要更新；完整尺寸准备好后才切换最终开关状态。
            if (target.LengthMm.HasValue)
            {
                SetDefaultAngleDimension(model, "D1@草图2", target.LengthMm.Value);
                SetDefaultAngleDimension(model, "D3@草图2", target.EndOffsetMm.Value);
            }
            SetDefaultAngleDimension(model, depthDimension, target.DepthMm);
            SetSmallOffsetNotchSuppression(cut, target.Suppressed, model.GetPathName());
        }

        private static void RequireNotchDepth(double actual, double expected, string identity)
        {
            if (!NotchDepthMatches(actual, expected))
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"角钢豁口反读不符：{identity}，实际{actual:0.0} mm，目标{expected:0.0} mm。"));
        }

        private sealed class AngleNotchTarget
        {
            internal IComponent2 Component { get; }
            internal string Feature { get; }
            internal string Dimension { get; }
            internal int Slot { get; }
            internal string Prefix { get; }
            internal AngleNotchTarget(IComponent2 component, string feature, string dimension, int slot, string prefix = null)
            { Component = component; Feature = feature; Dimension = dimension; Slot = slot; Prefix = prefix; }
        }

        private static (IModelDoc2 Assembly, AngleNotchTarget[] Targets) StraightNotchTargets(IModelDoc2 root, string templateKey)
        {
            var prefixes = BusbarBridgeDraftPreviewSceneFactory.TemplateAngleComponentPrefixes(templateKey);
            var models = prefixes.Distinct().ToArray();
            var active = (((IAssemblyDoc)root).GetComponents(false) as object[] ?? Array.Empty<object>())
                .OfType<IComponent2>().Where(c => !c.IsSuppressed()).ToArray();
            var targets = new List<AngleNotchTarget>();
            IComponent2 parent = null;
            foreach (var component in active)
            {
                var prefix = models.SingleOrDefault(p => ComponentIdentifierMatches(component, p));
                if (prefix == null) continue;
                var owner = component.GetParent();
                if (targets.Count > 0 && (owner?.Name2 != parent?.Name2 || owner?.GetID() != parent?.GetID()))
                    throw new InvalidOperationException("四根立角钢分属不同子装配体：" + root.GetPathName());
                parent = owner;
                var transform = component.Transform2?.ArrayData as double[];
                // 029 两端的上下角钢在原生模板里位于局部 ±Z，侧装后映射到总装高度。
                if (transform == null || transform.Length < 12 ||
                    !(Math.Abs(transform[9] * 1000d) > BridgeDesignPrecision.LengthToleranceMm) ||
                    !(Math.Abs(transform[11] * 1000d) > BridgeDesignPrecision.LengthToleranceMm) ||
                    double.IsInfinity(transform[9]) || double.IsInfinity(transform[11]))
                    throw new InvalidOperationException("角钢缺少有效的模板局部端位或侧位：" + component.Name2);
                var slot = BridgeAngleNotchPlan.Slot(transform[9], transform[11]);
                if (prefix != prefixes[slot])
                    throw new InvalidOperationException("角钢型号与已核验的模板局部位置不符：" + component.Name2);
                targets.Add(new AngleNotchTarget(component, templateKey == "SmallOffset" ? "豁口" : "切除-拉伸3",
                    "D2@草图4", slot, prefix));
            }
            if (targets.Count != 4 || targets.Select(t => t.Slot).Distinct().Count() != 4)
                throw new InvalidOperationException("基架无法唯一定位四个模板局部立角钢位置：" + root.GetPathName());
            if (parent == null) return (root, targets.ToArray());
            var assembly = parent.GetModelDoc2() as IModelDoc2;
            RequireDefaultReference(assembly, parent.ReferencedConfiguration, parent.Name2);
            return (assembly, targets.ToArray());
        }

        private static AngleNotchTarget[] CornerNotchTargets(IModelDoc2 root)
        {
            var components = ((IAssemblyDoc)root).GetComponents(false) as object[] ?? Array.Empty<object>();
            var active = components.OfType<IComponent2>().Where(c => !c.IsSuppressed()).ToArray();
            var bases = (((IAssemblyDoc)root).GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<IComponent2>()
                .Where(c => !c.IsSuppressed() && ComponentIdentifierMatches(c, "6GX.355.028")).ToArray();
            if (bases.Length != 1) throw new InvalidOperationException("028模板无法唯一定位顶层6GX.355.028：" + root.GetPathName());
            var baseAssembly = bases[0];
            RequireDefaultReference(baseAssembly.GetModelDoc2() as IModelDoc2, baseAssembly.ReferencedConfiguration, baseAssembly.Name2);
            IComponent2 Find(string prefix, int end, int side)
            {
                var matches = active.Where(c => c.Name2.StartsWith(baseAssembly.Name2 + "/", StringComparison.Ordinal) &&
                    ComponentIdentifierMatches(c, prefix)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("028子装配无法唯一定位立角钢：" + baseAssembly.Name2 + "/" + prefix);
                var component = matches[0];
                var transform = component.Transform2?.ArrayData as double[];
                if (transform == null || transform.Length < 12 || !(end * transform[9] > 0) || !(side * transform[11] > 0))
                    throw new InvalidOperationException("028立角钢变换与模板开口不符：" + component.Name2);
                return component;
            }
            var angle140 = Find("8GX.160.140", -1, -1);
            var angle141 = Find("8GX.160.141", 1, -1);
            var angle148 = Find("8GX.160.148", 1, 1);
            Find("8GX.160.147", -1, 1);
            // 在028原生坐标中，连接开口的两个切口沿X让位，外侧直排开口沿Z让位。
            return new[] { new AngleNotchTarget(angle140, "切除-拉伸5", "D1@草图9", 0),
                new AngleNotchTarget(angle141, "切除-拉伸6", "D2@草图10", 1),
                new AngleNotchTarget(angle141, "切除-拉伸5", "D1@草图9", 2),
                new AngleNotchTarget(angle148, "切除-拉伸5", "D1@草图9", 3) };
        }

        internal static bool NotchDepthMatches(double actual, double expected) =>
            (actual == 0d) == (expected == 0d) && Math.Abs(actual - expected) <= BridgeDesignPrecision.LengthToleranceMm;

        private static double ReadNotchDepth(AngleNotchTarget target, double maximumDepth = BridgeAngleNotchPlan.MaximumDepthMm)
        {
            var model = target.Component.GetModelDoc2() as IModelDoc2;
            RequireDefaultReference(model, target.Component.ReferencedConfiguration, target.Component.Name2);
            return ReadNotchDepth(model, target.Component.ReferencedConfiguration, target.Feature, target.Dimension, target.Component.Name2, maximumDepth);
        }

        internal static double ReadNotchDepth(IModelDoc2 model, string configuration, string feature, string dimension, string label,
            double maximumDepth = BridgeAngleNotchPlan.MaximumDepthMm)
        {
            var cut = NotchFeature(model, feature, label);
            var state = cut.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                new[] { configuration }) as bool[];
            if (state?.Length != 1) throw new InvalidOperationException("角钢缺少原豁口特征或引用配置：" + label + "/" + feature);
            if (state[0]) return 0d;
            var values = (model.Parameter(dimension) as Dimension)?.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                new[] { configuration }) as double[];
            var depth = values?.Length == 1 ? values[0] * 1000d : double.NaN;
            if (!(depth > 0d) || depth > maximumDepth + BridgeDesignPrecision.LengthToleranceMm)
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"角钢豁口尺寸不可读或超出{maximumDepth:0.0} mm上限：{label}/{dimension}"));
            RequireDefaultReference(model, model.ConfigurationManager.ActiveConfiguration.Name, label);
            if (cut.GetErrorCode2(out _) != 0 || !(cut.GetFaces() as object[] ?? Array.Empty<object>()).OfType<IFace2>().Any())
                throw new InvalidOperationException("角钢豁口没有有效的实体修剪面：" + label + "/" + feature);
            return depth;
        }

        private static Feature NotchFeature(IModelDoc2 model, string name, string label) =>
            (model as IPartDoc)?.FeatureByName(name) as Feature ??
            throw new InvalidOperationException("模板角钢缺少原豁口特征：" + label + "/" + name);

        internal static bool IsSharedSmallOffsetAngleEquation(string equation)
        {
            if (string.IsNullOrWhiteSpace(equation)) return false;
            var equals = equation.IndexOf('=');
            if (equals < 0) return false;
            var target = equation.Substring(0, equals).Trim().Trim('"');
            return MatchesAngleEquationTarget(target, "D1@草图1@8GX.160.001")
                || MatchesAngleEquationTarget(target, "D1@草图1@8GX.160.003")
                || MatchesAngleEquationTarget(target, "b@Sketch1@8GX.160.001")
                || MatchesAngleEquationTarget(target, "D2@Sketch1@8GX.160.003");
        }

        private static bool MatchesAngleEquationTarget(string target, string prefix)
        {
            return target.StartsWith(prefix, StringComparison.Ordinal) && target.Length > prefix.Length
                && (target[prefix.Length] == '.' || target[prefix.Length] == '<' || target[prefix.Length] == '-');
        }

        private static void DetachSmallOffsetSharedAngleEquations(IModelDoc2 model)
        {
            var manager = model.GetEquationMgr() as IEquationMgr
                ?? throw new InvalidOperationException("小偏移基架缺少方程式：" + model.GetPathName());
            // 角钢由实际文件的默认配置独立驱动，解除上级装配体对这些尺寸的共享写入。
            for (var i = manager.GetCount() - 1; i >= 0; i--)
                if (IsSharedSmallOffsetAngleEquation(manager.Equation[i]) && manager.Delete(i) < 0)
                    throw new InvalidOperationException("无法解除小偏移角钢共享方程式：" + model.GetPathName());
            for (var i = 0; i < manager.GetCount(); i++)
                if (IsSharedSmallOffsetAngleEquation(manager.Equation[i]))
                    throw new InvalidOperationException("小偏移角钢共享方程式仍然存在：" + model.GetPathName());
        }

        internal static int SmallOffsetAngleEnd(double[] transform)
        {
            if (transform == null || transform.Length < 12 || double.IsNaN(transform[9])
                || double.IsInfinity(transform[9]) || Math.Abs(transform[9] * 1000d) <= BridgeDesignPrecision.LengthToleranceMm)
                throw new InvalidOperationException("032立角钢缺少已核验的进出口变换。");
            // 032模板原点在桥向中点，进端X<0、出端X>0；不用Box或最近距离推断。
            return transform[9] < 0d ? -1 : 1;
        }

        internal static string NativeDefaultModelConfiguration(IModelDoc2 model)
        {
            if (model == null) throw new InvalidOperationException("无法识别未解析模型的原生默认配置。");
            var names = (model.GetConfigurationNames() as object[] ?? Array.Empty<object>())
                .Select(name => name as string).ToArray();
            return new NativeModelConfiguration().Resolve(names, name =>
            {
                var configuration = model.GetConfigurationByName(name) as IConfiguration
                    ?? throw new InvalidOperationException("模型配置无法读取：" + model.GetPathName() + "/" + name);
                return configuration.IsDerived();
            }, model.GetPathName());
        }

        internal static void RequireDefaultReference(IModelDoc2 model, string configuration, string identity)
        {
            new NativeModelConfiguration().RequireReference(configuration, NativeDefaultModelConfiguration(model), identity);
        }

        internal static string UseDefaultModelConfiguration(IModelDoc2 model)
        {
            var name = NativeDefaultModelConfiguration(model);
            new NativeModelConfiguration().Activate(name,
                () => model.ConfigurationManager.ActiveConfiguration.Name, model.ShowConfiguration2,
                () => EnsureSmallOffsetSealPlateRebuildSucceeded(model.EditRebuild3(), model.GetPathName()), model.GetPathName());
            return name;
        }

        private void DriveSmallOffsetAngleFiles(IModelDoc2 root, IModelDoc2 assembly,
            FaceToFaceSmallOffsetSealPlateDrivePlan plan, IDrawingNumberGenerator numbers, bool? templateNotches,
            BridgeAngleNotchPlan notchPlan)
        {
            RequireAngleInTransaction(root, assembly.GetPathName());
            UseDefaultModelConfiguration(assembly);
            var located = StraightNotchTargets(root, "SmallOffset");
            if (!BridgeGenerationFileBackup.Same(located.Assembly.GetPathName(), assembly.GetPathName()))
                throw new InvalidOperationException("032四根立角钢的实际所属装配体与封板子装不符：" + root.GetPathName());
            foreach (var prefix in new[] { "8GX.160.001", "8GX.160.003" })
                foreach (var end in new[] { -1, 1 })
                {
                    var component = FindSmallOffsetAngle(assembly, prefix, end);
                    var model = component.GetModelDoc2() as IModelDoc2;
                    RequireAngleInTransaction(root, component.GetPathName());
                    if (model == null) model = OpenAnglePart(component.GetPathName());
                    var configuration = NativeDefaultModelConfiguration(model);
                    if (component.ReferencedConfiguration == configuration) continue;
                    new SolidWorksDriver(_swApp).OpenOrActivateDocument(assembly.GetPathName(), rebuildAfterResolve: false);
                    component = FindSmallOffsetAngle(assembly, prefix, end);
                    component.ReferencedConfiguration = configuration;
                    EnsureSmallOffsetSealPlateRebuildSucceeded(assembly.EditRebuild3(), assembly.GetPathName());
                    RequireDefaultReference(model, FindSmallOffsetAngle(assembly, prefix, end).ReferencedConfiguration, prefix + "/" + end);
                }
            var targets = new List<FrameAngleFileTarget>();
            // 先冻结所有实例最终参数，再按文件决定共用或独立化，避免逐实例写入互相覆盖。
            foreach (var prefix in new[] { "8GX.160.001", "8GX.160.003" })
                foreach (var end in new[] { -1, 1 })
                {
                    var component = FindSmallOffsetAngle(assembly, prefix, end);
                    var path = component.GetPathName();
                    var model = OpenAnglePart(path);
                    var values = ReadDefaultAngleParameters(model, prefix, plan.FrameHeight);
                    var slot = BridgeAngleNotchPlan.Slot(end, prefix == "8GX.160.003" ? end : -end);
                    var special = BusbarBridgeDraftPreviewSceneFactory.SmallOffsetAngle(plan, slot);
                    if (special != null)
                    {
                        var depth = special.SuppressNotch ? 0d : special.NotchWidth.Value;
                        if (notchPlan?.InstanceDepthsMm != null)
                        {
                            RequireNotchDepth(notchPlan.StraightDepth(slot), depth, component.Name2);
                            depth = notchPlan.StraightDepth(slot);
                        }
                        values = new FrameAngleParameters(plan.FrameHeight, special.Width,
                            depth > 0d ? depth : values.NotchWidth, special.SuppressNotch);
                    }
                    else if (templateNotches.HasValue)
                    {
                        var depth = notchPlan?.StraightDepth(slot) ?? (templateNotches.Value ? 0d : 22d);
                        if (depth > 0d && !NotchDepthMatches(depth, 22d))
                            throw new InvalidOperationException("032角钢通用豁口目标不符合22.0 mm模板：" + component.Name2);
                        values = new FrameAngleParameters(plan.FrameHeight, values.Width,
                            depth > 0d ? depth : values.NotchWidth, depth == 0d);
                    }
                    targets.Add(new FrameAngleFileTarget(prefix, end, path, component.GetID(), values));
                }
            DriveAngleFiles(root, assembly, targets, numbers);
        }

        private SolidWorksParentDimensionDriver ParentDimensionsForFrame(IModelDoc2 root) =>
            new SolidWorksParentDimensionDriver(_swApp, path => BridgeGenerationFileBackup.Under(
                System.IO.Path.GetDirectoryName(root.GetPathName()), path));

        private void SynchronizeFrameParentDimensions(IModelDoc2 root)
        {
            var path = root.GetPathName();
            string key = null;
            var required = new List<(IComponent2 Target, string Dimension)>();
            if (SmallOffsetSealPlateIdentifierMatches(path, "5GX.355.024")) key = "Regular";
            else if (SmallOffsetSealPlateIdentifierMatches(path, "5GX.355.026")) key = FrameTemplateIdentity.FaceToWallLarge1.ToString();
            else if (SmallOffsetSealPlateIdentifierMatches(path, "5GX.355.029")) key = FrameTemplateIdentity.SideToSidePhaseChange029.ToString();
            else if (SmallOffsetSealPlateIdentifierMatches(path, "5GX.355.027"))
            {
                foreach (var target in StraightNotchTargets(root, FrameTemplateIdentity.FaceToWallLarge2.ToString()).Targets)
                {
                    required.Add((target.Component, "D1@草图1"));
                    if (target.Prefix == "8GX.160.136" || target.Prefix == "8GX.160.137")
                        required.Add((target.Component, "D3@草图4"));
                }
            }
            else if (SmallOffsetSealPlateIdentifierMatches(path, "5GX.355.028"))
            {
                var corner = CornerNotchTargets(root).Select(target => target.Component).GroupBy(component => component.GetID())
                    .Select(group => group.First()).ToArray();
                var owner = corner[0].GetParent();
                var angle147 = (((IAssemblyDoc)root).GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<IComponent2>()
                    .Single(component => !component.IsSuppressed() && component.GetParent()?.Name2 == owner.Name2 &&
                        ComponentIdentifierMatches(component, "8GX.160.147"));
                required.AddRange(corner.Concat(new[] { angle147 }).Select(component => (component, "D1@草图1")));
            }
            var peers = key == null ? null : StraightNotchTargets(root, key).Targets.GroupBy(target => target.Prefix)
                .Select(group => group.Select(target => target.Component).ToArray()).Where(group => group.Length > 1).ToArray();
            if (peers != null)
                foreach (var pair in peers.Where(pair => pair.Select(component => component.GetPathName())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
                    required.AddRange(pair.Select(component => (component, "D1@草图1")));
            ParentDimensionsForFrame(root).Synchronize(root, legacyPeers: peers, legacyTargets: required.Count == 0 ? null : required);
        }

        private NativeAngleFileOperations DriveAngleFiles(IModelDoc2 root, IModelDoc2 assembly,
            IReadOnlyList<FrameAngleFileTarget> targets, IDrawingNumberGenerator numbers)
        {
            var operations = new NativeAngleFileOperations(this, root, assembly);
            using (var context = new AngleAssemblyContext(_swApp, root))
            {
                try
                {
                    new FrameAngleFileDriver().Drive(targets, numbers, operations,
                        message => CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("角钢文件驱动计划", "装配体=" + assembly.GetPathName() + "；" + message));
                    operations.RestoreCommonSources(targets);
                }
                catch (Exception error) { context.Failure = error; throw; }
            }
            return operations;
        }

        private static IComponent2 FindSmallOffsetAngle(IModelDoc2 assembly, string prefix, int end)
        {
            var matches = (((IAssemblyDoc)assembly).GetComponents(true) as object[] ?? Array.Empty<object>())
                .OfType<IComponent2>().Where(c => ComponentIdentifierMatches(c, prefix) &&
                    SmallOffsetAngleEnd((double[])c.Transform2.ArrayData) == end).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("小偏移角钢实例不唯一：" + prefix + "/" + end + "；" + assembly.GetPathName());
            return matches[0];
        }

        private static FrameAngleParameters ReadDefaultAngleParameters(IModelDoc2 model, string prefix, double height)
        {
            UseDefaultModelConfiguration(model);
            var width = model.Parameter(prefix == "8GX.160.001" ? "b@Sketch1" : "D2@Sketch1") as Dimension;
            var notch = model.Parameter("D2@草图4") as Dimension;
            var feature = FindFeatureByName(model, "豁口");
            var state = feature?.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
            if (width == null || notch == null || state?.Length != 1)
                throw new InvalidOperationException("无法读取角钢默认配置的原尺寸及豁口：" + model.GetPathName());
            return new FrameAngleParameters(height, width.SystemValue * 1000d, notch.SystemValue * 1000d, state[0]);
        }

        private sealed class NativeAngleFileOperations : IFrameAngleFileOperations
        {
            private readonly SolidWorksFaceToFaceFrameModelOperations _owner;
            private readonly IModelDoc2 _root;
            private readonly IModelDoc2 _assembly;
            private readonly Dictionary<int, string> _independentPaths = new Dictionary<int, string>();
            private bool _restoredCommonSources;
            internal bool ReferencesChanged => _independentPaths.Count > 0 || _restoredCommonSources;
            internal NativeAngleFileOperations(SolidWorksFaceToFaceFrameModelOperations owner, IModelDoc2 root, IModelDoc2 assembly)
            { _owner = owner; _root = root; _assembly = assembly; }

            private IComponent2 FindTarget(FrameAngleFileTarget target)
            {
                var component = ((IAssemblyDoc)_assembly).GetComponentByID(target.ComponentId) as IComponent2;
                var path = _independentPaths.TryGetValue(target.ComponentId, out var independent) ? independent : target.Path;
                if (component == null || component.IsSuppressed() || !BridgeGenerationFileBackup.Same(component.GetPathName(), path))
                    throw new InvalidOperationException("角钢组件ID对应的实际文件与驱动计划不符：" + _assembly.GetPathName() + "/" + target);
                return component;
            }

            public void MakeIndependent(FrameAngleFileTarget target, FrameAngleFileTarget retained, string drawingNumber, string suffix)
            {
                var driver = new SolidWorksDriver(_owner._swApp);
                driver.OpenOrActivateDocument(_assembly.GetPathName(), rebuildAfterResolve: false);
                UseDefaultModelConfiguration(_assembly);
                SWHelper.EnsureAssemblyEditingContext(_owner._swApp, _assembly);
                // 比较两次求解完成后的状态，避免把旧装配位置与独立化后的新求解位置混比。
                EnsureSmallOffsetSealPlateRebuildSucceeded(_assembly.EditRebuild3(), _assembly.GetPathName());
                var component = FindTarget(target);
                var source = component.GetPathName();
                var dependencies = _owner.ParentDimensionsForFrame(_root);
                var inherited = dependencies.Capture(_root, FindInRoot(component));
                if (suffix != DrawingNumberHelper.ToFileNameSuffix(drawingNumber))
                    throw new InvalidOperationException("独立角钢图号与文件名后缀不一致。");
                var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source), target.Prefix + suffix + ".SLDPRT");
                if (System.IO.File.Exists(path)) throw new System.IO.IOException("独立角钢目标文件已存在：" + path);
                var context = "装配体=" + _assembly.GetPathName() + "；目标=" + target.Prefix + "/" + target.End + "；新文件=" + path;
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("角钢原生使之独立开始", context + "；原文件=" + source + "；新图号=" + drawingNumber);
                var before = CaptureAngleInstance(_assembly, component);
                var other = CaptureAngleInstance(_assembly, FindTarget(retained));
                TraceAngleIndependence("独立前目标", context, before);
                TraceAngleIndependence("独立前对角", context, other);
                _assembly.ClearSelection2(true);
                if (!component.Select4(false, null, false))
                    throw new InvalidOperationException("无法选择需要使之独立的角钢：" + target.Prefix + "/" + target.End);
                var selection = _assembly.SelectionManager as ISelectionMgr;
                if (selection?.GetSelectedObjectCount2(-1) != 1 || (selection.GetSelectedObject6(1, -1) as IComponent2)?.GetID() != before.Id)
                    throw new InvalidOperationException("角钢原生独立化选择集与目标实例不符：" + context);
                var independent = SolidWorksFileHelper.InvokeMakeIndependent(_assembly, path);
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("角钢原生使之独立返回", context + "；返回=" + independent + "；文件存在=" + System.IO.File.Exists(path));
                if (!independent || !System.IO.File.Exists(path))
                    throw new InvalidOperationException("角钢原生使之独立失败：" + path);
                _assembly.ClearSelection2(true);
                driver.OpenOrActivateDocument(_assembly.GetPathName(), rebuildAfterResolve: false);
                UseDefaultModelConfiguration(_assembly);
                SWHelper.EnsureAssemblyEditingContext(_owner._swApp, _assembly);
                EnsureSmallOffsetSealPlateRebuildSucceeded(_assembly.EditRebuild3(), _assembly.GetPathName());
                component = ((IAssemblyDoc)_assembly).GetComponentByID(before.Id) as IComponent2;
                var after = CaptureAngleInstance(_assembly, component);
                var otherAfter = CaptureAngleInstance(_assembly, ((IAssemblyDoc)_assembly).GetComponentByID(other.Id) as IComponent2);
                TraceAngleIndependence("独立后目标", context, after);
                TraceAngleIndependence("独立后对角", context, otherAfter);
                var verification = new FrameAngleIndependenceValidator().Validate(before, after, other, otherAfter, path);
                foreach (var detail in verification.Diagnostics)
                    CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("角钢独立化校验", context + "；" + detail);
                verification.RequireVerified();
                dependencies.Restore(_root, inherited);
                var model = _owner.OpenAnglePart(path);
                var configuration = NativeDefaultModelConfiguration(model);
                RequireDefaultReference(model, component.ReferencedConfiguration, component.Name2);
                var number = System.IO.Path.GetFileNameWithoutExtension(path);
                driver.SetCustomProperty(model, "图号", number);
                model.Extension.CustomPropertyManager[configuration].Add3("图号",
                    (int)swCustomInfoType_e.swCustomInfoText, number, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                string raw, resolved;
                model.Extension.CustomPropertyManager[configuration].Get4("图号", false, out raw, out resolved);
                if (raw != number) throw new InvalidOperationException("独立角钢默认配置图号未写入：" + path);
                SaveAngleModel(model);
                _independentPaths.Add(target.ComponentId, path);
            }

            private IComponent2 FindInRoot(IComponent2 component)
            {
                var source = component.GetPathName();
                var references = (((IAssemblyDoc)_root).GetComponents(false) as object[] ?? Array.Empty<object>())
                    .OfType<IComponent2>().Where(item => !item.IsSuppressed() && item.GetID() == component.GetID() &&
                        BridgeGenerationFileBackup.Same(item.GetPathName(), source) &&
                        (BridgeGenerationFileBackup.Same(_root.GetPathName(), _assembly.GetPathName()) ? item.GetParent() == null :
                            BridgeGenerationFileBackup.Same(item.GetParent()?.GetPathName(), _assembly.GetPathName()))).ToArray();
                if (references.Length != 1)
                    throw new InvalidOperationException("无法在基架树中唯一定位角钢：" + component.Name2);
                return references[0];
            }

            internal void RestoreCommonSources(IReadOnlyList<FrameAngleFileTarget> targets)
            {
                foreach (var pair in targets.GroupBy(target => target.Prefix, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderBy(target => target.End).ThenBy(target => target.ComponentId).ToArray()))
                {
                    if (pair.Length != 2 || !pair[0].SameDrive(pair[1])) continue;
                    var first = FindTarget(pair[0]);
                    var second = FindTarget(pair[1]);
                    if (BridgeGenerationFileBackup.Same(first.GetPathName(), second.GetPathName()) ||
                        !EquivalentAngleParts(first, second)) continue;
                    var dependencies = _owner.ParentDimensionsForFrame(_root);
                    var keep = dependencies.ChooseCommonSource(_root, FindInRoot(first), FindInRoot(second), pair[0].NotchDepthMm.HasValue);
                    if (keep < 0) continue;
                    ReplaceAngleSource(keep == 0 ? second : first, keep == 0 ? first : second, dependencies);
                }
            }

            private void ReplaceAngleSource(IComponent2 changing, IComponent2 retained, SolidWorksParentDimensionDriver dependencies)
            {
                var app = _owner._swApp;
                var driver = new SolidWorksDriver(app);
                var assemblyPath = _assembly.GetPathName();
                var sharedPath = retained.GetPathName();
                var original = FindInRoot(changing);
                var originalAddress = SolidWorksParentDimensionDriver.Address(original);
                var inherited = dependencies.Capture(_root, original);
                driver.OpenOrActivateDocument(assemblyPath, rebuildAfterResolve: false);
                SWHelper.EnsureAssemblyEditingContext(app, _assembly);
                var assembly = (IAssemblyDoc)_assembly;
                var before = CaptureAngleInstance(_assembly, changing);
                var other = CaptureAngleInstance(_assembly, retained);
                var oldIds = new HashSet<int>(LocalComponents().Select(component => component.GetID()));
                try
                {
                    var selection = _assembly.SelectionManager as ISelectionMgr;
                    if (!changing.Select4(false, null, false) || selection?.GetSelectedObjectCount2(-1) != 1 ||
                        (selection.GetSelectedObject6(1, -1) as IComponent2)?.GetID() != before.Id ||
                        !assembly.ReplaceComponents2(sharedPath, other.Configuration, false,
                            (int)swReplaceComponentsConfiguration_e.swReplaceComponentsConfiguration_MatchName, true))
                        throw new InvalidOperationException("恢复角钢共用引用失败：" + before.Name + " / " + sharedPath);
                }
                finally { _assembly.ClearSelection2(true); }

                driver.OpenOrActivateDocument(assemblyPath, rebuildAfterResolve: false);
                SWHelper.EnsureAssemblyEditingContext(app, _assembly);
                EnsureSmallOffsetSealPlateRebuildSucceeded(_assembly.EditRebuild3(), assemblyPath);
                var matches = LocalComponents().Where(component => !component.IsSuppressed() &&
                    (component.GetID() == before.Id || !oldIds.Contains(component.GetID())) &&
                    BridgeGenerationFileBackup.Same(component.GetPathName(), sharedPath)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException("替换后无法唯一定位恢复共用的角钢：" + before.Name);
                var replacement = matches[0];
                var after = CaptureAngleInstance(_assembly, replacement);
                var otherAfter = CaptureAngleInstance(_assembly, assembly.GetComponentByID(other.Id) as IComponent2);
                var newId = after.Id;
                if (newId != before.Id)
                {
                    var replacementId = newId.ToString(CultureInfo.InvariantCulture);
                    var originalId = before.Id.ToString(CultureInfo.InvariantCulture);
                    foreach (var mate in after.Mates.Concat(otherAfter.Mates))
                        foreach (var reference in mate.References.Where(reference => reference.Instance == replacementId))
                            reference.Instance = originalId;
                    after.Id = before.Id;
                }
                var verified = new FrameAngleIndependenceValidator().Validate(before, after, other, otherAfter, sharedPath);
                if (verified.Failures.Count != 0)
                    throw new InvalidOperationException("角钢恢复共用后校验失败：" + string.Join("；", verified.Failures));
                SWHelper.EnsureAssemblyEditingContext(app, _root);
                EnsureSmallOffsetSealPlateRebuildSucceeded(_root.EditRebuild3(), _root.GetPathName());
                var newAddress = SolidWorksParentDimensionDriver.Address(FindInRoot(replacement));
                dependencies.Restore(_root, SolidWorksParentDimensionDriver.RemapRestoredSource(inherited, originalAddress, newAddress));
                _restoredCommonSources = true;
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("角钢已恢复共用", before.Name + "；原文件=" + before.Path + "；共用文件=" + sharedPath);
            }

            private IComponent2[] LocalComponents() => (((IAssemblyDoc)_assembly).GetComponents(true) as object[] ?? Array.Empty<object>())
                .OfType<IComponent2>().ToArray();

            private static bool EquivalentAngleParts(IComponent2 first, IComponent2 second)
            {
                var config = first.ReferencedConfiguration;
                if (config != second.ReferencedConfiguration || !(first.GetModelDoc2() is IPartDoc a) || !(second.GetModelDoc2() is IPartDoc b))
                    return false;
                var aModel = (IModelDoc2)a;
                var bModel = (IModelDoc2)b;
                if (aModel.ConfigurationManager.ActiveConfiguration.Name != config || bModel.ConfigurationManager.ActiveConfiguration.Name != config)
                    return false;
                var aMaterial = a.GetMaterialPropertyName2(config, out var aDatabase);
                var bMaterial = b.GetMaterialPropertyName2(config, out var bDatabase);
                if (aMaterial != bMaterial || aDatabase != bDatabase || !PartFeatures(aModel).SequenceEqual(PartFeatures(bModel)) ||
                    !PartEquations(aModel).SequenceEqual(PartEquations(bModel))) return false;
                var aValues = ReferencedModelValueReader.FromComponent(first);
                var bValues = ReferencedModelValueReader.FromComponent(second);
                // 压缩的豁口不出现在实体里，仍须核对原草图尺寸，避免共用时丢失潜在加工参数。
                foreach (var name in new[] { "D1@草图1", "D1@草图4", "D2@草图4", "D3@草图4" })
                {
                    var hasA = aValues.TryDimension(name, out var aValue);
                    var hasB = bValues.TryDimension(name, out var bValue);
                    if (hasA != hasB || hasA && Math.Abs(aValue - bValue) > BridgeDesignPrecision.LengthToleranceMm) return false;
                }
                var aBodies = (a.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? Array.Empty<object>()).OfType<IBody2>().ToArray();
                var bBodies = (b.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? Array.Empty<object>()).OfType<IBody2>().ToList();
                if (aBodies.Length == 0 || aBodies.Length != bBodies.Count) return false;
                var identity = new[] { 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d };
                var comparer = new BridgeTransformComparison();
                foreach (var body in aBodies)
                {
                    var index = bBodies.FindIndex(candidate =>
                    {
                        if (!body.GetCoincidenceTransform2(candidate, out var map) || !(map?.ArrayData is double[] transform)) return false;
                        var difference = comparer.Compare(identity, transform);
                        return difference.Valid && difference.TranslationMm <= BridgeDesignPrecision.NumericTolerance &&
                            difference.RotationDegrees <= BridgeDesignPrecision.NumericTolerance && difference.Scale <= BridgeDesignPrecision.NumericTolerance;
                    });
                    if (index < 0) return false;
                    bBodies.RemoveAt(index);
                }
                return true;
            }

            private static IEnumerable<string> PartFeatures(IModelDoc2 part)
            {
                for (var feature = part.FirstFeature() as IFeature; feature != null; feature = feature.GetNextFeature() as IFeature)
                    yield return feature.GetTypeName2() + "|" + feature.Name + "|" + feature.IsSuppressed();
            }

            private static IEnumerable<string> PartEquations(IModelDoc2 part)
            {
                var manager = part.GetEquationMgr() as IEquationMgr;
                if (manager == null) yield break;
                for (var index = 0; index < manager.GetCount(); index++)
                    yield return manager.Suppression[index] + "|" + manager.Equation[index];
            }

            private void TraceAngleIndependence(string stage, string context, FrameAngleInstanceState state) =>
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("角钢使之独立/" + stage, context + "；" + (state?.ToString() ?? "实例未解析"));

            public void Drive(FrameAngleFileTarget target)
            {
                var component = FindTarget(target);
                var model = _owner.OpenAnglePart(component.GetPathName());
                var configuration = NativeDefaultModelConfiguration(model);
                RequireDefaultReference(model, component.ReferencedConfiguration, component.Name2);
                if (target.NotchDepthMm.HasValue)
                {
                    const string feature = "切除-拉伸3", dimension = "D2@草图4";
                    var depth = target.NotchDepthMm.Value;
                    WriteNotchDepth(model, feature, dimension, depth);
                    EnsureSmallOffsetSealPlateRebuildSucceeded(model.EditRebuild3(), model.GetPathName());
                    RequireNotchDepth(ReadNotchDepth(model, configuration, feature, dimension, component.Name2), depth, component.Name2);
                    SaveAngleModel(model);
                    return;
                }
                var values = target.Parameters;
                SetDefaultAngleDimension(model, "D1@草图1", values.Height - 45.5d);
                SetDefaultAngleDimension(model, target.Prefix == "8GX.160.001" ? "b@Sketch1" : "D2@Sketch1", values.Width);
                // 仅有一个原生配置的焊件也必须使用单配置方程式接口。
                SetSmallOffsetCoverEquation((IEquationMgr)model.GetEquationMgr(), configuration, model.GetConfigurationCount() == 1,
                    "D2@草图4", values.NotchWidth.ToString("R", CultureInfo.InvariantCulture) + "mm", false);
                SetSmallOffsetNotchSuppression(FindFeatureByName(model, "豁口")
                    ?? throw new InvalidOperationException("角钢缺少豁口：" + model.GetPathName()), values.SuppressNotch, model.GetPathName());
                EnsureSmallOffsetSealPlateRebuildSucceeded(model.EditRebuild3(), model.GetPathName());
                var actual = ReadDefaultAngleParameters(model, target.Prefix,
                    ((Dimension)model.Parameter("D1@草图1")).SystemValue * 1000d + 45.5d);
                if (!values.Same(actual)) throw new InvalidOperationException("角钢默认配置驱动后实际参数不符：" + model.GetPathName());
                RequireNotchDepth(ReadNotchDepth(model, configuration, "豁口", "D2@草图4", component.Name2, 40d),
                    values.SuppressNotch ? 0d : values.NotchWidth, component.Name2);
                SaveAngleModel(model);
            }
        }

        private static void SetDefaultAngleDimension(IModelDoc2 model, string name, double value)
        {
            var dimension = model.Parameter(name) as Dimension;
            if (dimension == null || value <= 0 || dimension.SetSystemValue3(value / 1000d,
                (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null) != 0)
                throw new InvalidOperationException("无法写入角钢默认配置尺寸：" + model.GetPathName() + "/" + name);
        }

        private sealed class AngleAssemblyContext : IDisposable
        {
            private readonly ISldWorks _app;
            private readonly IModelDoc2 _root;
            private readonly string _configuration;
            internal Exception Failure { get; set; }
            internal AngleAssemblyContext(ISldWorks app, IModelDoc2 root)
            { _app = app; _root = root; _configuration = NativeDefaultModelConfiguration(root); }
            public void Dispose()
            {
                RestoreSmallOffsetConfigurationAndEditingContext(Failure, null,
                    () => SWHelper.EnsureAssemblyEditingContext(_app, _root), "角钢原生默认配置", _configuration, _root.GetPathName());
            }
        }

        internal static void SaveAngleModel(IModelDoc2 model)
        {
            RequireDefaultReference(model, model.ConfigurationManager.ActiveConfiguration.Name, model.GetPathName());
            if (!model.GetSaveFlag()) return;
            var errors = 0; var warnings = 0;
            EnsureSmallOffsetSealPlateSaveSucceeded(model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                ref errors, ref warnings), errors, model.GetPathName());
        }

        private IModelDoc2 OpenAnglePart(string path)
        {
            var errors = 0; var warnings = 0;
            var model = _swApp.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, string.Empty, ref errors, ref warnings) as IModelDoc2;
            var active = _swApp.ActivateDoc3(path, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as IModelDoc2;
            if (model == null || active == null || !BridgeGenerationFileBackup.Same(active.GetPathName(), path))
                throw new InvalidOperationException("无法独立激活角钢零件：" + path + "；错误码=" + errors);
            UseDefaultModelConfiguration(active);
            return active;
        }
    }
}
