namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 侧对侧基架连接的实际基座类型。
    /// </summary>
    internal enum SideToSideFrameBaseKind
    {
        Base1 = 1,
        Base2 = 2
    }

    /// <summary>
    /// 基座2按本体宽度和偏心绝对值划分的结构类型。
    /// </summary>
    internal enum SideToSideFrameOffsetKind
    {
        None = 0,
        Small = 1,
        Large = 2
    }

    /// <summary>
    /// 严格物理反读转换后的侧对侧基架输入快照。
    /// 后续规划、预览和生成只消费该快照，不再重复推导柜体/基座规则。
    /// </summary>
    internal sealed class SideToSideFrameInitialization
    {
        internal SideToSideCabinetTopOpeningMode OpeningMode { get; set; }

        internal bool IsFrontHole { get; set; }

        internal string BCabinetBasePositionName { get; set; }

        internal SideToSideFrameBaseKind BaseKind { get; set; }

        internal SideToSideFrameOffsetKind OffsetKind { get; set; }
        internal bool IsLegacySmallOffsetStructure { get; set; }

        internal double TotalFrameLength { get; set; }

        internal double FrameWidth { get; set; }

        internal double FrameHeight { get; set; }

        internal double ACabinetBaseWidth { get; set; }

        internal double BCabinetBaseWidth { get; set; }

        internal double ACabinetBaseDepth { get; set; }

        internal double BCabinetBaseDepth { get; set; }

        internal double ACabinetInstallationHeight { get; set; }

        internal double BCabinetInstallationHeight { get; set; }

        internal double ACabinetBusbarCenterToBaseOffset { get; set; }

        internal double BCabinetBusbarCenterToBaseOffset { get; set; }

        internal double CabinetEccentricity { get; set; }

        internal double PhaseSpacing { get; set; }

        internal string BusbarSpec { get; set; }

        internal string BusbarMaterial { get; set; }

        internal int SinglePhaseRootCount { get; set; }

        internal double InsulatorBodyHeight { get; set; }

        internal bool ACabinetFrontHoleEnabled { get; set; }

        internal bool ACabinetBackHoleEnabled { get; set; }

        internal bool BCabinetFrontHoleEnabled { get; set; }

        internal bool BCabinetBackHoleEnabled { get; set; }
    }
}
