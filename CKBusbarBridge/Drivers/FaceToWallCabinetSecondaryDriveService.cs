using System;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对墙二次局部驱动的 SolidWorks 操作边界。
    /// 编排服务只决定调用与顺序，具体模型操作由 ViewModel 适配器完成。
    /// </summary>
    internal interface IFaceToWallCabinetSecondaryOperations
    {
        void DriveGeneralGlobals();

        void DriveCabinet(bool driveBody, bool driveFrontHole, bool driveRearHole);

        void DriveWallBushing();

        void DriveExtensionBusbar();

        void DriveInsulator();

        void DriveBase();

        void FinalizeDrive();

        void CaptureSnapshot();
    }

    /// <summary>
    /// 严格按照已确认顺序执行面对墙脏子驱动：
    /// 总装全局变量、柜体、穿墙套管、伸出排、绝缘子、基座、总装保存、快照。
    /// </summary>
    internal sealed class FaceToWallCabinetSecondaryDriveService
    {
        private readonly IFaceToWallCabinetSecondaryOperations _operations;

        internal FaceToWallCabinetSecondaryDriveService(
            IFaceToWallCabinetSecondaryOperations operations)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        }

        internal void Execute(FaceToWallCabinetChangeSet changes,
            Action<string, int, int> reportProgress = null)
        {
            if (changes == null)
            {
                throw new ArgumentNullException(nameof(changes));
            }

            var driveFront = changes.CabinetHoles.Contains(FaceToWallCabinetChangeAnalyzer.Front);
            var driveRear = changes.CabinetHoles.Contains(FaceToWallCabinetChangeAnalyzer.Rear);
            var totalSteps = 2
                + (changes.HasGeneralGlobals ? 1 : 0)
                + (changes.HasCabinetBody || driveFront || driveRear ? 1 : 0)
                + (changes.HasWallBushing ? 1 : 0)
                + (changes.HasExtensionBusbar ? 1 : 0)
                + (changes.HasInsulator ? 1 : 0)
                + (changes.HasBase ? 1 : 0);
            var completedSteps = 0;
            Action<string, Action> runStage = (stage, execute) =>
            {
                reportProgress?.Invoke(stage, completedSteps, totalSteps);
                execute();
                reportProgress?.Invoke(stage, ++completedSteps, totalSteps);
            };

            if (changes.HasGeneralGlobals)
            {
                runStage("正在更新整体参数", () => _operations.DriveGeneralGlobals());
            }

            if (changes.HasCabinetBody || driveFront || driveRear)
            {
                runStage("正在更新柜体尺寸及开孔", () => _operations.DriveCabinet(changes.HasCabinetBody, driveFront, driveRear));
            }

            if (changes.HasWallBushing)
            {
                runStage("正在更新穿墙套管", () => _operations.DriveWallBushing());
            }

            if (changes.HasExtensionBusbar)
            {
                runStage("正在更新伸出排", () => _operations.DriveExtensionBusbar());
            }

            if (changes.HasInsulator)
            {
                runStage("正在更新绝缘子", () => _operations.DriveInsulator());
            }

            if (changes.HasBase)
            {
                runStage("正在更新独立基座", () => _operations.DriveBase());
            }

            runStage("正在保存总装配体", () => _operations.FinalizeDrive());
            runStage("正在读取更新结果快照", () => _operations.CaptureSnapshot());
        }
    }
}
