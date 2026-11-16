using UiFeedback = CKBusbarBridgePreview.UiFeedback;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Windows;
using CKBusbarBridge.views;
using System.Windows.Input;

namespace CKBusbarBridge.viewmodels
{
    /// <summary>
    /// 面对面场景柜体驱动窗口的视图模型。
    /// </summary>
    public partial class CabinetDriverWindowVM : DimensionHighlightViewModel
    {
        /// <summary>
        /// 当前 SolidWorks 应用程序对象。
        /// 用于在生成完成后打开目标装配体模型。
        /// </summary>
        private readonly ISldWorks _swApp;
        private readonly CabinetDrawingNumberMetadataSession _drawingNumberMetadataSession;

        private const DesignScenarioId ScenarioId = DesignScenarioId.FaceToFace;

        /// <summary>
        /// 手动选择时保存项目根目录；首次生成开始后切换为实际项目目录。
        /// 该字段用于保存界面绑定值，并同步到参数对象中。
        /// </summary>
        private string _projectPath;

        /// <summary>
        /// 初始化 <see cref="CabinetDriverWindowVM" /> 的新实例。
        /// </summary>
        /// <param name="swApp">当前 SolidWorks 应用程序对象。</param>
        public CabinetDriverWindowVM(ISldWorks swApp)
        {
            _swApp = swApp;
            _drawingNumberMetadataSession = new CabinetDrawingNumberMetadataSession(
                new SolidWorksCabinetDrawingNumberMetadataStore(_swApp));
            ScenarioDisplayName = DesignScenarioDefaults.GetDisplayName(ScenarioId);

            Parameters = new CabinetDriverParameters();
            DesignScenarioDefaults.Apply(Parameters, ScenarioId);
            // 使用参数对象中的默认项目路径初始化界面字段。
            _projectPath = Parameters.ProjectPath;

            // 初始化“选择项目路径”命令，供界面按钮绑定调用。
            SelectProjectPathCommand = DelegateCommand.GetDelegateCommand(_ => SelectProjectPath());

            // 初始化“生成”命令，供界面按钮绑定调用。
            GenerateCommand = DelegateCommand.GetDelegateCommand(_ => UiFeedback.Error("实体生成暂未开发。", "母线桥项目"));
        }

        /// <summary>
        /// 当前设计场景在界面上的显示名称。
        /// </summary>
        public string ScenarioDisplayName { get; }

        /// <summary>
        /// 柜体驱动窗口使用的参数对象。
        /// 用于集中存储当前界面的配置数据。
        /// </summary>
        public CabinetDriverParameters Parameters { get; private set; }


        /// <summary>
        /// 当前项目路径。
        /// 当值发生变化时，会同步更新参数对象并通知界面刷新绑定内容。
        /// </summary>
        public string ProjectPath
        {
            get => _projectPath;
            set
            {
                // 如果新旧值相同，则不执行重复赋值和界面通知。
                if (_projectPath == value)
                {
                    return;
                }

                // 更新当前缓存的项目路径。
                _projectPath = value;

                // 项目切换时先重建图号会话，避免沿用上一个项目的草稿或已提交信息。
                _drawingNumberMetadataSession.BeginNewProject(value, ScenarioId);

                // 同步更新参数对象中的项目路径，确保业务数据与界面数据一致。
                if (Parameters != null) Parameters.ProjectPath = value;

                // 通知界面绑定属性已更新。
                RaisePropertyChanged(nameof(ProjectPath));
            }
        }

        /// <summary>
        /// 选择项目路径命令。
        /// </summary>
        public ICommand SelectProjectPathCommand { get; }

        /// <summary>
        /// 执行生成逻辑的命令。
        /// </summary>
        public ICommand GenerateCommand { get; }

        internal event Action<string> BaseParametersRequested;

        /// <summary>
        /// 柜体窗口共用的图号信息会话，仅允许界面启动图号弹窗，不暴露可替换入口。
        /// </summary>
        internal CabinetDrawingNumberMetadataSession DrawingNumberMetadataSession =>
            _drawingNumberMetadataSession;

        /// <summary>
        /// 生成按钮的显示状态。
        /// 当前始终显示。
        /// </summary>
        public Visibility GenerateButtonVisibility { get; } = Visibility.Visible;

        /// <summary>
        /// 打开文件夹选择对话框，供用户选择项目路径。
        /// 选择成功后会校验路径有效性，并更新到当前视图模型中。
        /// </summary>
        private void SelectProjectPath()
        {
            var initialDirectory = ProjectRootPreferenceStore.Default
                .LoadExistingProjectRoot() ?? ProjectPath;
            var dialog = new ProjectRootFolderDialog();
            if (!dialog.TrySelect(initialDirectory, out var directoryPath))
            {
                return;
            }

            ProjectPath = directoryPath;
            SaveProjectRootPreference(directoryPath);
        }

        private static void SaveProjectRootPreference(string projectRootPath)
        {
            if (ProjectRootPreferenceStore.Default.TrySave(
                projectRootPath,
                out var errorMessage))
            {
                return;
            }

            UiFeedback.Show(
                "项目根目录已设置，但无法记住该路径：\n" + errorMessage,
                "保存项目根目录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        internal static bool TryValidateStandaloneBasePlans(
            CabinetDriverParameters parameters,
            out string errorMessage)
        {
            return TryValidateStandaloneBasePlans(parameters, out errorMessage, validateNominalOpeningElevation: true);
        }

        internal static bool TryValidateStandaloneBasePlans(
            CabinetDriverParameters parameters,
            out string errorMessage,
            bool validateNominalOpeningElevation)
        {
            errorMessage = null;
            if (parameters?.General == null)
            {
                errorMessage = "柜体整体参数不能为空。";
                return false;
            }
            if (!BusbarPhaseSpacingRules.TryValidate(
                    parameters.General.BusbarSpec,
                    parameters.General.PhaseSpacing,
                    parameters.General.InsulationDistance,
                    out errorMessage))
            {
                return false;
            }
            try
            {
                var plans = FaceToFaceBaseDrivePlanner.CreatePlans(parameters);
                if (validateNominalOpeningElevation)
                    new CabinetOpeningElevationValidator().Validate(parameters, plans);
                if (!FaceToFaceOffsetFrameLengthRules.TryGetBusbarWidth(
                    parameters?.General?.BusbarSpec,
                    out var busbarWidth))
                {
                    throw new ArgumentException("无法从母排规格中解析有效排宽。");
                }

                var phaseSpacing = parameters?.General?.PhaseSpacing ?? 0d;
                var minimumBaseWidth =
                    CKBusbarBridgePreview.FaceToFaceUnifiedPlanningRules
                        .CalculateMinimumBaseWidth(phaseSpacing, busbarWidth,
                            parameters.General.InsulationDistance);
                foreach (var plan in plans)
                {
                    if (plan.BaseModel.Width + 0.001d >= minimumBaseWidth)
                    {
                        continue;
                    }

                    // 使用未取整的独立基座宽度下限，确保四个启用柜位都逐一校验。
                    throw new ArgumentOutOfRangeException(
                        nameof(parameters),
                        $"{plan.PositionName}基座宽度不能小于柜内三相布置要求："
                        + $"实际 {plan.BaseModel.Width.ToString("0.###", CultureInfo.InvariantCulture)} mm，"
                        + $"要求 {minimumBaseWidth.ToString("0.###", CultureInfo.InvariantCulture)} mm"
                        + "（相间距×2+排宽+2×(19.5+绝缘距离+5)）。");
                }

                return true;
            }
            catch (ArgumentException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
