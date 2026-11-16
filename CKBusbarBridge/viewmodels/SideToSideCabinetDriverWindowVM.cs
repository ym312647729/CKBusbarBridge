using UiFeedback = CKBusbarBridgePreview.UiFeedback;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using CKBusbarBridge.views;
using System.Windows.Input;

namespace CKBusbarBridge.viewmodels
{
    /// <summary>
    /// 侧对侧柜体选配与首次模型生成界面的视图模型。
    /// </summary>
    public sealed partial class SideToSideCabinetDriverWindowVM : DimensionHighlightViewModel
    {
        private string _projectPath;
        private readonly ISldWorks _swApp;
        private readonly CabinetDrawingNumberMetadataSession _drawingNumberMetadataSession;

        public SideToSideCabinetDriverWindowVM()
            : this(null)
        {
        }

        public SideToSideCabinetDriverWindowVM(ISldWorks swApp)
        {
            _swApp = swApp;
            _drawingNumberMetadataSession = new CabinetDrawingNumberMetadataSession(
                new SolidWorksCabinetDrawingNumberMetadataStore(_swApp));
            Parameters = new SideToSideCabinetDriverParameters();
            ScenarioDisplayName = DesignScenarioDefaults.GetDisplayName(DesignScenarioId.SideToSide);
            _projectPath = Parameters.ProjectPath;
            SelectProjectPathCommand = DelegateCommand.GetDelegateCommand(_ => SelectProjectPath());
            GenerateCommand = DelegateCommand.GetDelegateCommand(_ => UiFeedback.Error("实体生成暂未开发。", "母线桥项目"));
        }

        public string ScenarioDisplayName { get; }

        public SideToSideCabinetDriverParameters Parameters { get; private set; }

        public string ProjectPath
        {
            get => _projectPath;
            set
            {
                if (_projectPath == value)
                {
                    return;
                }

                _projectPath = value;
                _drawingNumberMetadataSession.BeginNewProject(
                    value,
                    DesignScenarioId.SideToSide);
                if (Parameters != null) Parameters.ProjectPath = value;
                RaisePropertyChanged(nameof(ProjectPath));
            }
        }

        public ICommand SelectProjectPathCommand { get; }

        public ICommand GenerateCommand { get; }

        /// <summary>
        /// 复用柜体窗口的图号信息会话；当前只在本窗口会话内保存草稿。
        /// </summary>
        internal CabinetDrawingNumberMetadataSession DrawingNumberMetadataSession =>
            _drawingNumberMetadataSession;

        /// <summary>
        /// 侧对侧柜体首次生成已接入，生成入口始终显示。
        /// </summary>
        public Visibility GenerateButtonVisibility { get; } = Visibility.Visible;

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

        internal event Action<string> BaseParametersRequested;
    }
}
