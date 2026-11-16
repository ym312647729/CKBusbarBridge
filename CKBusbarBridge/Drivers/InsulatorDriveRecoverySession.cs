using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    /// <summary>已定位写入对象的驱动或反读失败；模型身份、引用隔离错误不使用此类型。</summary>
    internal sealed class InsulatorDriveRetryException : InvalidOperationException
    {
        internal InsulatorDriveRetryException(string message) : base(message) { }
        internal InsulatorDriveRetryException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>一次生成独占的恢复清单，不跨窗口或生成批次共享状态。</summary>
    internal sealed class InsulatorDriveRecoverySession
    {
        internal const int MaximumAttempts = 5;
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly ISldWorks _application;
        private readonly Dictionary<string, PendingDrive> _pending = new Dictionary<string, PendingDrive>(StringComparer.OrdinalIgnoreCase);
        private readonly string _operationId = "绝缘子重试-" + Guid.NewGuid().ToString("N");

        private sealed class PendingDrive
        {
            internal string Path;
            internal string Configuration;
            internal Action<IModelDoc2, Action<IModelDoc2>> DriveAndValidate;
            internal Exception LastError;
            internal string InstalledNames;
            internal bool AssemblyAttemptsCompleted;
        }

        internal InsulatorDriveRecoverySession(ISldWorks application)
        { _application = application ?? throw new ArgumentNullException(nameof(application)); }

        internal bool HasFailures => _pending.Count > 0;
        internal bool Contains(string path) => !string.IsNullOrWhiteSpace(path) && _pending.ContainsKey(System.IO.Path.GetFullPath(path));

        internal void ConsolidateMountedDrive(IModelDoc2 installedModel, IEnumerable<string> preparedRoots,
            Action<IModelDoc2, Action<IModelDoc2>> driveAndValidate)
        {
            var paths = preparedRoots.Concat(new[] { installedModel.GetPathName() }).Select(System.IO.Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var failures = paths.Where(_pending.ContainsKey).Select(path => _pending[path]).ToArray();
            if (failures.Length == 0) return;
            foreach (var path in paths) _pending.Remove(path);
            var request = new PendingDrive {
                Path = System.IO.Path.GetFullPath(installedModel.GetPathName()),
                Configuration = installedModel.ConfigurationManager.ActiveConfiguration.Name,
                DriveAndValidate = driveAndValidate,
                LastError = new InsulatorDriveRetryException(string.Join(System.Environment.NewLine,
                    failures.Select(failure => failure.LastError.Message)))
            };
            _pending[request.Path] = request;
            Trace("逐相副本合并至实际安装模型", request, 0, request.LastError);
        }

        internal void Drive(IModelDoc2 model, Action<IModelDoc2, Action<IModelDoc2>> driveAndValidate,
            Action<IModelDoc2> registerModel, bool allowDeferred)
        {
            var request = new PendingDrive {
                Path = System.IO.Path.GetFullPath(model.GetPathName()),
                Configuration = model.ConfigurationManager.ActiveConfiguration.Name,
                DriveAndValidate = driveAndValidate
            };
            if (RunAttempts(request, "独立模型", registerModel))
            { _pending.Remove(request.Path); return; }
            if (!allowDeferred) throw new InsulatorDriveRetryException(
                "绝缘子独立驱动及反读连续5次未通过：" + request.Path + System.Environment.NewLine + request.LastError.Message, request.LastError);
            _pending[request.Path] = request;
            Trace("等待总装后补驱动", request, 0, request.LastError);
        }

        internal IModelDoc2 Activate(string path, string configuration)
        {
            var driver = new SolidWorksDriver(_application);
            var target = driver.OpenOrActivateDocumentWithoutResolve(path);
            if (target == null || !SamePath(target.GetPathName(), path))
                throw new InsulatorDriveRetryException("无法打开指定绝缘子模型：" + path);
            var actual = _application.ActiveDoc as IModelDoc2;
            if (actual == null || !SamePath(actual.GetPathName(), path))
            {
                var activationErrors = 0;
                _application.ActivateDoc3(target.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activationErrors);
                actual = _application.ActiveDoc as IModelDoc2;
            }
            if (actual == null || !SamePath(actual.GetPathName(), path) || actual.IsOpenedReadOnly())
                throw new InsulatorDriveRetryException("活动模型不正确或不可写：目标=" + path + "；实际=" + actual?.GetPathName());
            if (!string.Equals(actual.ConfigurationManager.ActiveConfiguration.Name, configuration, StringComparison.OrdinalIgnoreCase)
                && !actual.ShowConfiguration2(configuration))
                throw new InsulatorDriveRetryException("无法切换绝缘子原引用配置：" + path + "；配置=" + configuration);
            if (!string.Equals(actual.ConfigurationManager.ActiveConfiguration.Name, configuration, StringComparison.OrdinalIgnoreCase))
                throw new InsulatorDriveRetryException("绝缘子活动配置反读不符：" + path + "；期望=" + configuration);
            actual.ClearSelection2(true);
            if (actual is AssemblyDoc assembly)
            {
                var editTarget = assembly.GetEditTarget() as IModelDoc2;
                if (editTarget == null || !SamePath(editTarget.GetPathName(), path)
                    || !string.Equals(editTarget.ConfigurationManager.ActiveConfiguration.Name, configuration, StringComparison.OrdinalIgnoreCase))
                { assembly.EditAssembly(); actual.ClearSelection2(true); editTarget = assembly.GetEditTarget() as IModelDoc2; }
                if (editTarget == null || !SamePath(editTarget.GetPathName(), path)
                    || !string.Equals(editTarget.ConfigurationManager.ActiveConfiguration.Name, configuration, StringComparison.OrdinalIgnoreCase))
                    throw new InsulatorDriveRetryException("绝缘子编辑目标未切回指定模型：" + path + "；实际=" + editTarget?.GetPathName());
            }
            var confirmedActive = _application.ActiveDoc as IModelDoc2;
            if (confirmedActive == null || !SamePath(confirmedActive.GetPathName(), path)
                || !string.Equals(confirmedActive.ConfigurationManager.ActiveConfiguration.Name, configuration, StringComparison.OrdinalIgnoreCase))
                throw new InsulatorDriveRetryException("切换编辑目标后活动模型反读不符：目标=" + path + "；实际=" + confirmedActive?.GetPathName());
            return actual;
        }

        private bool RunAttempts(PendingDrive request, string stage, Action<IModelDoc2> registerModel)
        {
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                try
                {
                    Trace(stage + "开始", request, attempt, null);
                    var model = Activate(request.Path, request.Configuration);
                    registerModel?.Invoke(model);
                    request.DriveAndValidate(model, registerModel ?? (_ => { }));
                    Trace(stage + "反读通过", request, attempt, null);
                    return true;
                }
                catch (Exception error) when (error is InsulatorDriveRetryException || error is COMException)
                {
                    request.LastError = error;
                    Logger.Warn(error, "绝缘子驱动反读未通过：{0}；阶段={1}；次数={2}/{3}", request.Path, stage, attempt, MaximumAttempts);
                    Trace(stage + "反读失败", request, attempt, error);
                }
            }
            return false;
        }

        internal void CompleteInAssembly(IModelDoc2 totalModel)
        {
            if (_pending.Count == 0 || _pending.Values.All(request => request.AssemblyAttemptsCompleted)) return;
            var totalPath = totalModel.GetPathName();
            var totalConfiguration = totalModel.ConfigurationManager.ActiveConfiguration.Name;
            var hosts = new Dictionary<string, (IModelDoc2 Model, int Depth)>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in _pending.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                if (request.AssemblyAttemptsCompleted) continue;
                request.AssemblyAttemptsCompleted = true;
                var models = new List<IModelDoc2>();
                try
                {
                    var total = Activate(totalPath, totalConfiguration);
                    var assembly = total as AssemblyDoc ?? throw new InsulatorDriveRetryException("补驱动宿主不是总装配体：" + totalPath);
                    var occurrences = (assembly.GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>()
                        .Where(component => SamePath(component.GetPathName(), request.Path)).ToArray();
                    if (occurrences.Length == 0 || occurrences.Any(component => component.IsSuppressed()
                        || !string.Equals(component.ReferencedConfiguration, request.Configuration, StringComparison.OrdinalIgnoreCase)))
                        throw new InsulatorDriveRetryException("总装中未找到已解析且引用配置一致的绝缘子，未猜测补驱动目标：" + request.Path);
                    request.InstalledNames = string.Join("、", occurrences.Select(component => component.Name2).OrderBy(name => name, StringComparer.Ordinal));
                    foreach (var occurrence in occurrences)
                        for (var parent = occurrence.GetParent(); parent != null; parent = parent.GetParent())
                        {
                            var hostModel = parent.GetModelDoc2() as IModelDoc2;
                            if (hostModel == null) throw new InsulatorDriveRetryException("绝缘子宿主未解析：" + parent.Name2);
                            hosts[System.IO.Path.GetFullPath(hostModel.GetPathName())] = (hostModel, parent.Name2.Split('/').Length);
                        }
                    var succeeded = RunAttempts(request, "插入总装后", model => {
                        if (!models.Any(existing => SamePath(existing.GetPathName(), model.GetPathName()))) models.Add(model);
                    });
                    // 失败的尺寸也保留在输出副本中，供最终提示后的人工修正；不关闭总装引用的模型。
                    SaveDrivenDocuments(request, models);
                    if (succeeded) _pending.Remove(request.Path);
                }
                catch (Exception error) when (error is InsulatorDriveRetryException || error is COMException || error is IOException)
                {
                    request.LastError = request.LastError == null ? error : new InsulatorDriveRetryException(
                        request.LastError.Message + System.Environment.NewLine + error.Message, error);
                    Logger.Warn(error, "总装内绝缘子补驱动未完成：{0}", request.Path);
                    Trace("总装补驱动保留问题", request, MaximumAttempts, error);
                }
            }
            try
            {
                var total = Activate(totalPath, totalConfiguration);
                if (!total.ForceRebuild3(false) || !total.EditRebuild3())
                    throw new InsulatorDriveRetryException("绝缘子补驱动后总装重建未通过：" + totalPath);
                foreach (var host in hosts.Values.OrderByDescending(item => item.Depth)) Save(host.Model);
                Save(total);
            }
            catch (Exception error) when (error is InsulatorDriveRetryException || error is COMException || error is IOException)
            {
                Logger.Warn(error, "绝缘子补驱动后总装保存或重建未完成");
                _pending[totalPath] = new PendingDrive { Path = totalPath, Configuration = totalConfiguration,
                    LastError = error, AssemblyAttemptsCompleted = true };
            }
        }

        private void SaveDrivenDocuments(PendingDrive request, List<IModelDoc2> models)
        {
            var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [request.Path] = 0 };
            var root = _application.GetOpenDocumentByName(request.Path) as AssemblyDoc;
            foreach (var component in (root?.GetComponents(false) as object[] ?? Array.Empty<object>()).OfType<Component2>())
            {
                var model = component.GetModelDoc2() as IModelDoc2;
                if (model == null) continue; // 未加载且未参与驱动的压缩件不主动打开。
                var childPath = System.IO.Path.GetFullPath(model.GetPathName());
                var outputDirectory = System.IO.Path.GetDirectoryName(request.Path) + System.IO.Path.DirectorySeparatorChar;
                if (!childPath.StartsWith(outputDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InsulatorDriveRetryException("绝缘子保存引用超出当前副本目录：" + childPath);
                depths[childPath] = component.Name2.Split('/').Length;
                if (!models.Any(existing => SamePath(existing.GetPathName(), childPath))) models.Add(model);
            }
            foreach (var model in models.OrderBy(item => item.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                .ThenByDescending(item => depths.TryGetValue(System.IO.Path.GetFullPath(item.GetPathName()), out var depth) ? depth : 0))
                Save(model);
        }

        internal string BuildSummary()
        {
            if (_pending.Count == 0) return string.Empty;
            return "生成流程已结束，以下绝缘子问题需要手动修改。已生成模型保留，不能视为尺寸校验全部通过。"
                + System.Environment.NewLine + "独立驱动最多5次，插入总装后补驱动最多5次，每次均核验活动模型并重新反读。"
                + System.Environment.NewLine + string.Join(System.Environment.NewLine + System.Environment.NewLine,
                    _pending.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(item =>
                        "模型：" + item.Path + System.Environment.NewLine + "配置：" + item.Configuration
                        + (string.IsNullOrEmpty(item.InstalledNames) ? string.Empty : System.Environment.NewLine + "总装位置：" + item.InstalledNames)
                        + System.Environment.NewLine + "未完成项：" + item.LastError?.Message));
        }

        private void Save(IModelDoc2 model)
        {
            var errors = 0; var warnings = 0;
            if (!model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0)
                throw new IOException("绝缘子补驱动结果保存失败：" + model.GetPathName() + "；错误=" + errors + "；警告=" + warnings);
        }

        private void Trace(string stage, PendingDrive request, int attempt, Exception error)
            => ProjectLocalTestLog.Default.Write(_operationId, stage, () => {
                var actualModel = _application.ActiveDoc as IModelDoc2;
                var editTarget = (actualModel as AssemblyDoc)?.GetEditTarget() as IModelDoc2;
                return "模型=" + request.Path + "；配置=" + request.Configuration
                    + "；次数=" + attempt.ToString(CultureInfo.InvariantCulture) + "/5；活动模型=" + actualModel?.GetPathName()
                    + "；活动配置=" + actualModel?.ConfigurationManager.ActiveConfiguration.Name
                    + "；编辑目标=" + editTarget?.GetPathName() + "；编辑配置=" + editTarget?.ConfigurationManager.ActiveConfiguration.Name
                    + (error == null ? string.Empty : System.Environment.NewLine + error);
            });

        private bool SamePath(string first, string second) => !string.IsNullOrWhiteSpace(first)
            && !string.IsNullOrWhiteSpace(second) && string.Equals(System.IO.Path.GetFullPath(first), System.IO.Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }
}
