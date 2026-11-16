using GeometRi;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridgePreview
{
    /// <summary>由当前装配体实际零件识别出的中心线铜排。</summary>
    public sealed class ModeledBusbar
    {
        public Component2 Component { get; set; }
        /// <summary>纯参数来源的稳定身份；不冒充实际Component2。</summary>
        public string SourceIdentity { get; set; }
        /// <summary>从实际零件引用识别出的电气相序。</summary>
        public BusbarPhase_e Phase { get; set; } = BusbarPhase_e.Unknow;
        public PlanarBendBusbarSetting CenterlineSetting { get; set; }
        public Segment3d TerminalSegment { get; set; }
        public Point3d CenterPoint { get; set; }
    }
}
