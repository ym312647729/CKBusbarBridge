using System;

namespace CKBusbarBridge.models
{
    public class BusbarSegmentInfo
    {
        /// <summary>
        /// 第几段排（从 1 开始）。
        /// </summary>
        public int SegmentIndex { get; set; }

        /// <summary>
        /// 是否为换相排。
        /// </summary>
        public bool IsPhaseChangeBusbar { get; set; }

        /// <summary>
        /// 是否为立弯排。
        /// </summary>
        public bool IsVerticalBendBusbar { get; set; }

        /// <summary>
        /// 旧 ABC 整体特殊模板标识，仅保留兼容语义；新面对墙末段生成使用
        /// <see cref="BusbarGenerationPreparationResult.FaceToWallSpecialBusbarPhasePlans"/>。
        /// </summary>
        public FaceToWallSpecialBusbarTemplateKind FaceToWallSpecialTemplateKind { get; set; }

        /// <summary>
        /// 排长（mm）。
        /// </summary>
        public double BusbarLength { get; set; }
    }
}
