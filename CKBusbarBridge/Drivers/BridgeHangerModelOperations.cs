using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Prototype;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed class BridgeHangerDocumentCloser
    {
        private readonly IFaceToFaceFrameDocumentSession _session;
        private readonly Func<string, bool> _isVisible;
        internal BridgeHangerDocumentCloser(IFaceToFaceFrameDocumentSession session, Func<string, bool> isVisible)
        { _session = session ?? throw new ArgumentNullException(nameof(session)); _isVisible = isVisible ?? throw new ArgumentNullException(nameof(isVisible)); }

        internal void Close(string directory, IEnumerable<string> assemblyPaths)
        {
            var root = Path.GetFullPath(directory);
            var documents = _session.GetOpenDocuments().Where(d => BridgeGenerationFileBackup.Under(root, d.FilePath)).ToArray();
            // 模板不能随收尾写回，也不能因关闭父装配体而丢弃任一子件的未保存修改。
            var modified = documents.Where(d => d.IsModified).Select(d => d.FilePath).ToArray();
            if (modified.Length > 0) throw new InvalidOperationException("吊杆文档有未保存修改，已保留窗口：" + string.Join("；", modified));
            var assemblies = new HashSet<string>(assemblyPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            var paths = documents.Select(d => d.FilePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => assemblies.Contains(p) ? 0 : 1).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in paths)
            {
                // 关闭总成可能连带卸载子件；每次重新获取文档，不沿用已失效的COM包装对象。
                var current = _session.GetOpenDocuments().SingleOrDefault(d => BridgeGenerationFileBackup.Same(d.FilePath, path));
                if (current == null || !_isVisible(path)) continue;
                if (current.IsModified) throw new InvalidOperationException("关闭期间吊杆文档出现未保存修改，已保留窗口：" + path);
                current.Close();
            }
            var remaining = _session.GetOpenDocuments().Select(d => d.FilePath)
                .Where(p => BridgeGenerationFileBackup.Under(root, p) && _isVisible(p)).ToArray();
            if (remaining.Length > 0) throw new IOException("吊杆模型窗口仍未关闭：" + string.Join("；", remaining));
        }
    }

    internal sealed partial class BridgeHangerFileCleanup
    {
        private readonly string _directory;
        private readonly BridgeGenerationFileBackup _backup;
        private readonly Func<string, FaceToFaceFrameCleanupConfirmation> _close;
        private readonly IBridgeHangerCleanupSession _session;
        private readonly bool _requireTransactionBackup = true;
        internal BridgeHangerFileCleanup(string directory, BridgeGenerationFileBackup backup,
            Func<string, FaceToFaceFrameCleanupConfirmation> close)
        {
            _directory = Path.GetFullPath(directory);
            _backup = backup ?? throw new ArgumentNullException(nameof(backup));
            _close = close ?? throw new ArgumentNullException(nameof(close));
        }

        internal BridgeHangerFileCleanup(string directory, BridgeGenerationFileBackup backup,
            IBridgeHangerCleanupSession session, bool requireTransactionBackup = true)
        {
            _directory = Path.GetFullPath(directory);
            _backup = backup ?? throw new ArgumentNullException(nameof(backup));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _requireTransactionBackup = requireTransactionBackup;
        }

        internal void Execute(IEnumerable<string> candidates, Func<IEnumerable<string>> readReferences)
        {
            if (_requireTransactionBackup && !File.Exists(Path.Combine(_backup.Location, "transaction.state")))
                throw new InvalidOperationException("吊杆文件清理缺少有效事务备份。");
            var directories = candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var directory in directories)
                if (!BridgeGenerationFileBackup.Under(_directory, directory) || BridgeGenerationFileBackup.Same(_directory, directory) || !_backup.Owns(directory))
                    throw new InvalidOperationException("废弃吊杆目录超出本次事务输出范围，禁止清理：" + directory);
            if (_session != null)
            {
                ExecuteWithSession(directories, readReferences);
                return;
            }
            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory) || IsReferenced(directory, readReferences)) continue;
                RejectLinks(directory);
                var closed = _close(directory);
                if (closed?.IsConfirmed != true) throw new IOException("废弃吊杆引用关闭未确认：" + directory + "；" + closed?.Diagnostic);
                // 关闭文档可能触发宿主事件；删除前重新核验引用，保留仍被任何实例复用的整棵模型。
                if (IsReferenced(directory, readReferences)) continue;
                RejectLinks(directory);
                RequireFilesReleased(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
                Directory.Delete(directory, true);
                if (Directory.Exists(directory)) throw new IOException("废弃吊杆目录删除未确认：" + directory);
            }
        }

        private bool IsReferenced(string directory, Func<IEnumerable<string>> readReferences) =>
            readReferences().Any(path => BridgeGenerationFileBackup.Under(directory, path));

        private void RejectLinks(string directory)
        {
            for (var parent = directory; parent != null; parent = Path.GetDirectoryName(parent))
            {
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("吊杆清理目录包含链接，禁止删除：" + parent);
                if (BridgeGenerationFileBackup.Same(parent, _directory)) break;
            }
            RejectChildLinks(directory);
        }

        private void RejectChildLinks(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("吊杆清理目录包含链接，禁止删除：" + path);
                if ((attributes & FileAttributes.Directory) != 0) RejectChildLinks(path);
            }
        }
    }

    internal sealed class BridgeHangerModelOperations
    {
        internal const string OwnerProperty = "母线桥吊杆所属基架";
        internal const string ModelProperty = "母线桥吊杆共享模型";
        internal const string OwnerReferencePrefix = OwnerProperty + ":";
        private readonly ISldWorks _app;
        private readonly IModelDoc2 _root;
        private readonly string _directory;
        private readonly BridgeHangerGeometryReader _geometry;
        private readonly SolidWorksDriver _driver;
        private readonly string _templateAssemblyPath;
        private string TemplateAssemblyPath => _templateAssemblyPath ??
            Path.Combine(TemplateArchiveCacheManager.GetTemplateRootPath(), "吊杆模板", "5GX.000.001.SLDASM");
        internal BridgeHangerModelOperations(ISldWorks app, IModelDoc2 root, string directory, Func<string, bool> allowUnsaved = null,
            string templateAssemblyPath = null)
        {
            _app = app; _root = root; _directory = Path.GetFullPath(directory);
            _geometry = new BridgeHangerGeometryReader(app, allowUnsaved);
            _driver = new SolidWorksDriver(app);
            _templateAssemblyPath = templateAssemblyPath;
        }

        internal string TemplatePath()
        {
            var path = TemplateAssemblyPath;
            if (!File.Exists(path)) throw new FileNotFoundException("吊杆模板模型缺失：" + path, path);
            return path;
        }

        internal void CloseDocumentWindows(IEnumerable<string> outputAssemblies, bool closeOutputs)
        {
            var closer = new BridgeHangerDocumentCloser(new SolidWorksFaceToFaceFrameDocumentSession(_app), path =>
            {
                var document = (_app.GetDocuments() as object[] ?? Array.Empty<object>()).OfType<IModelDoc2>()
                    .SingleOrDefault(d => BridgeGenerationFileBackup.Same(d.GetPathName(), path));
                return document?.Visible == true;
            });
            var template = TemplateAssemblyPath;
            closer.Close(Path.GetDirectoryName(template), new[] { template });
            if (closeOutputs)
            {
                var paths = outputAssemblies.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // 生成流程只收尾本次有效模型的窗口，废弃目录留给成功后的非阻断清理。
                foreach (var folder in paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    closer.Close(folder, paths.Where(p => BridgeGenerationFileBackup.Under(folder, p)));
            }
            ActivateRoot();
        }

        internal string Owner(Component2 c)
        {
            var model = c.GetModelDoc2() as IModelDoc2;
            if (model == null) throw new InvalidOperationException("读取吊杆归属时组件未解析：" + c.Name2);
            if (Property(model, c.ReferencedConfiguration, ModelProperty) == "1")
            {
                var reference = c.ComponentReference ?? string.Empty;
                if (!reference.StartsWith(OwnerReferencePrefix, StringComparison.Ordinal) || reference.Length == OwnerReferencePrefix.Length)
                    throw new InvalidOperationException("共享吊杆实例缺少所属基架标识：" + c.Name2);
                return reference.Substring(OwnerReferencePrefix.Length);
            }
            var configured = _driver.GetConfigurationCustomProperty(model, c.ReferencedConfiguration, OwnerProperty);
            return string.IsNullOrWhiteSpace(configured) ? _driver.GetCustomProperty(model, OwnerProperty) : configured;
        }

        private string Property(IModelDoc2 model, string configuration, string name)
        {
            var value = _driver.GetConfigurationCustomProperty(model, configuration, name);
            return string.IsNullOrWhiteSpace(value) ? _driver.GetCustomProperty(model, name) : value;
        }

        internal void RequireOwned(Component2 c, string id, BridgeHangerBinding binding)
        {
            if (binding == null || binding.OwnerId != id || c.Name2 != binding.Instance || Owner(c) != id ||
                !BridgeGenerationFileBackup.Same(c.GetPathName(), binding.Path) || c.ReferencedConfiguration != binding.Configuration ||
                !BridgeGenerationFileBackup.Under(_directory, c.GetPathName()))
                throw new InvalidOperationException("吊杆实例、基架归属或项目引用不符，禁止接管未知模型：" + c.Name2);
        }

        internal BridgeHangerBinding PrepareModel(BridgeHangerPlan plan, IDrawingNumberGenerator numbers)
        {
            var drawingNumber = numbers.PeekNextDrawingNumber();
            var suffix = numbers.PeekNextFileNameSuffix();
            var folder = Path.Combine(_directory, suffix.Substring(1));
            if (Directory.Exists(folder) || File.Exists(folder))
                throw new IOException("待分配吊杆图号的输出目录已存在，禁止覆盖：" + folder);
            var template = TemplatePath();
            _driver.OpenOrActivateDocument(template, rebuildAfterResolve: false);
            if (!BridgeGenerationFileBackup.Same(_app.IActiveDoc2?.GetPathName(), template))
                throw new InvalidOperationException("复制吊杆前未能激活指定模板：" + template);
            new SolidWorksFileHelper((SldWorks)_app).CopyFileUsePackAndGoWithSuffix(template, folder, suffix);
            var path = Path.Combine(folder, "5GX.000.001" + suffix + ".SLDASM");
            if (!File.Exists(path)) throw new IOException("吊杆完整复制后缺少目标总成：" + path);
            RebindCopiedReferences(template, path, suffix);
            var model = _driver.OpenOrActivateDocument(path, rebuildAfterResolve: false);
            ExternalizeVirtualParts(model, folder, suffix);
            RequireIsolatedReferences(model, folder);
            _driver.SetCustomProperty(model, ModelProperty, "1");
            _driver.SetCustomProperty(model, OwnerProperty, string.Empty);
            Drive(model, plan);
            SaveTree(model, folder);
            numbers.CommitNext(drawingNumber);
            return new BridgeHangerBinding { Path = path, Configuration = model.ConfigurationManager.ActiveConfiguration.Name,
                Width = plan.Pose.Width, AngleLength = plan.AngleLength };
        }

        internal BridgeHangerBinding Insert(Component2 frame, BridgeHangerBinding prepared, BridgeHangerPlan plan)
        {
            var model = _driver.OpenOrActivateDocument(prepared.Path, rebuildAfterResolve: false);
            if (model.ConfigurationManager.ActiveConfiguration.Name != prepared.Configuration && !model.ShowConfiguration2(prepared.Configuration))
                throw new InvalidOperationException("无法激活复用吊杆的配置：" + prepared.Configuration);
            ActivateRoot();
            var component = ((AssemblyDoc)_root).AddComponent5(prepared.Path,
                (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                string.Empty, false, string.Empty, 0, 0, 0);
            if (component == null || !BridgeGenerationFileBackup.Same(component.GetPathName(), prepared.Path))
                throw new InvalidOperationException("吊杆总成插入失败：" + plan.Pose.FrameId);
            component.ComponentReference = OwnerReferencePrefix + plan.Pose.HangerId;
            var binding = new BridgeHangerBinding { HangerId = plan.Pose.HangerId, FrameId = plan.Pose.FrameId, Instance = component.Name2,
                Path = prepared.Path, Configuration = component.ReferencedConfiguration };
            return Update(frame, component, binding, plan);
        }

        internal BridgeHangerBinding Update(Component2 frame, Component2 component, BridgeHangerBinding previous, BridgeHangerPlan plan)
        {
            var parameters = new BridgeReferencedModelParameters(component, p => BridgeGenerationFileBackup.Under(_directory, p));
            var changed = Math.Abs(parameters.Global("宽度") - plan.Pose.Width) > .001 || Math.Abs(parameters.Global("角钢高度") - plan.AngleLength) > .001;
            if (changed) throw new InvalidOperationException("共享吊杆模型参数未按目标预先准备，禁止原位修改影响其他实例：" + component.Name2);
            ActivateRoot();
            if (plan.Pose.LocalOffset.HasValue)
            {
                if (Mates(component).Length > 0) Detach(component, previous);
                if (!PoseMatches(component, plan.Pose) || !component.IsFixed())
                    PlaceAtCenterline(component, plan.Pose);
                var positioned = new BridgeHangerBinding { HangerId = plan.Pose.HangerId, FrameId = plan.Pose.FrameId,
                    FrameInstance = frame.Name2, Instance = component.Name2, Path = component.GetPathName(),
                    Configuration = component.ReferencedConfiguration, LocalOffset = plan.Pose.LocalOffset,
                    Width = plan.Pose.Width, AngleLength = plan.AngleLength, InstallationHeight = plan.InstallationHeight };
                if (ShowAssembly(component)) SaveTree(component.GetModelDoc2() as IModelDoc2, Path.GetDirectoryName(component.GetPathName()));
                Validate(component, frame, positioned, plan);
                return positioned;
            }
            changed = ShowAssembly(component) || changed;
            if (!MateMatches(component, frame, previous.Mate) || !CoordinatesCoincide(component, frame))
            {
                Detach(component, previous);
                if (!PublicMethod.AddMateUseCoord(_app, (AssemblyDoc)_root, frame, component,
                    BridgeHangerGeometry.CoordinateName, BridgeHangerGeometry.CoordinateName))
                    throw new InvalidOperationException("吊杆与基架坐标系重合配合失败：" + plan.Pose.FrameId);
                changed = true;
            }
            if (changed && !_root.EditRebuild3()) throw new InvalidOperationException("吊杆装配后总装求解失败：" + plan.Pose.FrameId);
            var matches = Mates(component).Where(m => Endpoint(m, component.Name2) && Endpoint(m, frame.Name2)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("吊杆必须唯一配合到所属基架：" + plan.Pose.FrameId);
            var name = (matches[0] as IFeature)?.Name;
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("无法登记吊杆实际配合特征。");
            var binding = new BridgeHangerBinding { FrameId = plan.Pose.FrameId, FrameInstance = frame.Name2,
                Instance = component.Name2, Path = component.GetPathName(), Configuration = component.ReferencedConfiguration,
                Mate = name, Width = plan.Pose.Width, AngleLength = plan.AngleLength, InstallationHeight = plan.InstallationHeight };
            Validate(component, frame, binding, plan);
            if (changed) SaveTree(component.GetModelDoc2() as IModelDoc2, Path.GetDirectoryName(component.GetPathName()));
            return binding;
        }

        internal BridgeHangerBinding ReplaceModel(Component2 frame, Component2 component, BridgeHangerBinding previous,
            BridgeHangerBinding prepared, BridgeHangerPlan plan)
        {
            Detach(component, previous);
            ActivateRoot();
            var assembly = (AssemblyDoc)_root;
            var oldId = component.GetID();
            _root.ClearSelection2(true);
            try
            {
                if (!component.Select(false) || !assembly.ReplaceComponents2(prepared.Path, prepared.Configuration, false,
                    (int)swReplaceComponentsConfiguration_e.swReplaceComponentsConfiguration_ManuallySelect, false))
                    throw new InvalidOperationException("替换吊架规格失败：" + previous.Instance);
            }
            finally { _root.ClearSelection2(true); }
            var updated = assembly.GetComponentByID(oldId) as Component2;
            if (updated == null || !BridgeGenerationFileBackup.Same(updated.GetPathName(), prepared.Path) ||
                updated.ReferencedConfiguration != prepared.Configuration)
                throw new InvalidOperationException("替换吊架规格后未保留原安装实例：" + previous.Instance);
            updated.ComponentReference = OwnerReferencePrefix + plan.Pose.HangerId;
            return Update(frame, updated, new BridgeHangerBinding { HangerId = plan.Pose.HangerId, FrameId = plan.Pose.FrameId,
                Instance = updated.Name2, Path = updated.GetPathName(), Configuration = updated.ReferencedConfiguration }, plan);
        }

        internal bool Matches(Component2 hanger, Component2 frame, BridgeHangerBinding binding, BridgeHangerPlan plan)
        {
            RequireOwned(hanger, plan.Pose.HangerId, binding);
            var parameters = new BridgeReferencedModelParameters(hanger, p => BridgeGenerationFileBackup.Under(_directory, p));
            return NamingMatches(hanger) && Descendants(hanger).All(c => !c.IsHidden(false)) &&
                Math.Abs(parameters.Global("宽度") - plan.Pose.Width) < .001 &&
                Math.Abs(parameters.Global("角钢高度") - plan.AngleLength) < .001 &&
                binding.FrameId == plan.Pose.FrameId && binding.FrameInstance == frame.Name2 &&
                (plan.Pose.LocalOffset.HasValue
                    ? binding.LocalOffset.HasValue && Math.Abs(binding.LocalOffset.Value - plan.Pose.LocalOffset.Value) <= BridgeHangerGeometry.Tolerance &&
                        hanger.IsFixed() && Mates(hanger).Length == 0 && PoseMatches(hanger, plan.Pose)
                    : MateMatches(hanger, frame, binding.Mate) && CoordinatesCoincide(hanger, frame));
        }

        internal bool NamingMatches(Component2 hanger)
        {
            var name = Path.GetFileNameWithoutExtension(hanger.GetPathName());
            const string rootName = "5GX.000.001";
            if (!name.StartsWith(rootName + ".", StringComparison.OrdinalIgnoreCase)) return false;
            var suffix = name.Substring(rootName.Length);
            string contract;
            int serial;
            if (!DrawingNumberHelper.TryParse(suffix.Substring(1), out contract, out serial)) return false;
            var assembly = hanger.GetModelDoc2() as AssemblyDoc;
            if (assembly == null) throw new InvalidOperationException("吊杆总成未解析：" + hanger.Name2);
            return Property((IModelDoc2)assembly, hanger.ReferencedConfiguration, ModelProperty) == "1" &&
                BridgeGenerationFileBackup.Same(Path.GetDirectoryName(hanger.GetPathName()), Path.Combine(_directory, suffix.Substring(1))) &&
                (assembly.GetComponents(false) as object[] ?? Array.Empty<object>())
                    .OfType<Component2>().All(c => !c.IsVirtual && Path.GetFileNameWithoutExtension(c.GetPathName()).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private bool ShowAssembly(Component2 hanger)
        {
            var changed = false;
            // 先显示父装配体，再显示内部附件，避免沿用模板或旧输出的隐藏状态。
            foreach (var component in Descendants(hanger).OrderBy(c => c.Name2.Count(ch => ch == '/')).ThenBy(c => c.Name2, StringComparer.Ordinal))
            {
                if (!component.IsHidden(false)) continue;
                component.Visible = (int)swComponentVisibilityState_e.swComponentVisible;
                if (component.IsHidden(false)) throw new InvalidOperationException("吊杆附件未能设置为显示：" + component.Name2);
                changed = true;
            }
            return changed;
        }

        internal BridgeHangerBinding ReadReusableModel(Component2 hanger, BridgeHangerBinding binding)
        {
            RequireOwned(hanger, binding.OwnerId, binding);
            if (!NamingMatches(hanger)) return null;
            var parameters = new BridgeReferencedModelParameters(hanger, p => BridgeGenerationFileBackup.Under(_directory, p));
            return new BridgeHangerBinding { HangerId = binding.HangerId, FrameId = binding.FrameId, Instance = hanger.Name2, Path = hanger.GetPathName(),
                Configuration = hanger.ReferencedConfiguration, Width = parameters.Global("宽度"), AngleLength = parameters.Global("角钢高度") };
        }

        internal void Validate(Component2 hanger, Component2 frame, BridgeHangerBinding binding, BridgeHangerPlan plan)
        {
            if (!Matches(hanger, frame, binding, plan)) throw new InvalidOperationException("吊杆参数或安装配合未达到生成目标：" + plan.Pose.FrameId);
            var local = _geometry.Coordinate(hanger);
            var origin = local.Skip(9).Take(3).Select(v => v * 1000).ToArray();
            var up = local.Skip(3).Take(3).ToArray();
            var across = local.Skip(6).Take(3).ToArray();
            var pieces = Descendants(hanger).ToArray();
            var plates = pieces.Where(c => Path.GetFileName(c.GetPathName()).StartsWith("8ZX.202.518.", StringComparison.OrdinalIgnoreCase)).ToArray();
            var angles = pieces.Where(c => Path.GetFileName(c.GetPathName()).StartsWith("8GX.100.002.", StringComparison.OrdinalIgnoreCase)).ToArray();
            var rods = pieces.Where(c => Path.GetFileName(c.GetPathName()).Contains("通丝吊杆")).ToArray();
            var crossbars = pieces.Where(c => Path.GetFileName(c.GetPathName()).StartsWith("8ZX.202.004.", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (plates.Length != 2 || angles.Length != 2 || rods.Length != 2 || crossbars.Length != 1)
                throw new InvalidOperationException("吊杆模板引用树与已实测结构不符：" + hanger.Name2);
            var crossbarEnds = _geometry.EndPlanes(crossbars[0], across);
            if (Math.Abs(crossbarEnds[1]-crossbarEnds[0]-plan.Pose.Width-200) > BridgeHangerGeometry.Tolerance)
                throw new InvalidOperationException("吊杆下横梁实际长度未随基架宽度同步：" + plan.Pose.FrameId);
            foreach (var plate in plates)
            {
                var height = _geometry.EndPlanes(plate, up)[1] - origin.Zip(up, (a,b)=>a*b).Sum();
                if (Math.Abs(height - plan.InstallationHeight) > BridgeHangerGeometry.Tolerance)
                    throw new InvalidOperationException("吊杆顶部安装面未到达横梁下表面：" + plan.Pose.FrameId);
            }
            foreach (var angle in angles)
            {
                var ends = _geometry.EndPlanes(angle, up);
                if (Math.Abs(ends[1]-ends[0]-plan.AngleLength) > BridgeHangerGeometry.Tolerance)
                    throw new InvalidOperationException("吊杆角钢实际长度与驱动值不符：" + plan.Pose.FrameId);
            }
            foreach (var rod in rods)
            {
                var ends = _geometry.EndPlanes(rod, up);
                if (Math.Abs(ends[1] - ends[0] - BridgeHangerGeometry.RodLength) > BridgeHangerGeometry.Tolerance)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "基架 {2} 的吊杆通丝实测长度为 {0:0.0} mm，模板要求 {1:0.0} mm。",
                        ends[1] - ends[0], BridgeHangerGeometry.RodLength, plan.Pose.FrameId));
            }
        }

        internal void Detach(Component2 component, BridgeHangerBinding binding)
        {
            ActivateRoot();
            foreach (var mate in Mates(component))
            {
                var feature = mate as IFeature;
                if (feature == null || feature.Name != binding.Mate || !Endpoint(mate, component.Name2))
                    throw new InvalidOperationException("吊杆带有未登记的额外配合，不能自动移除：" + component.Name2);
                var name = feature.Name;
                _root.ClearSelection2(true);
                try
                {
                    if (!feature.Select2(false, 0) || !_root.Extension.DeleteSelection2(0) ||
                        Mates(component).Any(m => (m as IFeature)?.Name == name))
                        throw new InvalidOperationException("删除吊杆旧配合失败：" + name);
                }
                finally { _root.ClearSelection2(true); }
            }
        }

        internal void Remove(Component2 component, BridgeHangerBinding binding)
        {
            Detach(component, binding);
            var name = component.Name2;
            _root.ClearSelection2(true);
            try
            {
                if (!component.Select(false) || !_root.Extension.DeleteSelection2(0))
                    throw new InvalidOperationException("删除已移除基架的吊杆失败：" + binding.FrameId);
                if ((((AssemblyDoc)_root).GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<Component2>().Any(c => c.Name2 == name))
                    throw new InvalidOperationException("吊杆删除未得到确认：" + name);
            }
            finally { _root.ClearSelection2(true); }
        }

        private bool MateMatches(Component2 hanger, Component2 frame, string name)
        {
            var mates = Mates(hanger);
            if (mates.Length != 1 || (mates[0] as IFeature)?.Name != name || !Endpoint(mates[0], hanger.Name2) || !Endpoint(mates[0], frame.Name2)) return false;
            bool warning;
            if ((mates[0] as IFeature).GetErrorCode2(out warning) != 0) return false;
            var suppression = (mates[0] as IFeature).IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration,
                new[] { _root.ConfigurationManager.ActiveConfiguration.Name }) as bool[];
            return suppression?.Length == 1 && !suppression[0] && mates[0].Type == (int)swMateType_e.swMateCOORDINATE;
        }
        private bool CoordinatesCoincide(Component2 a, Component2 b)
        {
            var x = _geometry.Coordinate(a); var y = _geometry.Coordinate(b);
            return Enumerable.Range(0, 12).All(i => Math.Abs(x[i]-y[i]) <= (i<9 ? 1e-6 : .00001));
        }
        private bool PoseMatches(Component2 component, BridgeHangerPose pose)
        {
            var actual = _geometry.Coordinate(component);
            var axes = new[] { pose.Along, pose.Up, pose.Across };
            var origin = actual.Skip(9).Take(3).Select(value => value * 1000).ToArray();
            var delta = origin.Zip(pose.Origin, (x, y) => x - y).ToArray();
            return Math.Sqrt(delta.Sum(value => value * value)) <= BridgeHangerGeometry.Tolerance &&
                axes.Select((axis, index) => Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                    axis.Zip(actual.Skip(index * 3).Take(3), (x, y) => x * y).Sum()))) * 180.0 / Math.PI)
                    .All(angle => angle <= BridgeHangerGeometry.Tolerance);
        }

        private void PlaceAtCenterline(Component2 component, BridgeHangerPose pose)
        {
            var coordinate = (component.FeatureByName(BridgeHangerGeometry.CoordinateName)?.GetDefinition() as ICoordinateSystemFeatureData)?.Transform;
            if (coordinate == null) throw new InvalidOperationException("吊架模板缺少安装坐标系：" + component.Name2);
            var data = pose.Along.Concat(pose.Up).Concat(pose.Across).Concat(pose.Origin.Select(value => value / 1000.0))
                .Concat(new[] { 1.0, 0.0, 0.0, 0.0 }).ToArray();
            var destination = _app.IGetMathUtility().CreateTransform(data) as MathTransform;
            var transform = (coordinate.Inverse() as MathTransform)?.Multiply(destination) as MathTransform;
            if (transform == null) throw new InvalidOperationException("无法求得吊架安装变换：" + pose.HangerId);
            var assembly = (AssemblyDoc)_root;
            _root.ClearSelection2(true);
            try
            {
                if (!component.Select(false)) throw new InvalidOperationException("无法选择吊架：" + component.Name2);
                if (component.IsFixed()) assembly.UnfixComponent();
                if (component.IsFixed()) throw new InvalidOperationException("无法解除吊架的固定状态：" + component.Name2);
                component.Transform2 = transform;
                assembly.FixComponent();
                if (!component.IsFixed() || !PoseMatches(component, pose))
                    throw new InvalidOperationException("吊架的实际安装中心线或方向未达到目标：" + pose.HangerId);
            }
            finally { _root.ClearSelection2(true); }
        }
        private bool Endpoint(IMate2 mate, string name)
        {
            for (var i=0; i<mate.GetMateEntityCount(); i++)
            {
                var e=mate.MateEntity(i) as IMateEntity2;
                if (e?.ReferenceComponent?.Name2==name && (e.Reference as IFeature)?.Name==BridgeHangerGeometry.CoordinateName) return true;
            }
            return false;
        }
        private IMate2[] Mates(Component2 c) => (c.GetMates() as object[] ?? Array.Empty<object>()).OfType<IMate2>().ToArray();
        private IEnumerable<Component2> Descendants(Component2 c)
        {
            if (c.IsSuppressed()) yield break;
            yield return c;
            foreach (var child in (c.GetChildren() as object[] ?? Array.Empty<object>()).OfType<Component2>())
                foreach (var item in Descendants(child)) yield return item;
        }
        private void ActivateRoot()
        {
            var errors=0;
            var activated=_app.ActivateDoc3(_root.GetPathName(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as IModelDoc2;
            if (activated==null || !BridgeGenerationFileBackup.Same(activated.GetPathName(),_root.GetPathName()))
                throw new InvalidOperationException("吊杆操作无法返回所属总装。");
        }
        private void Drive(IModelDoc2 model, BridgeHangerPlan plan)
        {
            if (model.IsOpenedReadOnly()) throw new InvalidOperationException("吊杆输出为只读："+model.GetPathName());
            _driver.ApplyGlobalVariablesRequired(model,new Dictionary<string,object>{{"宽度",plan.Pose.Width},{"角钢高度",plan.AngleLength}},false);
            ((IEquationMgr)model.GetEquationMgr()).EvaluateAll();
            if (!model.ForceRebuild3(false)) throw new InvalidOperationException("吊杆参数驱动后模型求解失败："+plan.Pose.FrameId);
            ((IEquationMgr)model.GetEquationMgr()).EvaluateAll();
            if (!model.ForceRebuild3(false)) throw new InvalidOperationException("吊杆尺寸链二次求解失败："+plan.Pose.FrameId);
        }
        private void ExternalizeVirtualParts(IModelDoc2 model,string folder,string suffix)
        {
            var components=(((AssemblyDoc)model).GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>().ToArray();
            foreach (var c in components.Where(c=>c.IsVirtual).OrderByDescending(c=>c.Name2.Count(x=>x=='/')).ThenBy(c=>c.Name2,StringComparer.Ordinal))
            {
                if (!c.IsVirtual) continue; // 同一虚拟零件的另一实例可能已随共享引用一起外置。
                var leaf=c.Name2.Split('/').Last();
                var context=leaf.IndexOf('^');
                if (context>=0) leaf=leaf.Substring(0,context);
                var safe=new string(leaf.Select(ch=>Path.GetInvalidFileNameChars().Contains(ch)?'_':ch).ToArray());
                var extension=(c.GetModelDoc2() as IModelDoc2)?.GetType()==(int)swDocumentTypes_e.swDocASSEMBLY?".SLDASM":".SLDPRT";
                if (!c.SaveVirtualComponent(Path.Combine(folder,safe+suffix+extension)))
                    throw new InvalidOperationException("吊杆虚拟零件外置失败："+c.Name2);
            }
        }
        private void RebindCopiedReferences(string source, string copy, string suffix)
        {
            var folder = Path.GetDirectoryName(copy);
            var sources = Dependencies(source, true).Where(p => !EmbeddedDependency(p) && File.Exists(p)).Concat(new[] { source })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var mapping = sources.ToDictionary(p => p, p => Path.Combine(folder,
                Path.GetFileNameWithoutExtension(p) + suffix + Path.GetExtension(p)), StringComparer.OrdinalIgnoreCase);
            if (mapping.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapping.Count || mapping.Values.Any(p => !File.Exists(p)))
                throw new InvalidOperationException("吊杆复制文件映射重复或输出缺失：" + copy);
            // SW2022追加后缀后仍可能保留源路径；只对尚未打开的副本重绑定，随后才允许驱动。
            foreach (var file in Directory.GetFiles(folder).Where(p => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }
                .Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (_app.GetOpenDocumentByName(file) != null)
                    throw new InvalidOperationException("吊杆副本尚在打开，不能修改磁盘引用：" + file);
                foreach (var reference in Dependencies(file, false))
                {
                    if (BridgeGenerationFileBackup.Under(folder, reference) || EmbeddedDependency(reference)) continue;
                    if (!mapping.TryGetValue(reference, out var replacement))
                        throw new InvalidOperationException("吊杆副本包含未登记的模板引用：" + reference);
                    if (!_app.ReplaceReferencedDocument(file, reference, replacement))
                        throw new InvalidOperationException("吊杆副本引用重绑定失败：" + file + "；" + reference);
                }
            }
            foreach (var reference in Dependencies(copy, true).Where(p => !EmbeddedDependency(p)))
                if (!BridgeGenerationFileBackup.Under(folder, reference) || !File.Exists(reference))
                    throw new InvalidOperationException("吊杆磁盘引用仍指向本实例目录之外：" + reference);
        }
        private bool EmbeddedDependency(string path) => path.IndexOf('^') >= 0;
        private string[] Dependencies(string path, bool traverse)
        {
            var raw = _app.GetDocumentDependencies2(path, traverse, false, false) as string[] ?? Array.Empty<string>();
            if (raw.Length % 2 != 0) throw new InvalidOperationException("吊杆文件引用清单格式无效：" + path);
            return Enumerable.Range(0, raw.Length / 2).Select(i => raw[i*2+1]).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        private void RequireIsolatedReferences(IModelDoc2 model,string folder)
        {
            foreach (var c in (((AssemblyDoc)model).GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>())
                if (c.IsVirtual || !BridgeGenerationFileBackup.Under(folder,c.GetPathName()))
                    throw new InvalidOperationException("吊杆引用未隔离到本实例输出，禁止驱动模板或其他吊杆："+c.GetPathName());
        }
        private void SaveTree(IModelDoc2 model,string folder)
        {
            var components=(((AssemblyDoc)model).GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>().Where(c=>!c.IsSuppressed());
            foreach (var c in components.OrderByDescending(c=>c.Name2.Count(x=>x=='/')).ThenBy(c=>c.Name2,StringComparer.Ordinal)
                .GroupBy(c=>c.GetPathName(),StringComparer.OrdinalIgnoreCase).Select(g=>g.First()))
            {
                if (!BridgeGenerationFileBackup.Under(folder,c.GetPathName())) throw new InvalidOperationException("吊杆保存引用超出本实例目录。");
                Save(c.GetModelDoc2() as IModelDoc2);
            }
            Save(model);
        }
        private void Save(IModelDoc2 model)
        {
            if (model==null) throw new InvalidOperationException("吊杆引用文档未解析。");
            int errors=0,warnings=0;
            if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent,ref errors,ref warnings) || errors!=0)
                throw new IOException("吊杆模型保存失败："+model.GetPathName()+"；错误码="+errors.ToString(CultureInfo.InvariantCulture));
        }
    }
}
