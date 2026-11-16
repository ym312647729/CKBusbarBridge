using CKBusbarBridge.Models;
using CKBusbarBridge.viewmodels;
using SolidWorks.Interop.sldworks;
using System;
using System.IO;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 使用 SolidWorks 读写项目总装配体“图号生成信息”的生产存储实现。
    /// </summary>
    internal sealed class SolidWorksCabinetDrawingNumberMetadataStore :
        ICabinetDrawingNumberMetadataStore
    {
        private readonly ISldWorks _swApp;

        internal SolidWorksCabinetDrawingNumberMetadataStore(ISldWorks swApp)
        {
            _swApp = swApp;
        }

        public CabinetDrawingNumberMetadataLoadResult Load(
            string projectPath,
            DesignScenarioId scenarioId)
        {
            var modelPath = ResolveModelPath(projectPath, scenarioId);
            if (!File.Exists(modelPath))
            {
                // 新项目只返回“模型不存在”，不得在图号弹窗阶段复制或准备模板。
                return new CabinetDrawingNumberMetadataLoadResult(false, null, null);
            }

            if (_swApp == null)
            {
                return new CabinetDrawingNumberMetadataLoadResult(
                    true,
                    null,
                    "未获取到SolidWorks应用对象，无法读取总装配体图号信息。");
            }

            try
            {
                var driver = new SolidWorksDriver(_swApp);
                var model = driver.OpenOrActivateDocument(modelPath);
                var metadataJson = driver.GetCustomProperty(
                    model,
                    DrawingNumberMetadataHelper.PropertyName);
                return new CabinetDrawingNumberMetadataLoadResult(
                    true,
                    metadataJson,
                    null);
            }
            catch (Exception ex)
            {
                return new CabinetDrawingNumberMetadataLoadResult(
                    true,
                    null,
                    $"读取总装配体图号信息失败：{ex.Message}");
            }
        }

        public bool TryWriteExisting(
            string projectPath,
            DesignScenarioId scenarioId,
            string metadataJson,
            out string errorMessage)
        {
            var modelPath = ResolveModelPath(projectPath, scenarioId);
            if (!File.Exists(modelPath))
            {
                errorMessage = $"未找到项目总装配体：{modelPath}";
                return false;
            }

            if (_swApp == null)
            {
                errorMessage = "未获取到SolidWorks应用对象，无法保存图号生成信息。";
                return false;
            }

            try
            {
                var driver = new SolidWorksDriver(_swApp);
                var model = driver.OpenOrActivateDocument(modelPath);
                return TryWriteModel(model, metadataJson, out errorMessage);
            }
            catch (Exception ex)
            {
                errorMessage = $"写入项目总装配体失败（{modelPath}）：{ex.Message}";
                return false;
            }
        }

        public bool TryWriteModel(
            IModelDoc2 totalAssemblyModel,
            string metadataJson,
            out string errorMessage)
        {
            if (_swApp == null)
            {
                errorMessage = "未获取到SolidWorks应用对象，无法保存图号生成信息。";
                return false;
            }

            if (totalAssemblyModel == null)
            {
                errorMessage = "当前没有可写入的总装配体。";
                return false;
            }

            try
            {
                var driver = new SolidWorksDriver(_swApp);
                driver.SetCustomProperty(
                    totalAssemblyModel,
                    DrawingNumberMetadataHelper.PropertyName,
                    metadataJson);
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"写入总装配体图号信息失败：{ex.Message}";
                return false;
            }
        }

        private static string ResolveModelPath(
            string projectPath,
            DesignScenarioId scenarioId)
        {
            return Path.Combine(
                projectPath ?? string.Empty,
                DesignScenarioDefaults.GetMainAssemblyRelativePath(scenarioId));
        }
    }
}
