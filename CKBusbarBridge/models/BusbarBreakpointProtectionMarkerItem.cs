namespace CKBusbarBridge.models
{
    /// <summary>
    /// 大偏移转角禁区的只读预览标记。
    /// 该标记不属于母排断点集合，也不参与段数、长度或生成参数计算。
    /// </summary>
    public sealed class BusbarBreakpointProtectionMarkerItem
    {
        public double PreviewLeft { get; set; }

        public string ToolTipText { get; set; }
    }
}
