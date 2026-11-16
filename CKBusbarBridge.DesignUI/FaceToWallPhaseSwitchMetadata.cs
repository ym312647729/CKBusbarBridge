using System;

namespace CKBusbarBridge.Models
{
    internal sealed class FaceToWallPhaseSwitchMetadata
    {
        internal const string PropertyName = "面对墙是否换相";

        internal string Encode(bool isPhaseSwitched) => isPhaseSwitched ? "true" : "false";

        internal bool Read(string text)
        {
            // 旧项目没有此参数，沿用不换相；已有记录不能用默认值掩盖损坏。
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (bool.TryParse(text.Trim(), out var value)) return value;
            throw new InvalidOperationException("总装“面对墙是否换相”参数无效，请在柜体驱动中重新设置。");
        }
    }
}
