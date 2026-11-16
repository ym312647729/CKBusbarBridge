using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    internal interface IFaceToFaceFrameModelOperations
    {
        void ApplyGlobalVariablesAndRebuild(
            string assemblyPath,
            IReadOnlyDictionary<string, object> variables);

        void ApplySmallOffsetSealPlatesAndRebuild(
            string assemblyPath,
            FaceToFaceSmallOffsetSealPlateDrivePlan plan,
            IDrawingNumberGenerator drawingNumbers = null, bool? suppressTemplateAngleNotches = null,
            BridgeAngleNotchPlan angleNotches = null);

        void ApplyTemplateAngleNotches(string assemblyPath, FaceToFaceFrameModelPlan plan, IDrawingNumberGenerator drawingNumbers);

        void SaveModel(string assemblyPath);

        void WriteCustomProperty(
            string assemblyPath,
            string propertyName,
            string value);

        void PackAndGoWithSuffix(
            string assemblyPath,
            string outputDirectory,
            string suffix);

        FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
            string directoryPath);

        FaceToFaceFrameCleanupConfirmation RequestCloseDocumentsUnderDirectory(
            string directoryPath);
    }

    internal interface IFaceToFaceFrameDriver
    {
        string PrepareModel(
            FaceToFaceFrameModelPlan plan,
            string templateAssemblyPath,
            string outputDirectory,
            IDrawingNumberGenerator drawingNumbers);

        void DriveExistingModel(
            string assemblyPath,
            FaceToFaceFrameModelPlan targetPlan);
    }

    /// <summary>
    /// 首次生成基架期间的总装配体生命周期边界。
    /// 生成服务通过该边界先关闭总装、批量生成全部物理模型，再重新打开总装并创建新的装配器。
    /// </summary>
    internal interface IFaceToFaceFrameInitialGenerationSession
    {
        string CaptureModelMapping();

        void SaveAndCloseTotalAssembly();

        FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
            string directoryPath);

        IFaceToFaceFrameAssembler ReopenTotalAssembly();

        void EnsureTotalAssemblyOpen();
    }

    internal interface IFaceToFaceFrameInsulatorDriver
    {
        string PrepareInsulator(
            string projectPath,
            FaceToFaceFrameInsulatorDrivePlan plan);

        void AssembleIntoFrame(
            string frameAssemblyPath,
            string insulatorAssemblyPath,
            FaceToFaceFrameModelPlan modelPlan);

        FaceToFaceFrameCleanupConfirmation CloseDocumentsAfterFailure(
            string frameAssemblyPath,
            string insulatorAssemblyPath);
    }
}
