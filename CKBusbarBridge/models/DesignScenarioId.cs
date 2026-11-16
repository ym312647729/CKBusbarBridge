namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 柜体/母线桥设计场景标识。
    /// </summary>
    public enum DesignScenarioId
    {
        /// <summary>
        /// 面对面场景：A/B 柜相对安装（当前已实现的标准方案）。
        /// </summary>
        FaceToFace = 0,

        /// <summary>
        /// 面对墙场景：柜体背靠墙体安装，参数与模板与面对面场景不同。
        /// </summary>
        FaceToWall = 1,

        /// <summary>
        /// 侧对侧场景：A/B 柜以侧面相对的方式安装。
        /// </summary>
        SideToSide = 2
    }
}
