using GeometRi;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridgePreview
{
    public enum SwCurveType
    {
        Line = 0,
        Arc = 1,
        Other = 2
    }

    /// <summary>从 SolidWorks 边抽取出的虚拟边。</summary>
    public sealed class EdgeInto
    {
        public ISldWorks swApp { get; set; }
        public Component2 Comp { get; set; }
        public Edge Edge { get; set; }
        public Curve Curve { get; set; }
        public SwCurveType CurveType { get; set; } = SwCurveType.Other;
        public Segment3d Segment { get; set; }
        public Segment3d SegmentInPart { get; set; }

        public EdgeInto Copy()
        {
            return new EdgeInto
            {
                swApp = swApp,
                Comp = Comp,
                Edge = Edge,
                Curve = Curve,
                CurveType = CurveType,
                Segment = CopySegment(Segment),
                SegmentInPart = CopySegment(SegmentInPart)
            };
        }

        private static Segment3d CopySegment(Segment3d sourceSegment)
        {
            return sourceSegment == null
                ? null
                : new Segment3d(sourceSegment.P1.Copy(), sourceSegment.P2.Copy());
        }
    }
}
