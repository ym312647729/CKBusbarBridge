using System.Collections.Generic;

namespace CKBusbarBridge.models
{
    /// <summary>
    /// 铜排相序。
    /// </summary>
    public enum BusbarPhaseSequence
    {
        A = 0,
        B = 1,
        C = 2
    }

    /// <summary>
    /// 面对墙单相特殊铜排模板类型。
    /// </summary>
    public enum FaceToWallPhaseSpecialTemplateKind
    {
        VerticalBend = 0,
        MilledBar = 1,
        OffsetConnection = 2,
        StraightRaiseOnly = 3
    }

    /// <summary>
    /// 面对墙单相特殊铜排接头类型。
    /// </summary>
    public enum FaceToWallSpecialBusbarHeadKind
    {
        StraightHead = 0,
        BridgeHead = 1
    }

    /// <summary>
    /// 面对墙末段某一相特殊铜排的生成计划。
    /// </summary>
    public sealed class FaceToWallSpecialBusbarPhasePlan
    {
        public FaceToWallSpecialBusbarPhasePlan(
            int segmentIndex,
            BusbarPhaseSequence phase,
            string drawingNumber,
            double deltaX,
            double deltaY,
            double deltaZ,
            int offsetDirection,
            double offsetSize,
            FaceToWallPhaseSpecialTemplateKind templateKind,
            FaceToWallSpecialBusbarHeadKind headKind,
            string outputDirectoryPath)
        {
            SegmentIndex = segmentIndex;
            Phase = phase;
            DrawingNumber = drawingNumber;
            DeltaX = deltaX;
            DeltaY = deltaY;
            DeltaZ = deltaZ;
            OffsetDirection = offsetDirection;
            OffsetSize = offsetSize;
            TemplateKind = templateKind;
            HeadKind = headKind;
            OutputDirectoryPath = outputDirectoryPath;
            DrivenDimensions = new Dictionary<string, double>();
        }

        public int SegmentIndex { get; private set; }

        public BusbarPhaseSequence Phase { get; private set; }

        public string DrawingNumber { get; private set; }

        public double DeltaX { get; private set; }

        public double DeltaY { get; private set; }

        public double DeltaZ { get; private set; }

        /// <summary>
        /// 模板全局变量“偏移方向”：0 为右偏，1 为左偏。
        /// </summary>
        public int OffsetDirection { get; private set; }

        /// <summary>
        /// 模板全局变量“偏移尺寸”，等于 |DeltaZ|。
        /// </summary>
        public double OffsetSize { get; private set; }

        public FaceToWallPhaseSpecialTemplateKind TemplateKind { get; private set; }

        public FaceToWallSpecialBusbarHeadKind HeadKind { get; private set; }

        public string OutputDirectoryPath { get; private set; }

        /// <summary>
        /// 模板复制后的装配体完整路径，由驱动阶段解析并回填。
        /// </summary>
        public string OutputAssemblyPath { get; internal set; }

        /// <summary>
        /// 模板草图尺寸名到毫米值的映射；尺寸计算规则未配置时为空。
        /// </summary>
        public IReadOnlyDictionary<string, double> DrivenDimensions { get; internal set; }
    }
}
