using System;

namespace CKBusbarBridge.Drivers
{
    internal interface ISideToSideCabinetSecondaryTransaction
    {
        void SaveBaseline();

        void Complete();

        void Restore();
    }

    internal interface ISideToSideCabinetSecondaryOperations
    {
        void DriveRootGlobals();

        void DriveCabinet(
            string cabinetName,
            bool driveBody,
            bool driveFrontHole,
            bool driveBackHole,
            bool switchOpeningMode);

        void DriveExtensionBusbar();

        void DriveInsulatorModels();

        void DriveBaseLifecycle();

        void FinalizeDrive();

        void CaptureSnapshot();

        void CommitSnapshot();
    }

    internal sealed class SideToSideCabinetSecondaryDriveService
    {
        private readonly ISideToSideCabinetSecondaryOperations _operations;
        private readonly Func<ISideToSideCabinetSecondaryTransaction>
            _transactionFactory;

        internal SideToSideCabinetSecondaryDriveService(
            ISideToSideCabinetSecondaryOperations operations,
            Func<ISideToSideCabinetSecondaryTransaction> transactionFactory)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _transactionFactory = transactionFactory
                ?? throw new ArgumentNullException(nameof(transactionFactory));
        }

        internal void Execute(SideToSideCabinetChangeSet changes,
            Action<string, int, int> reportProgress = null)
        {
            if (changes == null)
            {
                throw new ArgumentNullException(nameof(changes));
            }
            if (changes.IsEmpty)
            {
                return;
            }

            var transaction = _transactionFactory();
            if (transaction == null)
            {
                throw new InvalidOperationException("无法创建侧对侧二次驱动事务。");
            }

            var totalSteps = 5
                + (changes.HasRootGlobals ? 1 : 0)
                + (IsCabinetDirty("A柜", changes.HasACabinetBody, changes) ? 1 : 0)
                + (IsCabinetDirty("B柜", changes.HasBCabinetBody, changes) ? 1 : 0)
                + (changes.HasExtensionBusbar ? 1 : 0)
                + (changes.HasInsulatorModels ? 1 : 0)
                + (changes.HasBaseLifecycle ? 1 : 0);
            var completedSteps = 0;
            Action<string, Action> runStage = (stage, execute) =>
            {
                reportProgress?.Invoke(stage, completedSteps, totalSteps);
                execute();
                reportProgress?.Invoke(stage, ++completedSteps, totalSteps);
            };
            runStage("正在保存更新前基线", transaction.SaveBaseline);
            try
            {
                if (changes.HasRootGlobals)
                {
                    runStage("正在更新整体参数", () => _operations.DriveRootGlobals());
                }

                DriveCabinetIfDirty("A柜", changes.HasACabinetBody, changes, runStage);
                DriveCabinetIfDirty("B柜", changes.HasBCabinetBody, changes, runStage);

                if (changes.HasExtensionBusbar)
                {
                    runStage("正在更新伸出排", () => _operations.DriveExtensionBusbar());
                }
                if (changes.HasInsulatorModels)
                {
                    runStage("正在更新绝缘子", () => _operations.DriveInsulatorModels());
                }
                if (changes.HasBaseLifecycle)
                {
                    runStage("正在更新独立基座", () => _operations.DriveBaseLifecycle());
                }

                runStage("正在保存总装配体", () => _operations.FinalizeDrive());
                runStage("正在读取更新结果快照", () => _operations.CaptureSnapshot());
                runStage("正在提交更新事务", () => transaction.Complete());
            }
            catch (Exception primary)
            {
                reportProgress?.Invoke("更新失败，正在恢复总装配体", completedSteps, totalSteps);
                try { transaction.Restore(); }
                catch (Exception recovery) { throw new AggregateException("侧对侧更新失败且事务恢复未完成，请保留备份。", primary, recovery); }
                throw;
            }
            // 文件已经提交，内存快照异常不能再次调用已完成事务的Restore。
            runStage("正在提交更新结果快照", () => _operations.CommitSnapshot());
        }

        private static bool IsCabinetDirty(string cabinetName, bool driveBody,
            SideToSideCabinetChangeSet changes)
        {
            return driveBody
                || changes.CabinetHoles.Contains(cabinetName + "前")
                || changes.CabinetHoles.Contains(cabinetName + "后");
        }

        private void DriveCabinetIfDirty(
            string cabinetName,
            bool driveBody,
            SideToSideCabinetChangeSet changes,
            Action<string, Action> runStage)
        {
            var driveFront = changes.CabinetHoles.Contains(cabinetName + "前");
            var driveBack = changes.CabinetHoles.Contains(cabinetName + "后");
            if (!IsCabinetDirty(cabinetName, driveBody, changes))
            {
                return;
            }

            runStage("正在更新" + cabinetName + "尺寸及开孔", () => _operations.DriveCabinet(
                cabinetName,
                driveBody,
                driveFront,
                driveBack,
                false));
        }
    }
}
