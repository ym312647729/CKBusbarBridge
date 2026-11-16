using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using System.Collections.Generic;

namespace CKBusbarBridge.Drivers
{
    internal interface IFaceToFaceFrameStoredModelReader
    {
        FrameTemplateIdentity ReadStoredTemplateIdentity(string modelPath);
    }

    /// <summary>
    /// 使用显式委托而不是 Func&lt;IModelDoc2,...&gt;，避免嵌入式 SolidWorks 互操作类型
    /// 作为泛型实参跨程序集时触发 No-PIA 类型边界错误。
    /// </summary>
    internal delegate FaceToFaceFrameReadResult FaceToFaceFrameSecondaryReadback(
        IModelDoc2 totalAssemblyModel,
        string projectPath,
        string ordinaryBasePositionName,
        string expectedMappingJson);

    internal interface IFaceToFaceFrameExistingModelUpdater
    {
        void UpdateExistingModel(
            string projectPath,
            string modelPath,
            FaceToFaceFrameModelPlan targetPlan,
            FaceToFaceFrameInsulatorParameters insulatorParameters);
    }

    internal interface IFaceToFaceFrameExistingInsulatorReconciler
    {
        void ReconcileExistingFrame(
            string projectPath,
            string frameAssemblyPath,
            FaceToFaceFrameModelPlan targetPlan,
            FaceToFaceFrameInsulatorParameters insulatorParameters);
    }

    internal interface IFaceToFaceFrameSecondaryAssemblyOperations
    {
        FaceToFaceFrameParameterSnapshot CaptureCurrentSnapshot(
            string projectPath,
            string ordinaryBasePositionName);

        /// <summary>
        /// 保存完整总装基线，并把总装文件复制到本次事务目录；后续允许保存预拆状态时，
        /// 文件级备份仍能恢复真正的完整装配体。
        /// </summary>
        void SaveBaseline();

        string CaptureMapping();

        void BackupModelDirectory(FaceToFaceFrameModelSnapshot model);

        void RegisterNewModelDirectory(string directoryPath);

        /// <summary>
        /// 登记并清空模板身份迁移模型的原目录。调用前必须已经备份目录、移除全部组件引用并确认文档关闭。
        /// 与新增目录不同，该目录在事务开始时合法存在，失败回滚时必须从备份完整恢复。
        /// </summary>
        void RegisterRecreatedModelDirectory(
            FaceToFaceFrameModelSnapshot existingModel,
            string directoryPath);

        FaceToFaceFrameAssemblyToken InsertPosition(
            FaceToFaceFramePositionPlan targetPosition,
            string targetModelPath);

        void RemovePosition(FaceToFaceFramePositionSnapshot oldPosition);

        /// <summary>
        /// 所有预拆完成后只刷新总装内存引用树，不保存当前不完整装配体。
        /// </summary>
        void RebuildAfterDetach();

        /// <summary>
        /// 保存已经完成业务预拆的总装，然后关闭总装及其原引用文档。
        /// 物理基架驱动和 PackAndGo 必须在该方法成功之后开始。
        /// </summary>
        void SaveDetachedAssemblyAndClose();

        /// <summary>
        /// 从原路径重新打开总装、重建所有可用层级，并重新绑定装配器和反读上下文。
        /// 该步骤必须发生在全部物理模型完成之后、第一次统一插入之前。
        /// </summary>
        void ReopenAssemblyAndForceRebuild();

        void RemateFinalChain(
            IReadOnlyList<FaceToFaceFrameAssemblyToken> finalTokens,
            string ordinaryBasePositionName);

        FaceToFaceFrameParameterSnapshot ValidateFinalChain(
            string projectPath,
            string ordinaryBasePositionName,
            string expectedMappingJson);

        void WriteMappingAndSave(string mappingJson);

        /// <summary>
        /// 回滚前关闭当前总装且不保存，保证可以安全覆盖预拆后已经落盘的总装文件。
        /// 总装已经关闭时该操作为幂等操作。
        /// </summary>
        void CloseTotalAssemblyWithoutSave();

        IReadOnlyCollection<string> GetReferencedFrameModelPaths();

        FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
            string directoryPath);

        /// <summary>
        /// 恢复所有已修改/重建模型目录、删除本次新增目录，并从事务备份覆盖恢复原始总装文件。
        /// 调用前总装必须已经关闭。
        /// </summary>
        void RestoreBackupsAndRemoveNewDirectories();

        void ClearTransactionBackups();
    }

    internal sealed class FaceToFaceFrameExistingInsulator
    {
        internal FaceToFaceFrameExistingInsulator(
            int index,
            string modelPath,
            object nativeComponent)
        {
            Index = index;
            ModelPath = modelPath;
            NativeComponent = nativeComponent;
        }

        internal int Index { get; }

        internal string ModelPath { get; }

        internal object NativeComponent { get; }
    }

    internal interface IFaceToFaceFrameExistingInsulatorOperations
    {
        IReadOnlyList<FaceToFaceFrameExistingInsulator> GetGeneratedInsulators();

        string TargetInsulatorPath { get; }

        void SetPlacementDimension(string dimensionName, double position);

        object InsertInsulator(int insulatorIndex);

        void MateInsulator(
            int insulatorIndex,
            object insertedInsulator,
            FaceToFaceFrameInsulatorPlacementRoute route);

        void DeleteInsulator(int insulatorIndex, object insertedInsulator);

        bool SaveInsulator();

        FaceToFaceFrameCleanupConfirmation CloseInsulator();

        void RebuildAndSaveFrame();
    }
}
