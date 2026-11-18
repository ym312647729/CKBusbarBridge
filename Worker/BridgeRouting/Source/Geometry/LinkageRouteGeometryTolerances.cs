namespace RouteLinkBar.Algorithm.Geometry
{
    /// <summary>
    /// 联络排纯算法统一使用的毫米制几何容差。每个常量按语义命名，
    /// 禁止再用同一个无量纲 epsilon 同时比较点距、方向点积和端面范围。
    /// </summary>
    public static class LinkageRouteGeometryTolerances
    {
        public const double ExactPointCoincidence = 1e-8d;
        public const double PointCoincidence = 1e-6d;
        public const double ExactDirection = 1e-8d;
        public const double Direction = 1e-6d;
        public const double AssemblyDirection = 1e-4d;
        public const double DistanceComparison = 1e-6d;
        public const double TerminalFace = 1e-4d;
        public const double PositiveVolume = 1e-6d;
        /// <summary>最终线性尺寸验收允许0.5mm误差（含边界）；不用于求交、归一化或方向判断。</summary>
        public const double FinalAcceptanceMillimeters = 0.5d;
        public const double FinalAcceptanceComparison = FinalAcceptanceMillimeters + DistanceComparison;
    }
}
