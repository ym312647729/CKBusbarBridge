using AutoOpeningHoleDataModel;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CKBusbarBridge.Helpers
{
    public static class SwExtention
    {

        /// <summary>
        /// 根据“预设名称 + 名称后缀 + 实例号”拼接完整组件名称并获取对应组件对象。
        /// 示例：预设名称=直排，名称后缀=.C01234.001，实例号=1 -> 完整名称=直排.C01234.001-1。
        /// </summary>
        /// <param name="presetName">组件预设名称（如：直排）。</param>
        /// <param name="nameSuffix">组件名称后缀（如：.C01234.001）。</param>
        /// <param name="modelInstanceNumber">组件实例号（如：1）。</param>
        /// <returns>匹配到的组件对象；未匹配到返回 null。</returns>
        public static Component2 GetComponentByComposedName(this AssemblyDoc lAsmDoc, string presetName, string nameSuffix, int modelInstanceNumber)
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                throw new ArgumentException("预设名称不能为空。", nameof(presetName));
            }

            if (string.IsNullOrWhiteSpace(nameSuffix))
            {
                throw new ArgumentException("名称后缀不能为空。", nameof(nameSuffix));
            }

            if (modelInstanceNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(modelInstanceNumber), "实例号必须大于 0。");
            }

            var normalizedPresetName = presetName.Trim();
            var normalizedSuffix = nameSuffix.Trim();
            var fullComponentName = $"{normalizedPresetName}{normalizedSuffix}-{modelInstanceNumber}";
            return lAsmDoc.GetComponentByName(fullComponentName) as Component2;
        }

        public static Component2 ResolveSegmentChild(this AssemblyDoc lAsmDoc, Component2 segmentAssembly, string presetName, int instanceNo, string stageName)
        {
            var renameSuffix = GetRenameSuffix(segmentAssembly);
            if (string.IsNullOrWhiteSpace(renameSuffix))
            {
                throw new InvalidOperationException($"{stageName}未能从组件实例名中解析重命名后缀：{segmentAssembly?.Name2}");
            }

            var child = lAsmDoc.GetComponentByComposedName(presetName, renameSuffix, instanceNo);
            if (child == null)
            {
                throw new InvalidOperationException($"{stageName}未找到子组件：{presetName}{renameSuffix}-{instanceNo}");
            }

            return child;
        }

        public static string GetRenameSuffix(Component2 component)
        {
            var fullName = component?.Name2 ?? string.Empty;
            var noInstanceName = Regex.Replace(fullName, @"-\d+$", string.Empty);
            var dotIndex = noInstanceName.IndexOf('.');
            return dotIndex >= 0 ? noInstanceName.Substring(dotIndex) : string.Empty;
        }

        public static void CheckModelNeedSave(this ModelDoc2 swModel)
        {

            if (swModel != null)
            {
                var getSaveFlg = swModel.GetSaveFlag();

                var readOnly = swModel.IsOpenedReadOnly();
                if (getSaveFlg && readOnly == false)
                {
                    swModel.Save();


                }
            }

        }
    }
}
