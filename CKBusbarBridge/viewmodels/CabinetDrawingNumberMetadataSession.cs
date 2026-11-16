using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using System;

namespace CKBusbarBridge.viewmodels
{
    /// <summary>
    /// 柜体驱动图号信息存储边界；生产实现负责 SolidWorks 模型读写，测试实现可隔离 COM。
    /// </summary>
    internal interface ICabinetDrawingNumberMetadataStore
    {
        CabinetDrawingNumberMetadataLoadResult Load(
            string projectPath,
            DesignScenarioId scenarioId);

        bool TryWriteExisting(
            string projectPath,
            DesignScenarioId scenarioId,
            string metadataJson,
            out string errorMessage);

        bool TryWriteModel(
            IModelDoc2 totalAssemblyModel,
            string metadataJson,
            out string errorMessage);
    }

    /// <summary>
    /// 从项目总装配体读取图号信息的不可变结果。
    /// </summary>
    internal sealed class CabinetDrawingNumberMetadataLoadResult
    {
        internal CabinetDrawingNumberMetadataLoadResult(
            bool assemblyExists,
            string metadataJson,
            string warningMessage)
        {
            AssemblyExists = assemblyExists;
            MetadataJson = metadataJson;
            WarningMessage = warningMessage;
        }

        internal bool AssemblyExists { get; }

        internal string MetadataJson { get; }

        internal string WarningMessage { get; }
    }

    /// <summary>
    /// 管理单个柜体驱动窗口内的图号草稿、已提交数据及总装配体落盘状态。
    /// 项目路径切换时必须重建全部状态，防止不同项目之间串用图号。
    /// </summary>
    internal sealed class CabinetDrawingNumberMetadataSession
    {
        private readonly ICabinetDrawingNumberMetadataStore _store;

        private string _projectPath;
        private DesignScenarioId _scenarioId;
        private bool _assemblyExists;
        private string _draftMetadataJson;
        private DrawingNumberMetadataSnapshot _draftSnapshot;
        private string _committedMetadataJson;
        private DrawingNumberMetadataSnapshot _committedSnapshot;
        private string _warningMessage;
        private bool _isNewProject;

        internal CabinetDrawingNumberMetadataSession(
            ICabinetDrawingNumberMetadataStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        internal bool HasCommittedMetadata => _committedSnapshot != null;

        internal bool HasPendingPersistence { get; private set; }

        internal string CommittedMetadataJson => _committedMetadataJson;

        internal DrawingNumberMetadataSnapshot CommittedSnapshot => _committedSnapshot;

        /// <summary>
        /// 开始一个首次生成会话。用户选择的是项目根目录，不从该目录扫描或识别旧项目。
        /// </summary>
        internal void BeginNewProject(string projectRootPath, DesignScenarioId scenarioId)
        {
            Reset();
            _projectPath = projectRootPath;
            _scenarioId = scenarioId;
            _isNewProject = true;
        }

        /// <summary>
        /// 首次生成创建“合同号(场景)”目录后，保留已提交图号并切换其后续落盘路径。
        /// </summary>
        internal void RelocateNewProject(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目路径不能为空。", nameof(projectPath));
            }

            _projectPath = projectPath;
            _isNewProject = false;
        }

        /// <summary>
        /// 切换项目时先清除旧项目的全部状态，再尝试加载新项目总装配体中的图号信息。
        /// </summary>
        internal void ChangeProject(string projectPath, DesignScenarioId scenarioId)
        {
            Reset();
            _projectPath = projectPath;
            _scenarioId = scenarioId;
            _isNewProject = false;

            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return;
            }

            CabinetDrawingNumberMetadataLoadResult loadResult;
            try
            {
                loadResult = _store.Load(projectPath, scenarioId);
            }
            catch (Exception ex)
            {
                _warningMessage = $"读取总装配体图号信息失败：{ex.Message}";
                return;
            }

            if (loadResult == null)
            {
                return;
            }

            _assemblyExists = loadResult.AssemblyExists;
            _warningMessage = loadResult.WarningMessage;
            if (string.IsNullOrWhiteSpace(loadResult.MetadataJson))
            {
                return;
            }

            if (!DrawingNumberMetadataSnapshot.TryParse(
                    loadResult.MetadataJson,
                    out var snapshot))
            {
                _warningMessage = "当前总装配体中的「图号生成信息」格式无效，将按空白表单打开。";
                return;
            }

            // 已有模型中的有效属性已经落盘，同时作为弹窗反填草稿和生成可用快照。
            _draftMetadataJson = loadResult.MetadataJson;
            _draftSnapshot = snapshot;
            _committedMetadataJson = loadResult.MetadataJson;
            _committedSnapshot = snapshot;
            HasPendingPersistence = false;
        }

        /// <summary>
        /// 返回弹窗反填快照；写入失败时仍返回草稿，方便用户修改后重试。
        /// </summary>
        internal void GetDialogState(
            out DrawingNumberMetadataSnapshot snapshot,
            out string warningMessage)
        {
            snapshot = _draftSnapshot;
            warningMessage = _warningMessage;
        }

        /// <summary>
        /// 新项目提交图号前检查合同号是否已被项目根目录下的文件夹占用。
        /// 旧项目修改图号时不检查自身所在目录。
        /// </summary>
        internal string GetContractFolderConflictMessage(string contractNumber)
        {
            if (!_isNewProject || string.IsNullOrWhiteSpace(_projectPath))
            {
                return null;
            }

            try
            {
                var existingDirectory = ProjectDirectoryPathBuilder
                    .FindExistingContractDirectory(_projectPath, contractNumber);
                return string.IsNullOrWhiteSpace(existingDirectory)
                    ? null
                    : $"合同号“{contractNumber.Trim().ToUpperInvariant()}”已使用，"
                        + $"项目根目录下已存在文件夹：\n{existingDirectory}";
            }
            catch (Exception ex)
            {
                return "无法检查项目根目录中的合同号文件夹：\n" + ex.Message;
            }
        }

        /// <summary>
        /// 校验弹窗提交内容；已有总装配体立即写入，新项目仅在当前会话暂存。
        /// </summary>
        internal bool TryCommit(string metadataJson, out string errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(metadataJson) ||
                !DrawingNumberMetadataSnapshot.TryParse(metadataJson, out var snapshot))
            {
                errorMessage = "图号生成信息格式无效，无法保存。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.ContractNumber))
            {
                errorMessage = "图号生成信息中缺少有效的「合同号」。";
                return false;
            }

            // 无论模型写入是否成功都保留有效草稿，避免用户重新录入整张表单。
            _draftMetadataJson = metadataJson;
            _draftSnapshot = snapshot;
            _warningMessage = null;

            if (_assemblyExists &&
                !_store.TryWriteExisting(
                    _projectPath,
                    _scenarioId,
                    metadataJson,
                    out errorMessage))
            {
                return false;
            }

            _committedMetadataJson = metadataJson;
            _committedSnapshot = snapshot;
            HasPendingPersistence = !_assemblyExists;
            return true;
        }

        /// <summary>
        /// 柜体生成前确认当前项目已经有一份通过校验的图号信息。
        /// </summary>
        internal bool TryEnsureCommitted(out string errorMessage)
        {
            if (_committedSnapshot != null)
            {
                errorMessage = null;
                return true;
            }

            errorMessage = "请先点击「图号生成」填写并确认图号信息。";
            return false;
        }

        /// <summary>
        /// 总装配体创建并打开后，将已提交数据写入模型；失败时保留待落盘状态供重试。
        /// </summary>
        internal bool TryPersistToModel(
            IModelDoc2 totalAssemblyModel,
            out string errorMessage)
        {
            if (!TryEnsureCommitted(out errorMessage))
            {
                return false;
            }

            if (!_store.TryWriteModel(
                    totalAssemblyModel,
                    _committedMetadataJson,
                    out errorMessage))
            {
                HasPendingPersistence = true;
                return false;
            }

            _assemblyExists = true;
            HasPendingPersistence = false;
            return true;
        }

        /// <summary>
        /// 基座生成把下一未用图号写回总装后，同步窗口会话，避免图号弹窗仍显示已用起始号。
        /// </summary>
        internal void AdoptPersistedSnapshot(DrawingNumberMetadataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            var json = snapshot.ToJson();
            _draftMetadataJson = json;
            _draftSnapshot = snapshot;
            _committedMetadataJson = json;
            _committedSnapshot = snapshot;
            _assemblyExists = true;
            HasPendingPersistence = false;
            _warningMessage = null;
        }

        private void Reset()
        {
            _projectPath = null;
            _assemblyExists = false;
            _draftMetadataJson = null;
            _draftSnapshot = null;
            _committedMetadataJson = null;
            _committedSnapshot = null;
            _warningMessage = null;
            _isNewProject = false;
            HasPendingPersistence = false;
        }
    }

    /// <summary>
    /// 固定两个柜体场景共用的生成前置顺序：先校验图号，再准备总装配体，最后写入图号信息。
    /// 返回 false 时调用方必须停止后续柜体参数驱动。
    /// </summary>
    internal static class CabinetDrawingNumberGenerationCoordinator
    {
        internal static bool TryPrepareModel(
            CabinetDrawingNumberMetadataSession session,
            Func<object> prepareModel,
            out IModelDoc2 totalAssemblyModel,
            out string errorMessage)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (prepareModel == null) throw new ArgumentNullException(nameof(prepareModel));

            totalAssemblyModel = null;
            if (!session.TryEnsureCommitted(out errorMessage))
            {
                return false;
            }

            // 避免将嵌入式 SolidWorks Interop 类型作为泛型参数跨程序集暴露。
            totalAssemblyModel = prepareModel() as IModelDoc2;
            return session.TryPersistToModel(totalAssemblyModel, out errorMessage);
        }
    }
}
