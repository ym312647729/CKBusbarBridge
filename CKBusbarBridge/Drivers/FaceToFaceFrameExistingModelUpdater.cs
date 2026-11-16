using CKBusbarBridge.Models;
using System;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 对既有基架原位驱动外形参数，并把内部绝缘子数量、模型引用和定位同步到目标计划。
    /// </summary>
    internal sealed class FaceToFaceFrameExistingModelUpdater
        : IFaceToFaceFrameExistingModelUpdater
    {
        private readonly IFaceToFaceFrameDriver _frameDriver;
        private readonly IFaceToFaceFrameExistingInsulatorReconciler _insulatorReconciler;

        internal FaceToFaceFrameExistingModelUpdater(
            IFaceToFaceFrameDriver frameDriver,
            IFaceToFaceFrameExistingInsulatorReconciler insulatorReconciler)
        {
            _frameDriver = frameDriver ?? throw new ArgumentNullException(nameof(frameDriver));
            _insulatorReconciler = insulatorReconciler
                ?? throw new ArgumentNullException(nameof(insulatorReconciler));
        }

        public void UpdateExistingModel(
            string projectPath,
            string modelPath,
            FaceToFaceFrameModelPlan targetPlan,
            FaceToFaceFrameInsulatorParameters insulatorParameters)
        {
            if (targetPlan == null) throw new ArgumentNullException(nameof(targetPlan));

            // 先完成基架尺寸驱动，再按最终宽度、绝缘子数量和定位对账内部组件。
            // 外层二次事务已经备份整个 SourceName 目录，任一步失败都会恢复两者。
            _frameDriver.DriveExistingModel(modelPath, targetPlan);
            _insulatorReconciler.ReconcileExistingFrame(
                projectPath,
                modelPath,
                targetPlan,
                insulatorParameters);
        }
    }
}
