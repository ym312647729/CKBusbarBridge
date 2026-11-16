using CKBusbarBridge.Helpers;
using CKBusbarBridge.models;
using System;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 保留面对墙特殊铜排的进端坐标规则；旧L排总成模板装配流程已移除。
    /// </summary>
    public sealed class FaceToWallSpecialBusbarAssembler
    {
        private const string StandardSpecialBusbarInCoordinateName = "进";

        internal static string GetSpecialBusbarInComponentPresetName(
            FaceToWallSpecialBusbarPhasePlan plan)
        {
            if (plan.TemplateKind == FaceToWallPhaseSpecialTemplateKind.MilledBar)
            {
                return MilledBarEntryCoordinateSelection.EntryComponentPresetName;
            }

            // 偏移连接排直头的进端零件名为“1”，其余单相特殊模板的进端零件名为“1-1”。
            return plan.TemplateKind == FaceToWallPhaseSpecialTemplateKind.OffsetConnection
                && plan.HeadKind == FaceToWallSpecialBusbarHeadKind.StraightHead
                    ? "1"
                    : "1-1";
        }

        internal static int GetSpecialBusbarInComponentInstanceNumber(
            FaceToWallSpecialBusbarPhasePlan plan)
        {
            if (plan.TemplateKind == FaceToWallPhaseSpecialTemplateKind.MilledBar)
            {
                return MilledBarEntryCoordinateSelection.EntryComponentInstanceNumber;
            }

            if (plan.TemplateKind == FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly)
            {
                return 6;
            }

            // 偏移连接排桥头的进端为“1-1.*-6”，其余既有模板进端实例号均为 1。
            return plan.TemplateKind == FaceToWallPhaseSpecialTemplateKind.OffsetConnection
                && plan.HeadKind == FaceToWallSpecialBusbarHeadKind.BridgeHead
                    ? 6
                    : 1;
        }

        internal static string GetSpecialBusbarInCoordinateName(
            FaceToWallSpecialBusbarPhasePlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            return plan.TemplateKind == FaceToWallPhaseSpecialTemplateKind.MilledBar
                ? MilledBarEntryCoordinateSelector
                    .Resolve(plan.OffsetDirection)
                    .InCoordinateName
                : StandardSpecialBusbarInCoordinateName;
        }
    }
}
