using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CKBusbarBridgePreview
{
    /// <summary>面对墙单相铜排预览模板类型。</summary>
    public enum FaceToWallPreviewTemplateKind
    {
        VerticalBend = 0,
        MilledBar = 1,
        OffsetConnection = 2,
        StraightRaiseOnly = 3
    }

    /// <summary>面对墙单相铜排预览接头类型。</summary>
    public enum FaceToWallPreviewHeadKind
    {
        StraightHead = 0,
        BridgeHead = 1
    }

    /// <summary>
    /// 面对墙末段某一相的预览数据；不依赖主项目的领域类型。
    /// </summary>
    public sealed class FaceToWallPhasePreviewData
    {
        public FaceToWallPhasePreviewData(
            int phaseIndex,
            FaceToWallPreviewTemplateKind templateKind,
            FaceToWallPreviewHeadKind headKind,
            double deltaX,
            double deltaY,
            double deltaZ,
            int offsetDirection,
            double offsetSize,
            IDictionary<string, double> drivenDimensions)
        {
            if (drivenDimensions == null)
            {
                throw new ArgumentNullException(nameof(drivenDimensions));
            }

            PhaseIndex = phaseIndex;
            TemplateKind = templateKind;
            HeadKind = headKind;
            DeltaX = deltaX;
            DeltaY = deltaY;
            DeltaZ = deltaZ;
            OffsetDirection = offsetDirection;
            OffsetSize = offsetSize;
            DrivenDimensions = new ReadOnlyDictionary<string, double>(
                new Dictionary<string, double>(drivenDimensions, StringComparer.OrdinalIgnoreCase));
        }

        public int PhaseIndex { get; private set; }

        public FaceToWallPreviewTemplateKind TemplateKind { get; private set; }

        public FaceToWallPreviewHeadKind HeadKind { get; private set; }

        public double DeltaX { get; private set; }

        public double DeltaY { get; private set; }

        public double DeltaZ { get; private set; }

        /// <summary>0 为右偏，1 为左偏。</summary>
        public int OffsetDirection { get; private set; }

        public double OffsetSize { get; private set; }

        public IReadOnlyDictionary<string, double> DrivenDimensions { get; private set; }
    }
}
