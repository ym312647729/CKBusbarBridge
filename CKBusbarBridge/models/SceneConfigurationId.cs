namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 通用场景系统配置：在 <see cref="DesignScenarioId"/> 三大场景
    /// （面对面/面对墙/侧对侧）基础上，组合布局形态与是否换相，
    /// 唯一标识母排设置当前所需的全部场景。
    /// 共 15 个场景：
    /// 面对面 6 个（常规/大偏移/小偏移 × 换相与否）、
    /// 侧对侧 7 个（场景1垂直换相 + 场景2常规与偏移换相共 6 个）、
    /// 面对墙 2 个（常规/抬高）。
    /// </summary>
    public enum SceneConfigurationId
    {
        /// <summary>
        /// 未指定/未识别的场景配置（默认值）。
        /// </summary>
        None = 0,

        /// <summary>
        /// 面对面常规。
        /// </summary>
        FaceToFaceRegular = 1,

        /// <summary>
        /// 面对面大偏移（出现两个转角基架）。
        /// </summary>
        FaceToFaceLargeOffset = 2,

        /// <summary>
        /// 面对面小偏移（有偏移尺寸，但无转角基架）。
        /// </summary>
        FaceToFaceSmallOffset = 3,

        /// <summary>
        /// 面对面常规换相。
        /// </summary>
        FaceToFaceRegularPhaseChange = 4,

        /// <summary>
        /// 面对面大偏移换相。
        /// </summary>
        FaceToFaceLargeOffsetPhaseChange = 5,

        /// <summary>
        /// 面对面小偏移换相。
        /// </summary>
        FaceToFaceSmallOffsetPhaseChange = 6,

        /// <summary>
        /// 侧对侧垂直换相（侧对侧场景1，仅此一种，无偏移）。
        /// </summary>
        SideToSideVerticalPhaseChange = 7,

        /// <summary>
        /// 侧对侧常规（侧对侧场景2）。
        /// </summary>
        SideToSideRegular = 8,

        /// <summary>
        /// 侧对侧常规换相（侧对侧场景2）。
        /// </summary>
        SideToSideRegularPhaseChange = 9,

        /// <summary>
        /// 侧对侧大偏移（侧对侧场景2，同理按两个转角基架判定）。
        /// </summary>
        SideToSideLargeOffset = 10,

        /// <summary>
        /// 侧对侧大偏移换相（侧对侧场景2）。
        /// </summary>
        SideToSideLargeOffsetPhaseChange = 11,

        /// <summary>
        /// 侧对侧小偏移（侧对侧场景2，有偏移尺寸但无转角基架）。
        /// </summary>
        SideToSideSmallOffset = 12,

        /// <summary>
        /// 侧对侧小偏移换相（侧对侧场景2）。
        /// </summary>
        SideToSideSmallOffsetPhaseChange = 13,

        /// <summary>
        /// 面对墙常规（末节基架用于算法；
        /// 倒数第二个基架与柜体第一个基架高度一致）。
        /// </summary>
        FaceToWallRegular = 14,

        /// <summary>
        /// 面对墙抬高（需要 3 个基架；
        /// 倒数第二个基架与柜体第一个基架高度不一致）。
        /// </summary>
        FaceToWallRaised = 15
    }
}