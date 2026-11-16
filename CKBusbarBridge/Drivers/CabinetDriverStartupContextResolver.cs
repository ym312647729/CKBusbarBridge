using CKBusbarBridge.Models;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.IO;

namespace CKBusbarBridge
{
    /// <summary>
    /// 从 SolidWorks 当前活动文档识别柜体驱动窗口的启动场景与项目路径。
    /// 识别失败属于正常回退条件，因此所有预期内失败均以 false 返回，不在此处弹窗。
    /// </summary>
    internal static class CabinetDriverStartupContextResolver
    {
        private const string CabinetBaseDirectoryName = "柜体基座";
        private const string AssemblyRdTypeCustomPropertyName = "RD类型";
        private const string FaceToFaceLargeOffsetMarker = "大偏移";

        /// <summary>
        /// 尝试把 SolidWorks 当前活动总装配体转换为柜体驱动启动上下文。
        /// </summary>
        /// <param name="swApp">当前 SolidWorks 应用程序对象。</param>
        /// <param name="context">识别成功时返回场景、项目路径及当前总装配体。</param>
        /// <returns>当前文档满足自动识别全部条件时返回 true；否则返回 false。</returns>
        internal static bool TryResolve(
            ISldWorks swApp,
            out CabinetDriverStartupContext context)
        {
            context = null;
            var parameterDraft = Prototype.BridgeParameterDraftAccess.Find(swApp, swApp?.IActiveDoc2);
            if (parameterDraft != null)
            {
                var scenario = parameterDraft.Scenario == "face-to-face" ? DesignScenarioId.FaceToFace :
                    parameterDraft.Scenario == "side-to-side" ? DesignScenarioId.SideToSide : DesignScenarioId.FaceToWall;
                FaceToWallCabinetOrientation? orientation = scenario == DesignScenarioId.FaceToWall
                    ? (FaceToWallCabinetOrientation?)Enum.Parse(typeof(FaceToWallCabinetOrientation), parameterDraft.CabinetOrientation) : null;
                context = new CabinetDriverStartupContext(swApp.IActiveDoc2, scenario,
                    (string)parameterDraft.Parameters()["ProjectPath"], orientation);
                return true;
            }
            try
            {
                if (!TryReadActiveAssemblyIdentity(
                        swApp,
                        out var activeModel,
                        out var assemblyPath,
                        out var designScenarioText,
                        out var cabinetOrientationText,
                        out _))
                {
                    return false;
                }

                if (!TryResolveIdentity(
                        assemblyPath,
                        designScenarioText,
                        cabinetOrientationText,
                        out var scenarioId,
                        out var projectPath,
                        out var cabinetOrientation))
                {
                    return false;
                }

                // 保留当前活动模型引用，后续界面初始化直接从该模型读取参数。
                context = new CabinetDriverStartupContext(
                    activeModel,
                    scenarioId,
                    projectPath,
                    cabinetOrientation);
                return true;
            }
            catch
            {
                // COM 状态随用户操作变化，任何读取异常都应退回原场景选择流程。
                context = null;
                return false;
            }
        }

        /// <summary>
        /// 尝试仅从当前活动总装配体的识别属性读取设计场景。
        /// 不校验项目目录布局，供母线桥设计等只需场景身份的入口使用。
        /// </summary>
        internal static bool TryResolveScenario(
            ISldWorks swApp,
            out DesignScenarioId scenarioId)
        {
            return TryResolveScenario(swApp, out scenarioId, out _);
        }

        /// <summary>
        /// 尝试从当前活动总装配体识别场景；面对墙同时返回柜体朝向。
        /// </summary>
        internal static bool TryResolveScenario(
            ISldWorks swApp,
            out DesignScenarioId scenarioId,
            out FaceToWallCabinetOrientation? cabinetOrientation)
        {
            return TryResolveScenario(
                swApp,
                out scenarioId,
                out cabinetOrientation,
                out _);
        }

        /// <summary>
        /// 仅按当前活动装配体的组件树确认场景所需柜体子装配体存在，
        /// 不使用总装配体文件名或标题参与判断。
        /// </summary>
        internal static bool HasRequiredCabinetAssemblies(
            ISldWorks swApp,
            DesignScenarioId scenarioId)
        {
            try
            {
                var activeModel = swApp?.IActiveDoc2 as IModelDoc2;
                var parameterDraft = Prototype.BridgeParameterDraftAccess.Find(swApp, activeModel);
                if (parameterDraft != null) return parameterDraft.Scenario ==
                    (scenarioId == DesignScenarioId.FaceToFace ? "face-to-face" : scenarioId == DesignScenarioId.SideToSide ? "side-to-side" : "face-to-wall");
                var activeAssembly = activeModel as IAssemblyDoc;
                if (activeModel == null ||
                    activeAssembly == null ||
                    activeModel.GetType() !=
                    (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    return false;
                }

                var topLevelComponents =
                    activeAssembly.GetComponents(true) as object[] ??
                    Array.Empty<object>();
                var hasACabinet = false;
                var hasBCabinet = false;
                var hasFaceToWallCabinet = false;
                foreach (var componentObject in topLevelComponents)
                {
                    var component = componentObject as Component2;
                    var componentName = component?.Name2 ?? string.Empty;
                    hasACabinet |= componentName.IndexOf(
                        "A柜-子装配体",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    hasBCabinet |= componentName.IndexOf(
                        "B柜-子装配体",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    hasFaceToWallCabinet |= componentName.IndexOf(
                        "柜子装配体",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                }

                return scenarioId == DesignScenarioId.FaceToWall
                    ? hasFaceToWallCabinet
                    : hasACabinet && hasBCabinet;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 尝试从当前活动总装配体识别场景，并通过总装配体「RD类型」属性
        /// 明确区分面对面大偏移子场景。
        /// </summary>
        internal static bool TryResolveScenario(
            ISldWorks swApp,
            out DesignScenarioId scenarioId,
            out FaceToWallCabinetOrientation? cabinetOrientation,
            out bool isFaceToFaceLargeOffsetAssembly)
        {
            scenarioId = default(DesignScenarioId);
            cabinetOrientation = null;
            isFaceToFaceLargeOffsetAssembly = false;
            var parameterDraft = Prototype.BridgeParameterDraftAccess.Find(swApp, swApp?.IActiveDoc2);
            if (parameterDraft != null)
            {
                scenarioId = parameterDraft.Scenario == "face-to-face" ? DesignScenarioId.FaceToFace :
                    parameterDraft.Scenario == "side-to-side" ? DesignScenarioId.SideToSide : DesignScenarioId.FaceToWall;
                if (scenarioId == DesignScenarioId.FaceToWall) cabinetOrientation =
                    (FaceToWallCabinetOrientation)Enum.Parse(typeof(FaceToWallCabinetOrientation), parameterDraft.CabinetOrientation);
                return true;
            }
            try
            {
                if (!TryReadActiveAssemblyIdentity(
                        swApp,
                        out _,
                        out _,
                        out var designScenarioText,
                        out var cabinetOrientationText,
                        out var assemblyRdTypeText))
                {
                    return false;
                }

                if (!TryIdentifyScenarioFromCustomProperties(
                        designScenarioText,
                        cabinetOrientationText,
                        out scenarioId,
                        out cabinetOrientation))
                {
                    return false;
                }

                isFaceToFaceLargeOffsetAssembly =
                    IsFaceToFaceLargeOffsetAssembly(
                        scenarioId,
                        assemblyRdTypeText);
                return true;
            }
            catch
            {
                scenarioId = default(DesignScenarioId);
                cabinetOrientation = null;
                isFaceToFaceLargeOffsetAssembly = false;
                return false;
            }
        }

        /// <summary>
        /// 读取当前活动装配体及其识别用自定义属性。该方法访问 SolidWorks。
        /// </summary>
        private static bool TryReadActiveAssemblyIdentity(
            ISldWorks swApp,
            out IModelDoc2 activeModel,
            out string assemblyPath,
            out string designScenarioText,
            out string cabinetOrientationText,
            out string assemblyRdTypeText)
        {
            activeModel = swApp?.IActiveDoc2 as IModelDoc2;
            assemblyPath = null;
            designScenarioText = null;
            cabinetOrientationText = null;
            assemblyRdTypeText = null;

            if (activeModel == null
                || activeModel.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                return false;
            }

            assemblyPath = activeModel.GetPathName();
            designScenarioText = CKBusbarBridge.Drivers.BridgeModelReadOperations.GetCustomProperty(
                activeModel,
                DesignScenarioDefaults.ScenarioCustomPropertyKey);
            cabinetOrientationText = CKBusbarBridge.Drivers.BridgeModelReadOperations.GetCustomProperty(
                activeModel,
                FaceToWallCabinetOrientationDefaults.CabinetOrientationCustomPropertyName);
            assemblyRdTypeText = CKBusbarBridge.Drivers.BridgeModelReadOperations.GetCustomProperty(
                activeModel,
                AssemblyRdTypeCustomPropertyName);
            return true;
        }

        /// <summary>
        /// 面对面总装配体的「RD类型」只要包含“大偏移”即识别为大偏移子场景。
        /// 其他设计场景即使属性文本包含相同字样也不得误判。
        /// </summary>
        internal static bool IsFaceToFaceLargeOffsetAssembly(
            DesignScenarioId scenarioId,
            string assemblyRdTypeText)
        {
            return scenarioId == DesignScenarioId.FaceToFace &&
                !string.IsNullOrWhiteSpace(assemblyRdTypeText) &&
                assemblyRdTypeText.IndexOf(
                    FaceToFaceLargeOffsetMarker,
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 根据已取得的装配体路径和属性文本解析启动身份。
        /// 该方法不访问 SolidWorks，供解析逻辑单元测试使用。
        /// </summary>
        /// <param name="assemblyPath">已保存总装配体的完整路径。</param>
        /// <param name="designScenarioText">自定义属性「设计场景」的文本。</param>
        /// <param name="cabinetOrientationText">自定义属性「柜体朝向」的文本。</param>
        /// <param name="scenarioId">解析得到的设计场景。</param>
        /// <param name="projectPath">反推得到的项目根目录。</param>
        /// <param name="cabinetOrientation">面对墙朝向；面对面场景为空。</param>
        /// <returns>场景和项目路径均有效时返回 true。</returns>
        internal static bool TryResolveIdentity(
            string assemblyPath,
            string designScenarioText,
            string cabinetOrientationText,
            out DesignScenarioId scenarioId,
            out string projectPath,
            out FaceToWallCabinetOrientation? cabinetOrientation)
        {
            projectPath = null;
            scenarioId = default(DesignScenarioId);
            cabinetOrientation = null;

            if (!TryIdentifyScenarioFromCustomProperties(
                    designScenarioText,
                    cabinetOrientationText,
                    out scenarioId,
                    out cabinetOrientation)
                || !TryResolveProjectPath(assemblyPath, out projectPath))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 按生成写入路径识别场景：面对面、侧对侧看「设计场景」，面对墙看「柜体朝向」。
        /// 非面对墙场景与朝向属性同时有效时视为冲突，不猜测。
        /// </summary>
        internal static bool TryIdentifyScenarioFromCustomProperties(
            string designScenarioText,
            string cabinetOrientationText,
            out DesignScenarioId scenarioId,
            out FaceToWallCabinetOrientation? cabinetOrientation)
        {
            scenarioId = default(DesignScenarioId);
            cabinetOrientation = null;

            var hasOrientation = FaceToWallCabinetOrientationDefaults.TryParse(
                cabinetOrientationText,
                out var parsedOrientation);
            var isFaceToFace = string.Equals(
                designScenarioText == null ? null : designScenarioText.Trim(),
                DesignScenarioDefaults.GetDisplayName(DesignScenarioId.FaceToFace),
                StringComparison.OrdinalIgnoreCase);
            var isSideToSide = string.Equals(
                designScenarioText == null ? null : designScenarioText.Trim(),
                DesignScenarioDefaults.GetDisplayName(DesignScenarioId.SideToSide),
                StringComparison.OrdinalIgnoreCase);

            if (hasOrientation && (isFaceToFace || isSideToSide))
            {
                return false;
            }

            if (hasOrientation)
            {
                scenarioId = DesignScenarioId.FaceToWall;
                cabinetOrientation = parsedOrientation;
                return true;
            }

            if (isFaceToFace)
            {
                scenarioId = DesignScenarioId.FaceToFace;
                return true;
            }

            if (isSideToSide)
            {
                scenarioId = DesignScenarioId.SideToSide;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 项目根为已保存总装配体的父目录。不看文件名；旧“柜体基座”布局不再兼容。
        /// </summary>
        private static bool TryResolveProjectPath(
            string assemblyPath,
            out string projectPath)
        {
            projectPath = null;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return false;
            }

            try
            {
                var fullAssemblyPath = Path.GetFullPath(assemblyPath);
                if (!File.Exists(fullAssemblyPath)
                    || !string.Equals(
                        Path.GetExtension(fullAssemblyPath),
                        ".SLDASM",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var projectDirectory = Directory.GetParent(fullAssemblyPath);
                if (projectDirectory == null || !projectDirectory.Exists)
                {
                    return false;
                }

                if (string.Equals(
                        projectDirectory.Name,
                        CabinetBaseDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                projectPath = projectDirectory.FullName;
                return true;
            }
            catch
            {
                // 路径非法或文件访问失败时保持静默，由入口回退到现有场景选择框。
                return false;
            }
        }
    }
}
