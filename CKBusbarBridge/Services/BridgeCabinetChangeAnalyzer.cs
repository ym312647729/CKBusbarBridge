using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Prototype
{
    internal enum BridgeCabinetUpdateKind { Unchanged, Supports, Elevation, LocalOffset, WholeBridge }

    // 已经完成柜体业务校验的接口量；这里不实施两端等高规则。
    internal sealed class BridgeCabinetGeometry
    {
        internal double Span { get; set; }
        internal double Offset { get; set; }
        internal double HeightDifference { get; set; }
        internal double Elevation { get; set; }
        internal string Specification { get; set; }
        internal string Supports { get; set; }
        internal string Scenario { get; set; }
    }

    internal sealed class BridgeCabinetChangeAnalyzer
    {
        internal BridgeCabinetUpdateKind Compare(BridgeCabinetGeometry before, BridgeCabinetGeometry after)
        {
            if (before == null || after == null) throw new ArgumentNullException("接口快照");
            foreach (var value in new[] { before.Span, after.Span, before.Offset, after.Offset,
                before.HeightDifference, after.HeightDifference, before.Elevation, after.Elevation })
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new InvalidOperationException("桥体接口快照包含无效数值。");
            if (before.Scenario != after.Scenario || before.Specification != after.Specification ||
                Changed(before.Span, after.Span)) return BridgeCabinetUpdateKind.WholeBridge;
            if (Changed(before.Offset, after.Offset) || Changed(before.HeightDifference, after.HeightDifference))
                return BridgeCabinetUpdateKind.LocalOffset;
            if (Changed(before.Elevation, after.Elevation)) return BridgeCabinetUpdateKind.Elevation;
            return before.Supports != after.Supports ? BridgeCabinetUpdateKind.Supports : BridgeCabinetUpdateKind.Unchanged;
        }

        private bool Changed(double a, double b) => Math.Abs(a - b) > 1.0;
    }

    internal sealed class CabinetBridgeSynchronizationResult
    {
        internal bool Succeeded { get; set; }
        internal bool RecoveryRequired { get; set; }
        internal string Message { get; set; }
    }

    internal sealed class CabinetBridgeSynchronizationService
    {
        internal bool CompletePendingFile(bool exists, bool markedPending, bool committed,
            Action verifyCommittedModel, Action removePendingFile)
        {
            if (!exists) return false;
            if (markedPending || !committed)
                throw new InvalidOperationException("母线桥同步尚未完整提交，不能清理待同步记录。");
            if (verifyCommittedModel == null) throw new ArgumentNullException(nameof(verifyCommittedModel));
            if (removePendingFile == null) throw new ArgumentNullException(nameof(removePendingFile));
            verifyCommittedModel();
            removePendingFile();
            return true;
        }

        internal void VerifyBaseline(Action resolveBases, Action verifyRecord)
        {
            if (resolveBases == null) throw new ArgumentNullException(nameof(resolveBases));
            if (verifyRecord == null) throw new ArgumentNullException(nameof(verifyRecord));

            // 业务签名校验会反读基架链，必须先取得已核验的基座位置及实例映射。
            resolveBases();
            verifyRecord();
        }

        internal void Prepare(Action<string> verifySavedDocuments, Action captureBaseline,
            Action<Exception> recordBaselineFailure)
            => Prepare(null, verifySavedDocuments, captureBaseline, recordBaselineFailure);

        internal void Prepare(Func<string, bool> saveDocuments, Action<string> verifySavedDocuments,
            Action captureBaseline, Action<Exception> recordBaselineFailure)
        {
            if (verifySavedDocuments == null) throw new ArgumentNullException(nameof(verifySavedDocuments));
            if (captureBaseline == null) throw new ArgumentNullException(nameof(captureBaseline));
            if (recordBaselineFailure == null) throw new ArgumentNullException(nameof(recordBaselineFailure));

            VerifyPreparationDocuments(saveDocuments, verifySavedDocuments, "柜体修改前/基线读取前");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { captureBaseline(); }
                catch (Exception error)
                {
                    // 记录不兼容仍允许柜体独立更新；保存失败则在柜体写入前退出。
                    recordBaselineFailure(error);
                }
                // 反读后确有保存时重新取得基线，避免采用保存前的上下文引用或方程状态。
                if (!VerifyPreparationDocuments(saveDocuments, verifySavedDocuments, "柜体修改前/基线读取后")) return;
            }
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Warning("柜体基线准备状态不稳定", "连续三次反读后引用文档仍需保存，已中止柜体修改。");
            throw new InvalidOperationException("模型状态尚未稳定，柜体尚未修改。请退出模型编辑后重试。");
        }

        private bool VerifyPreparationDocuments(Func<string, bool> saveDocuments, Action<string> verifySavedDocuments, string stage)
        {
            try
            {
                var saved = saveDocuments?.Invoke(stage) ?? false;
                verifySavedDocuments(stage);
                return saved;
            }
            catch (Exception error)
            {
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("柜体修改前保存状态检查失败", error, stage);
                throw new InvalidOperationException("模型保存未完成，柜体尚未修改。请检查文件状态后重试。", error);
            }
        }

        internal CabinetBridgeSynchronizationResult Execute(Func<BridgeGenerationResult> synchronize,
            Action<string> markPending)
        {
            try
            {
                var result = synchronize();
                if (result.Outcome == BridgeGenerationOutcome.Completed || result.Outcome == BridgeGenerationOutcome.Unchanged)
                    return new CabinetBridgeSynchronizationResult { Succeeded = true, Message = result.Message };
                var recovery = result.Outcome == BridgeGenerationOutcome.RecoveryRequired || result.Outcome == BridgeGenerationOutcome.SavedReadbackFailed;
                var message = recovery ? "柜体修改已完成，母线桥恢复未完成，请先检查恢复记录：" + result.Message :
                    "柜体修改已完成，母线桥同步失败，已恢复同步前的桥体。请打开母线桥设计界面手动驱动。原因：" + result.Message;
                MarkPending(markPending, message);
                return new CabinetBridgeSynchronizationResult { RecoveryRequired = recovery, Message = message };
            }
            catch (Exception error)
            {
                // 同步入口收口：柜体已经提交，不能让桥体异常变成柜体失败。
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("柜体完成后桥体同步未完成", error);
                var message = "柜体修改已完成，母线桥未更新。请打开母线桥设计界面手动驱动。原因：" + error.Message;
                MarkPending(markPending, message);
                return new CabinetBridgeSynchronizationResult { Message = message };
            }
        }

        private void MarkPending(Action<string> write, string message)
        {
            try { write?.Invoke(message); }
            catch (Exception error) { CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("母线桥待手动驱动状态记录失败", error); }
        }
    }
}
