using CKBusbarBridge.Models;
using System;
using System.IO;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 解析共享解压缓存中的普通基座或增高基座模板总装配体路径。
    /// </summary>
    internal sealed class FaceToFaceBaseTemplateCopier : IFaceToFaceBaseTemplateCopier
    {
        private const string BaseTemplateFolderName = "基座";
        private const string ElevatedBaseTemplateFolderName = "增高基座";
        private const string BaseAssemblyFileName = "5GX.355.020.SLDASM";
        private const string ElevatedBaseAssemblyFileName = "5GX.355.021.SLDASM";
        private readonly string _templateRoot;

        internal FaceToFaceBaseTemplateCopier(string templateRoot)
        {
            if (string.IsNullOrWhiteSpace(templateRoot))
            {
                throw new ArgumentException("共享模板根目录不能为空。", nameof(templateRoot));
            }

            _templateRoot = Path.GetFullPath(templateRoot);
        }

        public string GetTemplateAssemblyPath(FaceToFaceBaseModelKind kind)
        {
            var templateFolderName = kind == FaceToFaceBaseModelKind.Base
                ? BaseTemplateFolderName
                : ElevatedBaseTemplateFolderName;
            var assemblyFileName = kind == FaceToFaceBaseModelKind.Base
                ? BaseAssemblyFileName
                : ElevatedBaseAssemblyFileName;
            var sourceDirectory = Path.Combine(
                _templateRoot,
                "基座模板",
                templateFolderName);
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"共享模板缓存中未找到{templateFolderName}目录：{sourceDirectory}");
            }

            var sourceAssemblyPath = Path.Combine(sourceDirectory, assemblyFileName);
            if (!File.Exists(sourceAssemblyPath))
            {
                throw new FileNotFoundException(
                    $"共享模板缓存中未找到{templateFolderName}总装配体：{sourceAssemblyPath}",
                    sourceAssemblyPath);
            }

            return sourceAssemblyPath;
        }
    }
}
