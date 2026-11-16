using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 定位侧对侧基座1、基座2和增高基座的共享模板总装配体。
    /// </summary>
    internal interface ISideToSideBaseTemplateLocator
    {
        string GetTemplateAssemblyPath(SideToSideBaseModelKind kind);
    }

    /// <summary>
    /// 从模板复制并完成一棵侧对侧基座模型树的全局变量驱动。
    /// </summary>
    internal interface ISideToSideBaseDriver
    {
        string PrepareModel(
            SideToSideBaseModelPlan plan,
            string sourceAssemblyPath,
            string outputDirectory,
            IDrawingNumberGenerator drawingNumbers);

        void InstallInsulators(
            string hostAssemblyPath,
            IReadOnlyList<string> insulatorAssemblyPaths,
            IReadOnlyList<SideToSideInsulatorPlacementPlan> placements);
    }

    /// <summary>
    /// 隔离 PackAndGo 和 SolidWorks 文档操作，供离线单元测试记录调用。
    /// </summary>
    internal interface ISideToSideBaseModelOperations
    {
        void PackAndGoWithSuffix(
            string assemblyPath,
            string outputDirectory,
            string suffix);

        void ApplyRequiredVariablesAndSave(
            string assemblyPath,
            IReadOnlyDictionary<string, double> variables,
            bool? hasElevatedBase,
            bool keepModelOpenAfterSave);
    }

    /// <summary>
    /// 只负责准备和驱动绝缘子模型；定位与装配由基座驱动流程负责。
    /// </summary>
    internal interface ISideToSideBaseInsulatorDriver
    {
        void ValidateTemplateAvailable(SideToSideInsulatorPlacementPlan plan);

        string ResolveOutputDirectory(
            string projectPath,
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan);

        string PrepareInsulator(
            string projectPath,
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan);

        void CloseOpenDocumentsUnderDirectory(string directoryPath);
    }

    /// <summary>
    /// 绝缘子驱动和宿主装配所需的底层操作。
    /// </summary>
    internal interface ISideToSideBaseInsulatorOperations
    {
        void ValidateTemplateAvailable(SideToSideInsulatorPlacementPlan plan);

        string ResolveOutputDirectory(
            string projectPath,
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan);

        string PrepareInsulator(
            string projectPath,
            SideToSideInsulatorPlacementPlan plan,
            double bracketHostSpan,
            bool closeAfterDrive);

        void CloseOpenDocumentsUnderDirectory(string directoryPath);

        object OpenHostAssembly(string hostAssemblyPath);

        void LoadComponentForInsertion(
            object hostSession,
            string componentPath);

        void WritePlacement(
            object hostSession,
            SideToSideInsulatorPlacementPlan plan);

        object InsertComponent(object hostSession, string componentPath);

        void AddCoordinateMate(
            object hostSession,
            object insertedComponent,
            string hostCoordinateName,
            string childCoordinateName);

        void RollbackInsertedComponents(
            object hostSession,
            IReadOnlyList<object> insertedComponents);

        void CompleteHost(
            object hostSession,
            string hostAssemblyPath,
            bool saveChanges,
            bool keepHostOpen,
            bool closeInsertedComponentDocuments);
    }

    /// <summary>
    /// 把已完成内部驱动的侧对侧基座装入总装，并提供事务回滚能力。
    /// </summary>
    internal interface ISideToSideBaseAssembler
    {
        SideToSideBaseAssemblyToken InsertBase1AtRoot(
            SideToSideBasePositionPlan position,
            string modelPath);

        SideToSideBaseAssemblyToken InsertElevatedAtRoot(
            SideToSideBasePositionPlan position,
            string modelPath);

        SideToSideBaseAssemblyToken InsertBase2OnElevated(
            SideToSideBasePositionPlan position,
            string modelPath,
            SideToSideBaseAssemblyToken elevatedBase);

        void SaveAndRebuild();

        void WriteModelMapping(string mappingJson);

        void Rollback(
            IReadOnlyList<SideToSideBaseAssemblyToken> instancesInReverseOrder);
    }
}
