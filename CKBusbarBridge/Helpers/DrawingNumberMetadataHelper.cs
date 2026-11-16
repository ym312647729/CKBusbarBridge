using CKBusbarBridge.viewmodels;
using SolidWorks.Interop.sldworks;
using System;
using System.IO;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 总装配体自定义属性「图号生成信息」的读取、写入与柜体装配体遗留数据迁移。
    /// </summary>
    public static class DrawingNumberMetadataHelper
    {
        public const string PropertyName = "图号生成信息";
        private const string CabinetBaseFolderName = "柜体基座";
        private const string CabinetAssemblyFileName = "柜体装配.SLDASM";

        /// <summary>
        /// 从指定模型读取「图号生成信息」并解析为快照。
        /// </summary>
        public static bool TryRead(SolidWorksDriver driver, IModelDoc2 model, out string json, out DrawingNumberMetadataSnapshot snapshot)
        {
            json = null;
            snapshot = null;
            if (driver == null || model == null)
            {
                return false;
            }

            json = driver.GetCustomProperty(model, PropertyName);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            if (!DrawingNumberMetadataSnapshot.TryParse(json, out snapshot))
            {
                json = null;
                snapshot = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 从总装配体读取；若缺失则尝试从已打开的柜体装配体迁移到总装配体。
        /// </summary>
        public static bool TryLoadWithLegacyFallback(
            SolidWorksDriver driver,
            IModelDoc2 totalAssemblyModel,
            bool migrateLegacyToTotalAssembly,
            out string json,
            out DrawingNumberMetadataSnapshot snapshot,
            out string warningMessage)
        {
            json = null;
            snapshot = null;
            warningMessage = null;

            if (driver == null || totalAssemblyModel == null)
            {
                return false;
            }

            if (TryRead(driver, totalAssemblyModel, out json, out snapshot))
            {
                return true;
            }

            var cabinetPath = GetLegacyCabinetAssemblyPath(totalAssemblyModel);
            if (string.IsNullOrWhiteSpace(cabinetPath))
            {
                return false;
            }

            var cabinetModel = driver.FindOpenDocumentByPath(cabinetPath);
            if (cabinetModel == null)
            {
                return false;
            }

            var legacyJson = driver.GetCustomProperty(cabinetModel, PropertyName);
            if (string.IsNullOrWhiteSpace(legacyJson))
            {
                return false;
            }

            if (!DrawingNumberMetadataSnapshot.TryParse(legacyJson, out snapshot))
            {
                warningMessage = @"柜体装配中的「图号生成信息」格式无效，已忽略。";
                return false;
            }

            json = legacyJson;

            if (!migrateLegacyToTotalAssembly)
            {
                return true;
            }

            try
            {
                driver.SetCustomProperty(totalAssemblyModel, PropertyName, legacyJson);
            }
            catch (Exception ex)
            {
                warningMessage = $"已从柜体装配读取图号信息，但写入总装配体失败：{ex.Message}";
            }

            return true;
        }

        /// <summary>
        /// 根据总装配体路径解析同项目下的柜体装配体路径。
        /// </summary>
        public static string GetLegacyCabinetAssemblyPath(IModelDoc2 totalAssemblyModel)
        {
            if (totalAssemblyModel == null)
            {
                return null;
            }

            var assemblyPath = totalAssemblyModel.GetPathName();
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return null;
            }

            var assemblyDirectoryPath = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrWhiteSpace(assemblyDirectoryPath))
            {
                return null;
            }

            var assemblyDirectoryInfo = new DirectoryInfo(assemblyDirectoryPath);
            string projectRoot;
            if (string.Equals(assemblyDirectoryInfo.Name, CabinetBaseFolderName, StringComparison.OrdinalIgnoreCase))
            {
                projectRoot = assemblyDirectoryInfo.Parent?.FullName;
            }
            else
            {
                projectRoot = assemblyDirectoryInfo.FullName;
            }

            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return null;
            }

            return Path.Combine(projectRoot, CabinetBaseFolderName, CabinetAssemblyFileName);
        }

        /// <summary>
        /// 从总装配体图号元数据创建本次生成流程唯一的图号生成器。
        /// 面对面和面对墙独立基座共用，避免两处各自解析图号格式。
        /// </summary>
        internal static IDrawingNumberGenerator CreateBaseDrawingNumberGenerator(
            DrawingNumberMetadataSnapshot metadata)
        {
            if (metadata == null || string.IsNullOrWhiteSpace(metadata.DrawingNumber))
            {
                throw new InvalidOperationException(
                    "总装配体缺少有效的“图号生成信息”，无法为独立基座分配不重复图号。");
            }

            try
            {
                return new DrawingNumberGenerator(metadata.DrawingNumber);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    $"总装配体“图号生成信息”中的图号格式无效：{metadata.DrawingNumber}",
                    exception);
            }
        }

        /// <summary>
        /// 根据生成器当前待分配图号构造写回总装的快照。
        /// 未消耗图号、流水号已耗尽时返回 false，避免把已用图号或无效值覆盖回去。
        /// </summary>
        internal static bool TryCreateAdvancedSnapshot(
            DrawingNumberMetadataSnapshot current,
            IDrawingNumberGenerator drawingNumbers,
            out DrawingNumberMetadataSnapshot advanced)
        {
            advanced = null;
            if (current == null || drawingNumbers == null)
            {
                return false;
            }

            string nextUnused;
            try
            {
                nextUnused = drawingNumbers.PeekNextDrawingNumber();
            }
            catch (InvalidOperationException)
            {
                // 9999 已提交后生成器耗尽，没有下一未用图号可写回。
                return false;
            }

            if (string.IsNullOrWhiteSpace(nextUnused)
                || string.Equals(
                    nextUnused,
                    current.DrawingNumber,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            advanced = current.WithDrawingNumber(nextUnused);
            return true;
        }

        /// <summary>
        /// 将本次生成后的下一未用图号写回总装配体「图号生成信息」，供基架、铜排继续递增。
        /// </summary>
        internal static bool TryPersistNextUnusedDrawingNumber(
            SolidWorksDriver driver,
            IModelDoc2 model,
            DrawingNumberMetadataSnapshot current,
            IDrawingNumberGenerator drawingNumbers,
            out DrawingNumberMetadataSnapshot persisted)
        {
            persisted = null;
            if (driver == null || model == null)
            {
                return false;
            }

            if (!TryCreateAdvancedSnapshot(current, drawingNumbers, out var advanced))
            {
                return false;
            }

            driver.SetCustomProperty(model, PropertyName, advanced.ToJson());
            persisted = advanced;
            return true;
        }
    }
}
