using CKBusbarBridge.Helpers;
using CKBusbarBridge.Services;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 脱离母排设置窗口生命周期的生成请求。
    /// 构造时深拷贝全部中心线参数，窗口和 Worker 释放后仍可独立执行。
    /// </summary>
    internal sealed class AlgorithmBusbarGenerationRequest
    {
        private readonly IReadOnlyList<AlgorithmBusbarGenerationGroup>
            _generationGroupSnapshots;

        public AlgorithmBusbarGenerationRequest(
            string sourceAssemblyIdentity,
            IReadOnlyList<AlgorithmBusbarGenerationGroup> generationGroups,
            string busbarMaterial)
        {
            if (string.IsNullOrWhiteSpace(sourceAssemblyIdentity))
            {
                throw new ArgumentException(
                    "母排生成请求缺少源装配体标识。",
                    nameof(sourceAssemblyIdentity));
            }

            SourceAssemblyIdentity = sourceAssemblyIdentity.Trim();
            BusbarMaterial = SolidWorksMaterialHelper
                .NormalizeBusbarMaterial(busbarMaterial);
            _generationGroupSnapshots =
                CreateGenerationGroupSnapshot(generationGroups);
        }

        public string SourceAssemblyIdentity { get; }

        public string BusbarMaterial { get; }

        public IReadOnlyList<AlgorithmBusbarGenerationGroup>
            CreateGenerationGroupsForExecution()
        {
            return CreateGenerationGroupSnapshot(_generationGroupSnapshots);
        }

        private static IReadOnlyList<AlgorithmBusbarGenerationGroup>
            CreateGenerationGroupSnapshot(
                IReadOnlyList<AlgorithmBusbarGenerationGroup>
                    sourceGenerationGroups)
        {
            if (sourceGenerationGroups == null ||
                sourceGenerationGroups.Count == 0)
            {
                throw new ArgumentException(
                    "母排生成请求没有可生成的普通段组。",
                    nameof(sourceGenerationGroups));
            }

            var generationGroupSnapshots =
                new List<AlgorithmBusbarGenerationGroup>(
                    sourceGenerationGroups.Count);
            for (var sourceGroupIndex = 0;
                sourceGroupIndex < sourceGenerationGroups.Count;
                sourceGroupIndex++)
            {
                var sourceGenerationGroup =
                    sourceGenerationGroups[sourceGroupIndex];
                if (sourceGenerationGroup == null)
                {
                    throw new ArgumentException(
                        "母排生成请求包含空的普通段组。",
                        nameof(sourceGenerationGroups));
                }

                var sourceGroupBusbars = sourceGenerationGroup.Busbars;
                if (sourceGroupBusbars == null || sourceGroupBusbars.Count == 0)
                {
                    throw new ArgumentException(
                        "母排生成请求中的普通段组没有铜排数据：" +
                        (sourceGenerationGroup.DisplayName ??
                            (sourceGroupIndex + 1).ToString()) + "。",
                        nameof(sourceGenerationGroups));
                }

                var busbarSettingSnapshots =
                    new List<PlanarBendBusbarSetting>(
                        sourceGroupBusbars.Count);
                for (var sourceBusbarIndex = 0;
                    sourceBusbarIndex < sourceGroupBusbars.Count;
                    sourceBusbarIndex++)
                {
                    var sourceBusbarSetting =
                        sourceGroupBusbars[sourceBusbarIndex];
                    if (sourceBusbarSetting == null)
                    {
                        throw new ArgumentException(
                            "母排生成请求中的普通段组包含空铜排：" +
                            (sourceGenerationGroup.DisplayName ??
                                (sourceGroupIndex + 1).ToString()) + "。",
                            nameof(sourceGenerationGroups));
                    }

                    busbarSettingSnapshots.Add(sourceBusbarSetting.Copy());
                }

                generationGroupSnapshots.Add(
                    new AlgorithmBusbarGenerationGroup
                    {
                        DisplayName = sourceGenerationGroup.DisplayName,
                        IsBridge = sourceGenerationGroup.IsBridge,
                        RestrictSourceReuseToCurrentGroup =
                            sourceGenerationGroup
                                .RestrictSourceReuseToCurrentGroup,
                        Busbars = busbarSettingSnapshots.AsReadOnly()
                    });
            }

            return generationGroupSnapshots.AsReadOnly();
        }
    }

    /// <summary>
    /// 独立母排生成程序。它不持有窗口或 ViewModel，只依赖请求快照和
    /// SolidWorks 宿主；必须由宿主主 STA 线程调用。
    /// </summary>
    internal sealed class AlgorithmBusbarGenerationProgram
    {
        private readonly ISldWorks _solidWorksApplication;

        public AlgorithmBusbarGenerationProgram(
            ISldWorks solidWorksApplication)
        {
            _solidWorksApplication = solidWorksApplication ??
                throw new ArgumentNullException(nameof(solidWorksApplication));
        }

        public void Run(AlgorithmBusbarGenerationRequest generationRequest)
        {
            if (generationRequest == null)
            {
                throw new ArgumentNullException(nameof(generationRequest));
            }

            try
            {
                var currentAssemblyIdentity = GetActiveAssemblyIdentity();
                if (!AreAssemblyIdentitiesEqual(
                        generationRequest.SourceAssemblyIdentity,
                        currentAssemblyIdentity))
                {
                    FaceToWallPreviewTrace.Error(
                        "母排正式生成-活动装配体校验失败",
                        new InvalidOperationException(
                            "母排设置窗口关闭后，当前活动装配体已发生切换；为避免生成到错误模型，本次母排生成已取消。"));
                    SolidWorksProgressToast.ShowError(
                        "母排生成",
                        "缺少原母排设置对应的活动装配体。");
                    return;
                }

                var generationService =
                    new AlgorithmBusbarGenerationService(
                        _solidWorksApplication);
                var generationResult = generationService.Generate(
                    generationRequest.CreateGenerationGroupsForExecution(),
                    generationRequest.BusbarMaterial);
                if (generationResult.IsSuccess)
                {
                    SolidWorksProgressToast.ShowInformation(
                        "成功生成",
                        generationResult.BuildSummaryMessage());
                }
                else
                {
                    FaceToWallPreviewTrace.Error(
                        "母排正式生成-结果失败",
                        new InvalidOperationException(
                            generationResult.BuildSummaryMessage()));
                    SolidWorksProgressToast.ShowError(
                        "母排生成",
                        ResolveConciseGenerationErrorMessage(
                            generationResult.BuildSummaryMessage()));
                }
            }
            catch (Exception generationProgramException)
            {
                FaceToWallPreviewTrace.Error(
                    "母排正式生成-程序失败",
                    generationProgramException);
                SolidWorksProgressToast.ShowError(
                    "母排生成",
                    ResolveConciseGenerationErrorMessage(
                        generationProgramException));
            }
        }

        private static string ResolveConciseGenerationErrorMessage(
            Exception generationException)
        {
            if (generationException is System.IO.FileNotFoundException)
            {
                return "缺少铜排生成所需模板文件。";
            }
            if (generationException is System.IO.DirectoryNotFoundException)
            {
                return "缺少铜排生成所需目录。";
            }
            if (generationException is UnauthorizedAccessException)
            {
                return "缺少铜排输出目录写入权限。";
            }

            return ResolveConciseGenerationErrorMessage(
                generationException?.Message);
        }

        private static string ResolveConciseGenerationErrorMessage(
            string detailedGenerationMessage)
        {
            var normalizedDetailedMessage =
                detailedGenerationMessage ?? string.Empty;
            if (normalizedDetailedMessage.IndexOf(
                    "材质",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedDetailedMessage.IndexOf(
                    "sldmat",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "缺少可用的铜排材质库或材质。";
            }
            if (normalizedDetailedMessage.IndexOf(
                    "模板",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "缺少铜排生成所需模板。";
            }
            if (normalizedDetailedMessage.IndexOf(
                    "装配体",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "缺少铜排生成所需装配体。";
            }
            if (normalizedDetailedMessage.IndexOf(
                    "图号",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "缺少有效的图号信息。";
            }
            if (normalizedDetailedMessage.IndexOf(
                    "路径",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedDetailedMessage.IndexOf(
                    "方向",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedDetailedMessage.IndexOf(
                    "点",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "缺少有效的铜排路径数据。";
            }

            return "缺少铜排生成所需的模型或参数数据。";
        }

        private string GetActiveAssemblyIdentity()
        {
            var activeAssemblyModel =
                _solidWorksApplication.IActiveDoc2 as ModelDoc2;
            if (activeAssemblyModel == null ||
                activeAssemblyModel.GetType() !=
                    (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                return null;
            }

            var activeAssemblyPath = activeAssemblyModel.GetPathName();
            if (!string.IsNullOrWhiteSpace(activeAssemblyPath))
            {
                return "PATH:" + activeAssemblyPath.Trim();
            }

            var activeAssemblyTitle = activeAssemblyModel.GetTitle();
            return string.IsNullOrWhiteSpace(activeAssemblyTitle)
                ? null
                : "UNSAVED:" + activeAssemblyTitle.Trim();
        }

        private static bool AreAssemblyIdentitiesEqual(
            string expectedAssemblyIdentity,
            string currentAssemblyIdentity)
        {
            return !string.IsNullOrWhiteSpace(expectedAssemblyIdentity) &&
                !string.IsNullOrWhiteSpace(currentAssemblyIdentity) &&
                string.Equals(
                    expectedAssemblyIdentity,
                    currentAssemblyIdentity,
                    StringComparison.OrdinalIgnoreCase);
        }

    }
}
