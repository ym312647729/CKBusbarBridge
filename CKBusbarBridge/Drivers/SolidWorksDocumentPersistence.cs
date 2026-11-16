using NLog;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CKBusbarBridgePreview;

namespace CKBusbarBridge.Drivers
{
    /// <summary>当前模型的可写性检查、原生保存及诊断；关联子件交给 SolidWorks 处理。</summary>
    internal sealed class SolidWorksDocumentPersistence
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly ISldWorks _application;

        internal SolidWorksDocumentPersistence(ISldWorks application)
        {
            _application = application ?? throw new ArgumentNullException(nameof(application));
        }

        internal void PrepareForGeneration()
        {
            var preference = (int)swUserPreferenceToggle_e.swExtRefOpenReadOnly;
            _application.SetUserPreferenceToggle(preference, false);
            if (_application.GetUserPreferenceToggle(preference))
                throw new InvalidOperationException("无法取消勾选“以只读方式打开参考文件”，已停止模型生成。");
            var environment = $"SolidWorks={_application.RevisionNumber()}；插件={typeof(SolidWorksDocumentPersistence).Assembly.GetName().Version}；参考文件只读=False。";
            Logger.Info("模型生成环境：{0}", environment);
            FaceToWallPreviewTrace.Info("模型生成环境", environment);
        }

        internal void EnsureWritable(IModelDoc2 model, string expectedPath = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var path = model.GetPathName();
            SolidWorksFileHelper.ValidateOpenedDocumentPath(expectedPath ?? path, path, 0, 0);
            try
            {
                if (File.Exists(path)) EnsureWritableFile(path, model.IsOpenedReadOnly());
                else
                {
                    var owner = RequireVirtualStorageModel(model);
                    try { EnsureWritableFile(owner.GetPathName(), model.IsOpenedReadOnly() || owner.IsOpenedReadOnly()); }
                    finally { SolidWorksFileHelper.ReleaseComReference(owner); }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                || exception is InvalidOperationException)
            {
                Logger.Error(exception, "模型写入前可写性检查失败：{0}", path);
                FaceToWallPreviewTrace.Error("模型写入前检查失败", exception, path);
                throw;
            }
            Logger.Debug("写入前检查：文件={0}；只读=False；待保存={1}。", path, model.GetSaveFlag());
        }

        internal static void EnsureWritableFile(string path, bool openedReadOnly)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("待写入模型不存在或尚未保存：" + path, path);
            if (openedReadOnly || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new InvalidOperationException("模型为只读，尚未写入参数：" + path);

            // SW 已打开的模型可能禁止其他句柄写入；用独立临时文件探测目录权限，避免误判 SW 自身的锁。
            var probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), ".dq-write-probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose)) { }
        }

        internal void SaveModel(IModelDoc2 model, string expectedPath = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var path = model.GetPathName();
            SolidWorksFileHelper.ValidateOpenedDocumentPath(expectedPath ?? path, path, 0, 0);
            if (File.Exists(path))
            {
                SaveCurrentDocument(model);
                return;
            }

            // 虚拟文档由已核验的独立宿主承载，向该宿主提交原生保存。
            var owner = RequireVirtualStorageModel(model);
            try { EnsureWritable(owner); owner.SetSaveFlag(); SaveCurrentDocument(owner); }
            finally { SolidWorksFileHelper.ReleaseComReference(owner); }
        }

        private void SaveCurrentDocument(IModelDoc2 model)
        {
            var path = model.GetPathName();
            try
            {
                new SolidWorksDocumentConflictGuard(_application).PrepareForSave(model);
                var policy = new SolidWorksDocumentSavePolicy();
                var isAssembly = model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
                var entry = policy.CreateEntry(path, 0, isAssembly, true,
                    () => model.GetSaveFlag(), () => NeedsRebuild(model), () => EnsureWritable(model, path),
                    () => model.EditRebuild3(), () => SaveDocumentCore(model, path),
                    save => SaveInRebuildContext(model, save), saveEvenIfClean: isAssembly);
                policy.SaveCurrent(entry);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "模型保存失败：文件={0}。", path);
                FaceToWallPreviewTrace.Error("模型保存失败", exception, path);
                throw;
            }
        }

        private void SaveInRebuildContext(IModelDoc2 model, Action save)
        {
            var original = _application.IActiveDoc2;
            try
            {
                var wasVisible = model.Visible;
                var configuration = ActiveConfiguration(model);
                var originalConfiguration = original == null ? null : ActiveConfiguration(original);
                using (var scope = new SolidWorksDocumentRebuildScope(() => IsActive(model),
                    () =>
                    {
                        ActivateForSave(model);
                        RequireConfiguration(model, configuration);
                    }, () =>
                    {
                        if (original != null)
                        {
                            ActivateForSave(original);
                            RequireConfiguration(original, originalConfiguration);
                        }
                        if (!wasVisible) model.Visible = false;
                        if (!IsActive(original)) throw new InvalidOperationException("保存求解后活动文档未恢复。");
                    }))
                    scope.Rebuild(() =>
                    {
                        save();
                        RequireConfiguration(model, configuration);
                        return true;
                    });
            }
            finally { SolidWorksFileHelper.ReleaseComReference(original); }
        }

        private string ActiveConfiguration(IModelDoc2 model)
        {
            var manager = model.ConfigurationManager;
            IConfiguration configuration = null;
            try
            {
                configuration = manager?.ActiveConfiguration;
                if (configuration == null || string.IsNullOrWhiteSpace(configuration.Name))
                    throw new InvalidOperationException("保存求解时无法核验原活动配置：" + model.GetPathName());
                return configuration.Name;
            }
            finally
            {
                SolidWorksFileHelper.ReleaseComReference(configuration);
                SolidWorksFileHelper.ReleaseComReference(manager);
            }
        }

        private void RequireConfiguration(IModelDoc2 model, string expected)
        {
            if (!string.Equals(ActiveConfiguration(model), expected, StringComparison.Ordinal))
                throw new InvalidOperationException("保存求解期间活动配置发生变化：" + model.GetPathName() + "；原配置=" + expected);
        }

        private bool IsActive(IModelDoc2 expected)
        {
            var active = _application.IActiveDoc2;
            try { return SameDocument(active, expected); }
            finally { SolidWorksFileHelper.ReleaseComReference(active); }
        }

        private void ActivateForSave(IModelDoc2 expected)
        {
            var title = expected.GetTitle();
            if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("保存求解目标缺少标题：" + expected.GetPathName());
            var errors = 0;
            var activated = _application.ActivateDoc3(title, false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as IModelDoc2;
            try
            {
                if (!SameDocument(activated, expected))
                    throw new InvalidOperationException("保存求解激活文档身份不符：" + expected.GetPathName() + "；errors=" + errors);
            }
            finally { SolidWorksFileHelper.ReleaseComReference(activated); }
        }

        private bool SameDocument(IModelDoc2 left, IModelDoc2 right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            // 也支持尚未保存的原活动文档；比较原生 COM 身份，不按标题或目录猜测。
            var leftIdentity = IntPtr.Zero;
            var rightIdentity = IntPtr.Zero;
            try
            {
                leftIdentity = Marshal.GetIUnknownForObject(left);
                rightIdentity = Marshal.GetIUnknownForObject(right);
                return leftIdentity == rightIdentity;
            }
            finally
            {
                if (rightIdentity != IntPtr.Zero) Marshal.Release(rightIdentity);
                if (leftIdentity != IntPtr.Zero) Marshal.Release(leftIdentity);
            }
        }

        private IModelDoc2 RequireVirtualStorageModel(IModelDoc2 model)
        {
            var extension = model.Extension;
            try
            {
                object paths;
                object titles;
                if (!extension.IsVirtualComponent3(out paths, out titles) || !(paths is string[] owners) || owners.Length == 0)
                    throw new InvalidOperationException("模型没有独立文件且无法核验虚拟组件宿主：" + model.GetPathName());
                var ownerPath = owners[owners.Length - 1];
                if (string.IsNullOrWhiteSpace(ownerPath) || !File.Exists(ownerPath)
                    || string.Equals(Path.GetFullPath(ownerPath), Path.GetFullPath(model.GetPathName()), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("虚拟组件的独立宿主路径无效：" + ownerPath);
                return new SolidWorksDriver(_application).FindOpenDocumentByPathConfirmed(ownerPath)
                    ?? throw new InvalidOperationException("虚拟组件宿主未加载：" + ownerPath);
            }
            finally { SolidWorksFileHelper.ReleaseComReference(extension); }
        }


        private bool NeedsRebuild(IModelDoc2 model)
        {
            var extension = model.Extension;
            try
            {
                if (extension == null) throw new InvalidOperationException("模型扩展不可用，无法核验重建状态：" + model.GetPathName());
                return extension.NeedsRebuild2 != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt;
            }
            finally { SolidWorksFileHelper.ReleaseComReference(extension); }
        }

        private SolidWorksDocumentSaveOutcome SaveDocumentCore(IModelDoc2 model, string path)
        {
            var errors = 0;
            var warnings = 0;
            // 由 SolidWorks 原生处理装配体关联保存，程序不再遍历子件逐件调用 Save3。
            var options = swSaveAsOptions_e.swSaveAsOptions_Silent;
            if (model.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                options |= swSaveAsOptions_e.swSaveAsOptions_SaveReferenced;
            var saved = model.Save3((int)options, ref errors, ref warnings);
            var dirty = model.GetSaveFlag();
            var detail = $"文件={path}；saved={saved}；errors={errors}({DescribeBits(typeof(swFileSaveError_e), errors)})；"
                + $"warnings={warnings}({DescribeBits(typeof(swFileSaveWarning_e), warnings)})；待保存={dirty}；"
                + $"只读={model.IsOpenedReadOnly()}；文件属性={File.GetAttributes(path)}；"
                + $"SolidWorks={_application.RevisionNumber()}；插件={typeof(SolidWorksDocumentPersistence).Assembly.GetName().Version}";
            if (!saved || errors != 0)
            {
                Logger.Error("模型保存失败：{0}", detail);
            }
            if (warnings != 0) Logger.Warn("模型保存警告：{0}", detail);
            else Logger.Debug("模型保存结果：{0}", detail);
            var rebuildWarning = (warnings & (int)(swFileSaveWarning_e.swFileSaveWarning_NeedsRebuild
                | swFileSaveWarning_e.swFileSaveWarning_RebuildError)) != 0;
            return new SolidWorksDocumentSaveOutcome(saved, errors, rebuildWarning, detail);
        }

        internal static string DescribeBits(Type enumType, int value)
        {
            if (value == 0) return "无";
            var names = new List<string>();
            var remaining = value;
            foreach (var flag in Enum.GetValues(enumType).Cast<object>()
                .Select(item => Convert.ToInt32(item)).Distinct().OrderBy(item => item))
            {
                if (flag <= 0 || (flag & (flag - 1)) != 0 || (value & flag) == 0) continue;
                names.Add(Enum.GetName(enumType, flag));
                remaining &= ~flag;
            }
            if (remaining != 0) names.Add("未知位=" + remaining);
            return string.Join("|", names);
        }
    }
}
