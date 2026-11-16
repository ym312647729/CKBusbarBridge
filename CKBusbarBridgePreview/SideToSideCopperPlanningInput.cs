using System;
using System.Collections.Generic;
using System.Linq;
using GeometRi;

namespace CKBusbarBridgePreview
{
    /// <summary>实际柜端接口的值快照；中间基架和架内绝缘子不属于铜排输入。</summary>
    public sealed class SideToSideConnectionGeometry
    {
        public Point3d Center { get; set; }
        public Point3d SolidCenter { get; set; }
        public Vector3d Normal { get; set; }
        public double Radius { get; set; }
        internal InsulatorConnectionInfo ToConnection() => new InsulatorConnectionInfo
        {
            ConnectionCircleCenter = Center.Copy(), ComponentCenter = SolidCenter.Copy(),
            ConnectionPlane = new Plane3d(Center.Copy(), Normal.Copy()), ConnectionCircleRadius = Radius
        };
    }

    public sealed class SideToSideExtensionGeometry
    {
        // 来自已核验柜体组件子树的精确实例身份，供专用反读按需读取真实搭接面。
        public string Instance { get; set; }
        public BusbarPhase_e Phase { get; set; }
        public PlanarBendBusbarSetting Shape { get; set; }
        public Point3d TerminalStart { get; set; }
        public Point3d TerminalEnd { get; set; }
        internal ModeledBusbar ToBar() => new ModeledBusbar
        {
            Phase = Phase, CenterlineSetting = Shape.Copy(),
            TerminalSegment = new Segment3d(TerminalStart.Copy(), TerminalEnd.Copy()),
            CenterPoint = new Point3d(Shape.PointList.Average(p => p.X), Shape.PointList.Average(p => p.Y), Shape.PointList.Average(p => p.Z))
        };
    }

    public sealed class SideToSideCabinetGeometry
    {
        public string PhaseSequence { get; set; }
        public Point3d Opening { get; set; }
        public double OpeningHeight { get; set; }
        public double BaseWidth { get; set; }
        // 柜体驱动的绝缘子+25mm支架高度，不是中间基架反读值。
        public double InsulatorWithBracketHeight { get; set; }
        // 已核验的增高基座“上”与基座2安装坐标系重合点，作为三相共同折弯起点。
        public Point3d ElevatedOrdinaryJoint { get; set; }
        public double CalculateConnectionHeight()
        {
            if (Opening == null) throw new InvalidOperationException("基座孔口中心缺失。");
            return new FaceToFaceCabinetEndpointGeometry(OpeningHeight, InsulatorWithBracketHeight, 41.5d, 0d)
                .CalculateInsulatorConnectionHeight(Opening.Y + OpeningHeight / 2d);
        }
        public SideToSideExtensionGeometry[] Extensions { get; set; }
        public SideToSideConnectionGeometry[] OrdinaryConnections { get; set; }
        public SideToSideConnectionGeometry[] ElevatedConnections { get; set; }
    }

    /// <summary>规范坐标X为柜间桥向、Y为竖直、Z为横向，所有长度为毫米。</summary>
    public sealed class SideToSideCopperPlanningInput
    {
        public bool FixedPhaseChange { get; set; }
        // 柜体驱动严格反读的有符号偏心；零偏移端点直接取实际接口，模板差异须在模型源头修正。
        public double CabinetEccentricity { get; set; }
        public SideToSideCabinetGeometry A { get; set; }
        public SideToSideCabinetGeometry B { get; set; }
        public double PhaseSpacing { get; set; }
        public int RootCount { get; set; }
        public double Width { get; set; }
        public double Thickness { get; set; }
        public double Clearance { get; set; } = 125d;
    }

    public sealed class SideToSideCopperPiece
    {
        public string Side { get; }
        public string Phase { get; }
        public bool IsBridge { get; }
        public PlanarBendBusbarSetting Shape { get; }
        internal SideToSideCopperPiece(string side, string phase, PlanarBendBusbarSetting shape, bool isBridge = false)
        { Side = side; Phase = phase; Shape = shape.Copy(); IsBridge = isBridge; }
    }
}
