using UiFeedback = CKBusbarBridgePreview.UiFeedback;
using CKBusbarBridge.viewmodels;
using System;
using System.Windows;

namespace CKBusbarBridge.views
{
    /// <summary>
    /// 各柜体驱动窗口共用的“图号生成”弹窗启动与提交逻辑。
    /// </summary>
    internal static class DrawingNumberDialogLauncher
    {
        internal static void Show(
            Window owner,
            CabinetDrawingNumberMetadataSession session)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (session == null) throw new ArgumentNullException(nameof(session));

            try
            {
                session.GetDialogState(out var snapshot, out var warningMessage);
                if (!string.IsNullOrWhiteSpace(warningMessage))
                {
                    UiFeedback.Show(
                        warningMessage,
                        "提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                var dialog = new GenerateDrawingNumberWindow
                {
                    Owner = owner,
                    ContractFolderConflictMessageProvider =
                        session.GetContractFolderConflictMessage
                };
                if (snapshot != null)
                {
                    dialog.ApplyFromSnapshot(snapshot);
                }

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                if (!session.TryCommit(dialog.CommittedPayloadJson, out var errorMessage))
                {
                    UiFeedback.Show(
                        errorMessage,
                        "保存失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                CommandWindowForegroundRestorer.Restore(owner);
            }
        }
    }
}
