using System;
using System.Collections.Generic;

namespace CKBusbarBridge.Models
{
    /// <summary>
    /// 各设计场景的显示名称、模板路径及默认参数。
    /// </summary>
    public static class DesignScenarioDefaults
    {
        private const string FaceToFaceTemplateZipRelativePath = "template/面对面.zip";
        private const string FaceToWallTemplateZipRelativePath = "template/面对墙.zip";
        private const string FaceToFaceTemplateCacheFolderName = "面对面";
        private const string FaceToWallTemplateCacheFolderName = "面对墙";
        private const string FaceToFaceTemplateContentRelativePath = "";
        private const string FaceToWallTemplateContentRelativePath = "";
        private const string FaceToFaceMainAssemblyRelativePath = "柜体装配.SLDASM";
        private const string FaceToWallMainAssemblyRelativePath = "面对墙柜体装配.SLDASM";
        private const string FaceToFaceBusbarTemplateFolderName = "铜排模板";
        private const string FaceToWallBusbarTemplateFolderName = "铜排模板";
        private const string SideToSideCabinetTemplateUnavailableMessage =
            "侧对侧场景不能使用面对面/面对墙柜体模板。";

        /// <summary>
        /// 总装配体自定义属性「设计场景」的属性名，值为场景显示名称。
        /// </summary>
        public const string ScenarioCustomPropertyKey = "设计场景";

        /// <summary>
        /// 获取场景在界面上的显示名称。
        /// </summary>
        public static string GetDisplayName(DesignScenarioId scenarioId)
        {
            switch (scenarioId)
            {
                case DesignScenarioId.SideToSide:
                    return "侧对侧场景";
                case DesignScenarioId.FaceToWall:
                    return "面对墙场景";
                case DesignScenarioId.FaceToFace:
                default:
                    return "面对面场景";
            }
        }

        /// <summary>
        /// 获取场景对应的柜体总装配体相对路径（相对于项目目录）。
        /// </summary>
        public static string GetMainAssemblyRelativePath(DesignScenarioId scenarioId)
        {
            switch (scenarioId)
            {
                case DesignScenarioId.SideToSide:
                    return FaceToFaceMainAssemblyRelativePath;
                case DesignScenarioId.FaceToWall:
                    return FaceToWallMainAssemblyRelativePath;
                case DesignScenarioId.FaceToFace:
                default:
                    return FaceToFaceMainAssemblyRelativePath;
            }
        }

        /// <summary>
        /// 获取场景对应的铜排模板根目录名称（相对于项目目录，如「铜排」「铜排模板」）。
        /// </summary>
        public static string GetBusbarTemplateFolderName(DesignScenarioId scenarioId)
        {
            EnsureBusbarGenerationAvailable(scenarioId);
            switch (scenarioId)
            {
                case DesignScenarioId.FaceToWall:
                    return FaceToWallBusbarTemplateFolderName;
                case DesignScenarioId.SideToSide:
                    return FaceToFaceBusbarTemplateFolderName;
                case DesignScenarioId.FaceToFace:
                default:
                    return FaceToFaceBusbarTemplateFolderName;
            }
        }

        /// <summary>
        /// 获取场景对应的柜体模板压缩包相对路径（相对于插件 DLL 目录）。
        /// </summary>
        public static string GetTemplateZipRelativePath(DesignScenarioId scenarioId)
        {
            EnsureCabinetTemplateAvailable(scenarioId);
            switch (scenarioId)
            {
                case DesignScenarioId.FaceToWall:
                    return FaceToWallTemplateZipRelativePath;
                case DesignScenarioId.FaceToFace:
                default:
                    return FaceToFaceTemplateZipRelativePath;
            }
        }

        /// <summary>
        /// 获取场景模板压缩包在 template 目录中的解压缓存文件夹名称。
        /// </summary>
        public static string GetTemplateCacheFolderName(DesignScenarioId scenarioId)
        {
            EnsureCabinetTemplateAvailable(scenarioId);
            switch (scenarioId)
            {
                case DesignScenarioId.FaceToWall:
                    return FaceToWallTemplateCacheFolderName;
                case DesignScenarioId.FaceToFace:
                default:
                    return FaceToFaceTemplateCacheFolderName;
            }
        }

        /// <summary>
        /// 获取场景模板内容相对于解压缓存目录的路径。
        /// 当前两套模板的基架、铜排、绝缘子模板目录均直接位于各自缓存根目录。
        /// </summary>
        public static string GetTemplateContentRelativePath(DesignScenarioId scenarioId)
        {
            EnsureCabinetTemplateAvailable(scenarioId);
            switch (scenarioId)
            {
                case DesignScenarioId.FaceToWall:
                    return FaceToWallTemplateContentRelativePath;
                case DesignScenarioId.FaceToFace:
                default:
                    return FaceToFaceTemplateContentRelativePath;
            }
        }

        /// <summary>
        /// 将指定场景的默认参数应用到面对面场景参数对象。
        /// </summary>
        public static void Apply(CabinetDriverParameters parameters, DesignScenarioId scenarioId)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            switch (scenarioId)
            {
                case DesignScenarioId.SideToSide:
                    throw new NotSupportedException(SideToSideCabinetTemplateUnavailableMessage);
                case DesignScenarioId.FaceToWall:
                    throw new ArgumentException("面对墙场景请使用 FaceToWallCabinetDriverParameters 重载。", nameof(scenarioId));
                case DesignScenarioId.FaceToFace:
                default:
                    ApplyFaceToFaceDefaults(parameters);
                    break;
            }
        }

        /// <summary>
        /// 将指定场景的默认参数应用到面对墙场景参数对象。
        /// </summary>
        public static void Apply(FaceToWallCabinetDriverParameters parameters, DesignScenarioId scenarioId)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            switch (scenarioId)
            {
                case DesignScenarioId.SideToSide:
                    throw new NotSupportedException(SideToSideCabinetTemplateUnavailableMessage);
                case DesignScenarioId.FaceToWall:
                    ApplyFaceToWallDefaults(parameters);
                    break;
                case DesignScenarioId.FaceToFace:
                default:
                    throw new ArgumentException("面对面场景请使用 CabinetDriverParameters 重载。", nameof(scenarioId));
            }
        }

        /// <summary>
        /// 面对面场景沿用现有默认参数，无需额外覆盖。
        /// </summary>
        private static void ApplyFaceToFaceDefaults(CabinetDriverParameters parameters)
        {
        }

        /// <summary>
        /// 面对墙场景的默认参数。后续可在此集中维护与面对面场景的差异项。
        /// </summary>
        private static void ApplyFaceToWallDefaults(FaceToWallCabinetDriverParameters parameters)
        {
            if (parameters?.General == null)
            {
                return;
            }

            parameters.General.BusbarSpec = CabinetGeneralParameters.DefaultBusbarSpec;
            parameters.General.SinglePhaseRootCount = CabinetGeneralParameters.DefaultSinglePhaseRootCount;
        }

        /// <summary>
        /// 尝试从「设计场景」属性值解析场景标识；解析失败时回退为面对面场景。
        /// </summary>
        public static DesignScenarioId ParseScenarioId(string scenarioValue)
        {
            if (string.IsNullOrWhiteSpace(scenarioValue))
            {
                return DesignScenarioId.FaceToFace;
            }

            var normalized = scenarioValue.Trim();
            if (string.Equals(normalized, GetDisplayName(DesignScenarioId.FaceToWall), StringComparison.Ordinal)
                || string.Equals(normalized, nameof(DesignScenarioId.FaceToWall), StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "FaceToWall", StringComparison.OrdinalIgnoreCase))
            {
                return DesignScenarioId.FaceToWall;
            }

            if (string.Equals(normalized, GetDisplayName(DesignScenarioId.FaceToFace), StringComparison.Ordinal)
                || string.Equals(normalized, nameof(DesignScenarioId.FaceToFace), StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "FaceToFace", StringComparison.OrdinalIgnoreCase))
            {
                return DesignScenarioId.FaceToFace;
            }

            if (string.Equals(normalized, GetDisplayName(DesignScenarioId.SideToSide), StringComparison.Ordinal)
                || string.Equals(normalized, nameof(DesignScenarioId.SideToSide), StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "SideToSide", StringComparison.OrdinalIgnoreCase))
            {
                return DesignScenarioId.SideToSide;
            }

            if (Enum.TryParse(normalized, ignoreCase: true, result: out DesignScenarioId parsed))
            {
                return parsed;
            }

            return DesignScenarioId.FaceToFace;
        }

        /// <summary>
        /// 获取所有可选择的场景列表。
        /// </summary>
        public static IReadOnlyList<DesignScenarioId> GetSelectableScenarios()
        {
            return new[]
            {
                DesignScenarioId.FaceToFace,
                DesignScenarioId.FaceToWall,
                DesignScenarioId.SideToSide
            };
        }

        /// <summary>
        /// 基架设计窗口支持全部三个已定义场景；未知枚举值必须显式拒绝。
        /// </summary>
        internal static void EnsureFrameGenerationAvailable(DesignScenarioId scenarioId)
        {
            if (!Enum.IsDefined(typeof(DesignScenarioId), scenarioId))
            {
                throw new ArgumentOutOfRangeException(nameof(scenarioId));
            }
        }

        internal static bool IsBusbarGenerationAvailable(DesignScenarioId scenarioId)
        {
            EnsureFrameGenerationAvailable(scenarioId);
            return true;
        }

        internal static void EnsureBusbarGenerationAvailable(DesignScenarioId scenarioId)
        {
            IsBusbarGenerationAvailable(scenarioId);
        }

        private static void EnsureCabinetTemplateAvailable(DesignScenarioId scenarioId)
        {
            EnsureFrameGenerationAvailable(scenarioId);
            if (scenarioId == DesignScenarioId.SideToSide)
            {
                throw new NotSupportedException(
                    SideToSideCabinetTemplateUnavailableMessage);
            }
        }
    }
}
