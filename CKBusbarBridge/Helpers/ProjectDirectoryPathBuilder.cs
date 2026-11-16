using CKBusbarBridge.Models;
using System;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 按“合同号(场景)”规则在用户选择的项目根目录下创建实际项目目录。
    /// </summary>
    internal static class ProjectDirectoryPathBuilder
    {
        internal static string CreateProjectDirectory(
            string projectRootPath,
            string contractNumber,
            DesignScenarioId scenarioId)
        {
            var fullRootPath = ValidateProjectRootPath(projectRootPath);
            var normalizedContractNumber = ValidateContractNumber(contractNumber);
            var existingDirectory = FindExistingContractDirectory(
                fullRootPath,
                normalizedContractNumber);
            if (!string.IsNullOrWhiteSpace(existingDirectory))
            {
                throw new IOException(
                    $"合同号“{normalizedContractNumber}”已使用，项目根目录下已存在文件夹：{existingDirectory}");
            }

            var projectDirectoryName = string.Format(
                "{0}({1})",
                normalizedContractNumber,
                GetScenarioFolderName(scenarioId));
            var projectPath = Path.Combine(fullRootPath, projectDirectoryName);
            Directory.CreateDirectory(projectPath);
            return projectPath;
        }

        /// <summary>
        /// 在项目根目录的直属子目录中查找已占用合同号的目录。
        /// 同时识别旧式“合同号”和当前“合同号(场景)”命名。
        /// </summary>
        internal static string FindExistingContractDirectory(
            string projectRootPath,
            string contractNumber)
        {
            var fullRootPath = ValidateProjectRootPath(projectRootPath);
            var normalizedContractNumber = ValidateContractNumber(contractNumber);
            var scenarioPrefix = normalizedContractNumber + "(";

            return Directory.EnumerateDirectories(fullRootPath)
                .FirstOrDefault(directory =>
                {
                    var directoryName = Path.GetFileName(directory);
                    return string.Equals(
                            directoryName,
                            normalizedContractNumber,
                            StringComparison.OrdinalIgnoreCase)
                        || directoryName.StartsWith(
                            scenarioPrefix,
                            StringComparison.OrdinalIgnoreCase);
                });
        }

        private static string ValidateProjectRootPath(string projectRootPath)
        {
            if (string.IsNullOrWhiteSpace(projectRootPath))
            {
                throw new ArgumentException("项目根目录不能为空。", nameof(projectRootPath));
            }

            var fullRootPath = Path.GetFullPath(projectRootPath);
            if (!Directory.Exists(fullRootPath))
            {
                throw new DirectoryNotFoundException("项目根目录不存在：" + fullRootPath);
            }

            return fullRootPath;
        }

        private static string ValidateContractNumber(string contractNumber)
        {
            var normalizedContractNumber = contractNumber == null
                ? string.Empty
                : contractNumber.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalizedContractNumber))
            {
                throw new ArgumentException("合同号不能为空。", nameof(contractNumber));
            }

            if (normalizedContractNumber.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("合同号包含不能用于文件夹名称的字符。", nameof(contractNumber));
            }

            return normalizedContractNumber;
        }

        internal static string GetScenarioFolderName(DesignScenarioId scenarioId)
        {
            switch (scenarioId)
            {
                case DesignScenarioId.FaceToFace:
                    return "面对面";
                case DesignScenarioId.FaceToWall:
                    return "面对墙";
                case DesignScenarioId.SideToSide:
                    return "侧对侧";
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenarioId), scenarioId, "未知的项目场景。");
            }
        }
    }
}
