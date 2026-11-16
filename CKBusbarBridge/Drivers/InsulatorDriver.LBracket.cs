using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CKBusbarBridge.Helpers;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    public partial class InsulatorDriver
    {
        private void EnsureCopiedInsulatorReferencesAreIsolated(string sourceRoot, string copiedRoot, string suffix)
        {
            var directory = Path.GetFullPath(Path.GetDirectoryName(copiedRoot)) + Path.DirectorySeparatorChar;
            var sources = ReadInsulatorDocumentDependencies(sourceRoot, true).Concat(new[] { Path.GetFullPath(sourceRoot) })
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            var mapping = sources.ToDictionary(p => p,
                p => Path.Combine(directory, Path.GetFileNameWithoutExtension(p) + suffix + Path.GetExtension(p)), StringComparer.OrdinalIgnoreCase);
            if (mapping.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.Count || mapping.Values.Any(p => !File.Exists(p)))
                throw new InvalidOperationException("绝缘子PackAndGo副本缺失或文件名映射不唯一：" + copiedRoot);
            var rewrites = new List<Tuple<string, string, string>>();
            foreach (var copy in mapping.Values.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var reference in ReadInsulatorDocumentDependencies(copy, false))
                {
                    if (reference.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!mapping.TryGetValue(reference, out var replacement))
                        throw new InvalidOperationException("绝缘子副本包含未映射的外部引用：" + copy + "；" + reference);
                    rewrites.Add(Tuple.Create(copy, reference, replacement));
                }
            }
            var openPaths = new HashSet<string>(((swApp.GetDocuments() as object[]) ?? Array.Empty<object>()).OfType<IModelDoc2>()
                .Select(d => d.GetPathName()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            if (rewrites.Any(r => openPaths.Contains(r.Item1)))
                throw new InvalidOperationException("绝缘子副本仍有外部引用，需先关闭副本文档再修复引用：" + copiedRoot);
            // SW2022实测：PackAndGo虽已追加文件名后缀，副本内部仍可能保留源路径。仅修改关闭的副本。
            foreach (var rewrite in rewrites)
                if (!swApp.ReplaceReferencedDocument(rewrite.Item1, rewrite.Item2, rewrite.Item3))
                    throw new InvalidOperationException("绝缘子副本引用重绑定失败：" + rewrite.Item1 + "；" + rewrite.Item2 + " → " + rewrite.Item3);
            EnsureInsulatorReferencesInsideOutputTree(copiedRoot);
        }

        private void EnsureInsulatorReferencesInsideOutputTree(string root)
        {
            var directory = Path.GetFullPath(Path.GetDirectoryName(root)) + Path.DirectorySeparatorChar;
            var references = ReadInsulatorDocumentDependencies(root, true);
            if (references.Length == 0) throw new InvalidOperationException("绝缘子根装配体引用清单缺失：" + root);
            foreach (var reference in references)
                if (!reference.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !File.Exists(reference))
                    throw new InvalidOperationException("绝缘子磁盘引用不在当前输出树内或文件缺失，禁止打开驱动：" + root + "；" + reference);
        }

        private string[] ReadInsulatorDocumentDependencies(string path, bool traverse)
        {
            var raw = swApp.GetDocumentDependencies2(path, traverse, false, false) as string[] ?? Array.Empty<string>();
            if (raw.Length % 2 != 0) throw new InvalidOperationException("绝缘子文件引用清单格式无效：" + path);
            return Enumerable.Range(0, raw.Length / 2).Select(i => raw[i * 2 + 1])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), p)))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private sealed partial class InsulatorSolidWorksDriveOperations : ILBracketInsulatorDriveOperations
        {
            private readonly ISldWorks _app;
            private readonly SolidWorksDriver _driver;
            private readonly IModelDoc2 _root;
            private readonly AssemblyDoc _assembly;
            private readonly string _directory;
            private readonly Action<IModelDoc2> _register;
            private readonly InsulatorDriveDiagnostics _diagnostics;
            private readonly InsulatorDriveRecoverySession _activation;
            private readonly Dictionary<string, IModelDoc2> _models = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, IModelDoc2> _padAssemblies = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, IModelDoc2> _padParts = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
            private IModelDoc2 _bracket;

            internal InsulatorSolidWorksDriveOperations(ISldWorks app, SolidWorksDriver driver,
                IModelDoc2 root, Action<IModelDoc2> register)
                : this(app, driver, root, register, null)
            {
            }

            internal InsulatorSolidWorksDriveOperations(ISldWorks app, SolidWorksDriver driver,
                IModelDoc2 root, Action<IModelDoc2> register, InsulatorDriveDiagnostics diagnostics)
            {
                _app = app; _driver = driver; _root = root; _register = register;
                _diagnostics = diagnostics;
                _activation = new InsulatorDriveRecoverySession(app);
                _assembly = root as AssemblyDoc ?? throw new InvalidOperationException("L型绝缘子不是装配体：" + root.GetPathName());
                _directory = Path.GetFullPath(Path.GetDirectoryName(root.GetPathName())) + Path.DirectorySeparatorChar;
            }

            public void Prepare()
            {
                // 先取得整棵模板树；N从小变大时，旧压缩状态不能让待驱动零件从清单中消失。
                Activate(_root);
                var supports = ApplyLBracketPadSubAssemblySuppression(_assembly, null, _root.GetPathName());
                _bracket = RequireModel(UniquePart(Children(_root), "8GX.043.021-L"), swDocumentTypes_e.swDocPART);
                foreach (var support in supports.OrderBy(c => c.Name2, StringComparer.Ordinal))
                {
                    var model = RequireModel(support, swDocumentTypes_e.swDocASSEMBLY);
                    var key = Identity(model);
                    if (!_padAssemblies.ContainsKey(key)) _padAssemblies.Add(key, model);
                }
                // 所有父文档先登记，共用的叶子后登记，反向保存时不会先保存另一个父文档。
                foreach (var model in _padAssemblies.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value))
                {
                    Activate(model);
                    SetStackPattern(model, false);
                    var parts = PadComponents(model);
                    foreach (var pair in parts.OrderBy(p => p.Key))
                    {
                        SetSuppressed(pair.Value, false);
                        var part = RequireModel(pair.Value, swDocumentTypes_e.swDocPART);
                        _padParts[Identity(part)] = part;
                    }
                    RequireModel(UniquePart(Children(model), "绝缘子-L"), swDocumentTypes_e.swDocPART);
                }
            }

            public void DriveParts(LBracketInsulatorDrivePlan plan)
            {
                // 顶层外部方程和零件自有方程先持有同一个目标值，重建仍按叶子到父级进行。
                WriteVariables(_root, new Dictionary<string, object> {
                    [PhaseSpacingGlobalVariableName] = plan.PhaseSpacing, [BracketLengthGlobalVariableName] = plan.BracketLength });
                WriteVariables(_bracket, new Dictionary<string, object> { [PhaseSpacingGlobalVariableName] = plan.PhaseSpacing });
                Rebuild(_bracket);
                RequireActual(_bracket.GetPathName() + "/相间距", Global(_bracket, PhaseSpacingGlobalVariableName), plan.PhaseSpacing);
                foreach (var part in _padParts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value))
                {
                    WritePadThickness(part, plan.PadThickness);
                    Rebuild(part);
                    RequireActual(part.GetPathName() + "/实体厚度", PhysicalPadThickness(part), plan.PadThickness);
                }
            }

            public void DrivePadAssemblies(LBracketInsulatorDrivePlan plan)
            {
                foreach (var model in _padAssemblies.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value))
                {
                    WriteVariables(model, new Dictionary<string, object> {
                        [InsulatorHeightGlobalVariableName] = plan.BodyHeight, [BusbarThicknessGlobalVariableName] = plan.Thickness });
                    Rebuild(model);
                    var originals = PadComponents(model);
                    var originalTransforms = originals.ToDictionary(p => p.Key, p => p.Value.Transform2?.ArrayData as double[]);
                    foreach (var pair in originals.OrderBy(p => p.Key))
                        SetSuppressed(pair.Value, !plan.EnabledPadIndexes.Contains(pair.Key));
                    Rebuild(model);
                    SetStackPattern(model, plan.UseStackedPads && plan.EnabledPadIndexes.Count > 0);
                    if (plan.UseStackedPads && plan.EnabledPadIndexes.Count > 0)
                        SynchronizeStackCopies(model, plan.EnabledPadIndexes, plan.PadThickness, originalTransforms);
                    Rebuild(model);
                }
            }

            public void DriveAssembly(LBracketInsulatorDrivePlan plan)
            {
                Activate(_root);
                ApplyLBracketPadSubAssemblySuppression(_assembly, plan.SuppressedSupportIndexes, _root.GetPathName());
                Rebuild(_root);
            }

            public LBracketInsulatorReadback Readback()
            {
                foreach (var pair in _models.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.Equals(pair.Key, Identity(pair.Value), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("L型绝缘子反读时活动配置已变化：" + pair.Key);
                    VerifyFeatures(pair.Value);
                }
                VerifyFeatures(_root);
                var partValues = _padParts.ToDictionary(p => p.Key, p => new[] {
                    BaseThickness(p.Value), SheetThickness(p.Value), PhysicalPadThickness(p.Value) }, StringComparer.OrdinalIgnoreCase);
                var pads = _padAssemblies.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new LBracketPadAssemblyReadback {
                    Identity = p.Key, BodyHeight = Global(p.Value, InsulatorHeightGlobalVariableName),
                    Thickness = Global(p.Value, BusbarThicknessGlobalVariableName),
                    Distance1 = Equation(p.Value, "D1@距离1", false), Distance2 = Equation(p.Value, "D1@距离2", false),
                    Distance3 = Equation(p.Value, "D1@距离3", false),
                    StackPatternPresent = StackPattern(p.Value) != null,
                    StackPatternSuppressed = StackPattern(p.Value)?.IsSuppressed() ?? true,
                    StackSpacing = StackPattern(p.Value) == null ? 0d : Dimension(p.Value, "D3@排厚12阵列"),
                    ActiveStackCopies = StackCopies(p.Value, partValues),
                    Members = PadComponents(p.Value).OrderBy(member => member.Key).Select(member => ReadPad(member.Key, member.Value, partValues)).ToArray()
                }).ToArray();
                var supports = Children(_root).Where(c => TryResolveLBracketPadComponentIndex(c, out _))
                    .Select(ReadSupport).OrderBy(s => s.Index).ToArray();
                return new LBracketInsulatorReadback {
                    TopPhaseSpacing = Global(_root, PhaseSpacingGlobalVariableName), TopBracketLength = Global(_root, BracketLengthGlobalVariableName),
                    BracketPhaseSpacing = Global(_bracket, PhaseSpacingGlobalVariableName), BracketLength = Dimension(_bracket, "D2@草图2"),
                    BracketPitch1 = Dimension(_bracket, "D3@阵列(线性)1"), BracketPitch2 = Dimension(_bracket, "D4@阵列(线性)1"),
                    BracketThickness = SheetThickness(_bracket), PadAssemblies = pads, Supports = supports
                };
            }

            private LBracketPadMemberReadback ReadPad(int index, Component2 component, IDictionary<string, double[]> values)
            {
                var key = ComponentIdentity(component);
                if (!values.TryGetValue(key, out var actual))
                    throw new InvalidOperationException("L型垫块反读引用已变化：" + component.Name2);
                return new LBracketPadMemberReadback { Index = index, Identity = key, Suppressed = component.IsSuppressed(),
                    BaseThickness = actual[0], SheetThickness = actual[1], PhysicalThickness = actual[2] };
            }

            private LBracketSupportReadback ReadSupport(Component2 component)
            {
                if (!TryResolveLBracketPadComponentIndex(component, out var index))
                    throw new InvalidOperationException("L型绝缘子安装实例编号缺失：" + component.Name2);
                var result = new LBracketSupportReadback { Index = index, Suppressed = component.IsSuppressed() };
                if (result.Suppressed) return result;
                var model = RequireModel(component, swDocumentTypes_e.swDocASSEMBLY);
                result.PadAssemblyIdentity = Identity(model);
                var body = UniquePart((component.GetChildren() as object[] ?? Array.Empty<object>()).OfType<Component2>(), "绝缘子-L");
                RequireModel(body, swDocumentTypes_e.swDocPART);
                var geometry = InsulatorConnectionPlaneReader.ReadConnectionOnly(body, _app);
                if (geometry.ConnectionCircleCenter == null || geometry.ConnectionPlane == null)
                    throw new InvalidOperationException("L型绝缘子实际连接圆缺失：" + body.Name2);
                result.X = geometry.ConnectionCircleCenter.X; result.Y = geometry.ConnectionCircleCenter.Y;
                result.Z = geometry.ConnectionCircleCenter.Z; result.Radius = geometry.ConnectionCircleRadius;
                return result;
            }

            private IModelDoc2 RequireModel(Component2 component, swDocumentTypes_e type)
            {
                var path = Path.GetFullPath(component.GetPathName());
                if (!path.StartsWith(_directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("L型绝缘子引用不在当前输出树内，禁止驱动源模板：" + component.Name2 + "；" + path);
                var model = component.GetModelDoc2() as IModelDoc2;
                if (model == null || model.GetType() != (int)type ||
                    !string.Equals(Path.GetFullPath(model.GetPathName()), path, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(component.ReferencedConfiguration) ||
                    !string.Equals(model.ConfigurationManager?.ActiveConfiguration?.Name, component.ReferencedConfiguration, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("L型绝缘子模型或引用配置不一致：" + component.Name2 + "；" + path);
                var key = Identity(model);
                if (!_models.ContainsKey(key)) { _models.Add(key, model); _register(model); }
                return model;
            }

            private void Activate(IModelDoc2 model)
            {
                _diagnostics?.Capture("激活文档前", model);
                _activation.Activate(model.GetPathName(), model.ConfigurationManager.ActiveConfiguration.Name);
                _diagnostics?.Capture("激活文档后", model);
            }

            private void WriteVariables(IModelDoc2 model, IDictionary<string, object> variables)
            {
                Activate(model);
                _diagnostics?.Capture("批量写变量前", model);
                try
                {
                    _driver.ApplyGlobalVariablesRequired(model, variables, rebuildAfterChange: false,
                        equationWriteObserver: (manager, index, previous, requested) =>
                        {
                            // Status 必须紧跟 setter 读取，读取其他方程属性会覆盖这个状态。
                            var writeStatus = manager.Status;
                            var actualEquation = manager.Equation[index];
                            _diagnostics?.EquationWritten(model, index, previous, requested, writeStatus, actualEquation);
                            if (writeStatus < 0)
                                throw new InsulatorDriveRetryException("绝缘子方程写入失败：" + model.GetPathName()
                                    + "；索引=" + index + "；状态=" + writeStatus
                                    + "；请求=" + requested + "；实际=" + actualEquation);
                        });
                }
                finally { _diagnostics?.Capture("批量写变量后", model); }
            }

            private void Rebuild(IModelDoc2 model)
            {
                Activate(model);
                _diagnostics?.Capture("原重建调用前", model);
                if (!model.ForceRebuild3(false) || !model.EditRebuild3())
                {
                    _diagnostics?.Capture("原重建调用返回失败", model);
                    throw new InsulatorDriveRetryException("绝缘子重建失败：" + model.GetPathName());
                }
                _diagnostics?.Capture("原重建调用后", model);
            }

            private void WritePadThickness(IModelDoc2 model, double thickness)
            {
                Activate(model);
                var feature = Feature(model, "基体-法兰1", "SMBaseFlange");
                var data = feature.GetDefinition() as IBaseFlangeFeatureData;
                if (data == null) throw new InvalidOperationException("L型垫块基体法兰定义缺失：" + model.GetPathName());
                var access = false; var applied = false;
                try
                {
                    access = data.AccessSelections(model, null);
                    if (!access) throw new InsulatorDriveRetryException("无法取得垫块厚度编辑访问：" + model.GetPathName());
                    data.Thickness = thickness / 1000d;
                    applied = feature.ModifyDefinition(data, model, null);
                    if (!applied) throw new InsulatorDriveRetryException("垫块实体厚度写入失败：" + model.GetPathName()
                        + "；期望=" + thickness.ToString("R", CultureInfo.InvariantCulture));
                }
                catch (COMException ex)
                {
                    throw new InsulatorDriveRetryException("垫块实体厚度驱动失败：" + model.GetPathName(), ex);
                }
                finally
                {
                    if (access && !applied) data.ReleaseSelectionAccess();
                    model.ClearSelection2(true);
                }
            }

            private static Component2[] Children(IModelDoc2 model) =>
                (((AssemblyDoc)model).GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<Component2>().ToArray();

            private static Component2 UniquePart(IEnumerable<Component2> components, string prefix)
            {
                var candidates = components.Where(c => MatchesPart(c, prefix)).ToArray();
                if (candidates.Length != 1 || candidates[0].IsSuppressed())
                    throw new InvalidOperationException("L型绝缘子必须唯一确定未压缩的零件：" + prefix);
                return candidates[0];
            }

            private static bool MatchesPart(Component2 component, string prefix)
            {
                var name = Path.GetFileNameWithoutExtension(component.GetPathName());
                return string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
            }

            private static Dictionary<int, Component2> PadComponents(IModelDoc2 model, string prefix = "8UQ.584.013-L")
            {
                // 副本替换零件引用后SW会重编号，业务位置按已核验的距离配合链1→3→2识别。
                var first = MatePads(model, "距离1", prefix);
                var middle = MatePads(model, "距离2", prefix);
                var last = MatePads(model, "距离3", prefix);
                if (first.Length != 1 || middle.Length != 2 || last.Length != 2
                    || !middle.Any(c => c.Name2 == first[0].Name2))
                    throw new InvalidOperationException("L型垫块距离配合链缺失或不唯一：" + model.GetPathName());
                var third = middle.Single(c => c.Name2 != first[0].Name2);
                if (!last.Any(c => c.Name2 == third.Name2))
                    throw new InvalidOperationException("L型垫块距离3未连接中间垫块：" + model.GetPathName());
                var second = last.Single(c => c.Name2 != third.Name2);
                if (second.Name2 == first[0].Name2)
                    throw new InvalidOperationException("L型垫块距离配合链形成重复环：" + model.GetPathName());
                return new Dictionary<int, Component2> { [1] = first[0], [3] = third, [2] = second };
            }

            private static Component2[] MatePads(IModelDoc2 model, string name, string prefix)
            {
                var feature = ((AssemblyDoc)model).FeatureByName(name) as Feature;
                var mate = feature?.GetSpecificFeature2() as Mate2;
                if (mate == null) throw new InvalidOperationException("L型垫块原距离配合缺失：" + model.GetPathName() + "/" + name);
                return Enumerable.Range(0, mate.GetMateEntityCount())
                    .Select(i => mate.MateEntity(i)?.ReferenceComponent as Component2)
                    .Where(c => c != null && MatchesPart(c, prefix))
                    .GroupBy(c => c.Name2, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            }

            private static void SetSuppressed(Component2 component, bool suppressed)
            {
                component.SetSuppression2((int)(suppressed ? swComponentSuppressionState_e.swComponentSuppressed : swComponentSuppressionState_e.swComponentResolved));
                if (component.IsSuppressed() != suppressed)
                    throw new InsulatorDriveRetryException("垫块压缩状态未生效：" + component.Name2
                        + "；实际=" + component.IsSuppressed() + "；期望=" + suppressed);
            }

            private static Feature StackPattern(IModelDoc2 model) =>
                ((AssemblyDoc)model).FeatureByName(TwelveMillimeterPadPatternName) as Feature;

            private static void SetStackPattern(IModelDoc2 model, bool enabled)
            {
                var feature = StackPattern(model);
                if (feature == null)
                {
                    if (enabled) throw new InvalidOperationException("L型12厚绝缘子缺少排厚12阵列：" + model.GetPathName());
                    return;
                }
                if (feature.IsSuppressed() == !enabled) return;
                var action = enabled ? swFeatureSuppressionAction_e.swUnSuppressFeature : swFeatureSuppressionAction_e.swSuppressFeature;
                if (!feature.SetSuppression2((int)action, (int)swInConfigurationOpts_e.swThisConfiguration, null)
                    || feature.IsSuppressed() == enabled)
                    throw new InsulatorDriveRetryException("绝缘子排厚12阵列状态写入失败：" + model.GetPathName());
            }

            private static int StackCopies(IModelDoc2 model, IDictionary<string, double[]> partValues)
            {
                var copies = StackMembers(model);
                if (copies.Any(c => !partValues.ContainsKey(ComponentIdentity(c))))
                    throw new InvalidOperationException("L型叠加垫块引用模型或配置不一致：" + model.GetPathName());
                return copies.Count(c => !c.IsSuppressed());
            }

            private static Component2[] StackMembers(IModelDoc2 model, string prefix = "8UQ.584.013-L")
            {
                var originals = new HashSet<string>(PadComponents(model, prefix).Values.Select(c => c.Name2), StringComparer.OrdinalIgnoreCase);
                return Children(model).Where(c => MatchesPart(c, prefix) && !originals.Contains(c.Name2)).ToArray();
            }

            private void SynchronizeStackCopies(IModelDoc2 model, IReadOnlyList<int> enabledIndexes, double thickness,
                IDictionary<int, double[]> originalTransforms, string prefix = "8UQ.584.013-L")
            {
                var copies = StackMembers(model, prefix);
                foreach (var copy in copies) SetSuppressed(copy, false);
                Rebuild(model);
                var matched = new HashSet<int>();
                foreach (var copy in copies)
                {
                    var transform = copy.Transform2?.ArrayData as double[];
                    var candidates = originalTransforms.Where(p => MatchesStackOffset(p.Value, transform, thickness)).ToArray();
                    if (candidates.Length != 1 || !matched.Add(candidates[0].Key))
                        throw new InsulatorDriveRetryException("叠加垫块未唯一对应原垫块的6mm变换：" + copy.Name2);
                    SetSuppressed(copy, !enabledIndexes.Contains(candidates[0].Key));
                }
                if (matched.Count != 3)
                    throw new InsulatorDriveRetryException("排厚12阵列未完整覆盖三个原垫块：" + model.GetPathName());
            }

            private static bool MatchesStackOffset(double[] original, double[] copy, double thickness)
            {
                if (original == null || copy == null || original.Length < 12 || copy.Length < 12) return false;
                if (Enumerable.Range(0, 9).Any(i => Math.Abs(original[i] - copy[i]) > 1e-8d)) return false;
                var distance = Math.Sqrt(Enumerable.Range(9, 3).Sum(i => (copy[i] - original[i]) * (copy[i] - original[i]))) * 1000d;
                return !double.IsNaN(distance) && Math.Abs(distance - thickness) < LBracketInsulatorDrivePlan.ToleranceMm;
            }

            private static string Identity(IModelDoc2 model) =>
                Path.GetFullPath(model.GetPathName()) + "|" + model.ConfigurationManager.ActiveConfiguration.Name;
            private static string ComponentIdentity(Component2 component) =>
                Path.GetFullPath(component.GetPathName()) + "|" + component.ReferencedConfiguration;
            private static double Global(IModelDoc2 model, string name) => Equation(model, name, true);

            private static double Equation(IModelDoc2 model, string name, bool global)
            {
                var manager = model.GetEquationMgr() as IEquationMgr;
                var indexes = new List<int>();
                if (manager != null) for (var i = 0; i < manager.GetCount(); i++)
                {
                    var expression = manager.Equation[i]; var equals = expression.IndexOf('=');
                    if (equals > 0 && manager.GlobalVariable[i] == global && expression.Substring(0, equals).Trim().Trim('"') == name)
                        indexes.Add(i);
                }
                if (indexes.Count != 1) throw new InvalidOperationException("L型绝缘子原方程缺失或重复：" + model.GetPathName() + "/" + name);
                return manager.Value[indexes[0]];
            }

            private static double Dimension(IModelDoc2 model, string name)
            {
                var value = model.Parameter(name) as Dimension;
                if (value == null) throw new InvalidOperationException("L型绝缘子原驱动尺寸缺失：" + model.GetPathName() + "/" + name);
                return value.SystemValue * 1000d;
            }

            private static Feature Feature(IModelDoc2 model, string name, string type)
            {
                var feature = ((PartDoc)model).FeatureByName(name) as Feature;
                if (feature == null || feature.GetTypeName2() != type)
                    throw new InvalidOperationException("L型绝缘子原驱动特征缺失：" + model.GetPathName() + "/" + name);
                return feature;
            }

            private static double BaseThickness(IModelDoc2 model)
            {
                var data = Feature(model, "基体-法兰1", "SMBaseFlange").GetDefinition() as IBaseFlangeFeatureData;
                if (data == null) throw new InvalidOperationException("L型垫块原基体法兰不可读取：" + model.GetPathName());
                return data.Thickness * 1000d;
            }

            private static double SheetThickness(IModelDoc2 model)
            {
                var data = Feature(model, "钣金8", "SheetMetal").GetDefinition() as ISheetMetalFeatureData;
                if (data == null) throw new InvalidOperationException("L型绝缘子原钣金定义不可读取：" + model.GetPathName());
                return data.Thickness * 1000d;
            }

            private static double PhysicalPadThickness(IModelDoc2 model)
            {
                var bodies = (((PartDoc)model).GetBodies2(0, false) as object[] ?? Array.Empty<object>()).OfType<Body2>().ToArray();
                if (bodies.Length != 1) throw new InvalidOperationException("L型垫块必须为单一实体：" + model.GetPathName());
                var planes = (bodies[0].GetFaces() as object[] ?? Array.Empty<object>()).OfType<Face2>()
                    .Select(f => new { Face = f, Surface = f.GetSurface() as Surface })
                    .Where(p => p.Surface != null && p.Surface.IsPlane())
                    .Select(p => new { Area = p.Face.GetArea(), Parameters = p.Surface.PlaneParams as double[] })
                    .OrderByDescending(p => p.Area).ToArray();
                if (planes.Length < 2 || planes.Take(2).Any(p => p.Parameters == null || p.Parameters.Length != 6) ||
                    Math.Abs(planes[0].Area - planes[1].Area) > 1e-10d)
                    throw new InvalidOperationException("L型垫块的两侧实际大平面无法唯一核验：" + model.GetPathName());
                var a = planes[0].Parameters; var b = planes[1].Parameters;
                var dot = Enumerable.Range(0, 3).Sum(i => a[i] * b[i]);
                if (Math.Abs(Math.Abs(dot) - 1d) > 1e-8d)
                    throw new InvalidOperationException("L型垫块两侧实际平面不平行：" + model.GetPathName());
                return Math.Abs(Enumerable.Range(0, 3).Sum(i => a[i] * (b[i + 3] - a[i + 3]))) * 1000d;
            }

            private static void VerifyFeatures(IModelDoc2 model)
            {
                for (var feature = model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
                {
                    VerifyFeature(model, feature);
                    if (feature.GetTypeName2() == "MateGroup")
                        for (var mate = feature.GetFirstSubFeature() as Feature; mate != null; mate = mate.GetNextSubFeature() as Feature)
                            VerifyFeature(model, mate);
                }
            }

            private static void VerifyFeature(IModelDoc2 model, Feature feature)
            {
                var code = feature.GetErrorCode2(out var warning);
                if (code != 0 && !warning)
                    throw new InsulatorDriveRetryException("绝缘子驱动后特征或配合错误：" + model.GetPathName() + "/" + feature.Name + "；错误码=" + code);
            }
        }
    }
}
