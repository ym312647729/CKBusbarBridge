using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.models;
using System;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 按“特殊模板类型/接头类型”固定目录定位面对墙单相特殊铜排模板。
    /// </summary>
    public sealed class FaceToWallSpecialBusbarTemplateLocator
    {
        public string ResolveAssemblyPath(FaceToWallSpecialBusbarPhasePlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var directoryPath = Path.Combine(
                TemplateCacheManager.GetTemplateRootPath(DesignScenarioId.FaceToWall),
                DesignScenarioDefaults.GetBusbarTemplateFolderName(DesignScenarioId.FaceToWall),
                GetTemplateKindFolderName(plan.TemplateKind),
                GetHeadFolderName(plan.HeadKind));
            if (!Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException(
                    string.Format("{0}相特殊铜排模板目录不存在：{1}", plan.Phase, directoryPath));
            }

            var assemblyPaths = Directory
                .GetFiles(directoryPath, "*.SLDASM", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (assemblyPaths.Length != 1)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "{0}相特殊铜排模板目录必须且只能包含一个SLDASM：{1}，当前数量：{2}",
                        plan.Phase,
                        directoryPath,
                        assemblyPaths.Length));
            }

            return assemblyPaths[0];
        }

        internal static string GetTemplateKindFolderName(FaceToWallPhaseSpecialTemplateKind kind)
        {
            switch (kind)
            {
                case FaceToWallPhaseSpecialTemplateKind.VerticalBend:
                    return "立弯";
                case FaceToWallPhaseSpecialTemplateKind.MilledBar:
                    return "铣排";
                case FaceToWallPhaseSpecialTemplateKind.OffsetConnection:
                    return "偏移连接排";
                case FaceToWallPhaseSpecialTemplateKind.StraightRaiseOnly:
                    return "直排仅抬高";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知特殊铜排模板类型。");
            }
        }

        internal static string GetHeadFolderName(FaceToWallSpecialBusbarHeadKind kind)
        {
            switch (kind)
            {
                case FaceToWallSpecialBusbarHeadKind.BridgeHead:
                    return "桥头";
                case FaceToWallSpecialBusbarHeadKind.StraightHead:
                    return "直头";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知特殊铜排接头类型。");
            }
        }
    }
}
