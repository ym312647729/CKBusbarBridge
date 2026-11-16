namespace CKBusbarBridge.models
{
    /// <summary>
    /// 面对墙末段某一相特殊铜排的纯几何规划结果。
    /// 不包含图号、输出目录或 SolidWorks 文件信息，可供生成和预览共同使用。
    /// </summary>
    public sealed class FaceToWallSpecialBusbarGeometryPlan
    {
        internal FaceToWallSpecialBusbarGeometryPlan(
            int segmentIndex,
            BusbarPhaseSequence phase,
            double deltaX,
            double deltaY,
            double deltaZ,
            int offsetDirection,
            double offsetSize,
            FaceToWallPhaseSpecialTemplateKind templateKind,
            FaceToWallSpecialBusbarHeadKind headKind)
        {
            SegmentIndex = segmentIndex;
            Phase = phase;
            DeltaX = deltaX;
            DeltaY = deltaY;
            DeltaZ = deltaZ;
            OffsetDirection = offsetDirection;
            OffsetSize = offsetSize;
            TemplateKind = templateKind;
            HeadKind = headKind;
        }

        public int SegmentIndex { get; private set; }

        public BusbarPhaseSequence Phase { get; private set; }

        public double DeltaX { get; private set; }

        public double DeltaY { get; private set; }

        public double DeltaZ { get; private set; }

        /// <summary>0 为右偏，1 为左偏。</summary>
        public int OffsetDirection { get; private set; }

        public double OffsetSize { get; private set; }

        public FaceToWallPhaseSpecialTemplateKind TemplateKind { get; private set; }

        public FaceToWallSpecialBusbarHeadKind HeadKind { get; private set; }
    }
}
