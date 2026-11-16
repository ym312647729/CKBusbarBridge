using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;

namespace CKBusbarBridgePreview
{
    /// <summary>从当前面对面总装解析实际模型，并预览 B 柜至 A 柜的全部固定铜排。</summary>
    public static class FaceToFaceModelBusbarPreviewService
    {
        public static FaceToFaceModelBusbarPlan CreatePlan(
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            bool isLargeOffset,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            bool isSmallOffset = false,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                largeOffsetBreakpointProtectionRanges = null,
            double phaseChangeInsulationClearance = 125d,
            bool useMilledBarForLargeSmallOffset = false,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            int? requestedPhaseChangeIntervalIndex = null,
            bool allowInsufficientPhaseChangeClearancePreview = false,
            FaceToFaceCabinetEndpointGeometry bCabinetEndpoint = null,
            FaceToFaceCabinetEndpointGeometry aCabinetEndpoint = null)
        {
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            axisSetting = axisSetting ?? BusbarBridgeAxisSetting.CreateDefault();
            FaceToWallPreviewTrace.Info(
                "面对面模型固定铜排数据-开始",
                "长度=" + axisSetting.LengthDirection +
                "，高度=" + axisSetting.HeightDirection +
                "，宽度=" + axisSetting.WidthDirection +
                "，大偏移=" + isLargeOffset +
                "，小偏移=" + isSmallOffset +
                "，小偏移工艺=按偏移量及排宽自动选择" +
                "，中间排规格=" +
                (generatedBusbarWidth?.ToString("0.###") ?? "柜顶排宽") +
                "x" +
                (generatedBusbarThickness?.ToString("0.###") ?? "柜顶排厚") +
                "，单相叠排数=" + singlePhaseStackCount +
                "，断点=" + string.Join(
                    "/",
                    breakpointPositions ?? Array.Empty<double>()));
            try
            {
                var partCatalog = AssemblyPartCatalog.Create(swApp);
                var extensionBusbars = CabinetTopExtensionBusbarReader.Read(
                    partCatalog,
                    swApp,
                    axisSetting);
                return FaceToFaceModelBusbarPlanner.Create(
                    partCatalog,
                    swApp,
                    axisSetting,
                    extensionBusbars,
                    breakpointPositions,
                    singlePhaseStackCount,
                    isLargeOffset,
                    bendingRadius,
                    bendAllowance,
                    isSmallOffset,
                    largeOffsetBreakpointProtectionRanges,
                    phaseChangeInsulationClearance,
                    useMilledBarForLargeSmallOffset,
                    generatedBusbarWidth,
                    generatedBusbarThickness,
                    requestedPhaseChangeIntervalIndex,
                    false,
                    null,
                    allowInsufficientPhaseChangeClearancePreview,
                    bCabinetEndpoint,
                    aCabinetEndpoint);
            }
            catch (Exception createPlanException)
            {
                FaceToWallPreviewTrace.Error(
                    "面对面模型固定铜排数据-失败",
                    createPlanException,
                    "大偏移=" + isLargeOffset +
                    "，小偏移=" + isSmallOffset);
                throw;
            }
        }

        public static List<FreeDrawBody> Preview(
            SldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            int singlePhaseStackCount,
            IReadOnlyList<double> breakpointPositions,
            bool isLargeOffset,
            double bendingRadius = 10d,
            double bendAllowance = 20d,
            bool isSmallOffset = false,
            IReadOnlyList<FaceToFaceLargeOffsetBreakpointProtectionRange>
                largeOffsetBreakpointProtectionRanges = null,
            double phaseChangeInsulationClearance = 125d,
            bool useMilledBarForLargeSmallOffset = false,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            int? requestedPhaseChangeIntervalIndex = null,
            FaceToFaceCabinetEndpointGeometry bCabinetEndpoint = null,
            FaceToFaceCabinetEndpointGeometry aCabinetEndpoint = null)
        {
            if (swApp == null) throw new ArgumentNullException(nameof(swApp));
            var busbarPlan = CreatePlan(
                swApp,
                axisSetting,
                singlePhaseStackCount,
                breakpointPositions,
                isLargeOffset,
                bendingRadius,
                bendAllowance,
                isSmallOffset,
                largeOffsetBreakpointProtectionRanges,
                phaseChangeInsulationClearance,
                useMilledBarForLargeSmallOffset,
                generatedBusbarWidth,
                generatedBusbarThickness,
                requestedPhaseChangeIntervalIndex,
                true,
                bCabinetEndpoint,
                aCabinetEndpoint);
            return FaceToWallModelBusbarPreviewService.CreatePreviewBodies(
                swApp,
                busbarPlan.GeneratedBusbars,
                busbarPlan.CabinetTopExtensionBusbars,
                "面对面固定铜排路径已经生成，但未创建出铜排临时实体。");
        }

        /// <summary>
        /// 仅解析面对面无偏移的自动换相状态及中间基架默认断点，供界面
        /// 在预览前标记换相基架和可调整的换相段边界。
        /// </summary>
        public static FaceToFacePhaseChangeRegionPlan
            AnalyzeNoOffsetPhaseChangeLayout(
            ISldWorks swApp,
            BusbarBridgeAxisSetting axisSetting,
            double insulationClearance = 125d,
            double? generatedBusbarWidth = null,
            double? generatedBusbarThickness = null,
            int singlePhaseStackCount = 1,
            double bendAllowance = 20d,
            bool preferBCabinetFirstSegment = false)
        {
            if (swApp == null)
            {
                throw new ArgumentNullException(nameof(swApp));
            }

            axisSetting = axisSetting ??
                BusbarBridgeAxisSetting.CreateDefault();
            var partCatalog = AssemblyPartCatalog.Create(swApp);
            var extensionBusbars = CabinetTopExtensionBusbarReader.Read(
                partCatalog,
                swApp,
                axisSetting);
            return FaceToFaceModelBusbarPlanner
                .CreateNoOffsetPhaseChangeLayout(
                    partCatalog,
                    axisSetting,
                    extensionBusbars,
                    insulationClearance,
                    generatedBusbarWidth,
                    generatedBusbarThickness,
                    singlePhaseStackCount,
                    bendAllowance,
                    preferBCabinetFirstSegment);
        }
    }
}
