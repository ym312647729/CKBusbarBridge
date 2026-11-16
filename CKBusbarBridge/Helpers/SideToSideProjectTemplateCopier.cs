using System.IO;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 从共享“模板.zip”的侧对侧模板目录补齐项目文件。
    /// </summary>
    internal static class SideToSideProjectTemplateCopier
    {
        private const string TemplateFolderName = "侧对侧模板";
        private const string MainAssemblyFileName = "柜体装配.SLDASM";

        internal static string PrepareProject(string projectPath)
        {
            var sourceDirectory = Path.Combine(
                TemplateArchiveCacheManager.GetTemplateRootPath(),
                TemplateFolderName);
            return CopyMissingTemplateFiles(sourceDirectory, projectPath);
        }

        internal static string CopyMissingTemplateFiles(
            string sourceDirectory,
            string projectPath)
        {
            return ProjectTemplateCopier.CopyMissingTemplateFiles(
                sourceDirectory,
                projectPath,
                MainAssemblyFileName,
                "侧对侧模板");
        }
    }
}
