using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 侧对侧绝缘子模型驱动器，只负责准备绝缘子模型。
    /// </summary>
    internal sealed class SideToSideBaseInsulatorDriver : ISideToSideBaseInsulatorDriver
    {
        private readonly ISideToSideBaseInsulatorOperations _operations;

        internal SideToSideBaseInsulatorDriver(ISideToSideBaseInsulatorOperations operations)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        public void ValidateTemplateAvailable(SideToSideInsulatorPlacementPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            _operations.ValidateTemplateAvailable(plan);
        }

        public string ResolveOutputDirectory(
            string projectPath,
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            return _operations.ResolveOutputDirectory(projectPath, plan, bracketHostSpan);
        }

        public string PrepareInsulator(
            string projectPath,
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            return _operations.PrepareInsulator(
                projectPath,
                plan,
                bracketHostSpan,
                closeAfterDrive: true);
        }

        public void CloseOpenDocumentsUnderDirectory(string directoryPath)
        {
            _operations.CloseOpenDocumentsUnderDirectory(directoryPath);
        }

    }

    /// <summary>
    /// SolidWorks 下的绝缘子准备、定位尺寸写入、组件插入和坐标系配合实现。
    /// </summary>
    internal sealed class SolidWorksSideToSideBaseInsulatorOperations
        : ISideToSideBaseInsulatorOperations
    {
        // 与通用绝缘子驱动器一致：支架长度 = 支架所在方向的宿主尺寸 - 20mm。
        private const double BracketLengthHostSpanOffset = 20d;

        private sealed class HostSession
        {
            internal HostSession(
                SolidWorksDriver driver,
                IModelDoc2 model,
                AssemblyDoc assembly)
            {
                Driver = driver;
                Model = model;
                Assembly = assembly;
            }

            internal SolidWorksDriver Driver { get; }

            internal IModelDoc2 Model { get; }

            internal AssemblyDoc Assembly { get; }

            internal List<string> InsertedComponentPaths { get; }
                = new List<string>();

            internal List<IModelDoc2> LoadedComponentModels { get; }
                = new List<IModelDoc2>();
        }

        private readonly ISldWorks _swApp;
        private readonly IModelDoc2 _restoreContextModel;
        private readonly InsulatorDriver _driver;

        internal SolidWorksSideToSideBaseInsulatorOperations(
            ISldWorks swApp,
            IModelDoc2 restoreContextModel,
            InsulatorDriveRecoverySession driveRecovery = null)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _restoreContextModel = restoreContextModel
                ?? throw new ArgumentNullException(nameof(restoreContextModel));
            _driver = new InsulatorDriver(swApp, driveRecovery);
        }

        public void ValidateTemplateAvailable(SideToSideInsulatorPlacementPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            SideToSideInsulatorTemplateLocator.GetTemplateAssemblyPath(
                TemplateArchiveCacheManager.GetTemplateRootPath(),
                plan.HostModelKind);
        }

        public string PrepareInsulator(
            string projectPath,
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan,
            bool closeAfterDrive)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var templateAssemblyPath =
                SideToSideInsulatorTemplateLocator.GetTemplateAssemblyPath(
                    TemplateArchiveCacheManager.GetTemplateRootPath(),
                    plan.HostModelKind);
            var templateDirectory = Path.GetDirectoryName(templateAssemblyPath);
            var isolationName = BuildIsolationDirectoryName(plan, bracketHostSpan);
            var isBase1 = plan.HostModelKind == SideToSideBaseModelKind.Base1;
            var modelFileNameSuffix = isBase1
                ? ResolveBase1InstallationModelName(plan)
                : null;
            var suppressedLBracketPadComponentIndexes = isBase1
                ? ResolveBase1SuppressedPadComponentIndexes(plan)
                : null;
            var path = _driver.PrepareUniversalThreePadInsulatorFromTemplateDirectory(
                isBase1,
                projectPath,
                templateDirectory,
                plan.BusbarSpec,
                plan.PhaseSpacing,
                InsulatorHeightRules.ToBodyHeight(plan.InsulatorHeight),
                bracketHostSpan,
                plan.SinglePhaseRootCount,
                _restoreContextModel,
                isolationName,
                closeAfterDrive,
                Path.GetFileName(templateAssemblyPath),
                modelFileNameSuffix,
                suppressedLBracketPadComponentIndexes,
                plan.Layout);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException(
                    "侧对侧基座绝缘子驱动后未找到模型：" + path,
                    path);
            }

            return path;
        }

        public void CloseOpenDocumentsUnderDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return;
            }

            // 回滚删除绝缘子目录前必须确认所有 SolidWorks 文档已关闭。
            new SolidWorksDriver(_swApp).CloseOpenDocumentsUnderDirectoryConfirmed(
                directoryPath,
                null);
        }

        public string ResolveOutputDirectory(
            string projectPath,
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            return InsulatorDriver.BuildThreePadInsulatorOutputDirectory(
                projectPath,
                BuildIsolationDirectoryName(plan, bracketHostSpan),
                InsulatorHeightRules.ToBodyHeight(plan.InsulatorHeight),
                plan.PhaseSpacing,
                bracketHostSpan - BracketLengthHostSpanOffset);
        }

        public object OpenHostAssembly(string hostAssemblyPath)
        {
            if (string.IsNullOrWhiteSpace(hostAssemblyPath) || !File.Exists(hostAssemblyPath))
            {
                throw new FileNotFoundException(
                    "侧对侧绝缘子宿主基座不存在：" + hostAssemblyPath,
                    hostAssemblyPath);
            }

            var driver = new SolidWorksDriver(_swApp);
            IModelDoc2 model = null;
            try
            {
                model = driver.OpenOrActivateDocument(Path.GetFullPath(hostAssemblyPath));
                var assembly = model as AssemblyDoc;
                if (model == null || assembly == null)
                {
                    throw new InvalidOperationException(
                        "侧对侧绝缘子宿主不是有效装配体：" + hostAssemblyPath);
                }

                SWHelper.EnsureAssemblyEditingContext(_swApp, model);
                return new HostSession(driver, model, assembly);
            }
            catch
            {
                if (model != null)
                {
                    try
                    {
                        // 打开后初始化失败也要立即释放文档，否则回滚时新生成目录会被占用。
                        driver.TryCloseDocumentByPath(Path.GetFullPath(hostAssemblyPath));
                    }
                    catch
                    {
                        // 保留初始化失败的原始异常。
                    }

                    try
                    {
                        SWHelper.EnsureAssemblyEditingContext(_swApp, _restoreContextModel);
                    }
                    catch
                    {
                        // 恢复上下文失败不应覆盖宿主初始化的原始异常。
                    }

                    SolidWorksFileHelper.ReleaseComReference(model);
                }

                throw;
            }
        }

        public void WritePlacement(
            object hostSession,
            SideToSideInsulatorPlacementPlan plan)
        {
            var session = RequireSession(hostSession);
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var dimension = session.Model.Parameter(plan.PlacementDimensionName) as Dimension;
            if (dimension == null)
            {
                throw new InvalidOperationException(
                    "侧对侧基座缺少绝缘子定位尺寸“"
                    + plan.PlacementDimensionName
                    + "”："
                    + session.Model.GetPathName());
            }

            // SolidWorks 长度系统值为米，界面和规划值统一为毫米。
            dimension.SystemValue = plan.PlacementHeight / 1000d;
        }

        public void LoadComponentForInsertion(
            object hostSession,
            string componentPath)
        {
            var session = RequireSession(hostSession);
            if (string.IsNullOrWhiteSpace(componentPath) || !File.Exists(componentPath))
            {
                throw new FileNotFoundException(
                    "待加载的侧对侧基座绝缘子不存在：" + componentPath,
                    componentPath);
            }

            var fullPath = Path.GetFullPath(componentPath);
            var model = _swApp.GetOpenDocumentByName(fullPath) as IModelDoc2
                ?? session.Driver.OpenOrActivateDocument(fullPath);
            if (model == null)
            {
                throw new InvalidOperationException(
                    "无法打开已驱动的侧对侧基座绝缘子模型：" + fullPath);
            }

            session.LoadedComponentModels.Add(model);
            SWHelper.EnsureAssemblyEditingContext(_swApp, session.Model);
        }

        public object InsertComponent(object hostSession, string componentPath)
        {
            var session = RequireSession(hostSession);
            if (string.IsNullOrWhiteSpace(componentPath) || !File.Exists(componentPath))
            {
                throw new FileNotFoundException(
                    "待插入的侧对侧基座绝缘子不存在：" + componentPath,
                    componentPath);
            }

            SWHelper.EnsureAssemblyEditingContext(_swApp, session.Model);
            var fullPath = Path.GetFullPath(componentPath);
            var component = session.Assembly.AddComponent5(
                fullPath,
                (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                string.Empty,
                false,
                string.Empty,
                0,
                0,
                0);
            if (component != null)
            {
                session.InsertedComponentPaths.Add(fullPath);
            }

            return component;
        }

        public void AddCoordinateMate(
            object hostSession,
            object insertedComponent,
            string hostCoordinateName,
            string childCoordinateName)
        {
            var session = RequireSession(hostSession);
            var component = insertedComponent as Component2;
            if (component == null)
            {
                throw new ArgumentException("绝缘子组件句柄无效。", nameof(insertedComponent));
            }

            // 父坐标系位于当前宿主装配体根级，parent 参数必须传 null。
            if (!PublicMethod.AddMateUseCoord(
                _swApp,
                session.Assembly,
                null,
                component,
                hostCoordinateName,
                childCoordinateName))
            {
                throw new InvalidOperationException(
                    "侧对侧基座绝缘子坐标系配合失败：父="
                    + hostCoordinateName
                    + "，子="
                    + childCoordinateName
                    + "。");
            }
        }

        public void RollbackInsertedComponents(
            object hostSession,
            IReadOnlyList<object> insertedComponents)
        {
            var session = RequireSession(hostSession);
            if (insertedComponents == null)
            {
                return;
            }

            foreach (var component in insertedComponents.OfType<Component2>().Reverse())
            {
                session.Model.ClearSelection2(true);
                if (component.Select(false))
                {
                    session.Assembly.DeleteSelections(
                        (int)swDeleteSelectionOptions_e.swDelete_Children);
                }
            }

            session.Model.ClearSelection2(true);
        }

        public void CompleteHost(
            object hostSession,
            string hostAssemblyPath,
            bool saveChanges,
            bool keepHostOpen,
            bool closeInsertedComponentDocuments)
        {
            var session = RequireSession(hostSession);
            var completedSuccessfully = false;
            try
            {
                if (saveChanges)
                {
                    FaceToFaceBaseOperationGuard.EnsureRebuildSucceeded(
                        session.Model.EditRebuild3(),
                        "侧对侧基座绝缘子装配",
                        hostAssemblyPath);
                    var errors = 0;
                    var warnings = 0;
                    var saved = session.Model.Save3(
                        (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                            | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                        ref errors,
                        ref warnings);
                    if (!saved || errors != 0)
                    {
                        throw new InvalidOperationException(
                            "保存侧对侧绝缘子宿主失败："
                            + hostAssemblyPath
                            + "；errors="
                            + errors
                            + "；warnings="
                            + warnings);
                    }
                }

                completedSuccessfully = true;
            }
            finally
            {
                var leaveHostOpen = completedSuccessfully && keepHostOpen;
                try
                {
                    if (!leaveHostOpen)
                    {
                        session.Driver.TryCloseDocumentByPath(Path.GetFullPath(hostAssemblyPath));
                    }
                }
                finally
                {
                    try
                    {
                        if (closeInsertedComponentDocuments)
                        {
                            // 宿主保存后不再需要绝缘子作为独立打开文档，
                            // 成功与失败路径均关闭绝缘子装配体及其子零件。
                            foreach (var directory in session.InsertedComponentPaths
                                .Select(Path.GetDirectoryName)
                                .Where(path => !string.IsNullOrWhiteSpace(path))
                                .Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                session.Driver.CloseOpenDocumentsUnderDirectory(directory);
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            // 无论宿主是否保持打开，都恢复总装上下文，后续组件只能插入总装根级。
                            SWHelper.EnsureAssemblyEditingContext(_swApp, _restoreContextModel);
                        }
                        finally
                        {
                            foreach (var componentModel in session.LoadedComponentModels)
                            {
                                SolidWorksFileHelper.ReleaseComReference(componentModel);
                            }

                            SolidWorksFileHelper.ReleaseComReference(session.Model);
                        }
                    }
                }
            }
        }

        private static HostSession RequireSession(object value)
        {
            return value as HostSession
                ?? throw new ArgumentException("侧对侧基座宿主会话无效。", nameof(value));
        }

        private static string BuildIsolationDirectoryName(
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan)
        {
            if (!FaceToFaceBusbarCenterDistanceHelper.TryParseBusbarThickness(
                plan.BusbarSpec,
                out var busbarThickness))
            {
                throw new InvalidOperationException(
                    "无法从侧对侧铜排规格中解析排厚：" + plan.BusbarSpec);
            }

            var physicalIdentity = InsulatorDriver.BuildThreePadInsulatorIsolationDirectoryName(
                busbarThickness,
                plan.PhaseSpacing,
                InsulatorHeightRules.ToBodyHeight(plan.InsulatorHeight),
                bracketHostSpan,
                plan.SinglePhaseRootCount);
            if (plan.HostModelKind != SideToSideBaseModelKind.Base1)
            {
                return physicalIdentity;
            }

            return physicalIdentity
                + (plan.Layout == null ? string.Empty : "-Z" + plan.Layout.Identity)
                + "-" + ResolveBase1InstallationModelName(plan);
        }

        private static string ResolveBase1InstallationModelName(
            SideToSideInsulatorPlacementPlan plan)
        {
            for (var index = 1; index <= 3; index++)
            {
                if (plan.HostCoordinateName.IndexOf(
                    "绝缘子安装坐标系" + index,
                    StringComparison.Ordinal) >= 0)
                {
                    return "I" + index;
                }
            }

            throw new InvalidOperationException(
                "无法从基座1绝缘子安装坐标系解析独立模型编号："
                + plan.HostCoordinateName);
        }

        internal static IReadOnlyCollection<int> ResolveBase1SuppressedPadComponentIndexes(
            SideToSideInsulatorPlacementPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var installationModelName = ResolveBase1InstallationModelName(plan);
            switch (installationModelName)
            {
                case "I1":
                    return Array.Empty<int>();
                case "I2":
                    return new[] { 2 };
                case "I3":
                    return new[] { 1, 3 };
                default:
                    throw new InvalidOperationException(
                        "未配置基座1绝缘子独立模型的垫块压缩规则："
                        + installationModelName);
            }
        }
    }
}
