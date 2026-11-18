using GeometRi;

namespace RouteLinkBar.Algorithm.Contracts
{
    /// <summary>
    /// 固定端 HoleInfoOnFace 的纯数据快照；点和尺寸为毫米，不包含 SolidWorks 句柄。
    /// 用于实际始端搭接面内孔轮廓验收；不包含建模开孔操作。
    /// </summary>
    public sealed class LinkageTerminalHoleInfo
    {
        public Point3d PlanePoint { get; set; }
        public Vector3d PlaneNormal { get; set; }
        public Vector3d Vector { get; set; }
        public Vector3d HoleVectorInPart { get; set; }
        public Vector3d HoleVector { get; set; }
        public Point3d CenterPointInPart { get; set; }
        public Point3d CenterPointInPart2 { get; set; }
        public Point3d CenterPoint { get; set; }
        public Point3d CenterPoint2 { get; set; }
        public int HoleTypeValue { get; set; }
        /// <summary>沿用铜排大师：圆孔为直径，腰孔为圆弧半径。</summary>
        public double DimD { get; set; }
        public double DimX { get; set; }
        public double DimY { get; set; }
        public bool NotYahua { get; set; }
    }
}
