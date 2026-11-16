using System;
using System.Globalization;
using System.Linq;
using CKBusbarBridge.Drivers;
using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;

namespace CKBusbarBridge.Models
{
    internal sealed class FaceToWallInstallationModelReader
    {
        internal FaceToWallInstallationValues Read(IModelDoc2 root, double wallWidth, double wallHeight)
        {
            var explicitValues = new FaceToWallInstallationMetadata().Read(root.GetCustomInfoValue(string.Empty, FaceToWallInstallationMetadata.PropertyName));
            if (explicitValues != null)
            {
                explicitValues.Validate(wallWidth, wallHeight);
                return explicitValues;
            }
            var assembly = root as AssemblyDoc ?? throw new InvalidOperationException("墙孔安装参数只能从总装配体反读。");
            var hasFrame = false;
            var terminals = (assembly.GetComponents(true) as object[] ?? Array.Empty<object>()).OfType<Component2>()
                .Where(c => !c.IsSuppressed()).Where(c =>
                {
                    var model = c.GetModelDoc2() as IModelDoc2;
                    if (model == null) throw new InvalidOperationException("墙孔安装参数反读遇到未解析组件：" + c.Name2);
                    var value = model.GetCustomInfoValue(c.ReferencedConfiguration, FrameTemplateIdentityMetadata.PropertyName);
                    if (string.IsNullOrWhiteSpace(value)) value = model.GetCustomInfoValue(string.Empty, FrameTemplateIdentityMetadata.PropertyName);
                    var identified = FrameTemplateIdentityMetadata.TryParse(value, out var identity);
                    var type = model.GetCustomInfoValue(c.ReferencedConfiguration, "RD类型");
                    if (string.IsNullOrWhiteSpace(type)) type = model.GetCustomInfoValue(string.Empty, "RD类型");
                    if (identified || type == "基架") hasFrame = true;
                    return identified && identity == FrameTemplateIdentity.FaceToWallLarge1;
                }).ToArray();
            if (terminals.Length == 0)
            {
                if (hasFrame || !string.IsNullOrWhiteSpace(root.GetCustomInfoValue(string.Empty, "母线桥正式生成记录")))
                    throw new InvalidOperationException("已有面对墙生成记录缺少唯一墙端基架驱动来源，不能猜测墙孔安装参数。");
                // 尚未生成桥体的旧柜体项目没有历史间距，沿用首次规划的零间距初值。
                return new FaceToWallInstallationValues(0, 0, 0, 0, 0).AsLegacyRead();
            }
            if (terminals.Length != 1) throw new InvalidOperationException("面对墙末端基架驱动来源重复。");
            var terminal = terminals[0];
            var reader = ReferencedModelValueReader.FromComponent(terminal);
            var horizontal = Math.Round((wallWidth - reader.Global("基架宽度")) / 2d, 3, MidpointRounding.AwayFromZero);
            var vertical = Math.Round((wallHeight - reader.Global("基架高度")) / 2d, 3, MidpointRounding.AwayFromZero);
            var terminalModel = (IModelDoc2)terminal.GetModelDoc2();
            var entryText = terminalModel.GetCustomInfoValue(terminal.ReferencedConfiguration, FaceToWallFrameDrivePlanner.WallEntryDistancePropertyName);
            if (string.IsNullOrWhiteSpace(entryText)) entryText = terminalModel.GetCustomInfoValue(string.Empty, FaceToWallFrameDrivePlanner.WallEntryDistancePropertyName);
            if (!double.TryParse(entryText, NumberStyles.Float, CultureInfo.InvariantCulture, out var entry))
                throw new InvalidOperationException("已有末端基架缺少可反读的伸入墙距离驱动记录。");
            // 老版只有成对的对称间距，按原驱动宽高和原伸入量迁移，不测实体包围盒。
            var legacy = new FaceToWallInstallationValues(vertical, vertical, horizontal, horizontal, entry);
            legacy.Validate(wallWidth, wallHeight);
            return legacy.AsLegacyRead();
        }
    }
}
