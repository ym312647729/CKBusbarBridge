using CKBusbarBridge.Models;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 按柜体朝向选择共享“模板.zip”中的面对墙项目模板。
    /// </summary>
    internal static class FaceToWallProjectTemplateCopier
    {
        internal static string PrepareProject(
            string projectPath,
            FaceToWallCabinetOrientation orientation)
        {
            return PrepareProjectFromTemplateRoot(
                TemplateArchiveCacheManager.GetTemplateRootPath(),
                projectPath,
                orientation);
        }

        /// <summary>
        /// 从给定模板根目录准备项目；该入口不依赖 SolidWorks，便于验证真实文件复制行为。
        /// </summary>
        internal static string PrepareProjectFromTemplateRoot(
            string templateRoot,
            string projectPath,
            FaceToWallCabinetOrientation orientation)
        {
            var sourceDirectory = FaceToWallCabinetOrientationDefaults
                .GetTemplateDirectoryPath(templateRoot, orientation);

            return ProjectTemplateCopier.CopyMissingTemplateFiles(
                sourceDirectory,
                projectPath,
                FaceToWallCabinetOrientationDefaults.MainAssemblyFileName,
                FaceToWallCabinetOrientationDefaults.GetDisplayName(orientation) + "模板");
        }
    }
}
