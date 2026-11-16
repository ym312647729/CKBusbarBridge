using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 首次生成基架时保存并关闭当前总装；全部物理基架准备完成后，
    /// 从原完整路径重新打开总装并创建不持有旧 COM 引用的新装配器。
    /// </summary>
    internal sealed class SolidWorksFaceToFaceFrameInitialGenerationSession
        : IFaceToFaceFrameInitialGenerationSession
    {
        private readonly ISldWorks _swApp;
        private readonly string _totalAssemblyPath;
        private readonly string _activeConfigurationName;
        private readonly IReadOnlyCollection<string> _referencedDocumentPaths;
        private readonly ISet<string> _readOnlyCopperPaths;
        private readonly IFrameBaseComponentResolver _baseComponentResolver;
        private readonly FaceToFaceFrameScopedDocumentCleaner _documentCleaner;
        private IModelDoc2 _totalAssemblyModel;

        internal void ReleaseCompletedModelContext() { _totalAssemblyModel = null; }

        internal SolidWorksFaceToFaceFrameInitialGenerationSession(
            ISldWorks swApp,
            IModelDoc2 totalAssemblyModel,
            IFrameBaseComponentResolver baseComponentResolver,
            IEnumerable<string> readOnlyCopperPaths = null)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _totalAssemblyModel = totalAssemblyModel
                ?? throw new ArgumentNullException(nameof(totalAssemblyModel));
            if (!(totalAssemblyModel is AssemblyDoc)
                || totalAssemblyModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException(
                    "首次生成基架需要有效的 SolidWorks 总装配体文档。");
            }

            var path = totalAssemblyModel.GetPathName();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    "请先保存当前总装配体，再生成基架。");
            }

            _totalAssemblyPath = Path.GetFullPath(path);
            if (!File.Exists(_totalAssemblyPath))
            {
                throw new FileNotFoundException(
                    "当前总装配体文件不存在：" + _totalAssemblyPath,
                    _totalAssemblyPath);
            }

            _activeConfigurationName = TryGetActiveConfigurationName(totalAssemblyModel);
            _referencedDocumentPaths = CaptureReferencedDocumentPaths(
                totalAssemblyModel as AssemblyDoc);
            if (readOnlyCopperPaths != null)
                _readOnlyCopperPaths = new HashSet<string>(readOnlyCopperPaths, StringComparer.OrdinalIgnoreCase);
            _baseComponentResolver = baseComponentResolver
                ?? throw new ArgumentNullException(nameof(baseComponentResolver));
            _documentCleaner = new FaceToFaceFrameScopedDocumentCleaner(
                new SolidWorksFaceToFaceFrameDocumentSession(_swApp));
        }

        internal IModelDoc2 TotalAssemblyModel
        {
            get
            {
                var model = FindOpenTotalAssembly();
                if (model == null)
                {
                    throw new InvalidOperationException(
                        "原总装配体当前未打开：" + _totalAssemblyPath);
                }

                _totalAssemblyModel = model;
                return model;
            }
        }

        public string CaptureModelMapping()
        {
            return new SolidWorksDriver(_swApp).GetCustomProperty(
                TotalAssemblyModel,
                FaceToFaceBaseModelMappingHelper.PropertyName);
        }

        public void SaveAndCloseTotalAssembly()
        {
            var model = TotalAssemblyModel;
            var errors = 0;
            var warnings = 0;
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("保存总装开始", _totalAssemblyPath);
            var saved = model.Save3(
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                    | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
            {
                throw new InvalidOperationException(
                    $"关闭前保存总装配体失败：errors={errors}，warnings={warnings}，"
                    + _totalAssemblyPath);
            }

            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("保存总装完成", _totalAssemblyPath);
            CloseTotalAssemblyCore(model, _readOnlyCopperPaths);
        }

        /// <summary>
        /// 丢弃当前总装内存修改并关闭文档。该入口仅供文件事务回滚使用；
        /// 与 <see cref="SaveAndCloseTotalAssembly"/> 的唯一区别是不会在关闭前 Save3。
        /// </summary>
        internal void CloseTotalAssemblyWithoutSave()
        {
            var model = FindOpenTotalAssembly();
            if (model != null)
            {
                CloseTotalAssemblyCore(model);
                return;
            }

            // 事务可能已在“保存并关闭”之后失败。总装已经关闭时仍复核构造时捕获的
            // 原引用文档，保证恢复磁盘文件之前不残留旧模型 COM 文档。
            _totalAssemblyModel = null;
            CloseReferencedDocuments();
        }

        private void CloseTotalAssemblyCore(IModelDoc2 model, ISet<string> mayRemainLoaded = null)
        {
            var title = model.GetTitle();
            if (string.IsNullOrWhiteSpace(title))
            {
                throw new InvalidOperationException(
                    "当前总装配体缺少有效文档标题，无法关闭：" + _totalAssemblyPath);
            }

            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("关闭总装开始", _totalAssemblyPath);
            _swApp.CloseDoc(title);
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("关闭总装返回", _totalAssemblyPath);
            _totalAssemblyModel = null;
            if (FindOpenTotalAssembly() != null)
            {
                throw new InvalidOperationException(
                    "SolidWorks 未能关闭当前总装配体：" + _totalAssemblyPath);
            }

            CloseReferencedDocuments(mayRemainLoaded);
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("关闭总装及所需引用完成", _totalAssemblyPath);
        }

        public FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
            string directoryPath)
        {
            return _documentCleaner.CloseDocumentsUnderDirectory(directoryPath);
        }

        public IFaceToFaceFrameAssembler ReopenTotalAssembly()
        {
            var model = OpenOrActivateTotalAssembly(rebuildAfterResolve: true);
            return new FaceToFaceFrameAssembler(
                _swApp,
                model,
                _baseComponentResolver);
        }

        /// <summary>
        /// 只打开、去轻量化、恢复配置并激活总装，不在打开器内部重建。
        /// 二次驱动会在该方法返回后立即重建总装顶层配合，
        /// 已完成驱动并保存的基架不需要再次递归重建，更不能连带重建铜排。
        /// </summary>
        internal IModelDoc2 ReopenTotalAssemblyModelWithoutRebuild()
        {
            return OpenOrActivateTotalAssembly(rebuildAfterResolve: false);
        }

        public void EnsureTotalAssemblyOpen()
        {
            OpenOrActivateTotalAssembly(rebuildAfterResolve: true);
        }

        private IModelDoc2 OpenOrActivateTotalAssembly(bool rebuildAfterResolve)
        {
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("重新打开基架总装开始", _totalAssemblyPath);
            var model = FindOpenTotalAssembly()
                ?? new SolidWorksDriver(_swApp).OpenOrActivateDocument(
                    _totalAssemblyPath,
                    // 总装关闭期间，引用的基架/子件可能已经在磁盘上发生变化。
                    // 此处只负责打开及去轻量化，必须等文档激活、原配置恢复成功后，
                    // 再按 rebuildAfterResolve 的要求统一重建，避免在错误配置中提前重建。
                    rebuildAfterResolve: false);
            if (!(model is AssemblyDoc)
                || model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY
                || !PathsEqual(model.GetPathName(), _totalAssemblyPath))
            {
                throw new InvalidOperationException(
                    "重新打开的文档不是原总装配体：" + _totalAssemblyPath);
            }

            // 必须先把原总装激活，再读取/切换配置。SolidWorks 在文档刚打开但尚未成为
            // ActiveDoc 时，ShowConfiguration2 的返回值并不适合作为最终状态判断依据。
            var activationErrors = 0;
            var activated = _swApp.ActivateDoc3(
                model.GetTitle(),
                true,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc,
                ref activationErrors) as IModelDoc2;
            if (activated == null
                || activationErrors != 0
                || !PathsEqual(activated.GetPathName(), _totalAssemblyPath))
            {
                throw new InvalidOperationException(
                    $"重新激活原总装配体失败：errors={activationErrors}，"
                    + _totalAssemblyPath);
            }

            RestoreActiveConfiguration(
                _activeConfigurationName,
                _totalAssemblyPath,
                () => TryGetActiveConfigurationName(activated),
                configurationName => activated.ShowConfiguration2(configurationName),
                () => TryGetConfigurationNames(activated));

            if (rebuildAfterResolve)
            {
                RebuildTopLevelAssembly(activated.ForceRebuild3, _totalAssemblyPath);
            }

            _totalAssemblyModel = activated;
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("重新打开基架总装完成", _totalAssemblyPath);
            return activated;
        }

        /// <summary>
        /// 基架已在各自模型文档中驱动、重建并保存；总装只更新顶层配合。
        /// TopOnly=false 会递归进入铜排等无关子装配，禁止用于基架总装流程。
        /// </summary>
        internal static void RebuildTopLevelAssembly(
            Func<bool, bool> forceRebuild,
            string assemblyIdentity)
        {
            if (forceRebuild == null) throw new ArgumentNullException(nameof(forceRebuild));
            if (!forceRebuild(true))
            {
                throw new InvalidOperationException(
                    "基架总装顶层重建失败：" + assemblyIdentity);
            }
        }

        /// <summary>
        /// 恢复重新打开前的活动配置。
        /// </summary>
        /// <remarks>
        /// SolidWorks 的 <c>ShowConfiguration2</c> 在目标配置已经处于活动状态等情况下，
        /// 可能返回 <c>false</c>，但文档实际配置并未错误。因此本方法遵循以下规则：
        /// 1. 当前配置已经等于目标配置时不重复切换；
        /// 2. 确实需要切换时调用一次 <c>ShowConfiguration2</c>；
        /// 3. 无论 COM 方法返回 true、false 或抛出异常，都重新读取实际活动配置，
        ///    仅在实际配置仍不等于目标配置时才判定失败。
        /// 该方法使用委托隔离 COM 调用，使上述状态判定可以由单元测试直接覆盖。
        /// </remarks>
        internal static void RestoreActiveConfiguration(
            string expectedConfigurationName,
            string totalAssemblyPath,
            Func<string> getActiveConfigurationName,
            Func<string, bool> showConfiguration,
            Func<IReadOnlyList<string>> getAvailableConfigurationNames)
        {
            if (string.IsNullOrWhiteSpace(expectedConfigurationName))
            {
                return;
            }

            if (getActiveConfigurationName == null)
                throw new ArgumentNullException(nameof(getActiveConfigurationName));
            if (showConfiguration == null)
                throw new ArgumentNullException(nameof(showConfiguration));
            if (getAvailableConfigurationNames == null)
                throw new ArgumentNullException(nameof(getAvailableConfigurationNames));

            var currentConfigurationName = getActiveConfigurationName();
            if (ConfigurationNamesEqual(currentConfigurationName, expectedConfigurationName))
            {
                // 最常见的正常路径：重新打开总装后 SolidWorks 已自动恢复原配置。
                // 跳过同配置的重复 ShowConfiguration2，也就不会把其 false 返回误判为失败。
                return;
            }

            var showReturned = false;
            Exception showException = null;
            try
            {
                showReturned = showConfiguration(expectedConfigurationName);
            }
            catch (Exception exception)
            {
                // 先保存异常，随后仍读取实际配置。个别 COM 调用即使报告异常，
                // 文档状态也可能已经完成切换，最终应以实际后置条件为准。
                showException = exception;
            }

            var actualConfigurationName = getActiveConfigurationName();
            if (ConfigurationNamesEqual(actualConfigurationName, expectedConfigurationName))
            {
                return;
            }

            IReadOnlyList<string> availableConfigurationNames;
            try
            {
                availableConfigurationNames = getAvailableConfigurationNames()
                    ?? Array.Empty<string>();
            }
            catch
            {
                availableConfigurationNames = Array.Empty<string>();
            }

            var availableText = availableConfigurationNames.Count == 0
                ? "（无法读取或无配置）"
                : string.Join("、", availableConfigurationNames);
            var showResultText = showException == null
                ? showReturned.ToString()
                : "异常：" + showException.Message;
            throw new InvalidOperationException(
                $"重新打开总装配体后无法恢复配置“{expectedConfigurationName}”："
                + $"当前配置“{actualConfigurationName ?? "（无法读取）"}”，"
                + $"可用配置：{availableText}，"
                + $"ShowConfiguration2 结果：{showResultText}。"
                + totalAssemblyPath,
                showException);
        }

        private IModelDoc2 FindOpenTotalAssembly()
        {
            var documents = _swApp.GetDocuments() as object[];
            return documents?
                .OfType<IModelDoc2>()
                .FirstOrDefault(document => PathsEqual(
                    SafeGetPath(document),
                    _totalAssemblyPath));
        }

        private void CloseReferencedDocuments(ISet<string> mayRemainLoaded = null)
        {
            new FaceToFaceFrameReferencedDocumentCloser(
                FindOpenReferencedDocument, title => _swApp.CloseDoc(title)).Close(_referencedDocumentPaths, mayRemainLoaded);
        }

        private FaceToFaceFrameDocumentCloseTarget FindOpenReferencedDocument(string path)
        {
            // 引用文档可能从未单独打开窗口，必须枚举当前加载文档，不能用按名称查询的空值推断已卸载。
            var rawDocuments = _swApp.GetDocuments();
            if (rawDocuments == null) return null;
            var documents = rawDocuments as object[]
                ?? throw new InvalidOperationException("无法确认引用文档关闭状态：SolidWorks 未返回当前文档数组。");
            foreach (var document in documents.OfType<IModelDoc2>())
            {
                var currentPath = document.GetPathName();
                if (!PathsEqual(currentPath, path)) continue;
                return new FaceToFaceFrameDocumentCloseTarget(currentPath, document.GetTitle(),
                    document.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY);
            }
            return null;
        }

        private static IReadOnlyCollection<string> CaptureReferencedDocumentPaths(
            AssemblyDoc totalAssembly)
        {
            if (totalAssembly == null)
            {
                return Array.Empty<string>();
            }

            object[] components;
            try
            {
                components = totalAssembly.GetComponents(false) as object[];
            }
            catch
            {
                components = null;
            }

            if (components == null || components.Length == 0)
            {
                return Array.Empty<string>();
            }

            return components
                .OfType<Component2>()
                .Select(component =>
                {
                    try { return component.GetPathName(); }
                    catch { return null; }
                })
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path =>
                {
                    try { return Path.GetFullPath(path); }
                    catch { return path; }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string TryGetActiveConfigurationName(IModelDoc2 model)
        {
            try
            {
                return model.ConfigurationManager?.ActiveConfiguration?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<string> TryGetConfigurationNames(IModelDoc2 model)
        {
            try
            {
                var names = model?.GetConfigurationNames() as object[];
                if (names == null || names.Length == 0)
                {
                    return Array.Empty<string>();
                }

                return names
                    .Select(name => name?.ToString())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                // 配置枚举只用于真实失败时丰富诊断信息，不应覆盖原恢复结果。
                return Array.Empty<string>();
            }
        }

        private static bool ConfigurationNamesEqual(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeGetPath(IModelDoc2 model)
        {
            try { return model?.GetPathName(); }
            catch { return null; }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
