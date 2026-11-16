using System.IO;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 面对面项目模板入口；实际复制规则与面对墙场景共用同一复制器。
    /// </summary>
    internal static class FaceToFaceProjectTemplateCopier
    {
        private const string FaceToFaceTemplateFolderName = "面对面模板";
        private const string MainAssemblyFileName = "柜体装配.SLDASM";

        internal static string PrepareProject(string projectPath)
        {
            var sourceDirectory = Path.Combine(
                TemplateArchiveCacheManager.GetTemplateRootPath(),
                FaceToFaceTemplateFolderName);
            return CopyMissingTemplateFiles(sourceDirectory, projectPath);
        }

        /// <summary>
        /// 保留原有测试入口和行为，只将算法委托给共享复制器。
        /// </summary>
        internal static string CopyMissingTemplateFiles(
            string sourceDirectory,
            string projectPath)
        {
            return ProjectTemplateCopier.CopyMissingTemplateFiles(
                sourceDirectory,
                projectPath,
                MainAssemblyFileName,
                "面对面模板");
        }
    }
}
