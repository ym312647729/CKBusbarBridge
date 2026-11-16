using CKBusbarBridge.AlgorithmWorker;
using CKBusbarBridgePreview;
using RD.WebPreview.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CKBusbarBridge.Services
{
    /// <summary>
    /// 将选中方案中的算法连接排转换为 Web 铜排预览场景。
    /// 面对墙场景只显示算法生成的中间排，不显示柜顶首段或穿墙套管固定排。
    /// 模型业务轴按“长度、宽度、高度”转换为 Web 的 X、Y、Z 坐标。
    /// </summary>
    internal static class BusbarAlgorithmWebPreviewSceneFactory
    {
        public static WebPreviewSceneDto Create(
            LinkageRouteWorkerRequestDto activeWorkerRequest,
            LinkageRouteWorkerSolutionDto selectedWorkerSolution,
            int schemeSequence,
            string solutionIdentity,
            bool isFaceToWallScenario,
            BusbarBridgeAxisSetting modelAxisSetting)
        {
            if (activeWorkerRequest == null)
            {
                throw new ArgumentNullException(nameof(activeWorkerRequest));
            }

            if (selectedWorkerSolution == null)
            {
                throw new ArgumentNullException(nameof(selectedWorkerSolution));
            }

            var normalizedModelAxisSetting = modelAxisSetting ??
                BusbarBridgeAxisSetting.CreateDefault();

            var previewScene = new WebPreviewSceneDto
            {
                SceneId = string.IsNullOrWhiteSpace(solutionIdentity)
                    ? "dq-busbar-scheme-" + schemeSequence
                    : "dq-busbar-" + solutionIdentity,
                Title = "母排方案 " + schemeSequence + " Web 预览",
                ShowCenterLines = false
            };
            var requestGroups = (activeWorkerRequest.Groups ??
                    Array.Empty<LinkageRouteWorkerGroupRequestDto>())
                .Where(requestGroup => requestGroup != null)
                .ToList();
            if (!isFaceToWallScenario)
            {
                for (var requestGroupIndex = 0;
                    requestGroupIndex < requestGroups.Count;
                    requestGroupIndex++)
                {
                    var requestGroup = requestGroups[requestGroupIndex];
                    var groupPreviewColor = ResolveGroupPreviewColor(
                        requestGroup.GroupKey);
                    AddTerminalAssembly(
                        previewScene,
                        requestGroup,
                        requestGroup.StartTerminal,
                        requestGroupIndex,
                        "start",
                        "始端",
                        "StartTerminal",
                        groupPreviewColor,
                        normalizedModelAxisSetting);
                    AddTerminalAssembly(
                        previewScene,
                        requestGroup,
                        requestGroup.EndTerminal,
                        requestGroupIndex,
                        "end",
                        "柜顶",
                        "CabinetTopTerminal",
                        groupPreviewColor,
                        normalizedModelAxisSetting);
                }
            }

            var requestGroupsByKey = requestGroups
                .Where(requestGroup =>
                    !string.IsNullOrWhiteSpace(requestGroup.GroupKey))
                .GroupBy(
                    requestGroup => requestGroup.GroupKey,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    requestGroupCollection => requestGroupCollection.Key,
                    requestGroupCollection => requestGroupCollection.First(),
                    StringComparer.OrdinalIgnoreCase);
            var solutionGroups = (selectedWorkerSolution.GroupSolutions ??
                    Array.Empty<LinkageRouteWorkerGroupSolutionDto>())
                .Where(solutionGroup => solutionGroup != null)
                .ToList();
            for (var solutionGroupIndex = 0;
                solutionGroupIndex < solutionGroups.Count;
                solutionGroupIndex++)
            {
                var solutionGroup = solutionGroups[solutionGroupIndex];
                requestGroupsByKey.TryGetValue(
                    solutionGroup.GroupKey ?? string.Empty,
                    out var matchingRequestGroup);
                AddSolutionGroup(
                    previewScene,
                    solutionGroup,
                    matchingRequestGroup,
                    solutionGroupIndex,
                    normalizedModelAxisSetting);
            }

            if (previewScene.Objects.Count == 0)
            {
                throw new InvalidOperationException(
                    "当前方案没有可用于 Web 预览的算法中间铜排数据。");
            }

            previewScene.Metadata["Source"] = "CKBusbarBridge";
            previewScene.Metadata["IncludesEndpoints"] =
                isFaceToWallScenario ? "false" : "true";
            previewScene.Metadata["WorkerProtocolVersion"] =
                LinkageRouteWorkerProtocol.CurrentVersion.ToString();
            return previewScene;
        }

        private static void AddTerminalAssembly(
            WebPreviewSceneDto previewScene,
            LinkageRouteWorkerGroupRequestDto requestGroup,
            LinkageRouteWorkerTerminalAssemblyDto terminalAssembly,
            int requestGroupIndex,
            string terminalObjectKey,
            string terminalDisplayName,
            string businessCategory,
            WebPreviewColorDto groupPreviewColor,
            BusbarBridgeAxisSetting modelAxisSetting)
        {
            var terminalMembers = (terminalAssembly?.Members ??
                    Array.Empty<LinkageRouteWorkerTerminalMemberDto>())
                .Where(terminalMember =>
                    IsValidPlanarSetting(terminalMember?.FixedBusbar))
                .ToList();
            for (var terminalMemberIndex = 0;
                terminalMemberIndex < terminalMembers.Count;
                terminalMemberIndex++)
            {
                var terminalMember = terminalMembers[terminalMemberIndex];
                previewScene.Objects.Add(new WebPreviewObjectDto
                {
                    ObjectId = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "endpoint-{0}-{1}-{2}",
                        requestGroupIndex,
                        terminalObjectKey,
                        terminalMemberIndex),
                    GroupId = requestGroup.GroupKey,
                    DisplayName = string.Format(
                        "{0} {1}-{2}",
                        terminalDisplayName,
                        ResolveGroupDisplayName(requestGroup),
                        terminalMemberIndex + 1),
                    ObjectKind = WebPreviewObjectKinds.Busbar,
                    BusinessCategory = businessCategory,
                    Color = groupPreviewColor,
                    Busbar = CreateBusbarGeometry(
                        terminalMember.FixedBusbar,
                        requestGroup.DisplayName,
                        modelAxisSetting)
                });
            }
        }

        private static void AddSolutionGroup(
            WebPreviewSceneDto previewScene,
            LinkageRouteWorkerGroupSolutionDto solutionGroup,
            LinkageRouteWorkerGroupRequestDto matchingRequestGroup,
            int solutionGroupIndex,
            BusbarBridgeAxisSetting modelAxisSetting)
        {
            var groupPreviewColor = ResolveGroupPreviewColor(
                solutionGroup.GroupKey);
            var groupDisplayName = ResolveGroupDisplayName(
                matchingRequestGroup,
                solutionGroup.GroupKey);
            var routePartOrdinal = 0;
            var addedRouteBusbar = false;
            var stackedMembers = (solutionGroup.StackMembers ??
                    Array.Empty<LinkageRouteWorkerStackedMemberDto>())
                .Where(stackedMember => stackedMember != null)
                .OrderBy(stackedMember => stackedMember.MemberIndex)
                .ToList();
            foreach (var stackedMember in stackedMembers)
            {
                foreach (var planarPart in stackedMember.PlanarParts ??
                    Array.Empty<LinkageRouteWorkerPlanarSettingDto>())
                {
                    if (!TryAddSolutionPlanarSetting(
                            previewScene,
                            planarPart,
                            solutionGroup.GroupKey,
                            groupDisplayName,
                            solutionGroupIndex,
                            "stack-" + stackedMember.MemberIndex,
                            routePartOrdinal++,
                            WebPreviewObjectKinds.Busbar,
                            "RouteBusbar",
                            groupPreviewColor,
                            modelAxisSetting))
                    {
                        continue;
                    }

                    addedRouteBusbar = true;
                }
            }

            if (!addedRouteBusbar)
            {
                foreach (var planarPart in solutionGroup.PlanarParts ??
                    Array.Empty<LinkageRouteWorkerPlanarSettingDto>())
                {
                    if (TryAddSolutionPlanarSetting(
                        previewScene,
                        planarPart,
                        solutionGroup.GroupKey,
                        groupDisplayName,
                        solutionGroupIndex,
                        "single",
                        routePartOrdinal++,
                        WebPreviewObjectKinds.Busbar,
                        "RouteBusbar",
                        groupPreviewColor,
                        modelAxisSetting))
                    {
                        addedRouteBusbar = true;
                    }
                }
            }

        }

        private static bool TryAddSolutionPlanarSetting(
            WebPreviewSceneDto previewScene,
            LinkageRouteWorkerPlanarSettingDto planarSetting,
            string groupKey,
            string groupDisplayName,
            int solutionGroupIndex,
            string objectSubgroupKey,
            int objectOrdinal,
            string objectKind,
            string businessCategory,
            WebPreviewColorDto previewColor,
            BusbarBridgeAxisSetting modelAxisSetting)
        {
            if (!IsValidPlanarSetting(planarSetting))
            {
                return false;
            }

            previewScene.Objects.Add(new WebPreviewObjectDto
            {
                ObjectId = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "route-{0}-{1}-{2}",
                    solutionGroupIndex,
                    objectSubgroupKey,
                    objectOrdinal),
                GroupId = groupKey,
                DisplayName = groupDisplayName + " " +
                    ResolveObjectDisplayName(businessCategory) + " " +
                    (objectOrdinal + 1),
                ObjectKind = objectKind,
                BusinessCategory = businessCategory,
                Color = previewColor,
                Busbar = CreateBusbarGeometry(
                    planarSetting,
                    groupDisplayName,
                    modelAxisSetting)
            });
            return true;
        }

        private static WebPreviewBusbarDto CreateBusbarGeometry(
            LinkageRouteWorkerPlanarSettingDto planarSetting,
            string phaseDisplayName,
            BusbarBridgeAxisSetting modelAxisSetting)
        {
            var centerlinePoints = TransformModelCoordinatesToWeb(
                planarSetting.PointList,
                modelAxisSetting);
            return new WebPreviewBusbarDto
            {
                IsCenterLine = planarSetting.IsCenter,
                PathPoints = TransformModelCoordinatesToWeb(
                    planarSetting.PointList,
                    modelAxisSetting),
                CenterLinePoints = centerlinePoints,
                ThicknessDirection = TransformModelCoordinateToWeb(
                    planarSetting.StartBusbarVector,
                    modelAxisSetting),
                WidthDirection = TransformModelCoordinateToWeb(
                    planarSetting.WidthVector,
                    modelAxisSetting),
                BusbarWidth = planarSetting.BusBarWidth,
                BusbarThickness = planarSetting.BusBarThickness,
                BendRadius = planarSetting.BarBendingRadius,
                StartLapWidth = planarSetting.StartLapWidth,
                EndLapWidth = planarSetting.EndLapWidth,
                Phase = phaseDisplayName
            };
        }

        private static IList<WebPreviewCoordinateDto> TransformModelCoordinatesToWeb(
            IEnumerable<LinkageRouteWorkerCoordinateDto> sourceCoordinates,
            BusbarBridgeAxisSetting modelAxisSetting)
        {
            return (sourceCoordinates ??
                    Enumerable.Empty<LinkageRouteWorkerCoordinateDto>())
                .Where(sourceCoordinate => sourceCoordinate != null)
                .Select(sourceCoordinate => TransformModelCoordinateToWeb(
                    sourceCoordinate,
                    modelAxisSetting))
                .ToList();
        }

        private static WebPreviewCoordinateDto TransformModelCoordinateToWeb(
            LinkageRouteWorkerCoordinateDto sourceCoordinate,
            BusbarBridgeAxisSetting modelAxisSetting)
        {
            if (sourceCoordinate == null)
            {
                return null;
            }

            var normalizedModelAxisSetting = modelAxisSetting ??
                BusbarBridgeAxisSetting.CreateDefault();
            return new WebPreviewCoordinateDto(
                ProjectCoordinate(
                    sourceCoordinate,
                    normalizedModelAxisSetting.LengthVector),
                ProjectCoordinate(
                    sourceCoordinate,
                    normalizedModelAxisSetting.WidthVector),
                ProjectCoordinate(
                    sourceCoordinate,
                    normalizedModelAxisSetting.HeightVector));
        }

        private static double ProjectCoordinate(
            LinkageRouteWorkerCoordinateDto sourceCoordinate,
            GeometRi.Vector3d axisDirection)
        {
            return sourceCoordinate.X * axisDirection.X +
                sourceCoordinate.Y * axisDirection.Y +
                sourceCoordinate.Z * axisDirection.Z;
        }

        private static bool IsValidPlanarSetting(
            LinkageRouteWorkerPlanarSettingDto planarSetting)
        {
            return planarSetting != null &&
                HasPreviewPoints(planarSetting.PointList) &&
                planarSetting.StartBusbarVector != null &&
                planarSetting.WidthVector != null &&
                IsFinitePositive(planarSetting.BusBarWidth) &&
                IsFinitePositive(planarSetting.BusBarThickness) &&
                IsFiniteNonNegative(planarSetting.BarBendingRadius);
        }

        private static bool HasPreviewPoints(
            IEnumerable<LinkageRouteWorkerCoordinateDto> previewPoints)
        {
            return previewPoints != null &&
                previewPoints.Count(previewPoint => previewPoint != null) >= 2;
        }

        private static bool IsFinitePositive(double numericValue)
        {
            return !double.IsNaN(numericValue) &&
                !double.IsInfinity(numericValue) &&
                numericValue > 0;
        }

        private static bool IsFiniteNonNegative(double numericValue)
        {
            return !double.IsNaN(numericValue) &&
                !double.IsInfinity(numericValue) &&
                numericValue >= 0;
        }

        private static string ResolveGroupDisplayName(
            LinkageRouteWorkerGroupRequestDto requestGroup,
            string fallbackGroupKey = null)
        {
            if (!string.IsNullOrWhiteSpace(requestGroup?.DisplayName))
            {
                return requestGroup.DisplayName.Trim();
            }

            return string.IsNullOrWhiteSpace(requestGroup?.GroupKey)
                ? fallbackGroupKey ?? "未分组"
                : requestGroup.GroupKey.Trim();
        }

        private static string ResolveObjectDisplayName(string businessCategory)
        {
            if (string.Equals(
                businessCategory,
                "SupportBlock",
                StringComparison.Ordinal))
            {
                return "垫块";
            }

            if (string.Equals(
                businessCategory,
                "RouteAccessory",
                StringComparison.Ordinal))
            {
                return "附件";
            }

            return "连接排";
        }

        private static WebPreviewColorDto ResolveGroupPreviewColor(
            string groupKey)
        {
            var normalizedGroupKey = (groupKey ?? string.Empty).Trim();
            if (normalizedGroupKey.EndsWith(
                "-A",
                StringComparison.OrdinalIgnoreCase))
            {
                return new WebPreviewColorDto(255, 255, 0);
            }

            if (normalizedGroupKey.EndsWith(
                "-B",
                StringComparison.OrdinalIgnoreCase))
            {
                return new WebPreviewColorDto(0, 128, 0);
            }

            if (normalizedGroupKey.EndsWith(
                "-C",
                StringComparison.OrdinalIgnoreCase))
            {
                return new WebPreviewColorDto(255, 0, 0);
            }

            return null;
        }
    }
}
