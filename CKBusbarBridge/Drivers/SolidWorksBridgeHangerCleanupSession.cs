using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    // 只保留应用入口和路径。这里不缓存 ModelDoc2/Component2，也不强制释放共享 RCW。
    internal sealed class SolidWorksBridgeHangerCleanupSession : IBridgeHangerCleanupSession
    {
        private readonly ISldWorks _app;
        private readonly string _rootPath;
        private readonly string _configuration;
        private readonly BridgeGenerationFileBackup _backup;
        private readonly Action _releaseCompletedHandles;
        private readonly Action _verifyReopened;
        private readonly Action<string> _progress;

        internal SolidWorksBridgeHangerCleanupSession(ISldWorks app, string rootPath, string configuration,
            BridgeGenerationFileBackup backup, Action releaseCompletedHandles, Action verifyReopened, Action<string> progress)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _rootPath = Path.GetFullPath(rootPath);
            _configuration = configuration;
            _backup = backup ?? throw new ArgumentNullException(nameof(backup));
            _releaseCompletedHandles = releaseCompletedHandles ?? throw new ArgumentNullException(nameof(releaseCompletedHandles));
            _verifyReopened = verifyReopened ?? throw new ArgumentNullException(nameof(verifyReopened));
            _progress = progress;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public IReadOnlyList<BridgeHangerCleanupDocument> ReadDocuments(IReadOnlyList<string> modelPaths)
        {
            var result = new List<BridgeHangerCleanupDocument>();
            var loaded = Documents();
            foreach (var model in loaded) result.Add(ReadDocument(model.GetPathName(), model));
            foreach (var path in modelPaths.Concat(new[] { _rootPath }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (result.Any(d => Same(d.Path, path))) continue;
                // GetDocuments 与按路径查询相互补充；两者都不为了探测状态而重新打开模型。
                var model = _app.GetOpenDocumentByName(path) as IModelDoc2;
                if (model != null) result.Add(ReadDocument(path, model));
                else if (File.Exists(path)) result.Add(ReadDocument(path, null));
            }
            return result.ToArray();
        }

        private BridgeHangerCleanupDocument ReadDocument(string path, IModelDoc2 model)
        {
            var label = string.IsNullOrWhiteSpace(path) ? "未保存文档：" + model.GetTitle() : Path.GetFullPath(path);
            // 外部装配、图纸只读取当前引用，不保存或关闭；压缩实例及跨配置依赖也纳入保护。
            var raw = model != null ? model.Extension.GetDependencies(true, false, false, false, false) :
                _app.GetDocumentDependencies2(path, true, false, false);
            var references = ParseDependencies(raw, label).ToList();
            if (model is AssemblyDoc assembly)
            {
                var components = assembly.GetComponents(false);
                if (components != null && !(components is object[]))
                    throw new InvalidOperationException("装配引用快照格式无效：" + label);
                foreach (var component in (components as object[] ?? Array.Empty<object>()).OfType<Component2>())
                {
                    var reference = component.GetPathName();
                    if (!string.IsNullOrWhiteSpace(reference)) references.Add(RequirePath(reference, label));
                }
            }
            return new BridgeHangerCleanupDocument(string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path),
                model != null, references.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), label);
        }

        private string[] ParseDependencies(object raw, string owner)
        {
            // API 对没有依赖的零件返回 null；异常和不合法的非空返回值不能当作“无引用”。
            if (raw == null) return Array.Empty<string>();
            var values = raw as string[];
            if (values == null && raw is object[] objects && objects.All(o => o is string)) values = objects.Cast<string>().ToArray();
            if (values == null || values.Length % 2 != 0) throw new InvalidOperationException("模型引用清单格式无效：" + owner);
            return Enumerable.Range(0, values.Length / 2).Select(i => RequirePath(values[i * 2 + 1], owner)).ToArray();
        }
        private string RequirePath(string path, string owner)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new InvalidOperationException("模型引用缺少绝对路径，不能确认清理范围：" + owner + "；" + path);
            return Path.GetFullPath(path);
        }

        public void SaveDocuments(IReadOnlyList<string> paths)
        {
            foreach (var path in paths) SaveCurrentDocument(path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SaveCurrentDocument(string path)
        {
            var model = Find(path);
            if (model == null || !model.GetSaveFlag()) return;
            if (!_backup.Owns(path)) throw new InvalidOperationException("待关闭生成文档未纳入事务备份：" + path);
            var errors = 0; var warnings = 0;
            if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0 || model.GetSaveFlag())
                throw new IOException("清理前保存生成文档失败：" + path + "；错误码=" + errors.ToString(CultureInfo.InvariantCulture));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CloseDocument(string path)
        {
            var model = Find(path);
            if (model == null) return;
            if (model.GetSaveFlag()) throw new IOException("关闭前生成文档再次发生修改：" + path);
            var title = model.GetTitle();
            if (string.IsNullOrWhiteSpace(title) || Documents().Any(d => d.GetTitle() == title && !Same(d.GetPathName(), path)))
                throw new IOException("无法唯一定位待关闭的模型文档：" + path);
            _app.CloseDoc(title);
            Trace("已请求关闭文档（包括隐藏驻留）：" + path);
        }

        public void ReleaseMemory()
        {
            _releaseCompletedHandles();
            // 只回收已经退出调用栈且不再被本次事务使用的包装对象。
            // GC 不能证明 SW 缓存已卸载；随后仍必须重新查驻留、探测文件锁并实际删除。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public void CloseProject()
        {
            _progress?.Invoke("正在释放旧吊杆模型占用…");
            _releaseCompletedHandles();
            CloseDocument(_rootPath);
            ReleaseMemory();
            RequireProjectClosed();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RequireProjectClosed()
        {
            if (Find(_rootPath) != null) throw new IOException("当前总装未能卸载，无法释放旧吊杆。详情见日志。");
        }

        public void ReopenProject()
        {
            var model = new SolidWorksDriver(_app).OpenOrActivateDocument(_rootPath, rebuildAfterResolve: false);
            if (model.ConfigurationManager.ActiveConfiguration.Name != _configuration) model.ShowConfiguration2(_configuration);
            if (model.ConfigurationManager.ActiveConfiguration.Name != _configuration)
                throw new IOException("清理后总装配置恢复失败。");
            SolidWorksDriver.EnsureAssemblyFullyResolved(model);
            _verifyReopened();
            Trace("旧吊杆清理后已重新打开当前总装并核验引用与吊杆绑定。");
        }

        public void Trace(string message) => FaceToWallPreviewTrace.Info("吊杆事务清理", message);
        private IModelDoc2 Find(string path) => _app.GetOpenDocumentByName(path) as IModelDoc2 ??
            Documents().SingleOrDefault(d => Same(d.GetPathName(), path));
        private IModelDoc2[] Documents()
        {
            var raw = _app.GetDocuments();
            if (raw == null) return Array.Empty<IModelDoc2>();
            var values = raw as object[];
            if (values == null || values.Any(o => !(o is IModelDoc2)))
                throw new InvalidOperationException("无法读取 SolidWorks 已加载文档清单。");
            return values.Cast<IModelDoc2>().ToArray();
        }
        private bool Same(string left, string right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
            BridgeGenerationFileBackup.Same(left, right);
    }
}
