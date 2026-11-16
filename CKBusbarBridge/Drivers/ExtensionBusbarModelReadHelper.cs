using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对面与侧对侧共用的柜内伸出排物理状态反读。
    /// </summary>
    internal static class ExtensionBusbarModelReadHelper
    {
        private const string PhaseSequencePropertyName = "伸出排相序";

        internal static void ResolveLightweightComponents(IModelDoc2 assemblyModel)
        {
            BridgeModelReadOperations.EnsureAssemblyFullyResolved(assemblyModel);
        }

        internal static ExtensionBusbarPhysicalParameters ReadRequired(IModelDoc2 rootModel, string nameFragment)
        {
            var component = CabinetAssemblyDimensionReader.FindFirstDescendantComponentContaining(
                rootModel, nameFragment, (int)swDocumentTypes_e.swDocASSEMBLY);
            var values = ReferencedModelValueReader.FromComponent(component);
            var source = CabinetAssemblyDimensionReader.FindFirstDescendantComponentContaining(
                rootModel, nameFragment, (int)swDocumentTypes_e.swDocPART);
            var part = ReferencedModelValueReader.FromComponent(source);
            var specification = ReadPropertyIfPresent(part, "规格型号") ?? ReadPropertyIfPresent(part, "铜排规格")
                ?? ReadPropertyIfPresent(new ReferencedModelValueReader(rootModel), TopBusbarSpecificationMetadata.PropertyName);
            var match = Regex.Match(specification ?? string.Empty,
                @"^(\d+(?:\.\d+)?)\s*[×xX*]\s*(\d+(?:\.\d+)?)(?:[一二三四1-4]排)?$");
            if (!match.Success)
                throw new InvalidOperationException("伸出排反读失败：原模型铜排规格缺失或无效：" + part.Identity);
            var quantity = values.Global("阵列数量");
            if (quantity < 0d || quantity > 3d || Math.Abs(quantity - Math.Round(quantity)) > .001d)
                throw new InvalidOperationException("伸出排反读失败：阵列数量必须为0到3的整数：" + values.Identity);
            return new ExtensionBusbarPhysicalParameters
            {
                PhaseSpacing = values.Global("相间距"), RootCount = (int)Math.Round(quantity) + 1,
                Specification = match.Groups[1].Value + "×" + match.Groups[2].Value,
                Material = ReadBusbarMaterial(part.Model, part.Configuration)
            };
        }

        private static string ReadPropertyIfPresent(ReferencedModelValueReader values, string propertyName)
        {
            return values.TryProperty(propertyName);
        }

        internal static string ReadPhaseSequence(
            IModelDoc2 rootModel,
            string assemblyNameFragment)
        {
            var component = CabinetAssemblyDimensionReader.FindFirstDescendantComponentContaining(
                rootModel,
                assemblyNameFragment,
                (int)swDocumentTypes_e.swDocASSEMBLY);
            var values = ReferencedModelValueReader.FromComponent(component);
            var sequence = values.Property(PhaseSequencePropertyName).ToUpperInvariant();
            if (sequence != "ABC" && sequence != "CBA")
                throw new InvalidOperationException("伸出排相序反读失败：" + values.Identity + "/" + values.Configuration);
            return sequence;
        }

        internal static string ResolveComponentConfigurationName(
            string referencedConfiguration,
            string activeConfiguration)
        {
            return !string.IsNullOrWhiteSpace(referencedConfiguration)
                ? referencedConfiguration
                : activeConfiguration;
        }

        internal static string ReadBusbarMaterial(
            IModelDoc2 sourcePartModel,
            string referencedConfiguration)
        {
            if (!(sourcePartModel is PartDoc partDoc))
            {
                throw new InvalidOperationException("铜排材质反读失败：原零件模型不可访问。");
            }
            if (string.IsNullOrWhiteSpace(referencedConfiguration))
                throw new InvalidOperationException("铜排材质反读失败：组件引用配置缺失。");
            var material = partDoc.GetMaterialPropertyName2(referencedConfiguration, out _);
            if (string.IsNullOrWhiteSpace(material))
                throw new InvalidOperationException("铜排材质反读失败：" + sourcePartModel.GetTitle() + "/" + referencedConfiguration);
            return SolidWorksMaterialHelper.NormalizeBusbarMaterial(material);
        }
    }

    internal sealed class ExtensionBusbarPhysicalParameters
    {
        internal double PhaseSpacing { get; set; }
        internal int RootCount { get; set; }
        internal string Specification { get; set; }
        internal string Material { get; set; }
    }
}
