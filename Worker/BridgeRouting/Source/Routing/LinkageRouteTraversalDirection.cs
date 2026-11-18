namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// 指定轴向骨架的构造视角。反向仅改变搜索遍历顺序，候选输出前必须
    /// 恢复为物理始端到物理柜顶的点序。
    /// </summary>
    internal enum LinkageRouteTraversalDirection
    {
        Forward = 0,
        Reverse = 1
    }
}
