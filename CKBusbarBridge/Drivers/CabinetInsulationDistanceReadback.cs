using System;
using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.Drivers
{
    internal static class CabinetInsulationDistanceReadback
    {
        internal static double ReadRequired(IModelDoc2 model)
        {
            if (model == null) throw new InvalidOperationException("绝缘距离反读失败：总装模型不可访问。");
            var text = model.GetCustomInfoValue(string.Empty, InsulationDistanceMetadata.PropertyName);
            if (!InsulationDistanceMetadata.TryParse(text, out var value))
                throw new InvalidOperationException("绝缘距离反读失败：总装原属性缺失或不是有效正数。");
            return value;
        }
    }
}
