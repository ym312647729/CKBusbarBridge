using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 按已确认的固定相对路径解析三种侧对侧基座模板。
    /// </summary>
    internal sealed class SideToSideBaseTemplateLocator : ISideToSideBaseTemplateLocator
    {
        private static readonly IReadOnlyDictionary<SideToSideBaseModelKind, string[]> RelativePaths =
            new Dictionary<SideToSideBaseModelKind, string[]>
            {
                [SideToSideBaseModelKind.Base1] = new[]
                {
                    "基座模板", "侧对侧基座1", "5GX.355.030.SLDASM"
                },
                [SideToSideBaseModelKind.Base2] = new[]
                {
                    "基座模板", "侧对侧基座2", "5GX.355.031.SLDASM"
                },
                [SideToSideBaseModelKind.ElevatedBase] = new[]
                {
                    "基座模板", "增高基座", "5GX.355.021.SLDASM"
                }
            };

        private readonly string _templateRoot;

        internal SideToSideBaseTemplateLocator(string templateRoot)
        {
            if (string.IsNullOrWhiteSpace(templateRoot))
            {
                throw new ArgumentException("共享模板根目录不能为空。", nameof(templateRoot));
            }

            _templateRoot = Path.GetFullPath(templateRoot);
        }

        public string GetTemplateAssemblyPath(SideToSideBaseModelKind kind)
        {
            if (!RelativePaths.TryGetValue(kind, out var parts))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "不支持的侧对侧基座模板类型。");
            }

            var directory = Path.Combine(_templateRoot, parts[0], parts[1]);
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    "未找到侧对侧" + kind + "模板目录：" + directory);
            }

            var path = Path.GetFullPath(Path.Combine(directory, parts[2]));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "未找到侧对侧" + kind + "模板总装配体：" + path,
                    path);
            }

            return path;
        }
    }

    /// <summary>
    /// 解析侧对侧基座内使用的三垫块绝缘子模板。
    /// </summary>
    internal static class SideToSideInsulatorTemplateLocator
    {
        internal static string GetTemplateAssemblyPath(
            string templateRoot,
            SideToSideBaseModelKind hostModelKind)
        {
            if (string.IsNullOrWhiteSpace(templateRoot))
            {
                throw new ArgumentException("共享模板根目录不能为空。", nameof(templateRoot));
            }

            string path;
            if (hostModelKind == SideToSideBaseModelKind.Base1)
            {
                path = BridgeModelReadOperations.ResolveLBracketInsulatorAssemblyPath(templateRoot);
            }
            else
            {
                path = BridgeModelReadOperations.ResolveConventionalInsulatorAssemblyPath(templateRoot);
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException(
                    "未找到侧对侧" + hostModelKind + "所需的绝缘子模板：" + path,
                    path);
            }

            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// 在项目模板复制前一次性校验当前侧对侧方案需要的所有模板。
    /// </summary>
    internal static class SideToSideBaseGenerationPreflight
    {
        internal static void ValidateTemplates(
            SideToSideCabinetDriverParameters parameters,
            string templateRoot)
        {
            var positions = SideToSideBaseDrivePlanner.CreatePlans(parameters);
            if (positions.Count == 0)
            {
                return;
            }

            var locator = new SideToSideBaseTemplateLocator(templateRoot);
            foreach (var kind in positions
                .SelectMany(position => position.HasElevatedBase
                    ? new[] { position.BaseModel.Kind, position.ElevatedBaseModel.Kind }
                    : new[] { position.BaseModel.Kind })
                .Distinct())
            {
                locator.GetTemplateAssemblyPath(kind);
            }

            foreach (var hostModelKind in positions
                .SelectMany(position => position.Insulators)
                .Select(insulator => insulator.HostModelKind)
                .Distinct())
            {
                SideToSideInsulatorTemplateLocator.GetTemplateAssemblyPath(
                    templateRoot,
                    hostModelKind);
            }
        }
    }
}
