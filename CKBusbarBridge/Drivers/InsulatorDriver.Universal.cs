using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    public partial class InsulatorDriver
    {
        internal const string UniversalOffsetProperty = "通用绝缘子偏移布局";

        private string[] UniversalInsulatorTemplateFiles(string template)
            => new[] { Path.GetFileName(template), "绝缘子垫块.SLDASM", "绝缘子.SLDPRT",
                "8UQ.584.013.SLDPRT", "8GX.043.002.SLDPRT", "8GX.043.021.SLDPRT" };

        private void RepairUniversalInsulatorOutputReferences(string template, string outputDirectory, string suffix)
        {
            Func<string, string> outputPath = name => Path.GetFullPath(Path.Combine(outputDirectory,
                Path.GetFileNameWithoutExtension(name) + suffix + Path.GetExtension(name)));
            var files = UniversalInsulatorTemplateFiles(template).Select(outputPath)
                .Concat(new[] { "8GX.043.002.SLDDRW", "8GX.043.021.SLDDRW" }.Select(outputPath).Where(File.Exists));
            var openPaths = new HashSet<string>((swApp.GetDocuments() as object[] ?? Array.Empty<object>())
                .OfType<IModelDoc2>().Select(d => d.GetPathName()).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            new InsulatorOutputReferenceRepair().Repair(files, p => ReadInsulatorDocumentDependencies(p, false),
                File.Exists, openPaths.Contains, (document, reference, target) => swApp.ReplaceReferencedDocument(document, reference, target));
        }

        internal void CopyUniversalTemplateWithPackAndGo(string template, string outputDirectory, string suffix)
        {
            var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(template));
            var required = UniversalInsulatorTemplateFiles(template);
            foreach (var name in required)
                if (!File.Exists(Path.Combine(sourceDirectory, name)))
                    throw new FileNotFoundException("通用绝缘子模板缺少契约文件：" + name, Path.Combine(sourceDirectory, name));
            var drawings = new[] { "8GX.043.002.SLDDRW", "8GX.043.021.SLDDRW" }
                .Where(name => File.Exists(Path.Combine(sourceDirectory, name)));
            var names = required.Concat(drawings).ToArray();
            var outputRoot = Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar;
            var staging = Path.GetFullPath(Path.Combine(outputDirectory, ".通用模板准备-" + Guid.NewGuid().ToString("N")));
            if (!staging.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("通用绝缘子准备目录超出输出树。");
            Directory.CreateDirectory(staging);
            var targets = names.ToDictionary(name => name, name => Path.Combine(staging,
                Path.GetFileNameWithoutExtension(name) + suffix + Path.GetExtension(name)), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in targets)
            {
                using (var input = new FileStream(Path.Combine(sourceDirectory, pair.Key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var output = new FileStream(pair.Value, FileMode.CreateNew, FileAccess.Write)) input.CopyTo(output);
            }
            // 固定模板契约明确每个引用文件；只修复副本内的旧作者路径，不搜索或修改源模板。
            foreach (var copy in targets.Values)
            {
                foreach (var reference in ReadInsulatorDocumentDependencies(copy, false))
                {
                    if (!targets.TryGetValue(Path.GetFileName(reference), out var replacement))
                        throw new InvalidOperationException("通用绝缘子模板含契约外引用：" + copy + "；" + reference);
                    if (!swApp.ReplaceReferencedDocument(copy, reference, replacement))
                        throw new InvalidOperationException("通用绝缘子准备副本重绑定失败：" + copy + "；" + reference);
                }
            }
            var stageRoot = targets[Path.GetFileName(template)];
            new SolidWorksFileHelper((SldWorks)swApp).CopyFileUsePackAndGoIncludingSuppressed(stageRoot, outputDirectory);
            var stagedOpen = (swApp.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<IModelDoc2>()
                .Select(d => d.GetPathName()).Where(p => p.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var path in stagedOpen)
            {
                var doc = swApp.GetOpenDocumentByName(path) as IModelDoc2;
                if (doc != null) swApp.CloseDoc(doc.GetTitle());
            }
            var finalByName = targets.Values.ToDictionary(Path.GetFileName,
                p => Path.Combine(outputDirectory, Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase);
            foreach (var copy in finalByName.Values)
            {
                // SW2022会遗漏未加载的压缩件/工程图；补齐固定契约中已规范引用的副本，不搜索同名文件。
                if (!File.Exists(copy)) File.Copy(Path.Combine(staging, Path.GetFileName(copy)), copy, false);
                foreach (var reference in ReadInsulatorDocumentDependencies(copy, false))
                {
                    if (!finalByName.TryGetValue(Path.GetFileName(reference), out var replacement))
                        throw new InvalidOperationException("通用绝缘子副本含契约外引用：" + copy + "；" + reference);
                    if (!string.Equals(reference, replacement, StringComparison.OrdinalIgnoreCase)
                        && !swApp.ReplaceReferencedDocument(copy, reference, replacement))
                        throw new InvalidOperationException("通用绝缘子输出副本重绑定失败：" + copy + "；" + reference);
                }
            }
            EnsureInsulatorReferencesInsideOutputTree(finalByName[Path.GetFileName(stageRoot)]);
            // staging已在上方核验位于本次输出树内，且仅由本次调用创建。
            Directory.Delete(staging, true);
        }

        internal static bool IsUniversalInsulator(IModelDoc2 model)
        {
            var assembly = model as AssemblyDoc;
            return assembly != null && (assembly.FeatureByName("L型支架") != null
                || assembly.FeatureByName("普通支架") != null);
        }

        internal static bool ReadUniversalLBracket(IModelDoc2 model)
        {
            var reader = new ReferencedModelValueReader(model);
            var l = reader.Suppressed("L型支架");
            var ordinary = reader.Suppressed("普通支架");
            if (l == ordinary) throw new InvalidOperationException("通用绝缘子的两种支架必须互斥：" + model.GetPathName());
            return !l;
        }

        internal static UniversalInsulatorLayout ReadUniversalLayout(ReferencedModelValueReader reader)
        {
            var length = reader.Global("支架长度");
            return new UniversalInsulatorLayout(length,
                UniversalInsulatorLayout.DistanceNames.Select(name => length / 2d - reader.Global(name)));
        }

        internal void DriveUniversalInsulator(IModelDoc2 model, UniversalInsulatorDrivePlan plan,
            Action<IModelDoc2> registerModel, bool offsetLayout)
        {
            EnsureInsulatorReferencesInsideOutputTree(model.GetPathName());
            var recovery = _driveRecovery ?? new InsulatorDriveRecoverySession(swApp);
            recovery.Drive(model, (currentModel, registerCurrentModel) =>
                DriveUniversalInsulatorOnce(currentModel, plan, registerCurrentModel, offsetLayout),
                registerModel, allowDeferred: _driveRecovery != null);
        }

        private void DriveUniversalInsulatorOnce(IModelDoc2 model, UniversalInsulatorDrivePlan plan,
            Action<IModelDoc2> registerModel, bool offsetLayout)
        {
            var diagnostics = new InsulatorDriveDiagnostics(swApp, model);
            diagnostics.Start(plan, offsetLayout);
            InsulatorSolidWorksDriveOperations operations = null;
            try
            {
                EnsureInsulatorReferencesInsideOutputTree(model.GetPathName());
                operations = new InsulatorSolidWorksDriveOperations(swApp, new SolidWorksDriver(swApp), model, registerModel, diagnostics);
                plan.Drive(operations);
                var properties = model.Extension.CustomPropertyManager[string.Empty];
                properties.Add3(UniversalOffsetProperty, (int)swCustomInfoType_e.swCustomInfoText,
                    offsetLayout ? "1" : "0", (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                diagnostics.Capture("驱动完成", model);
            }
            catch (Exception driveError)
            {
                // 在上层不保存关闭副本前保留运行时证据。
                diagnostics.Write("驱动失败异常", () => driveError.ToString());
                if (operations == null) diagnostics.Capture("失败时顶层方程", model);
                else operations.CaptureUniversalFailure();
                throw;
            }
        }

        internal void DriveMountedInsulatorRecovery(IModelDoc2 model, Prototype.BridgeInsulatorMountPlan mount,
            double bracketLength, double thickness, int roots, Action<IModelDoc2> register)
        {
            EnsureInsulatorReferencesInsideOutputTree(model.GetPathName());
            var diagnostics = new InsulatorDriveDiagnostics(swApp, model);
            new InsulatorSolidWorksDriveOperations(swApp, new SolidWorksDriver(swApp), model, register, diagnostics)
                .DriveMounted(mount, bracketLength, thickness, roots);
        }

        private sealed partial class InsulatorSolidWorksDriveOperations : IUniversalInsulatorDriveOperations
        {
            private IModelDoc2 _sharedInsulator;
            private IModelDoc2 _sharedPad;
            private IModelDoc2 _sharedBody;
            private Component2[] _universalGroups;
            private Component2 _universalBody;
            private List<string> _readbackFailures;

            internal void DriveMounted(Prototype.BridgeInsulatorMountPlan mount, double bracketLength, double thickness, int roots)
            {
                Activate(_root);
                SetBracketFolder("L型支架", true);
                SetBracketFolder("普通支架", false);
                // 异高组已替换为独立模型，按原安装角色逐组驱动，不能再套用共享子件约束。
                _universalGroups = FaceToFaceFrameInsulatorDriver.MountedGroups(_root);
                _bracket = RequireModel(UniquePart(Children(_root), "8GX.043.002"), swDocumentTypes_e.swDocPART);
                EnsureUniversalMountCoordinate(false);
                var layout = new UniversalInsulatorLayout(bracketLength, mount.Transverse);
                WriteVariables(_root, layout.Variables());
                var failures = new List<string>();
                for (var groupIndex = 0; groupIndex < 3; groupIndex++)
                {
                    Activate(_root);
                    SetSuppressed(_universalGroups[groupIndex], mount.BodyHeights[groupIndex] <= 0);
                    if (mount.BodyHeights[groupIndex] <= 0) continue;
                    _sharedInsulator = RequireModel(_universalGroups[groupIndex], swDocumentTypes_e.swDocASSEMBLY);
                    Activate(_sharedInsulator);
                    SetStackPattern(_sharedInsulator, false);
                    var pads = PadComponents(_sharedInsulator, "8UQ.584.013");
                    foreach (var pad in pads.Values) SetSuppressed(pad, false);
                    if (pads.Values.Select(ComponentIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                        throw new InvalidOperationException("逐相绝缘子垫块引用不唯一：" + _sharedInsulator.GetPathName());
                    _sharedPad = RequireModel(pads[1], swDocumentTypes_e.swDocPART);
                    _universalBody = UniquePart(Children(_sharedInsulator), "绝缘子");
                    _sharedBody = RequireModel(_universalBody, swDocumentTypes_e.swDocPART);
                    var plan = new UniversalInsulatorDrivePlan(layout, false, mount.BodyHeights[groupIndex], thickness, roots,
                        Enumerable.Range(0, 3).Where(index => mount.BodyHeights[index] <= 0).Select(index => index + 1));
                    try
                    {
                        _readbackFailures = null;
                        RebindSharedBodyEquation();
                        var operations = (IUniversalInsulatorDriveOperations)this;
                        operations.WriteSharedInsulator(plan);
                        operations.WriteSharedPad(plan);
                        operations.ConfigurePadInstances(plan);
                        operations.CompleteAndValidate(plan);
                    }
                    catch (InsulatorDriveRetryException mismatch)
                    { failures.Add("安装组" + (groupIndex + 1) + "：" + mismatch.Message); }
                }
                if (failures.Count > 0) throw new InsulatorDriveRetryException(string.Join(System.Environment.NewLine, failures));
            }

            void IUniversalInsulatorDriveOperations.Prepare(UniversalInsulatorDrivePlan plan)
            {
                Activate(_root);
                SetBracketFolder(plan.UseLBracket ? "普通支架" : "L型支架", true);
                SetBracketFolder(plan.UseLBracket ? "L型支架" : "普通支架", false);
                var all = Children(_root).Where(c => TryResolveLBracketPadComponentIndex(c, out _)).ToArray();
                if (all.Length != 3 || all.Select(c => ComponentIdentity(c)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                    throw new InvalidOperationException("通用绝缘子的三组子件必须共用同一模型和引用配置：" + _root.GetPathName());
                _universalGroups = ApplyLBracketPadSubAssemblySuppression(_assembly, plan.SuppressedGroups, _root.GetPathName()).ToArray();
                _sharedInsulator = RequireModel(_universalGroups[0], swDocumentTypes_e.swDocASSEMBLY);
                _bracket = RequireModel(UniquePart(Children(_root), plan.UseLBracket ? "8GX.043.021" : "8GX.043.002"), swDocumentTypes_e.swDocPART);
                Activate(_sharedInsulator);
                SetStackPattern(_sharedInsulator, false);
                var pads = PadComponents(_sharedInsulator, "8UQ.584.013");
                if (pads.Values.Select(ComponentIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                    throw new InvalidOperationException("通用绝缘子的垫块必须共用同一零件模型和引用配置。");
                foreach (var pad in pads.OrderBy(p => p.Key)) SetSuppressed(pad.Value, false);
                _sharedPad = RequireModel(pads[1], swDocumentTypes_e.swDocPART);
                _universalBody = UniquePart(Children(_sharedInsulator), "绝缘子");
                _sharedBody = RequireModel(_universalBody, swDocumentTypes_e.swDocPART);
                // 尺寸失败允许先装配；安装基准须在写尺寸之前准备好。
                EnsureUniversalMountCoordinate(plan.UseLBracket);
                RebindSharedBodyEquation();
            }

            void IUniversalInsulatorDriveOperations.WriteTopVariables(UniversalInsulatorDrivePlan plan)
            {
                WriteVariables(_root, plan.Layout.Variables());
            }

            void IUniversalInsulatorDriveOperations.WriteSharedInsulator(UniversalInsulatorDrivePlan plan)
            {
                WriteVariables(_sharedInsulator, new Dictionary<string, object> {
                    [InsulatorHeightGlobalVariableName] = plan.BodyHeight, [BusbarThicknessGlobalVariableName] = plan.Thickness });
            }

            void IUniversalInsulatorDriveOperations.WriteSharedPad(UniversalInsulatorDrivePlan plan)
            {
                WritePadThickness(_sharedPad, plan.PadThickness);
                Rebuild(_sharedPad);
                RequireActual("垫块实体厚度", PhysicalPadThickness(_sharedPad), plan.PadThickness);
            }

            void IUniversalInsulatorDriveOperations.ConfigurePadInstances(UniversalInsulatorDrivePlan plan)
            {
                Rebuild(_sharedInsulator);
                var pads = PadComponents(_sharedInsulator, "8UQ.584.013");
                var transforms = pads.ToDictionary(p => p.Key, p => p.Value.Transform2?.ArrayData as double[]);
                foreach (var pad in pads.OrderBy(p => p.Key)) SetSuppressed(pad.Value, !plan.EnabledPadIndexes.Contains(pad.Key));
                Rebuild(_sharedInsulator);
                var stacked = plan.UseStackedPads && plan.EnabledPadIndexes.Count > 0;
                SetStackPattern(_sharedInsulator, stacked);
                if (stacked) SynchronizeStackCopies(_sharedInsulator, plan.EnabledPadIndexes, plan.PadThickness, transforms, "8UQ.584.013");
                Rebuild(_sharedInsulator);
            }

            void IUniversalInsulatorDriveOperations.CompleteAndValidate(UniversalInsulatorDrivePlan plan)
            {
                Rebuild(_root);
                _readbackFailures = new List<string>();
                var bracketLengthActual = Global(_root, "支架长度");
                _diagnostics?.Readback(_root, "支架长度", bracketLengthActual, plan.Layout.BracketLength);
                RequireActual("支架长度", bracketLengthActual, plan.Layout.BracketLength);
                var bracketSketchLengthActual = UniversalBracketSketchLength();
                _diagnostics?.Readback(_bracket, "支架实际草图长度", bracketSketchLengthActual, plan.Layout.BracketLength);
                RequireActual("支架实际草图长度", bracketSketchLengthActual, plan.Layout.BracketLength);
                for (var index = 0; index < 3; index++)
                    RequireActual(UniversalInsulatorLayout.DistanceNames[index], Global(_root, UniversalInsulatorLayout.DistanceNames[index]), plan.Layout.Distances[index]);
                RequireActual("绝缘子高度", Global(_sharedInsulator, "绝缘子高度"), plan.BodyHeight);
                RequireActual("排厚", Global(_sharedInsulator, "排厚"), plan.Thickness);
                RequireActual("绝缘子本体实际高度", Dimension(_sharedBody, "D2@草图1"), plan.BodyHeight);
                RequireActual("距离1", Equation(_sharedInsulator, "D1@距离1", false), plan.Thickness);
                RequireActual("距离2", Equation(_sharedInsulator, "D1@距离2", false), plan.Thickness * 2d);
                RequireActual("距离3", Equation(_sharedInsulator, "D1@距离3", false), plan.Thickness * 2d);
                RequireActual("垫块基体厚度", BaseThickness(_sharedPad), plan.PadThickness);
                RequireActual("垫块钣金厚度", SheetThickness(_sharedPad), plan.PadThickness);
                var pads = PadComponents(_sharedInsulator, "8UQ.584.013");
                foreach (var pad in pads)
                    if (pad.Value.IsSuppressed() == plan.EnabledPadIndexes.Contains(pad.Key))
                        _readbackFailures.Add("垫块=" + pad.Value.Name2 + "；实际压缩=" + pad.Value.IsSuppressed()
                            + "；期望压缩=" + !plan.EnabledPadIndexes.Contains(pad.Key));
                var copies = StackMembers(_sharedInsulator, "8UQ.584.013");
                var expectedCopies = plan.UseStackedPads ? plan.EnabledPadIndexes.Count : 0;
                if (copies.Count(c => !c.IsSuppressed()) != expectedCopies)
                    _readbackFailures.Add("叠片数量；实际=" + copies.Count(c => !c.IsSuppressed()) + "；期望=" + expectedCopies);
                if (expectedCopies > 0) RequireActual("排厚12阵列间距", Dimension(_sharedInsulator, "D3@排厚12阵列"), 6d);
                foreach (var group in _universalGroups)
                {
                    if (group.IsSuppressed()) continue;
                    if (!FaceToFaceFrameInsulatorDriver.TryMountedGroupIndex(group, out var index))
                        throw new InvalidOperationException("绝缘子组安装角色缺失：" + group.Name2);
                    var transform = group.Transform2?.ArrayData as double[];
                    if (transform == null) throw new InvalidOperationException("通用绝缘子组变换不可读取：" + group.Name2);
                    RequireActual("绝缘子组" + index + "实际Z", transform[11] * 1000d,
                        plan.Layout.BracketLength / 2d - plan.Layout.Distances[index - 1]);
                }
                EnsureUniversalMountCoordinate(plan.UseLBracket);
                foreach (var pair in _models)
                {
                    if (!string.Equals(pair.Key, Identity(pair.Value), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("通用绝缘子驱动期间模型引用配置已变化：" + pair.Key);
                    VerifyFeatures(pair.Value);
                }
                VerifyFeatures(_root);
                if (_readbackFailures.Count > 0)
                    throw new InsulatorDriveRetryException("通用绝缘子驱动反读不符：" + _root.GetPathName()
                        + System.Environment.NewLine + string.Join(System.Environment.NewLine, _readbackFailures));
            }

            internal void CaptureUniversalFailure()
            {
                _diagnostics?.Capture("失败时顶层方程", _root);
                _diagnostics?.Capture("失败时共享绝缘子方程", _sharedInsulator);
                _diagnostics?.Capture("失败时支架方程", _bracket);
                if (_bracket != null)
                    _diagnostics?.Write("失败时支架草图几何", () =>
                        "支架实际草图长度=" + UniversalBracketSketchLength().ToString("R", CultureInfo.InvariantCulture));
            }

            private void SetBracketFolder(string name, bool suppressed)
            {
                var feature = _assembly.FeatureByName(name) as Feature;
                var folder = feature?.GetSpecificFeature2() as FeatureFolder;
                var members = folder?.GetFeatures() as object[];
                if (members == null || members.Length == 0) throw new InvalidOperationException("通用绝缘子支架文件夹缺失或为空：" + name);
                var action = suppressed ? swFeatureSuppressionAction_e.swSuppressFeature : swFeatureSuppressionAction_e.swUnSuppressFeature;
                if (!feature.SetSuppression2((int)action, (int)swInConfigurationOpts_e.swThisConfiguration, null))
                    throw new InsulatorDriveRetryException("通用绝缘子支架文件夹切换失败：" + name);
                var components = Children(_root);
                foreach (Feature member in members)
                {
                    var matches = components.Where(c => c.Name2.Split('/').Last() == member.Name).ToArray();
                    if (matches.Length != 1)
                        throw new InvalidOperationException("通用绝缘子支架文件夹成员状态不符：" + name + "/" + member.Name);
                    // 文件夹解压后成员可能仍为轻量化；显式解析同一成员，再核验实际组件状态。
                    SetSuppressed(matches[0], suppressed);
                }
            }

            private void RebindSharedBodyEquation()
            {
                Activate(_sharedInsulator);
                var manager = _sharedInsulator.GetEquationMgr() as IEquationMgr;
                if (manager == null) throw new InvalidOperationException("共享绝缘子子件缺少方程式。");
                var indexes = Enumerable.Range(0, manager.GetCount()).Where(i => manager.Equation[i].TrimStart().StartsWith("\"D2@草图1@", StringComparison.Ordinal)).ToArray();
                if (indexes.Length != 1) throw new InvalidOperationException("共享绝缘子本体高度方程缺失或重复。");
                var instance = Regex.Match(_universalBody.Name2, @"-(\d+)$");
                if (!instance.Success) throw new InvalidOperationException("共享绝缘子本体实例编号不可读取。");
                // PackAndGo可能只更新模型引用，不更新外部方程的文件名。只重绑定同一原尺寸到副本。
                manager.Equation[indexes[0]] = "\"D2@草图1@" + Path.GetFileNameWithoutExtension(_sharedBody.GetPathName())
                    + "<" + instance.Groups[1].Value + ">.Part\" = \"绝缘子高度\"";
                var rebindStatus = manager.Status;
                if (rebindStatus < 0)
                    throw new InsulatorDriveRetryException("绝缘子本体原高度方程写入失败：" + _sharedInsulator.GetPathName()
                        + "；实际=" + manager.Equation[indexes[0]] + "；期望关联=绝缘子高度");
            }

            private double UniversalBracketSketchLength()
            {
                var sketch = (((PartDoc)_bracket).FeatureByName("草图2") as Feature)?.GetSpecificFeature2() as Sketch;
                var points = (sketch?.GetSketchPoints2() as object[] ?? Array.Empty<object>()).OfType<SketchPoint>().ToArray();
                if (points.Length < 4) throw new InvalidOperationException("通用绝缘子支架长度草图缺失有效端点。");
                // 两种通用支架均已实测：草图2的Y方向为长度方向，端点为±L/2。
                return (points.Max(p => p.Y) - points.Min(p => p.Y)) * 1000d;
            }

            private void EnsureUniversalMountCoordinate(bool useLBracket)
            {
                Activate(_root);
                var feature = _assembly.FeatureByName("安装坐标系") as Feature;
                if (feature == null)
                {
                    _root.ClearSelection2(true);
                    // 旧普通/L型模板只读实测：安装原点均为(0,-25,0)mm，L型X/Z轴反向。
                    // SW2022新增数值坐标系API；动态调用保留项目既有interop引用版本。
                    feature = ((dynamic)_root.FeatureManager).CreateCoordinateSystemUsingNumericalValues(
                        true, 0d, -.025d, 0d, false, 0d, 0d, 0d) as Feature;
                    if (feature == null) throw new InvalidOperationException("无法为通用绝缘子副本建立原有安装坐标系。");
                    feature.Name = "安装坐标系";
                }
                var data = feature.GetDefinition() as ICoordinateSystemFeatureData;
                if (data == null) throw new InvalidOperationException("通用绝缘子安装坐标系不可读取。");
                if (data.XFlipped != useLBracket || data.ZFlipped != useLBracket)
                {
                    var access = data.AccessSelections((ModelDoc2)_root, null);
                    if (!access) throw new InvalidOperationException("无法编辑通用绝缘子安装方向。");
                    var applied = false;
                    try
                    {
                        data.XFlipped = useLBracket; data.ZFlipped = useLBracket;
                        applied = feature.ModifyDefinition(data, _root, null);
                        if (!applied) throw new InvalidOperationException("通用绝缘子安装方向写入失败。");
                    }
                    finally { if (!applied) data.ReleaseSelectionAccess(); }
                }
                var transform = ((ICoordinateSystemFeatureData)feature.GetDefinition()).Transform.ArrayData as double[];
                var expected = new[] { useLBracket ? -1d : 1d, 0, 0, 0, 1, 0, 0, 0, useLBracket ? -1d : 1d, 0, -.025d, 0, 1 };
                if (transform == null || transform.Length < expected.Length || expected.Where((value, index) => Math.Abs(value - transform[index]) > 1e-8d).Any())
                    throw new InvalidOperationException("通用绝缘子安装基准与原模板不一致。");
            }

            private void RequireActual(string source, double actual, double expected)
            {
                if (double.IsNaN(actual) || double.IsInfinity(actual) || Math.Abs(actual - expected) > UniversalInsulatorLayout.ToleranceMm)
                {
                    var failure = string.Format(CultureInfo.InvariantCulture,
                        "绝缘子驱动反读不符：{0}，实际={1:0.###}，期望={2:0.###}。", source, actual, expected);
                    if (_readbackFailures == null) throw new InsulatorDriveRetryException(failure);
                    _readbackFailures.Add(failure);
                }
            }
        }
    }
}
