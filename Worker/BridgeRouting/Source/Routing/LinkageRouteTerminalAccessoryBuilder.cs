using GeometRi;
using RouteLinkBar.Algorithm.CompositeLapMigration.MathUtility;
using RouteLinkBar.Algorithm.Contracts;
using RouteLinkBar.Algorithm.Geometry;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RouteLinkBar.Algorithm.Routing
{
    /// <summary>
    /// 根据最终叠排层位和真实搭接投影生成端部横向桥排及垫块。
    /// 垫块与普通铜排使用同一几何契约，仅在结果中标记不同附件类型。
    /// </summary>
    internal static partial class LinkageRouteTerminalAccessoryBuilder
    {
        private const double GeometryTolerance =
            LinkageRouteGeometryTolerances.PointCoincidence;
        private const double MinimumCabinetFixedGapSupportThickness = 1d;

        public static bool TryPrepareDoubleStartHorizontalBridgePlacement(
            LinkageRouteStackTerminalPlacement sourceStartPlacement,
            PlanarBendBusbarSetting baseStartSetting,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            bool useSectionSpecificBusbarSettings,
            out LinkageRouteStackTerminalPlacement preparedStartPlacement,
            out string failureReason)
        {
            preparedStartPlacement = CopyPlacement(sourceStartPlacement);
            failureReason = null;
            if (sourceStartPlacement == null)
            {
                failureReason = "双始端横向桥排缺少始端叠排层位。";
                return false;
            }

            if (routeRequest?.EnableSingleStartConnectionBar == true &&
                routeGroupRequest?.StartTerminal?.Members?.Count == 1)
            {
                return TryPrepareSingleStartHorizontalConnectionPlacement(
                    baseStartSetting, routeRequest, routeGroupRequest,
                    useSectionSpecificBusbarSettings, preparedStartPlacement,
                    out failureReason);
            }

            // 兼容分流在本次转换中只建立历史层位提案。完整候选进入方案前，
            // SearchContext 必须关闭该标记并按当前连接排规则重新实体化和终检；
            // 因此这里不得产生可直接发布的最终实体。
            if (routeRequest?.UseLegacyDoubleStartHorizontalTopologyProposal ==
                true)
            {
                return true;
            }

            if (routeRequest?.AllowDoubleStartMiddleLap != true ||
                routeGroupRequest?.StartTerminal?.Members?.Count < 2 ||
                !TryGetBoundaryDirection(
                    baseStartSetting,
                    true,
                    out var generatedStartLengthDirection) ||
                !LinkageRouteDoubleStartLapGeometry.TryCreateCollectiveFixedBusbar(
                    routeGroupRequest.StartTerminal,
                    out var collectiveStartFixedBusbar) ||
                !TryGetBoundaryDirection(
                    collectiveStartFixedBusbar,
                    true,
                    out var collectiveStartLengthDirection) ||
                Math.Abs(generatedStartLengthDirection.Dot(
                    collectiveStartLengthDirection)) < 1d - GeometryTolerance)
            {
                return true;
            }

            // 只有同一根实际生成排同时与双始端的两个固定排产生有效接触时，
            // 才能把它识别为连接排；仅凭接触数量会把普通叠排误当成连接排，
            // 进而错误地向双始端两侧延长。
            var expectedDoubleStartMemberIndices = new HashSet<int>(
                Enumerable.Range(0, routeGroupRequest.StartTerminal.Members.Count));
            var bridgeContactGroup = sourceStartPlacement.Contacts
                .Where(terminalContact => terminalContact != null &&
                    expectedDoubleStartMemberIndices.Contains(
                        terminalContact.FixedMemberIndex))
                .GroupBy(terminalContact => terminalContact.GeneratedMemberIndex)
                .Where(terminalContactGroup =>
                    terminalContactGroup
                        .Select(terminalContact => terminalContact.FixedMemberIndex)
                        .Distinct()
                        .OrderBy(fixedMemberIndex => fixedMemberIndex)
                        .SequenceEqual(expectedDoubleStartMemberIndices.OrderBy(
                            fixedMemberIndex => fixedMemberIndex)))
                .FirstOrDefault();
            // 横向搭接允许落在双始端整体范围之外，后续由连接排沿自身
            // 排长方向延伸覆盖外侧生成排。这里的旧投影检查只适用于
            // “当前首段已经落在固定端窗口内”的历史路径，会把可由桥排
            // 延伸解决的候选提前淘汰；连接排实体化和最终满搭接门禁再做
            // 最终确认。

            var bridgeGeneratedMemberIndex = bridgeContactGroup?.Key ??
                sourceStartPlacement.GeneratedMemberOffsets
                    .Select((offset, index) => new { offset, index })
                    .OrderBy(candidate => Math.Abs(candidate.offset -
                        sourceStartPlacement.BaseCenterOffset))
                    .Select(candidate => candidate.index)
                    .FirstOrDefault();
            if (bridgeGeneratedMemberIndex < 0 ||
                bridgeGeneratedMemberIndex >=
                    preparedStartPlacement.GeneratedMemberOffsets.Count)
            {
                failureReason = "双始端横向桥排对应的叠排成员索引超出范围。";
                return false;
            }

            var terminalThicknessDirection = routeGroupRequest.StartTerminal
                .ThicknessDirection.Normalized;
            var fixedReferencePoint = routeGroupRequest.StartTerminal.Members[0]
                .FixedBusbar.PointList[0];
            var collectiveFixedCenterCoordinate = new Vector3d(
                    fixedReferencePoint,
                    collectiveStartFixedBusbar.PointList[0])
                .Dot(terminalThicknessDirection);
            var currentGeneratedCenterCoordinate =
                sourceStartPlacement.BaseCenterOffset +
                sourceStartPlacement.GeneratedMemberOffsets[
                    bridgeGeneratedMemberIndex];
            var bridgeSurfaceSideSign = currentGeneratedCenterCoordinate +
                    GeometryTolerance >= collectiveFixedCenterCoordinate
                ? 1
                : -1;
            // 此处需要使用真正接触连接排的路径分段厚度。单根路径同时连接
            // 始端和柜顶时会按柜顶规格生成；若仍固定取“始端搭接排厚度”，
            // 会少偏移半个厚差，导致路径排与连接排发生正体积重叠。
            var generatedSingleBusbarThickness =
                useSectionSpecificBusbarSettings
                    ? baseStartSetting.BusBarThickness
                    : routeGroupRequest.GetBusbarThickness(routeRequest);
            var bridgeSingleBusbarThickness =
                useSectionSpecificBusbarSettings
                    ? routeGroupRequest.GetDoubleStartBridgeBusbarThickness(
                        routeRequest)
                    : generatedSingleBusbarThickness;
            var requiredGeneratedCenterCoordinate =
                collectiveFixedCenterCoordinate +
                bridgeSurfaceSideSign *
                 (collectiveStartFixedBusbar.BusBarThickness / 2d +
                  bridgeSingleBusbarThickness +
                  generatedSingleBusbarThickness / 2d);
            var bridgePlacementDisplacement =
                requiredGeneratedCenterCoordinate -
                currentGeneratedCenterCoordinate;
            var useSpacerlessStartTerminalLap = routeRequest
                .ShouldUseSpacerlessTerminalLap(true);
            var bridgeDisplacedGeneratedMemberIndices =
                useSpacerlessStartTerminalLap
                ? FindZeroGapGeneratedComponent(
                    sourceStartPlacement,
                    bridgeGeneratedMemberIndex,
                    generatedSingleBusbarThickness)
                : new[] { bridgeGeneratedMemberIndex };
            foreach (var displacedGeneratedMemberIndex in
                bridgeDisplacedGeneratedMemberIndices)
            {
                preparedStartPlacement.GeneratedMemberOffsets[
                    displacedGeneratedMemberIndex] += bridgePlacementDisplacement;
                if (useSpacerlessStartTerminalLap &&
                    Math.Abs(bridgePlacementDisplacement) > GeometryTolerance &&
                    !preparedStartPlacement
                        .SpacerlessMovedGeneratedMemberIndices.Contains(
                            displacedGeneratedMemberIndex))
                {
                    preparedStartPlacement
                        .SpacerlessMovedGeneratedMemberIndices.Add(
                            displacedGeneratedMemberIndex);
                }
            }
            preparedStartPlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex =
                bridgeGeneratedMemberIndex;
            preparedStartPlacement.DoubleStartHorizontalBridgeSideSign =
                bridgeSurfaceSideSign;
            preparedStartPlacement.DoubleStartHorizontalBridgeThickness =
                bridgeSingleBusbarThickness;
            preparedStartPlacement.DoubleStartHorizontalBridgePlacements.Clear();
            preparedStartPlacement.DoubleStartHorizontalBridgePlacements.Add(
                new LinkageRouteTerminalBridgePlacement
                {
                    GeneratedMemberIndex = bridgeGeneratedMemberIndex,
                    SideSign = bridgeSurfaceSideSign,
                    Thickness = bridgeSingleBusbarThickness
                });
            // 水平搭接输出需要覆盖始端排厚向的另一侧；路径搜索仍只保留
            // 原有单连接排提案，第二根连接排仅在实际铜排实体化阶段生成。
            var oppositeBridgeMemberIndex = !useSectionSpecificBusbarSettings
                ? ResolveAdjacentOppositeBridgeMemberIndex(
                    sourceStartPlacement.GeneratedMemberOffsets,
                    bridgeGeneratedMemberIndex,
                    bridgeSurfaceSideSign)
                : null;
            if (oppositeBridgeMemberIndex.HasValue)
            {
                preparedStartPlacement.DoubleStartHorizontalBridgePlacements.Add(
                    new LinkageRouteTerminalBridgePlacement
                    {
                        GeneratedMemberIndex = oppositeBridgeMemberIndex.Value,
                        SideSign = -bridgeSurfaceSideSign,
                        Thickness = bridgeSingleBusbarThickness
                    });
            }
            preparedStartPlacement.Signature += string.Format(
                CultureInfo.InvariantCulture,
                "|DOUBLE-START-H-BRIDGE:{0}:{1}:{2:F6}",
                bridgeGeneratedMemberIndex,
                bridgeSurfaceSideSign,
                bridgeSingleBusbarThickness);
            if (useSpacerlessStartTerminalLap)
            {
                preparedStartPlacement.Signature +=
                    "|SPACERLESS-BRIDGE-COMPONENT:" +
                    string.Join(",", bridgeDisplacedGeneratedMemberIndices);
            }
            return true;
        }

        private static bool TryPrepareSingleStartHorizontalConnectionPlacement(
            PlanarBendBusbarSetting generatedStartSetting,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            bool useSectionSpecificBusbarSettings,
            LinkageRouteStackTerminalPlacement preparedPlacement,
            out string failureReason)
        {
            failureReason = null;
            var fixedStartSetting = routeGroupRequest.StartTerminal.Members[0]?.FixedBusbar;
            if (!TryGetBoundaryDirection(generatedStartSetting, true, out var generatedDirection) ||
                !TryGetBoundaryDirection(fixedStartSetting, true, out var fixedDirection))
            {
                failureReason = "单始端连接排无法解析实际排长方向。";
                return false;
            }
            if (Math.Abs(generatedDirection.Dot(fixedDirection)) < 1d - GeometryTolerance)
                return true;
            var connectionThickness = useSectionSpecificBusbarSettings
                ? routeGroupRequest.GetDoubleStartBridgeBusbarThickness(routeRequest)
                : generatedStartSetting.BusBarThickness;
            var generatedThickness = generatedStartSetting.BusBarThickness;
            if (connectionThickness <= GeometryTolerance || generatedThickness <= GeometryTolerance ||
                preparedPlacement.GeneratedMemberOffsets.Count == 0)
            {
                failureReason = "单始端连接排缺少有效排厚或叠排层位。";
                return false;
            }
            // 固定排不动：两侧连接排贴固定排，生成排夹住连接排；多余成员向外顺排。
            var orderedLayers = preparedPlacement.GeneratedMemberOffsets
                .Select((offset, index) => new { Index = index, Coordinate =
                    preparedPlacement.BaseCenterOffset + offset })
                .OrderBy(layer => layer.Coordinate).ToList();
            var requiredNearLayerDistance = fixedStartSetting.BusBarThickness / 2d +
                connectionThickness + generatedThickness / 2d;
            preparedPlacement.DoubleStartHorizontalBridgePlacements.Clear();
            if (orderedLayers.Count == 1)
            {
                var singleLayer = orderedLayers[0];
                var actualSideSign = singleLayer.Coordinate < -GeometryTolerance ? -1 : 1;
                preparedPlacement.GeneratedMemberOffsets[singleLayer.Index] =
                    actualSideSign * requiredNearLayerDistance - preparedPlacement.BaseCenterOffset;
                preparedPlacement.DoubleStartHorizontalBridgePlacements.Add(
                    new LinkageRouteTerminalBridgePlacement { GeneratedMemberIndex = singleLayer.Index,
                        SideSign = actualSideSign, Thickness = connectionThickness });
            }
            else
            {
                var negativeLayerCount = Math.Max(1, Math.Min(orderedLayers.Count - 1,
                    orderedLayers.Count(layer => layer.Coordinate < -GeometryTolerance)));
                for (var layerPosition = 0; layerPosition < orderedLayers.Count; layerPosition++)
                {
                    var layer = orderedLayers[layerPosition];
                    var sideSign = layerPosition < negativeLayerCount ? -1 : 1;
                    var outwardLayerIndex = sideSign < 0
                        ? negativeLayerCount - 1 - layerPosition : layerPosition - negativeLayerCount;
                    preparedPlacement.GeneratedMemberOffsets[layer.Index] = sideSign *
                        (requiredNearLayerDistance + outwardLayerIndex *
                            (generatedThickness + (routeRequest.ShouldUseSpacerlessTerminalLap(true)
                                ? 0d : routeGroupRequest.StackStartSpace))) -
                        preparedPlacement.BaseCenterOffset;
                    if (outwardLayerIndex == 0)
                        preparedPlacement.DoubleStartHorizontalBridgePlacements.Add(
                            new LinkageRouteTerminalBridgePlacement { GeneratedMemberIndex = layer.Index,
                                SideSign = sideSign, Thickness = connectionThickness });
                }
            }
            // 骨架基准必须是插入连接排后的生成叠排中心，不能保留插入前的
            // 均值，否则单层/奇数层会产生短厚向折返，直达候选无法进入搜索。
            var meanOffset = preparedPlacement.GeneratedMemberOffsets.Average();
            preparedPlacement.BaseCenterOffset += meanOffset;
            for (var memberIndex = 0; memberIndex < preparedPlacement.GeneratedMemberOffsets.Count; memberIndex++)
                preparedPlacement.GeneratedMemberOffsets[memberIndex] -= meanOffset;
            var primaryConnectionPlacement = preparedPlacement.DoubleStartHorizontalBridgePlacements[0];
            preparedPlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex = primaryConnectionPlacement.GeneratedMemberIndex;
            preparedPlacement.DoubleStartHorizontalBridgeSideSign = primaryConnectionPlacement.SideSign;
            preparedPlacement.DoubleStartHorizontalBridgeThickness = connectionThickness;
            preparedPlacement.Signature += "|SINGLE-START-H-CONNECTION";
            return true;
        }

        internal static IReadOnlyList<LinkageRouteStackTerminalPlacement>
            PrepareSingleStartHorizontalConnectionPlacements(
                IReadOnlyList<LinkageRouteStackTerminalPlacement> sourcePlacements,
                LinkageMultiGroupRouteRequest request,
                LinkageRouteGroupRequest group,
                double generatedThickness)
        {
            if (request?.EnableSingleStartConnectionBar != true ||
                group?.StartTerminal?.Members?.Count != 1) return sourcePlacements;
            var referenceSetting = group.StartTerminal.Members[0].FixedBusbar.Copy();
            referenceSetting.BusBarThickness = generatedThickness;
            var preparedPlacements = new List<LinkageRouteStackTerminalPlacement>();
            foreach (var placement in sourcePlacements)
                if (TryPrepareDoubleStartHorizontalBridgePlacement(placement, referenceSetting,
                    request, group, false, out var prepared, out _)) preparedPlacements.Add(prepared);
            return preparedPlacements;
        }

        internal static bool TryGetConnectionFixedBusbar(
            LinkageTerminalAssembly startTerminal,
            out PlanarBendBusbarSetting connectionFixedBusbar)
        {
            if (startTerminal?.Members?.Count == 1)
            {
                connectionFixedBusbar = startTerminal.Members[0]?.FixedBusbar?.Copy();
                return IsUsableConnectionSetting(connectionFixedBusbar);
            }
            return LinkageRouteDoubleStartLapGeometry.TryCreateCollectiveFixedBusbar(
                startTerminal, out connectionFixedBusbar);
        }

        private static IReadOnlyList<int> FindZeroGapGeneratedComponent(
            LinkageRouteStackTerminalPlacement terminalPlacement,
            int anchorGeneratedMemberIndex,
            double generatedSingleBusbarThickness)
        {
            var orderedGeneratedLayers = terminalPlacement
                .GeneratedMemberOffsets
                .Select((generatedMemberOffset, generatedMemberIndex) => new
                {
                    GeneratedMemberIndex = generatedMemberIndex,
                    CenterCoordinate = terminalPlacement.BaseCenterOffset +
                        generatedMemberOffset
                })
                .OrderBy(generatedLayer => generatedLayer.CenterCoordinate)
                .ToList();
            var anchorLayerPosition = orderedGeneratedLayers.FindIndex(
                generatedLayer => generatedLayer.GeneratedMemberIndex ==
                    anchorGeneratedMemberIndex);
            if (anchorLayerPosition < 0)
            {
                return new[] { anchorGeneratedMemberIndex };
            }

            var componentMinimumPosition = anchorLayerPosition;
            while (componentMinimumPosition > 0 &&
                Math.Abs(
                    orderedGeneratedLayers[componentMinimumPosition]
                        .CenterCoordinate -
                    orderedGeneratedLayers[componentMinimumPosition - 1]
                        .CenterCoordinate -
                    generatedSingleBusbarThickness) <= GeometryTolerance)
            {
                componentMinimumPosition--;
            }

            var componentMaximumPosition = anchorLayerPosition;
            while (componentMaximumPosition + 1 <
                    orderedGeneratedLayers.Count &&
                Math.Abs(
                    orderedGeneratedLayers[componentMaximumPosition + 1]
                        .CenterCoordinate -
                    orderedGeneratedLayers[componentMaximumPosition]
                        .CenterCoordinate -
                    generatedSingleBusbarThickness) <= GeometryTolerance)
            {
                componentMaximumPosition++;
            }

            return orderedGeneratedLayers
                .Skip(componentMinimumPosition)
                .Take(componentMaximumPosition - componentMinimumPosition + 1)
                .Select(generatedLayer => generatedLayer.GeneratedMemberIndex)
                .OrderBy(generatedMemberIndex => generatedMemberIndex)
                .ToList();
        }

        public static bool TryBuild(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason,
            bool applyOutputTerminalLapGeometry = false)
        {
            failureReason = null;
            if (conversionResult == null || routeRequest == null ||
                routeGroupRequest == null)
            {
                failureReason = "端部附件生成缺少路径转换结果或请求参数。";
                return false;
            }

            conversionResult.Accessories.Clear();
            if (conversionResult.StartStackPlacement == null ||
                conversionResult.EndStackPlacement == null)
            {
                // 仅供中间诊断/反射测试构造的预制转换可能没有端部层位；
                // 这种对象不具备生成附件的依据，保持原有后续校验阶段。
                return true;
            }

            var useSpacerlessStartTerminalLap = routeRequest
                .ShouldUseSpacerlessTerminalLap(true);
            var useSpacerlessEndTerminalLap = routeRequest
                .ShouldUseSpacerlessTerminalLap(false);
            var startTerminalIsHorizontalLap =
                IsHorizontalStartTerminalLap(
                    conversionResult,
                    routeGroupRequest.StartTerminal);
            LinkageRouteAccessory doubleStartBridgeAccessory = null;
            var bridgePlacements = conversionResult.StartStackPlacement
                ?.DoubleStartHorizontalBridgePlacements;
            if (!startTerminalIsHorizontalLap)
            {
                bridgePlacements = Array.Empty<LinkageRouteTerminalBridgePlacement>();
            }
            if (startTerminalIsHorizontalLap &&
                (bridgePlacements == null || bridgePlacements.Count == 0))
            {
                if (conversionResult.StartStackPlacement
                        ?.DoubleStartHorizontalBridgeGeneratedMemberIndex.HasValue != true)
                {
                    bridgePlacements = Array.Empty<LinkageRouteTerminalBridgePlacement>();
                }
                else
                {
                    bridgePlacements = new[]
                    {
                        new LinkageRouteTerminalBridgePlacement
                        {
                            GeneratedMemberIndex = conversionResult.StartStackPlacement
                                .DoubleStartHorizontalBridgeGeneratedMemberIndex.Value,
                            SideSign = conversionResult.StartStackPlacement
                                .DoubleStartHorizontalBridgeSideSign,
                            Thickness = conversionResult.StartStackPlacement
                                .DoubleStartHorizontalBridgeThickness
                        }
                    };
                }
            }
            if (applyOutputTerminalLapGeometry &&
                !(routeRequest.EnableSingleStartConnectionBar &&
                    routeGroupRequest.StartTerminal?.Members?.Count == 1) &&
                bridgePlacements.Count == 1 &&
                conversionResult.StartStackPlacement?.GeneratedMemberOffsets?.Count > 1)
            {
                var primaryBridgePlacement = bridgePlacements[0];
                var oppositeBridgeMemberIndex =
                    ResolveAdjacentOppositeBridgeMemberIndex(
                        conversionResult.StartStackPlacement
                            .GeneratedMemberOffsets,
                        primaryBridgePlacement.GeneratedMemberIndex,
                        primaryBridgePlacement.SideSign);
                if (oppositeBridgeMemberIndex.HasValue)
                {
                    bridgePlacements = bridgePlacements
                        .Concat(new[]
                        {
                            new LinkageRouteTerminalBridgePlacement
                            {
                                GeneratedMemberIndex = oppositeBridgeMemberIndex.Value,
                                SideSign = -primaryBridgePlacement.SideSign,
                                Thickness = primaryBridgePlacement.Thickness
                            }
                        })
                        .ToList();
                }
            }
            foreach (var bridgePlacement in bridgePlacements)
            {
                if (!TryCreateDoubleStartHorizontalBridgeAccessoryForPlacement(
                        conversionResult,
                        routeRequest,
                        routeGroupRequest,
                        bridgePlacement,
                        out doubleStartBridgeAccessory,
                        out failureReason))
                {
                    return false;
                }
                conversionResult.Accessories.Add(doubleStartBridgeAccessory);
            }

            if (applyOutputTerminalLapGeometry)
            {
                // 附件式连接排和已经合并为主路径首段的 I/L 连接排
                // 都必须在垫块生成前完成始端首段统一。
                ApplyOutputTerminalLapGeometry(
                    conversionResult,
                    routeGroupRequest.StartTerminal);
                // 先固定连接排端点和层位，再只调整可折弯生成排的搭接段；
                // 垫块必须基于这组最终位置生成，不能在偏移前提前补缝隙。
                ApplyOutputTerminalBridgeLayerAlignment(
                    conversionResult,
                    routeGroupRequest.StartTerminal);
            }

            // 垫块/搭接区域以两侧连接排中覆盖范围最大的实际连接排为
            // 共同平面基准，避免仍按单侧短投影生成垫块。
            doubleStartBridgeAccessory = conversionResult.Accessories
                .Where(routeAccessory => routeAccessory?.Kind ==
                    LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                    routeAccessory.Setting?.PointList != null)
                .OrderByDescending(routeAccessory => routeAccessory.Setting
                    .PointList[0].DistanceTo(routeAccessory.Setting.PointList[
                        routeAccessory.Setting.PointList.Count - 1]))
                .FirstOrDefault() ?? doubleStartBridgeAccessory;

            if (useSpacerlessStartTerminalLap &&
                !TryValidateSpacerlessGeneratedTerminalLayers(
                    conversionResult.StackMembers,
                    routeGroupRequest.StartTerminal,
                    conversionResult.StartStackPlacement,
                    true,
                    conversionResult.StartTerminalMemberIndices,
                    "始端",
                    doubleStartBridgeAccessory,
                    out failureReason,
                    routeRequest.EnableSingleStartConnectionBar && routeGroupRequest.StartTerminal.Members.Count == 1
                        ? conversionResult.Accessories.Where(accessory =>
                            accessory.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge).ToList()
                        : null))
            {
                return false;
            }

            if (useSpacerlessEndTerminalLap &&
                !TryValidateSpacerlessGeneratedTerminalLayers(
                    conversionResult.StackMembers,
                    routeGroupRequest.EndTerminal,
                    conversionResult.EndStackPlacement,
                    false,
                    conversionResult.EndTerminalMemberIndices,
                    "柜顶",
                    null,
                    out failureReason))
            {
                return false;
            }

            if (!useSpacerlessStartTerminalLap &&
                !TryAddTerminalSupportBlocks(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    routeGroupRequest.StartTerminal,
                    conversionResult.StartStackPlacement,
                    true,
                    true,
                    doubleStartBridgeAccessory,
                    out failureReason))
            {
                return false;
            }

            if (!useSpacerlessEndTerminalLap &&
                !TryAddTerminalSupportBlocks(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    routeGroupRequest.EndTerminal,
                    conversionResult.EndStackPlacement,
                    false,
                    false,
                    null,
                    out failureReason))
            {
                return false;
            }

            LinkageRouteCabinetBridgeBuilder.AddAccessories(conversionResult, routeGroupRequest);
            if (!LinkageRouteCabinetBridgeBuilder.TryValidate(conversionResult, routeGroupRequest, out failureReason))
                return false;

            var uniqueAccessories = conversionResult.Accessories
                .Where(routeAccessory => routeAccessory?.Setting != null)
                .GroupBy(CreateAccessorySignature, StringComparer.Ordinal)
                .Select(routeAccessoryGroup => routeAccessoryGroup.First())
                .ToList();
            conversionResult.Accessories.Clear();
            foreach (var uniqueAccessory in uniqueAccessories)
            {
                conversionResult.Accessories.Add(uniqueAccessory);
            }

            // 此处为候选骨架估长。最终补块与分层完成后，由
            // LinkageRouteCopperLength 按整套最薄铜排门槛复算发布总长。
            conversionResult.ActualCopperLength = conversionResult.StackMembers
                .Where(stackMember => stackMember?.PlanarParts != null)
                .SelectMany(stackMember => stackMember.PlanarParts)
                .Where(planarPart => planarPart != null)
                .Sum(planarPart => planarPart.GetDevelopedLength()) +
                conversionResult.Accessories
                    .Where(routeAccessory =>
                        (routeAccessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge ||
                         routeAccessory?.Kind == LinkageRouteAccessoryKind.CabinetTopConnectionBridge) &&
                        routeAccessory.Setting != null)
                    .Sum(routeAccessory =>
                        routeAccessory.Setting.GetDevelopedLength());

            return true;
        }

        /// <summary>
        /// 复核最终转换结果中的双始端连接排。连接排规格必须由始端搭接排
        /// 动态推导，关联成员及其位置必须与端部层位一致。
        /// </summary>
        internal static bool TryValidateConnectionOutput(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out string failureReason)
        {
            failureReason = null;
            if (conversionResult == null || routeRequest == null ||
                routeGroupRequest == null)
            {
                failureReason = "连接排最终校验缺少路径转换结果或请求参数。";
                return false;
            }

            var bridgeAccessories = (conversionResult.Accessories ??
                    new List<LinkageRouteAccessory>())
                .Where(routeAccessory => routeAccessory?.Kind ==
                    LinkageRouteAccessoryKind.DoubleStartHorizontalBridge)
                .ToList();
            var bridgeSectionIndices = conversionResult
                .DoubleStartPhysicalBridgeSectionIndices ??
                new HashSet<int>();
            var physicalSectionCount = conversionResult.CenterlineParts?.Count ?? 0;
            if (bridgeSectionIndices.Any(sectionIndex => sectionIndex < 0 ||
                    sectionIndex >= physicalSectionCount) ||
                bridgeSectionIndices.Any(sectionIndex => sectionIndex != 0))
            {
                failureReason = "双始端连接排的物理段标记无效。";
                return false;
            }

            var hasMultiStart = routeRequest.AllowDoubleStartMiddleLap &&
                routeGroupRequest.StartTerminal?.Members?.Count >= 2;
            if (!TryValidateLengthSeparatedStartContacts(conversionResult,
                    routeGroupRequest, out failureReason)) return false;
            var shortStartLapLength = routeGroupRequest.GetShortPhysicalStartLapLength(routeRequest, true);
            if (double.IsNaN(shortStartLapLength))
            {
                failureReason = "短始端成员没有可证明的共同搭接直段，不能派生连接排或垂直姿态。";
                return false;
            }
            if (shortStartLapLength > GeometryTolerance &&
                !HasOnlyHorizontalShortStartLaps(conversionResult, routeGroupRequest))
            {
                failureReason = "固定始端实际搭接段长不足生成排宽，只允许水平搭接。";
                return false;
            }
            if (routeRequest.EnableSingleStartConnectionBar &&
                routeGroupRequest.StartTerminal?.Members?.Count == 1)
            {
                return TryValidateSingleStartConnectionOutput(conversionResult,
                    routeRequest, routeGroupRequest, bridgeAccessories, out failureReason);
            }
            if (!hasMultiStart)
            {
                if (bridgeAccessories.Count > 0 || bridgeSectionIndices.Count > 0)
                {
                    failureReason = "单始端方案不应包含双始端连接排。";
                    return false;
                }

                return true;
            }

            var expectedBridgeWidth = routeGroupRequest
                .GetDoubleStartBridgeBusbarWidth(routeRequest);
            var expectedBridgeThickness = routeGroupRequest
                .GetDoubleStartBridgeBusbarThickness(routeRequest);
            if (expectedBridgeWidth <= GeometryTolerance ||
                expectedBridgeThickness <= GeometryTolerance)
            {
                failureReason = "双始端连接排的派生排宽或排厚无效。";
                return false;
            }

            // 合并桥排已经成为首段实体，不能再同时保留独立连接排。
            if (bridgeSectionIndices.Count > 0)
            {
                var mergedBridgeSetting = physicalSectionCount > 0
                    ? conversionResult.CenterlineParts[0]
                    : null;
                if (bridgeAccessories.Count > 0 ||
                    !IsUsableConnectionSetting(mergedBridgeSetting) ||
                    mergedBridgeSetting.PointList.Count != 2 ||
                    Math.Abs(mergedBridgeSetting.BusBarWidth -
                        expectedBridgeWidth) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                    Math.Abs(mergedBridgeSetting.BusBarThickness -
                        expectedBridgeThickness) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                {
                    failureReason = "合并到首段的双始端连接排规格或几何无效。";
                    return false;
                }

                if (!LinkageRouteDoubleStartLapGeometry
                        .TryCreateCollectiveFixedBusbar(
                            routeGroupRequest.StartTerminal,
                            out var mergedCollectiveFixedBusbar) ||
                    conversionResult.StartTerminalMemberIndices == null ||
                    conversionResult.StartTerminalMemberIndices.Count == 0)
                {
                    failureReason =
                        "合并连接排无法解析双始端固定排或关联叠排成员。";
                    return false;
                }

                foreach (var mergedBridgeMemberIndex in conversionResult
                    .StartTerminalMemberIndices)
                {
                    var mergedBridgeMember = conversionResult.StackMembers
                        ?.FirstOrDefault(stackMember =>
                            stackMember?.MemberIndex == mergedBridgeMemberIndex);
                    var mergedBridgeMemberSetting = mergedBridgeMember
                        ?.PlanarParts?
                        .FirstOrDefault();
                    var mergedDownstreamSetting = mergedBridgeMember
                        ?.PlanarParts?
                        .Skip(1)
                        .FirstOrDefault(planarPart =>
                            planarPart?.PointList?.Count >= 2);
                    if (!IsUsableConnectionSetting(
                            mergedBridgeMemberSetting) ||
                        mergedBridgeMemberSetting.PointList.Count != 2 ||
                        Math.Abs(mergedBridgeMemberSetting.BusBarWidth -
                            expectedBridgeWidth) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                        Math.Abs(mergedBridgeMemberSetting.BusBarThickness -
                            expectedBridgeThickness) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                        mergedDownstreamSetting == null ||
                        !TryValidateBridgeLengthCoverage(
                            mergedBridgeMemberSetting,
                            mergedCollectiveFixedBusbar,
                            mergedDownstreamSetting,
                            out failureReason) ||
                        (!LinkageRouteTerminalLapRegionValidator
                            .TryCreateActualLapFootprint(
                                mergedBridgeMemberSetting,
                                true,
                                mergedCollectiveFixedBusbar,
                                true,
                                Math.Min(
                                    mergedBridgeMemberSetting.BusBarWidth,
                                    mergedCollectiveFixedBusbar.BusBarWidth),
                                out _, tolerance: LinkageRouteGeometryTolerances.FinalAcceptanceComparison) &&
                         !(LinkageRouteDoubleStartLapGeometry.TryCreateLengthSeparatedCollectiveFixedBusbar(
                               routeGroupRequest.StartTerminal, out _) &&
                           HasStartFixedContactChain(conversionResult,
                               routeGroupRequest.StartTerminal, mergedBridgeMemberSetting))))
                    {
                        if (string.IsNullOrWhiteSpace(failureReason))
                        {
                            failureReason = string.Format(
                                CultureInfo.InvariantCulture,
                                "第 {0} 层合并连接排未同时满足双始端与外侧生成排的满搭接。",
                                mergedBridgeMemberIndex + 1);
                        }
                        return false;
                    }
                }

                return true;
            }

            var startPlacement = conversionResult.StartStackPlacement;
            if (startPlacement == null)
            {
                // 垂直多始端实体路径可以是普通 L/I 过渡，不应因为没有
                // 水平连接排附件而被硬性要求 1/2 根连接排。
                if (bridgeAccessories.Count == 0 &&
                    !IsHorizontalStartTerminalLap(
                        conversionResult,
                        routeGroupRequest.StartTerminal))
                {
                    return true;
                }

                var expectedOutputBridgeMemberCount = Math.Min(
                    2,
                    Math.Max(
                        1,
                        conversionResult.StartTerminalMemberIndices?.Count > 0
                            ? conversionResult.StartTerminalMemberIndices.Count
                            : conversionResult.StackMembers?.Count ?? 0));
                if (expectedOutputBridgeMemberCount > 0 &&
                    bridgeAccessories.Count != expectedOutputBridgeMemberCount)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "双始端连接排数量不符合最终叠排成员：实际 {0} 根，要求 {1} 根。",
                        bridgeAccessories.Count,
                        expectedOutputBridgeMemberCount);
                    return false;
                }

                return ValidateConnectionAccessoriesStructure(
                    bridgeAccessories,
                    conversionResult.StackMembers,
                    routeGroupRequest,
                    routeRequest,
                    expectedBridgeWidth,
                    expectedBridgeThickness,
                    out failureReason);
            }

            var isHorizontalStartLap = IsHorizontalStartTerminalLap(
                conversionResult,
                routeGroupRequest.StartTerminal);
            var expectedPlacements = startPlacement
                .DoubleStartHorizontalBridgePlacements;
            if (!isHorizontalStartLap || expectedPlacements == null ||
                expectedPlacements.Count == 0)
            {
                if (bridgeAccessories.Count > 0)
                {
                    failureReason = "非水平双始端搭接不应输出横向连接排。";
                    return false;
                }

                return true;
            }

            var normalizedPlacements = expectedPlacements.ToList();
            if (normalizedPlacements.Count == 1 &&
                startPlacement.GeneratedMemberOffsets?.Count > 1)
            {
                var primaryPlacement = normalizedPlacements[0];
                var oppositeMemberIndex = ResolveAdjacentOppositeBridgeMemberIndex(
                    startPlacement.GeneratedMemberOffsets,
                    primaryPlacement.GeneratedMemberIndex,
                    primaryPlacement.SideSign);
                if (oppositeMemberIndex.HasValue)
                {
                    normalizedPlacements.Add(new LinkageRouteTerminalBridgePlacement
                    {
                        GeneratedMemberIndex = oppositeMemberIndex.Value,
                        SideSign = -primaryPlacement.SideSign,
                        Thickness = primaryPlacement.Thickness
                    });
                }
            }

            var expectedMemberIndices = normalizedPlacements
                .Select(placement => placement?.GeneratedMemberIndex ?? -1)
                .ToList();
            if (expectedMemberIndices.Any(memberIndex => memberIndex < 0) ||
                expectedMemberIndices.Distinct().Count() != expectedMemberIndices.Count ||
                bridgeAccessories.Count != expectedMemberIndices.Count ||
                !ValidateConnectionAccessoriesStructure(
                    bridgeAccessories,
                    conversionResult.StackMembers,
                    routeGroupRequest,
                    routeRequest,
                    expectedBridgeWidth,
                    expectedBridgeThickness,
                    out failureReason))
            {
                if (string.IsNullOrWhiteSpace(failureReason))
                {
                    failureReason = "双始端连接排数量或关联叠排成员不符合端部层位。";
                }

                return false;
            }

            var actualMemberIndices = bridgeAccessories
                .Select(routeAccessory => routeAccessory.RelatedGeneratedMemberIndex)
                .OrderBy(memberIndex => memberIndex)
                .ToList();
            if (!actualMemberIndices.SequenceEqual(
                    expectedMemberIndices.OrderBy(memberIndex => memberIndex)))
            {
                failureReason = "双始端连接排没有关联到预期的叠排成员。";
                return false;
            }

            if (!LinkageRouteDoubleStartLapGeometry.TryCreateCollectiveFixedBusbar(
                    routeGroupRequest.StartTerminal,
                    out var collectiveFixedBusbar))
            {
                failureReason = "无法解析双始端整体固定排，不能确认连接排位置。";
                return false;
            }

            foreach (var bridgeAccessory in bridgeAccessories)
            {
                var bridgeReferenceSetting = bridgeAccessory.Setting.Copy();
                bridgeReferenceSetting.BusBarWidth = expectedBridgeWidth;
                if (!LinkageRouteTerminalLapRegionValidator
                        .TryCreateProjectedLapFootprint(
                            bridgeReferenceSetting,
                            true,
                            collectiveFixedBusbar,
                            true,
                            routeGroupRequest.GetStartLapBusbarWidth(routeRequest),
                            out _))
                {
                    failureReason = "双始端连接排位置未落在整体始端搭接范围内。";
                    return false;
                }
            }

            return true;
        }

        private static bool TryValidateSingleStartConnectionOutput(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            IReadOnlyCollection<LinkageRouteAccessory> connectionAccessories,
            out string failureReason)
        {
            failureReason = null;
            if (!IsHorizontalStartTerminalLap(conversionResult, routeGroupRequest.StartTerminal))
            {
                if (connectionAccessories.Count == 0 &&
                    conversionResult.DoubleStartPhysicalBridgeSectionIndices.Count == 0) return true;
                failureReason = "单始端垂直搭接不应增加连接排。";
                return false;
            }
            var actualStartMemberCount = conversionResult.StartTerminalMemberIndices.Count > 0
                ? conversionResult.StartTerminalMemberIndices.Count : conversionResult.StackMembers.Count;
            var requiredConnectionCount = Math.Min(2, actualStartMemberCount);
            if (requiredConnectionCount == 0 || connectionAccessories.Count != requiredConnectionCount ||
                conversionResult.DoubleStartPhysicalBridgeSectionIndices.Count != 0 ||
                !TryGetConnectionFixedBusbar(routeGroupRequest.StartTerminal, out var fixedStartSetting))
            {
                failureReason = "单始端连接排数量或物理段身份不符合实际始端搭接叠排。";
                return false;
            }
            var thicknessDirection = routeGroupRequest.StartTerminal.ThicknessDirection.Normalized;
            var fixedCoordinate = DotPoint(fixedStartSetting.PointList[0], thicknessDirection);
            var seenConnectionMembers = new HashSet<int>();
            var connectionSides = new HashSet<int>();
            foreach (var connectionAccessory in connectionAccessories)
            {
                var connectionSetting = connectionAccessory?.Setting;
                var relatedMemberIndex = connectionAccessory?.RelatedGeneratedMemberIndex ?? -1;
                var generatedSetting = conversionResult.StackMembers.FirstOrDefault(member =>
                    member?.MemberIndex == relatedMemberIndex)?.PlanarParts?.FirstOrDefault();
                if (connectionAccessory?.TerminalSide != LinkageRouteTerminalSide.Start ||
                    !IsUsableConnectionSetting(connectionSetting) || !IsUsableConnectionSetting(generatedSetting) ||
                    !seenConnectionMembers.Add(relatedMemberIndex) ||
                    (conversionResult.StartTerminalMemberIndices.Count > 0 &&
                        !conversionResult.StartTerminalMemberIndices.Contains(relatedMemberIndex)) ||
                    Math.Abs(connectionSetting.BusBarWidth - routeGroupRequest.GetDoubleStartBridgeBusbarWidth(routeRequest)) >
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                    Math.Abs(connectionSetting.BusBarThickness - routeGroupRequest.GetDoubleStartBridgeBusbarThickness(routeRequest)) >
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                    Math.Abs(connectionSetting.PointList[0].DistanceTo(connectionSetting.PointList.Last()) - fixedStartSetting.BusBarWidth) >
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
                {
                    failureReason = "单始端连接排规格、长度或叠排关联无效。";
                    return false;
                }
                var relativeConnectionCoordinate = DotPoint(connectionSetting.PointList[0], thicknessDirection) - fixedCoordinate;
                var sideSign = Math.Sign(relativeConnectionCoordinate);
                var generatedCoordinate = DotPoint(generatedSetting.PointList[0], thicknessDirection) - fixedCoordinate;
                if (!connectionSides.Add(sideSign) || sideSign == 0 ||
                    Math.Abs(Math.Abs(relativeConnectionCoordinate) -
                        (fixedStartSetting.BusBarThickness + connectionSetting.BusBarThickness) / 2d) >
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                    Math.Abs(generatedCoordinate - relativeConnectionCoordinate - sideSign *
                        (connectionSetting.BusBarThickness + generatedSetting.BusBarThickness) / 2d) >
                            LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                    !LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(
                        connectionSetting, true, fixedStartSetting, true,
                        connectionSetting.BusBarWidth, out _) ||
                    !TryValidateBridgeLengthCoverage(connectionSetting, fixedStartSetting, generatedSetting, out failureReason))
                {
                    failureReason = "单始端连接排未在实际搭接侧贴合、夹排或完整覆盖搭接区域。" + failureReason;
                    return false;
                }
            }
            if (requiredConnectionCount == 2 && (!connectionSides.Contains(-1) || !connectionSides.Contains(1)))
            {
                failureReason = "单始端叠排连接排没有在两侧形成夹持。";
                return false;
            }
            return true;
        }

        private static bool HasOnlyHorizontalShortStartLaps(
            LinkageRouteCenterlineConversionResult conversionResult, LinkageRouteGroupRequest groupRequest)
        {
            if (!LinkageRouteTerminalPoseFactory.TryGetCommonContactSegmentLength(groupRequest.StartTerminal,
                groupRequest.EndTerminal?.RouteAnchor, out _, out var fixedContactDirection)) return false;
            var checkedGeneratedCount = 0;
            foreach (var generatedMember in conversionResult.StackMembers)
            {
                if (conversionResult.StartTerminalMemberIndices.Count > 0 &&
                    !conversionResult.StartTerminalMemberIndices.Contains(generatedMember.MemberIndex)) continue;
                // 独立物理桥排本身与固定始端垂直；限制的是桥后的始端搭接排。
                var generatedSetting = generatedMember.PlanarParts.FirstOrDefault(part =>
                    !conversionResult.DoubleStartPhysicalBridgeSectionIndices.Contains(part.PhysicalSectionIndex));
                if (generatedSetting?.PointList == null || generatedSetting.PointList.Count < 2) return false;
                var generatedDirection = new Vector3d(generatedSetting.PointList[0], generatedSetting.PointList[1]);
                if (generatedDirection.Norm <= GeometryTolerance ||
                    generatedDirection.Normalized.Cross(fixedContactDirection).Norm > GeometryTolerance) return false;
                checkedGeneratedCount++;
            }
            return checkedGeneratedCount > 0;
        }

        private static bool ValidateConnectionAccessoriesStructure(
            IReadOnlyCollection<LinkageRouteAccessory> bridgeAccessories,
            IList<LinkageStackedRouteMember> stackMembers,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageMultiGroupRouteRequest routeRequest,
            double expectedBridgeWidth,
            double expectedBridgeThickness,
            out string failureReason)
        {
            failureReason = null;
            var memberCount = stackMembers?.Count ?? 0;
            var seenMemberIndices = new HashSet<int>();
            foreach (var bridgeAccessory in bridgeAccessories ??
                Array.Empty<LinkageRouteAccessory>())
            {
                var relatedMemberIndex = bridgeAccessory?.RelatedGeneratedMemberIndex ?? -1;
                if (bridgeAccessory == null ||
                    bridgeAccessory.TerminalSide != LinkageRouteTerminalSide.Start ||
                    !IsUsableConnectionSetting(bridgeAccessory.Setting) ||
                    Math.Abs(bridgeAccessory.Setting.BusBarWidth -
                        expectedBridgeWidth) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                    Math.Abs(bridgeAccessory.Setting.BusBarThickness -
                        expectedBridgeThickness) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                    relatedMemberIndex < 0 || relatedMemberIndex >= memberCount ||
                    stackMembers[relatedMemberIndex]?.MemberIndex != relatedMemberIndex ||
                    !seenMemberIndices.Add(relatedMemberIndex))
                {
                    failureReason = "双始端连接排规格、位置或叠排关联索引无效。";
                    return false;
                }

                if (!LinkageRouteDoubleStartLapGeometry.TryCreateCollectiveFixedBusbar(
                        routeGroupRequest.StartTerminal,
                        out var collectiveFixedBusbar) ||
                    !LinkageRouteTerminalLapRegionValidator
                        .TryCreateProjectedLapFootprint(
                            bridgeAccessory.Setting,
                            true,
                            collectiveFixedBusbar,
                            true,
                            routeGroupRequest.GetStartLapBusbarWidth(routeRequest),
                            out _))
                {
                    failureReason = "双始端连接排位置未落在始端整体搭接范围内。";
                    return false;
                }

                var relatedGeneratedSetting = stackMembers[relatedMemberIndex]
                    ?.PlanarParts?
                    .FirstOrDefault(planarPart =>
                        planarPart?.PointList?.Count >= 2);
                if (relatedGeneratedSetting == null ||
                    !TryValidateBridgeLengthCoverage(
                        bridgeAccessory.Setting,
                        collectiveFixedBusbar,
                        relatedGeneratedSetting,
                        out failureReason))
                {
                    if (string.IsNullOrWhiteSpace(failureReason))
                    {
                        failureReason =
                            "双始端连接排未覆盖关联生成排的实际满搭接区域。";
                    }
                    return false;
                }
            }

            return true;
        }

        internal static bool TryValidateLengthSeparatedStartContacts(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageRouteGroupRequest groupRequest,
            out string failureReason)
        {
            failureReason = null;
            if (!LinkageRouteDoubleStartLapGeometry.TryCreateLengthSeparatedCollectiveFixedBusbar(
                    groupRequest?.StartTerminal, out _)) return true;
            var startMembers = conversionResult.StackMembers.Where(member =>
                conversionResult.StartTerminalMemberIndices.Count == 0 ||
                conversionResult.StartTerminalMemberIndices.Contains(member.MemberIndex)).ToList();
            if (startMembers.Count == 0)
            {
                failureReason = "沿排长分离的始端缺少真实生成成员。";
                return false;
            }
            foreach (var startMember in startMembers)
            {
                if (startMember.PlanarParts?.Count > 0 &&
                    HasStartFixedContactChain(conversionResult, groupRequest.StartTerminal, startMember.PlanarParts[0])) continue;
                failureReason = "始端第 " + (startMember.MemberIndex + 1) +
                    " 层未通过真实满搭接接触链连接全部固定始端。";
                return false;
            }
            return true;
        }

        private static bool HasStartFixedContactChain(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly fixedTerminal,
            PlanarBendBusbarSetting bridgeSetting)
        {
            var physicalBridgeLayers = conversionResult.StackMembers
                .Where(member => member?.PlanarParts?.Count > 0 &&
                    conversionResult.StartTerminalMemberIndices.Contains(member.MemberIndex))
                .Select(member => member.PlanarParts[0]);
            var physicalSupports = conversionResult.Accessories
                .Where(accessory => accessory?.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                    accessory.TerminalSide == LinkageRouteTerminalSide.Start &&
                    accessory.Setting?.PointList?.Count == 2)
                .Select(accessory => accessory.Setting);
            var physicalNodes = physicalBridgeLayers.Concat(physicalSupports).Distinct().ToList();
            var remainingFixedSettings = fixedTerminal.Members
                .Select(member => member.FixedBusbar).ToList();
            var visitedSettings = new HashSet<PlanarBendBusbarSetting> {bridgeSetting};
            var pendingSettings = new Queue<PlanarBendBusbarSetting>();
            pendingSettings.Enqueue(bridgeSetting);
            Func<PlanarBendBusbarSetting, PlanarBendBusbarSetting, bool> hasActualFullLap =
                (first, second) =>
                {
                    if (!LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(
                        first, true, second, true, bridgeSetting.BusBarWidth,
                        out var contactFootprint, tolerance: LinkageRouteGeometryTolerances.FinalAcceptanceComparison))
                        return false;
                    var contactWidthCoordinates = contactFootprint.Corners
                        .Select(point => DotPoint(point, bridgeSetting.WidthVector.Normalized)).ToList();
                    return contactWidthCoordinates.Max() - contactWidthCoordinates.Min() +
                        LinkageRouteGeometryTolerances.FinalAcceptanceComparison >= bridgeSetting.BusBarWidth;
                };
            while (pendingSettings.Count > 0)
            {
                var currentSetting = pendingSettings.Dequeue();
                remainingFixedSettings.RemoveAll(fixedSetting => hasActualFullLap(currentSetting, fixedSetting));
                if (remainingFixedSettings.Count == 0) return true;
                foreach (var nextSetting in physicalNodes)
                {
                    if (visitedSettings.Contains(nextSetting) || !hasActualFullLap(currentSetting, nextSetting)) continue;
                    visitedSettings.Add(nextSetting);
                    pendingSettings.Enqueue(nextSetting);
                }
            }
            return false;
        }

        private static bool TryValidateBridgeLengthCoverage(
            PlanarBendBusbarSetting bridgeSetting,
            PlanarBendBusbarSetting collectiveFixedBusbar,
            PlanarBendBusbarSetting generatedSetting,
            out string failureReason)
        {
            failureReason = null;
            if (!IsUsableConnectionSetting(bridgeSetting) ||
                collectiveFixedBusbar?.PointList?.Count < 2 ||
                generatedSetting?.PointList?.Count < 2)
            {
                return false;
            }

            var bridgeDirectionVector = new Vector3d(
                bridgeSetting.PointList[0],
                bridgeSetting.PointList[bridgeSetting.PointList.Count - 1]);
            if (bridgeDirectionVector.Norm <= GeometryTolerance)
            {
                return false;
            }

            var bridgeDirection = bridgeDirectionVector.Normalized;
            var bridgeCoordinates = bridgeSetting.PointList
                .Select(point => DotPoint(point, bridgeDirection))
                .ToList();
            var fixedCenterCoordinate = collectiveFixedBusbar.PointList
                .Average(point => DotPoint(point, bridgeDirection));
            var fixedHalfWidthProjection =
                collectiveFixedBusbar.WidthVector == null ||
                collectiveFixedBusbar.WidthVector.Norm <= GeometryTolerance
                    ? collectiveFixedBusbar.BusBarWidth / 2d
                    : Math.Abs(collectiveFixedBusbar.WidthVector.Normalized.Dot(
                        bridgeDirection)) *
                      collectiveFixedBusbar.BusBarWidth / 2d;
            var generatedBoundaryCoordinate = DotPoint(
                generatedSetting.PointList[0],
                bridgeDirection);
            var generatedFirstDirection = new Vector3d(generatedSetting.PointList[0], generatedSetting.PointList[1]).Normalized;
            var parallelBridgeLap = Math.Abs(generatedFirstDirection.Dot(bridgeDirection)) >= 1d - GeometryTolerance;
            var generatedHalfWidthProjection =
                generatedSetting.WidthVector == null ||
                generatedSetting.WidthVector.Norm <= GeometryTolerance
                    ? 0d
                    : Math.Abs(generatedSetting.WidthVector.Normalized.Dot(
                        bridgeDirection)) *
                      generatedSetting.BusBarWidth / 2d;
            var requiredMinimumCoordinate = Math.Min(
                fixedCenterCoordinate - fixedHalfWidthProjection,
                generatedBoundaryCoordinate -
                    generatedHalfWidthProjection);
            var requiredMaximumCoordinate = Math.Max(
                fixedCenterCoordinate + fixedHalfWidthProjection,
                generatedBoundaryCoordinate +
                    generatedHalfWidthProjection);
            if (parallelBridgeLap)
            {
                var overlapEndCoordinate = generatedBoundaryCoordinate +
                    generatedFirstDirection.Dot(bridgeDirection) * Math.Max(bridgeSetting.BusBarWidth, generatedSetting.BusBarWidth);
                requiredMinimumCoordinate = Math.Min(requiredMinimumCoordinate, overlapEndCoordinate);
                requiredMaximumCoordinate = Math.Max(requiredMaximumCoordinate, overlapEndCoordinate);
            }
            if (Math.Abs(bridgeCoordinates.Min() - requiredMinimumCoordinate) >
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison ||
                Math.Abs(bridgeCoordinates.Max() - requiredMaximumCoordinate) >
                    LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
            {
                failureReason =
                    "双始端连接排端点没有停在固定始端与关联生成排所需的最短覆盖边界。";
                return false;
            }

            if (parallelBridgeLap)
            {
                if (LinkageRouteTerminalLapRegionValidator.TryCreateActualLapFootprint(
                    bridgeSetting, false, generatedSetting, true,
                    Math.Max(bridgeSetting.BusBarWidth, generatedSetting.BusBarWidth),
                    out _, tolerance: LinkageRouteGeometryTolerances.FinalAcceptanceComparison)) return true;
                failureReason = "合并连接排与下游平行排未形成真实满搭接。";
                return false;
            }

            var expectedGeneratedSetting = generatedSetting.Copy();
            if (!LinkageRouteSectionSpecificationPostProcessor
                    .TryExtendPerpendicularBoundaryEndpoint(
                        expectedGeneratedSetting,
                        true,
                        bridgeSetting,
                        bridgeSetting.BusBarWidth) ||
                expectedGeneratedSetting.PointList[0].DistanceTo(
                    generatedSetting.PointList[0]) > LinkageRouteGeometryTolerances.FinalAcceptanceComparison)
            {
                failureReason =
                    "关联生成排端头没有到达双始端连接排的实际外侧边界。";
                return false;
            }

            var generatedBoundaryPoint = generatedSetting.PointList[0];
            var bridgeStartDistance = generatedBoundaryPoint.DistanceTo(
                bridgeSetting.PointList[0]);
            var bridgeEndDistance = generatedBoundaryPoint.DistanceTo(
                bridgeSetting.PointList[bridgeSetting.PointList.Count - 1]);
            return LinkageRouteTerminalLapRegionValidator
                .TryCreateProjectedLapFootprint(
                    bridgeSetting,
                    bridgeStartDistance <= bridgeEndDistance,
                    generatedSetting,
                    true,
                    Math.Min(
                        bridgeSetting.BusBarWidth,
                        generatedSetting.BusBarWidth),
                    out _);
        }

        private static bool IsUsableConnectionSetting(
            PlanarBendBusbarSetting connectionSetting)
        {
            return connectionSetting?.PointList != null &&
                connectionSetting.PointList.Count >= 2 &&
                connectionSetting.WidthVector != null &&
                connectionSetting.WidthVector.Norm > GeometryTolerance &&
                connectionSetting.StartBusbarVector != null &&
                connectionSetting.StartBusbarVector.Norm > GeometryTolerance &&
                connectionSetting.BusBarWidth > GeometryTolerance &&
                connectionSetting.BusBarThickness > GeometryTolerance;
        }

        /// <summary>
        /// 第二根连接排优先取第一根连接层朝另一搭接侧的相邻层。这样两个
        /// 连接成员沿原叠排厚向顺序分别落在固定排两侧，剩余成员只需向外
        /// 顺排，不会为了跨越连接成员而在后续段发生内外层交叉。
        /// </summary>
        private static int? ResolveAdjacentOppositeBridgeMemberIndex(
            IList<double> generatedMemberOffsets,
            int primaryBridgeMemberIndex,
            int primaryBridgeSideSign)
        {
            if (generatedMemberOffsets == null ||
                primaryBridgeMemberIndex < 0 ||
                primaryBridgeMemberIndex >= generatedMemberOffsets.Count ||
                generatedMemberOffsets.Count <= 1)
            {
                return null;
            }

            var normalizedPrimarySideSign = primaryBridgeSideSign < 0
                ? -1d
                : 1d;
            var primaryOffset = generatedMemberOffsets[
                primaryBridgeMemberIndex];
            var candidates = generatedMemberOffsets
                .Select((offset, index) => new
                {
                    Offset = offset,
                    Index = index,
                    Delta = offset - primaryOffset
                })
                .Where(candidate => candidate.Index != primaryBridgeMemberIndex)
                .ToList();
            var oppositeSideAdjacentMemberIndex = candidates
                .Where(candidate => candidate.Delta *
                    normalizedPrimarySideSign < -GeometryTolerance)
                .OrderBy(candidate => Math.Abs(candidate.Delta))
                .Select(candidate => (int?)candidate.Index)
                .FirstOrDefault();
            return oppositeSideAdjacentMemberIndex ?? candidates
                .OrderBy(candidate => Math.Abs(candidate.Delta))
                .Select(candidate => (int?)candidate.Index)
                .FirstOrDefault();
        }

        internal static bool IsHorizontalStartTerminalLap(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly)
        {
            var startPlanarSetting = conversionResult?.StackMembers?
                .Where(stackMember => stackMember?.PlanarParts != null)
                .OrderBy(stackMember => stackMember.MemberIndex)
                .Select(stackMember => stackMember.PlanarParts.FirstOrDefault())
                .FirstOrDefault(planarSetting => planarSetting?.PointList?.Count >= 2);
            if (!TryGetBoundaryDirection(
                    startPlanarSetting,
                    true,
                    out var generatedStartDirection) ||
                startTerminalAssembly?.Members == null)
            {
                return false;
            }

            if (LinkageRouteDoubleStartLapGeometry.TryCreateLengthSeparatedCollectiveFixedBusbar(
                    startTerminalAssembly, out var lengthSeparatedFixedView) &&
                TryGetBoundaryDirection(lengthSeparatedFixedView, true, out var collectiveBoundaryDirection))
                return Math.Abs(generatedStartDirection.Dot(collectiveBoundaryDirection)) >= 1d - GeometryTolerance;

            var fixedStartDirections = startTerminalAssembly.Members
                .Select(terminalMember => terminalMember?.FixedBusbar)
                .Where(fixedBusbar => fixedBusbar?.PointList?.Count >= 2)
                .Select(fixedBusbar =>
                {
                    TryGetBoundaryDirection(
                        fixedBusbar,
                        true,
                        out var fixedBoundaryDirection);
                    return fixedBoundaryDirection;
                })
                .Where(fixedBoundaryDirection =>
                    fixedBoundaryDirection != null &&
                    fixedBoundaryDirection.Norm > GeometryTolerance)
                .ToList();
            if (fixedStartDirections.Count == 0)
            {
                return false;
            }

            // 多始端固定排可能存在不同的点序方向，不能只拿 Members[0]
            // 判断水平搭接；只要实际首段与任一固定始端排长方向平行，
            // 就应进入双始端连接排路径。
            return fixedStartDirections.Any(fixedBoundaryDirection =>
                Math.Abs(generatedStartDirection.Dot(
                    fixedBoundaryDirection.Normalized)) >=
                    1d - GeometryTolerance);
        }

        internal static bool TrySeparateSingleStartHorizontalLapLayers(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            double terminalAdditionalSpace,
            out bool geometryAdjusted,
            out string failureReason)
        {
            return TryAdjustSingleStartHorizontalLapLayersCore(
                conversionResult,
                startTerminalAssembly,
                terminalAdditionalSpace,
                true,
                out geometryAdjusted,
                out failureReason);
        }

        /// <summary>
        /// 按请求级单始端水平居中开关执行最终输出调整。
        /// 开关关闭时只处理真实正体积重叠，不把已经合法但未居中的单始端
        /// 叠排重新推到固定始端两侧，避免误套用多始端的整体层位规则。
        /// </summary>
        internal static bool TryAdjustSingleStartHorizontalLapLayersForOutput(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            double terminalAdditionalSpace,
            bool enforceCenteredClamp,
            out bool geometryAdjusted,
            out string failureReason)
        {
            return TryAdjustSingleStartHorizontalLapLayersCore(
                conversionResult,
                startTerminalAssembly,
                terminalAdditionalSpace,
                enforceCenteredClamp,
                out geometryAdjusted,
                out failureReason);
        }

        private static bool TryAdjustSingleStartHorizontalLapLayersCore(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            double terminalAdditionalSpace,
            bool enforceCenteredClamp,
            out bool geometryAdjusted,
            out string failureReason)
        {
            geometryAdjusted = false;
            failureReason = null;
            if (startTerminalAssembly?.Members?.Count != 1 ||
                !IsHorizontalStartTerminalLap(
                    conversionResult,
                    startTerminalAssembly))
            {
                return true;
            }

            var fixedStartSetting = startTerminalAssembly.Members[0]
                ?.FixedBusbar;
            if (fixedStartSetting?.PointList?.Count < 2 ||
                fixedStartSetting.BusBarThickness <= GeometryTolerance ||
                startTerminalAssembly.ThicknessDirection == null ||
                startTerminalAssembly.ThicknessDirection.Norm <=
                    GeometryTolerance)
            {
                failureReason = "单始端水平搭接无法取得固定始端排厚层。";
                return false;
            }

            var terminalThicknessDirection = startTerminalAssembly
                .ThicknessDirection.Normalized;
            var fixedCenterCoordinate = DotPoint(
                fixedStartSetting.PointList[0],
                terminalThicknessDirection);
            var fixedHalfThickness =
                fixedStartSetting.BusBarThickness / 2d;
            var startMembers = conversionResult.StartTerminalMemberIndices
                .Select(memberIndex => new
                {
                    MemberIndex = memberIndex,
                    StackMember = conversionResult.StackMembers.FirstOrDefault(
                        stackMember => stackMember?.MemberIndex == memberIndex)
                })
                .Where(member => member.StackMember?.PlanarParts != null)
                .Select(member => new
                {
                    member.MemberIndex,
                    member.StackMember,
                    Setting = member.StackMember.PlanarParts.FirstOrDefault(
                        planarPart => planarPart?.PointList?.Count >= 2)
                })
                .Where(member => member.Setting != null)
                .Select(member => new
                {
                    member.MemberIndex,
                    member.StackMember,
                    member.Setting,
                    CenterCoordinate = DotPoint(
                        member.Setting.PointList[0],
                        terminalThicknessDirection)
                })
                .ToList();
            if (startMembers.Count == 0)
            {
                return true;
            }

            if (enforceCenteredClamp && startMembers.Count == 2)
            {
                // 单始端配两根叠排时，固定始端必须位于两根生成排之间。
                // 先按固定排厚、各生成排厚和始端总附加间距建立两侧夹持
                // 层位；垫块由调用方在此后按两侧真实余隙重新生成。
                var orderedClampMembers = startMembers
                    .OrderBy(member => member.CenterCoordinate)
                    .ToList();
                for (var clampMemberIndex = 0;
                    clampMemberIndex < orderedClampMembers.Count;
                    clampMemberIndex++)
                {
                    var clampMember = orderedClampMembers[clampMemberIndex];
                    var clampSideSign = clampMemberIndex == 0 ? -1 : 1;
                    var targetCenterDistance =
                        (fixedStartSetting.BusBarThickness +
                         clampMember.Setting.BusBarThickness +
                         Math.Max(0d, terminalAdditionalSpace)) / 2d;
                    var targetCenterCoordinate = fixedCenterCoordinate +
                        clampSideSign * targetCenterDistance;
                    var signedDisplacement = targetCenterCoordinate -
                        DotPoint(
                            clampMember.Setting.PointList[0],
                            terminalThicknessDirection);
                    if (Math.Abs(signedDisplacement) <= GeometryTolerance)
                    {
                        continue;
                    }

                    if (!TryMoveStartTerminalLapChain(
                            conversionResult,
                            clampMember.StackMember,
                            0,
                            terminalThicknessDirection,
                            signedDisplacement,
                            out failureReason))
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "单始端第 {0} 层搭接排无法移动到夹持层位：{1}",
                            clampMember.MemberIndex + 1,
                            failureReason);
                        return false;
                    }

                    geometryAdjusted = true;
                }

                return true;
            }

            for (var sideSign = -1; sideSign <= 1; sideSign += 2)
            {
                var sideMembers = startMembers
                    .Where(member => ResolveSingleStartMemberSideSign(
                            conversionResult.StartStackPlacement,
                            member.MemberIndex,
                            member.CenterCoordinate - fixedCenterCoordinate) ==
                        sideSign)
                    .OrderBy(member => sideSign *
                        (member.CenterCoordinate - fixedCenterCoordinate))
                    .ToList();
                var occupiedOuterBoundary = fixedHalfThickness;
                var requiredGroupSideDisplacement = 0d;
                foreach (var sideMember in sideMembers)
                {
                    var memberHalfThickness =
                        sideMember.Setting.BusBarThickness / 2d;
                    var currentSideCoordinate = sideSign *
                        (DotPoint(
                            sideMember.Setting.PointList[0],
                            terminalThicknessDirection) -
                         fixedCenterCoordinate);
                    var minimumCenterCoordinate = occupiedOuterBoundary +
                        memberHalfThickness;
                    if (currentSideCoordinate + GeometryTolerance <
                        minimumCenterCoordinate)
                    {
                        requiredGroupSideDisplacement = Math.Max(
                            requiredGroupSideDisplacement,
                            minimumCenterCoordinate - currentSideCoordinate);
                    }

                    occupiedOuterBoundary = Math.Max(
                        occupiedOuterBoundary,
                        currentSideCoordinate + memberHalfThickness);
                }

                if (requiredGroupSideDisplacement <= GeometryTolerance)
                {
                    continue;
                }

                // 单始端同侧叠排保持既有相对层距整体联动。只移动发生
                // 穿入的单层会让同组 I 排出现 I/N 混合拓扑，也会破坏
                // 后续中间搭接对应关系。
                var signedGroupDisplacement = sideSign *
                    requiredGroupSideDisplacement;
                foreach (var sideMember in sideMembers)
                {
                        if (!TryMoveStartTerminalLapChain(
                                conversionResult,
                                sideMember.StackMember,
                                0,
                                terminalThicknessDirection,
                                signedGroupDisplacement,
                                out failureReason))
                        {
                            failureReason = string.Format(
                                CultureInfo.InvariantCulture,
                                "单始端第 {0} 层搭接排无法消除与固定始端的重叠：{1}",
                                sideMember.MemberIndex + 1,
                                failureReason);
                            return false;
                        }

                        geometryAdjusted = true;
                }
            }

            return true;
        }

        internal static bool TryValidateSingleStartHorizontalLapLayers(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            out string failureReason)
        {
            return TryValidateSingleStartHorizontalLapLayersCore(
                conversionResult,
                startTerminalAssembly,
                true,
                out failureReason);
        }

        /// <summary>
        /// 最终输出阶段按请求级单始端水平居中开关复核搭接层位。
        /// 关闭居中时仍检查正体积重叠，但不要求两根生成排必须分处固定
        /// 始端两侧；这样自由水平搭接不会被错误按夹持方案淘汰。
        /// </summary>
        internal static bool TryValidateSingleStartHorizontalLapLayersForOutput(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            bool enforceCenteredClamp,
            out string failureReason,
            double acceptanceTolerance = GeometryTolerance)
        {
            return TryValidateSingleStartHorizontalLapLayersCore(
                conversionResult,
                startTerminalAssembly,
                enforceCenteredClamp,
                out failureReason,
                acceptanceTolerance);
        }

        private static bool TryValidateSingleStartHorizontalLapLayersCore(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly,
            bool enforceCenteredClamp,
            out string failureReason,
            double acceptanceTolerance = GeometryTolerance)
        {
            failureReason = null;
            if (startTerminalAssembly?.Members?.Count != 1 ||
                !IsHorizontalStartTerminalLap(
                    conversionResult,
                    startTerminalAssembly))
            {
                return true;
            }

            var fixedStartSetting = startTerminalAssembly.Members[0]
                ?.FixedBusbar;
            if (fixedStartSetting?.PointList?.Count < 2 ||
                startTerminalAssembly.ThicknessDirection == null ||
                startTerminalAssembly.ThicknessDirection.Norm <=
                    GeometryTolerance)
            {
                return true;
            }

            var terminalThicknessDirection = startTerminalAssembly
                .ThicknessDirection.Normalized;
            var fixedCenterCoordinate = DotPoint(
                fixedStartSetting.PointList[0],
                terminalThicknessDirection);
            var generatedSideSigns = new HashSet<int>();
            foreach (var memberIndex in conversionResult
                .StartTerminalMemberIndices)
            {
                var generatedSetting = conversionResult.StackMembers
                    .FirstOrDefault(stackMember => stackMember?.MemberIndex ==
                        memberIndex)
                    ?.PlanarParts?
                    .FirstOrDefault(planarPart =>
                        planarPart?.PointList?.Count >= 2);
                if (generatedSetting == null)
                {
                    continue;
                }

                var generatedCenterCoordinate = DotPoint(
                    generatedSetting.PointList[0],
                    terminalThicknessDirection);
                generatedSideSigns.Add(Math.Sign(
                    generatedCenterCoordinate - fixedCenterCoordinate));
                var requiredCenterDistance =
                    (fixedStartSetting.BusBarThickness +
                     generatedSetting.BusBarThickness) / 2d;
                var overlapThickness = requiredCenterDistance - Math.Abs(
                    generatedCenterCoordinate - fixedCenterCoordinate);
                if (overlapThickness > acceptanceTolerance)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "单始端第 {0} 层搭接排与固定始端仍有 {1:F3} mm 正体积重叠。",
                        memberIndex + 1,
                        overlapThickness);
                    return false;
                }
            }

            if (enforceCenteredClamp &&
                conversionResult.StartTerminalMemberIndices.Count == 2 &&
                (!generatedSideSigns.Contains(-1) ||
                 !generatedSideSigns.Contains(1)))
            {
                failureReason =
                    "单始端配两根叠排时，固定始端没有被两侧生成排夹持。";
                return false;
            }

            return true;
        }

        private static int ResolveSingleStartMemberSideSign(
            LinkageRouteStackTerminalPlacement startStackPlacement,
            int memberIndex,
            double currentRelativeCoordinate)
        {
            if (Math.Abs(currentRelativeCoordinate) > GeometryTolerance)
            {
                return currentRelativeCoordinate < 0d ? -1 : 1;
            }

            if (startStackPlacement?.GeneratedMemberOffsets != null &&
                memberIndex >= 0 &&
                memberIndex < startStackPlacement.GeneratedMemberOffsets.Count &&
                Math.Abs(startStackPlacement.GeneratedMemberOffsets[memberIndex]) >
                    GeometryTolerance)
            {
                return startStackPlacement.GeneratedMemberOffsets[memberIndex] <
                    0d
                    ? -1
                    : 1;
            }

            return memberIndex % 2 == 0 ? -1 : 1;
        }

        private static bool IsStartOutputIBar(
            LinkageRouteCenterlineConversionResult conversionResult)
        {
            if (conversionResult?.CenterlineParts == null ||
                conversionResult.CenterlineParts.Count != 1 ||
                conversionResult.StackMembers == null ||
                conversionResult.StackMembers.Count == 0)
            {
                return false;
            }

            return conversionResult.StackMembers.All(stackMember =>
                stackMember?.PlanarParts?.Count == 1 &&
                stackMember.PlanarParts[0]?.PointList?.Count == 2);
        }

        /// <summary>
        /// 方案已经选定后，按独立双始端连接排的实际宽度修正主路径首段端点。
        /// 连接排所在的始端搭接排成员确定有效端面后，同组其他始端搭接排
        /// 成员必须沿同一首段方向对齐到这个端面，保证每根排具有相同的
        /// 垂直搭接高度；该修正只改变最终输出几何，不参与候选搜索、排序
        /// 或淘汰。
        /// </summary>
        internal static void ApplyOutputTerminalLapGeometry(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly)
        {
            if (conversionResult?.Accessories == null ||
                conversionResult.StackMembers == null)
            {
                return;
            }

            var bridgeAccessories = conversionResult.Accessories
                .Where(routeAccessory =>
                    routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                    routeAccessory.Setting?.PointList != null &&
                    routeAccessory.Setting.PointList.Count >= 2);
            if (!bridgeAccessories.Any())
            {
                var isMergedPhysicalBridgeHorizontalLap =
                    IsMergedPhysicalBridgeHorizontalLap(conversionResult);
                if ((conversionResult.DoubleStartPhysicalBridgeSectionIndices
                        .Contains(0) || isMergedPhysicalBridgeHorizontalLap) &&
                    (startTerminalAssembly == null ||
                        IsHorizontalStartTerminalLap(
                            conversionResult,
                            startTerminalAssembly)) &&
                    isMergedPhysicalBridgeHorizontalLap)
                {
                    ApplyPhysicalBridgeStartLapGeometry(
                        conversionResult,
                        startTerminalAssembly);
                }
                return;
            }

            PlanarBendBusbarSetting collectiveFixedStartBusbar = null;
            if (startTerminalAssembly != null)
            {
                TryGetConnectionFixedBusbar(
                    startTerminalAssembly,
                    out collectiveFixedStartBusbar);
            }

            foreach (var bridgeAccessory in bridgeAccessories)
            {
                if (bridgeAccessory.RelatedGeneratedMemberIndex < 0)
                {
                    continue;
                }

                var bridgeGeneratedMember = conversionResult.StackMembers
                    .FirstOrDefault(stackMember =>
                        stackMember?.MemberIndex ==
                        bridgeAccessory.RelatedGeneratedMemberIndex);
                var bridgeGeneratedSetting = bridgeGeneratedMember?.PlanarParts
                    ?.FirstOrDefault(planarPart => planarPart?.PointList != null &&
                        planarPart.PointList.Count >= 2);
                var bridgeSetting = bridgeAccessory.Setting;
                if (bridgeGeneratedSetting == null || bridgeSetting == null ||
                    bridgeSetting.BusBarWidth <= 0d)
                {
                    continue;
                }

                // 单始端连接排只覆盖固定始端排宽，不套用双始端向外延伸。
                if (startTerminalAssembly?.Members?.Count == 1)
                {
                    bridgeGeneratedSetting.StartLapWidth = bridgeSetting.BusBarWidth;
                    continue;
                }
                if (!(collectiveFixedStartBusbar != null
                        ? TrySetBridgeLengthToRequiredLapCoverage(
                            bridgeSetting,
                            collectiveFixedStartBusbar,
                            bridgeGeneratedSetting)
                        : TryExtendBridgeLengthToGeneratedLap(
                            bridgeSetting,
                            bridgeGeneratedSetting)))
                {
                    conversionResult.Diagnostics.Add(
                        "双始端连接排无法沿排长方向覆盖外侧生成排搭接区域，已保留原几何。");
                    continue;
                }

                // The adjacent cabinet-top member may use a different
                // material width, but its start lap is against the bridge.
                // Keep the effective lap width in the generated setting so
                // geometry, diagnostics and exported JSON agree.
                bridgeGeneratedSetting.StartLapWidth =
                    bridgeSetting.BusBarWidth;

            }
        }

        /// <summary>
        /// 双始端连接排保持中心、方向和厚向层位不变，只沿自身排长方向延伸到
        /// 能覆盖目标生成排实际宽度的最短位置。既有两端只允许向外扩展，不回缩。
        /// </summary>
        private static bool TryExtendBridgeLengthToGeneratedLap(
            PlanarBendBusbarSetting bridgeSetting,
            PlanarBendBusbarSetting generatedSetting)
        {
            if (bridgeSetting?.PointList == null ||
                bridgeSetting.PointList.Count < 2 ||
                generatedSetting?.PointList == null ||
                generatedSetting.PointList.Count < 2 ||
                generatedSetting.BusBarWidth <= GeometryTolerance)
            {
                return false;
            }

            var bridgeStartPoint = bridgeSetting.PointList[0];
            var bridgeEndPoint = bridgeSetting.PointList[
                bridgeSetting.PointList.Count - 1];
            var bridgeLengthVector = new Vector3d(
                bridgeStartPoint,
                bridgeEndPoint);
            if (bridgeLengthVector.Norm <= GeometryTolerance)
            {
                return false;
            }

            var bridgeLengthDirection = bridgeLengthVector.Normalized;
            var bridgeLength = bridgeLengthVector.Norm;
            var generatedWidthProjectionHalfLength =
                generatedSetting.WidthVector == null ||
                generatedSetting.WidthVector.Norm <= GeometryTolerance
                    ? 0d
                    : Math.Abs(generatedSetting.WidthVector.Normalized.Dot(
                        bridgeLengthDirection)) *
                      generatedSetting.BusBarWidth / 2d;
            var generatedBoundaryCoordinate = new Vector3d(
                bridgeStartPoint,
                generatedSetting.PointList[0]).Dot(bridgeLengthDirection);
            var requiredMinimumCoordinate =
                generatedBoundaryCoordinate -
                generatedWidthProjectionHalfLength;
            var requiredMaximumCoordinate =
                generatedBoundaryCoordinate +
                generatedWidthProjectionHalfLength;

            if (requiredMinimumCoordinate < -GeometryTolerance)
            {
                bridgeSetting.PointList[0] = LinkageRouteSolidGeometry.Translate(
                    bridgeStartPoint,
                    bridgeLengthDirection,
                    requiredMinimumCoordinate);
            }
            if (requiredMaximumCoordinate > bridgeLength + GeometryTolerance)
            {
                bridgeSetting.PointList[bridgeSetting.PointList.Count - 1] =
                    LinkageRouteSolidGeometry.Translate(
                        bridgeStartPoint,
                        bridgeLengthDirection,
                        requiredMaximumCoordinate);
            }

            return true;
        }

        private static bool TrySetBridgeLengthToRequiredLapCoverage(
            PlanarBendBusbarSetting bridgeSetting,
            PlanarBendBusbarSetting collectiveFixedBusbar,
            PlanarBendBusbarSetting generatedSetting)
        {
            if (!TryGetRequiredBridgeLengthCoordinates(
                    bridgeSetting,
                    collectiveFixedBusbar,
                    generatedSetting,
                    out var bridgeLengthDirection,
                    out var requiredMinimumCoordinate,
                    out var requiredMaximumCoordinate))
            {
                return false;
            }

            var firstPointIndex = 0;
            var lastPointIndex = bridgeSetting.PointList.Count - 1;
            var firstCoordinate = DotPoint(
                bridgeSetting.PointList[firstPointIndex],
                bridgeLengthDirection);
            var lastCoordinate = DotPoint(
                bridgeSetting.PointList[lastPointIndex],
                bridgeLengthDirection);
            if (firstCoordinate <= lastCoordinate)
            {
                bridgeSetting.PointList[firstPointIndex] =
                    LinkageRouteSolidGeometry.Translate(
                        bridgeSetting.PointList[firstPointIndex],
                        bridgeLengthDirection,
                        requiredMinimumCoordinate - firstCoordinate);
                bridgeSetting.PointList[lastPointIndex] =
                    LinkageRouteSolidGeometry.Translate(
                        bridgeSetting.PointList[lastPointIndex],
                        bridgeLengthDirection,
                        requiredMaximumCoordinate - lastCoordinate);
            }
            else
            {
                bridgeSetting.PointList[firstPointIndex] =
                    LinkageRouteSolidGeometry.Translate(
                        bridgeSetting.PointList[firstPointIndex],
                        bridgeLengthDirection,
                        requiredMaximumCoordinate - firstCoordinate);
                bridgeSetting.PointList[lastPointIndex] =
                    LinkageRouteSolidGeometry.Translate(
                        bridgeSetting.PointList[lastPointIndex],
                        bridgeLengthDirection,
                        requiredMinimumCoordinate - lastCoordinate);
            }

            return true;
        }

        private static bool TryGetRequiredBridgeLengthCoordinates(
            PlanarBendBusbarSetting bridgeSetting,
            PlanarBendBusbarSetting collectiveFixedBusbar,
            PlanarBendBusbarSetting generatedSetting,
            out Vector3d bridgeLengthDirection,
            out double requiredMinimumCoordinate,
            out double requiredMaximumCoordinate)
        {
            bridgeLengthDirection = null;
            requiredMinimumCoordinate = 0d;
            requiredMaximumCoordinate = 0d;
            if (!IsUsableConnectionSetting(bridgeSetting) ||
                collectiveFixedBusbar?.PointList?.Count < 2 ||
                generatedSetting?.PointList?.Count < 2)
            {
                return false;
            }

            var bridgeDirectionVector = new Vector3d(
                bridgeSetting.PointList[0],
                bridgeSetting.PointList[bridgeSetting.PointList.Count - 1]);
            if (bridgeDirectionVector.Norm <= GeometryTolerance)
            {
                return false;
            }

            var normalizedBridgeLengthDirection =
                bridgeDirectionVector.Normalized;
            bridgeLengthDirection = normalizedBridgeLengthDirection;
            var fixedCenterCoordinate = collectiveFixedBusbar.PointList
                .Average(point => DotPoint(
                    point,
                    normalizedBridgeLengthDirection));
            var fixedHalfWidthProjection =
                collectiveFixedBusbar.WidthVector == null ||
                collectiveFixedBusbar.WidthVector.Norm <= GeometryTolerance
                    ? collectiveFixedBusbar.BusBarWidth / 2d
                    : Math.Abs(collectiveFixedBusbar.WidthVector.Normalized.Dot(
                        normalizedBridgeLengthDirection)) *
                      collectiveFixedBusbar.BusBarWidth / 2d;
            var generatedBoundaryCoordinate = DotPoint(
                generatedSetting.PointList[0],
                normalizedBridgeLengthDirection);
            var generatedHalfWidthProjection =
                generatedSetting.WidthVector == null ||
                generatedSetting.WidthVector.Norm <= GeometryTolerance
                    ? 0d
                    : Math.Abs(generatedSetting.WidthVector.Normalized.Dot(
                        normalizedBridgeLengthDirection)) *
                      generatedSetting.BusBarWidth / 2d;
            requiredMinimumCoordinate = Math.Min(
                fixedCenterCoordinate - fixedHalfWidthProjection,
                generatedBoundaryCoordinate -
                    generatedHalfWidthProjection);
            requiredMaximumCoordinate = Math.Max(
                fixedCenterCoordinate + fixedHalfWidthProjection,
                generatedBoundaryCoordinate +
                    generatedHalfWidthProjection);
            return true;
        }

        /// <summary>
        /// 合并到首段实体的双始端连接排只在水平搭接时统一首段长度。
        /// 垂直搭接的首段与下一段排长方向不平行，必须保留搜索阶段确定的
        /// 交叉搭接位置，不能调用水平连接排的端点传播逻辑。
        /// </summary>
        private static bool IsMergedPhysicalBridgeHorizontalLap(
            LinkageRouteCenterlineConversionResult conversionResult)
        {
            // 普通拆 U 的 L+L 也可能在接口处平行，不能仅凭方向认作连接桥。
            // 必须具有首个物理段的桥接身份，才可把后续排移到桥排包络外侧。
            if (conversionResult?.DoubleStartPhysicalBridgeSectionIndices
                    .Contains(0) != true ||
                conversionResult.StackMembers == null ||
                conversionResult.StartTerminalMemberIndices == null ||
                conversionResult.StartTerminalMemberIndices.Count == 0)
            {
                return false;
            }

            var mergedBridgeStartMembers = conversionResult.StackMembers
                .Where(stackMember => stackMember != null &&
                    conversionResult.StartTerminalMemberIndices.Contains(
                        stackMember.MemberIndex))
                .ToList();
            if (mergedBridgeStartMembers.Count !=
                    conversionResult.StartTerminalMemberIndices.Count)
            {
                return false;
            }

            return mergedBridgeStartMembers.All(stackMember =>
                AreMergedBridgeFollowingSegmentsParallel(
                    stackMember.PlanarParts?.Count > 0
                        ? stackMember.PlanarParts[0]
                        : null,
                    stackMember.PlanarParts?.Count > 1
                        ? stackMember.PlanarParts[1]
                        : null));
        }

        private static bool AreMergedBridgeFollowingSegmentsParallel(
            PlanarBendBusbarSetting bridgeSetting,
            PlanarBendBusbarSetting followingSetting)
        {
            if (bridgeSetting?.PointList == null ||
                bridgeSetting.PointList.Count < 2 ||
                followingSetting?.PointList == null ||
                followingSetting.PointList.Count < 2)
            {
                return false;
            }

            var bridgeTerminalDirection = new Vector3d(
                bridgeSetting.PointList[bridgeSetting.PointList.Count - 2],
                bridgeSetting.PointList[bridgeSetting.PointList.Count - 1]);
            var followingStartDirection = new Vector3d(
                followingSetting.PointList[0],
                followingSetting.PointList[1]);
            if (bridgeTerminalDirection.Norm <= GeometryTolerance ||
                followingStartDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            return Math.Abs(bridgeTerminalDirection.Normalized.Dot(
                followingStartDirection.Normalized)) >=
                1d - GeometryTolerance;
        }

        /// <summary>
        /// 方案已经完成候选搜索并进入最终异规格实体化后，修正双始端连接排
        /// 关联的始端物理段厚向层位。此入口不能在候选转换阶段调用，否则
        /// 会把输出几何修正错误地带入绝缘筛选和方案淘汰。
        /// </summary>
        internal static void ApplyOutputTerminalBridgeLayerAlignment(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly fixedStartTerminal = null)
        {
            if (conversionResult?.Accessories == null ||
                conversionResult.StackMembers == null)
            {
                return;
            }

            var hasHorizontalMultiStartBridge = conversionResult.Accessories
                .Any(routeAccessory => routeAccessory?.Kind ==
                    LinkageRouteAccessoryKind.DoubleStartHorizontalBridge);
            if (!hasHorizontalMultiStartBridge)
            {
                if (IsMergedPhysicalBridgeHorizontalLap(conversionResult))
                {
                    var mergedBridgeSettings = conversionResult
                        .StartTerminalMemberIndices
                        .Select(memberIndex => new
                        {
                            MemberIndex = memberIndex,
                            Setting = conversionResult.StackMembers
                                .FirstOrDefault(member =>
                                    member?.MemberIndex == memberIndex)
                                ?.PlanarParts?
                                .FirstOrDefault(setting =>
                                    setting?.PointList?.Count >= 2)
                        })
                        .Where(member => member.Setting != null)
                        .ToDictionary(
                            member => member.MemberIndex,
                            member => member.Setting);
                    AlignMergedPhysicalBridgeFollowingLayers(
                        conversionResult,
                        mergedBridgeSettings,
                        null);
                }
                return;
            }

            // 连接排保持固定层位；先恢复关联生成排的实际厚面贴合。
            // 非 I 排只调始端腿，I 排整体移动并沿物理搭接链传递位移，
            // 不能把“不能转 N”误当作“无需移动”。中间接口和最终垫块随后处理。
            if (UsesFixedConnectionBridgeLayerPolicy())
            {
                AlignFlexibleGeneratedLapSegmentsToFixedBridges(
                    conversionResult,
                    fixedStartTerminal);
                var fixedBridgeMemberIndices = conversionResult.Accessories
                    .Where(routeAccessory => routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.DoubleStartHorizontalBridge)
                    .Select(routeAccessory =>
                        routeAccessory.RelatedGeneratedMemberIndex)
                    .ToHashSet();
                AlignFollowingStartMemberLayersOutsideBridgePack(
                    conversionResult,
                    fixedBridgeMemberIndices);
                return;
            }

            // 水平多始端连接排所在端采用铜排逐层直接贴合，不允许再保留
            // 任何始端垫块。先清除垫块，再以连接排为固定基准排列主路径。
            for (var accessoryIndex = conversionResult.Accessories.Count - 1;
                accessoryIndex >= 0;
                accessoryIndex--)
            {
                var routeAccessory = conversionResult.Accessories[accessoryIndex];
                if (routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.SupportBlock &&
                    routeAccessory.TerminalSide ==
                        LinkageRouteTerminalSide.Start &&
                    string.IsNullOrWhiteSpace(routeAccessory.SupportBlockKey))
                {
                    conversionResult.Accessories.RemoveAt(accessoryIndex);
                }
            }

            var alignedGeneratedMemberIndices = new HashSet<int>();
            var movedFollowingGeneratedMemberIndices = new HashSet<int>();
            var outputBridgeAccessories = conversionResult.Accessories
                .Where(routeAccessory =>
                    routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                    routeAccessory.Setting?.PointList != null &&
                    routeAccessory.Setting.PointList.Count >= 2 &&
                    routeAccessory.Setting.StartBusbarVector != null &&
                    routeAccessory.Setting.StartBusbarVector.Norm >
                        GeometryTolerance)
                .ToList();
            var bridgeMemberIndices = outputBridgeAccessories
                .Select(routeAccessory => routeAccessory.RelatedGeneratedMemberIndex)
                .ToHashSet();
            var bridgePackThicknessDirection = outputBridgeAccessories.Count > 0
                ? outputBridgeAccessories[0].Setting.StartBusbarVector.Normalized
                : null;
            var bridgePackCenterCoordinate = bridgePackThicknessDirection == null
                ? 0d
                : (outputBridgeAccessories.Min(routeAccessory => DotPoint(
                        routeAccessory.Setting.PointList[0],
                        bridgePackThicknessDirection)) +
                   outputBridgeAccessories.Max(routeAccessory => DotPoint(
                        routeAccessory.Setting.PointList[0],
                        bridgePackThicknessDirection))) / 2d;
            foreach (var bridgeAccessory in outputBridgeAccessories.Where(
                routeAccessory =>
                    routeAccessory.RelatedGeneratedMemberIndex >= 0))
            {
                if (!alignedGeneratedMemberIndices.Add(
                        bridgeAccessory.RelatedGeneratedMemberIndex))
                {
                    continue;
                }

                var bridgeGeneratedMember = conversionResult.StackMembers
                    .FirstOrDefault(stackMember =>
                        stackMember?.MemberIndex ==
                        bridgeAccessory.RelatedGeneratedMemberIndex);
                var bridgeGeneratedSetting = bridgeGeneratedMember?.PlanarParts
                    ?.FirstOrDefault(planarPart =>
                        planarPart?.PointList != null &&
                        planarPart.PointList.Count >= 2);
                if (bridgeGeneratedSetting == null)
                {
                    continue;
                }

                var generatedBeforePoint = bridgeGeneratedSetting.PointList[0];
                var bridgeSetting = bridgeAccessory.Setting;

                var preferredBridgeSideSign = bridgePackThicknessDirection == null
                    ? 0
                    : Math.Sign(DotPoint(
                            bridgeAccessory.Setting.PointList[0],
                            bridgePackThicknessDirection) -
                        bridgePackCenterCoordinate);
                if (!TryAlignBridgeGeneratedLayerToAccessory(
                        conversionResult,
                        bridgeGeneratedMember,
                        bridgeAccessory.Setting,
                        preferredBridgeSideSign,
                        out var bridgeLayerAlignmentFailureReason) &&
                    !string.IsNullOrWhiteSpace(bridgeLayerAlignmentFailureReason))
                {
                    conversionResult.Diagnostics.Add(
                        bridgeLayerAlignmentFailureReason);
                }
                else
                {
                    var generatedMemberSetting = bridgeGeneratedSetting;
                    var generatedAfterPoint = generatedMemberSetting.PointList[0];
                    var terminalThicknessDirection = bridgeSetting.StartBusbarVector
                        .Normalized;
                    var generatedMemberDisplacement = new Vector3d(
                        generatedBeforePoint,
                        generatedAfterPoint);
                    if (generatedMemberDisplacement.Norm > GeometryTolerance)
                    {
                        var generatedMemberOriginalCoordinate = new Vector3d(
                            bridgeSetting.PointList[0], generatedBeforePoint)
                            .Dot(terminalThicknessDirection);
                        var signedLayerDisplacement = generatedMemberDisplacement
                            .Dot(terminalThicknessDirection);
                        foreach (var followingMemberIndex in conversionResult
                            .StartTerminalMemberIndices
                            .Where(memberIndex =>
                                !bridgeMemberIndices.Contains(memberIndex) &&
                                !movedFollowingGeneratedMemberIndices.Contains(
                                    memberIndex)))
                        {
                            var followingMember = conversionResult.StackMembers
                                .FirstOrDefault(stackMember =>
                                    stackMember?.MemberIndex ==
                                        followingMemberIndex);
                            var followingSetting = followingMember?.PlanarParts?
                                .FirstOrDefault(planarPart => planarPart?.PointList != null &&
                                    planarPart.PointList.Count >= 2);
                            if (followingSetting == null)
                            {
                                continue;
                            }

                            var followingMemberCoordinate = new Vector3d(
                                bridgeSetting.PointList[0],
                                followingSetting.PointList[0])
                                .Dot(terminalThicknessDirection);
                            var isFollowingMovedMember =
                                signedLayerDisplacement > GeometryTolerance
                                    ? followingMemberCoordinate >=
                                        generatedMemberOriginalCoordinate -
                                        GeometryTolerance
                                    : followingMemberCoordinate <=
                                        generatedMemberOriginalCoordinate +
                                        GeometryTolerance;
                            if (isFollowingMovedMember)
                            {
                                if (TryMoveStartTerminalLapChain(
                                        conversionResult,
                                        followingMember,
                                        0,
                                        terminalThicknessDirection,
                                        signedLayerDisplacement,
                                        out var followingMoveFailureReason))
                                {
                                    movedFollowingGeneratedMemberIndices.Add(
                                        followingMemberIndex);
                                }
                                else if (!string.IsNullOrWhiteSpace(
                                             followingMoveFailureReason) &&
                                    !conversionResult.Diagnostics.Contains(
                                        followingMoveFailureReason))
                                {
                                    conversionResult.Diagnostics.Add(
                                        followingMoveFailureReason);
                                }
                            }
                        }
                    }
                }
            }

            // 连接排关联成员移动后，原本位于其外侧的叠排也必须一起向外
            // 传递位移。这里按连接排整体包络重新排列两侧非连接成员，
            // 防止只修正直接搭接排后又与相邻外侧排发生正体积重叠。
            AlignFollowingStartMemberLayersOutsideBridgePack(
                conversionResult,
                bridgeMemberIndices);
        }

        private static void AlignFlexibleGeneratedLapSegmentsToFixedBridges(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly fixedStartTerminal)
        {
            var bridgeAccessories = conversionResult.Accessories
                .Where(routeAccessory =>
                    routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                    routeAccessory.RelatedGeneratedMemberIndex >= 0 &&
                    routeAccessory.Setting?.PointList?.Count >= 2 &&
                    routeAccessory.Setting.StartBusbarVector != null &&
                    routeAccessory.Setting.StartBusbarVector.Norm >
                        GeometryTolerance)
                .ToList();
            if (bridgeAccessories.Count == 0)
            {
                return;
            }

            var bridgePackThicknessDirection = bridgeAccessories[0].Setting
                .StartBusbarVector.Normalized;
            var bridgePackCenterCoordinate =
                (bridgeAccessories.Min(routeAccessory => DotPoint(
                     routeAccessory.Setting.PointList[0],
                     bridgePackThicknessDirection)) +
                 bridgeAccessories.Max(routeAccessory => DotPoint(
                     routeAccessory.Setting.PointList[0],
                     bridgePackThicknessDirection))) / 2d;
            PlanarBendBusbarSetting primaryBridgeGeneratedSetting = null;
            foreach (var bridgeAccessory in bridgeAccessories.OrderBy(
                routeAccessory => routeAccessory.RelatedGeneratedMemberIndex))
            {
                var generatedMember = conversionResult.StackMembers
                    .FirstOrDefault(stackMember =>
                        stackMember?.MemberIndex ==
                            bridgeAccessory.RelatedGeneratedMemberIndex);
                if (generatedMember == null)
                {
                    continue;
                }

                var preferredBridgeSideSign = Math.Sign(DotPoint(
                        bridgeAccessory.Setting.PointList[0],
                        bridgePackThicknessDirection) -
                    bridgePackCenterCoordinate);
                if (preferredBridgeSideSign == 0 &&
                    fixedStartTerminal?.Members?.Count > 0)
                {
                    var fixedMemberCoordinates = fixedStartTerminal.Members
                        .Where(member => member?.FixedBusbar?.PointList?.Count > 0)
                        .Select(member => DotPoint(
                            member.FixedBusbar.PointList[0],
                            bridgePackThicknessDirection))
                        .ToList();
                    if (fixedMemberCoordinates.Count > 0)
                    {
                        preferredBridgeSideSign = Math.Sign(DotPoint(
                                bridgeAccessory.Setting.PointList[0],
                                bridgePackThicknessDirection) -
                            fixedMemberCoordinates.Average());
                    }
                }
                if (preferredBridgeSideSign == 0)
                {
                    preferredBridgeSideSign = conversionResult
                        .StartStackPlacement?
                        .DoubleStartHorizontalBridgePlacements?
                        .FirstOrDefault(placement => placement != null &&
                            placement.GeneratedMemberIndex ==
                                bridgeAccessory.RelatedGeneratedMemberIndex)
                        ?.SideSign ?? 0;
                }
                if (!TryAlignBridgeGeneratedLayerToAccessory(
                        conversionResult,
                        generatedMember,
                        bridgeAccessory.Setting,
                        preferredBridgeSideSign,
                        out var failureReason) &&
                    !string.IsNullOrWhiteSpace(failureReason) &&
                    !conversionResult.Diagnostics.Contains(failureReason))
                {
                    conversionResult.Diagnostics.Add(failureReason);
                }
                else if (primaryBridgeGeneratedSetting == null)
                {
                    primaryBridgeGeneratedSetting = generatedMember.PlanarParts
                        .FirstOrDefault(planarPart =>
                            planarPart?.PointList?.Count >= 2);
                }
            }

            if (primaryBridgeGeneratedSetting != null &&
                !TryAlignStartMembersToBridgeEndpoint(
                    conversionResult,
                    primaryBridgeGeneratedSetting,
                    out var endpointAlignmentFailureReason) &&
                !string.IsNullOrWhiteSpace(endpointAlignmentFailureReason) &&
                !conversionResult.Diagnostics.Contains(
                    endpointAlignmentFailureReason))
            {
                conversionResult.Diagnostics.Add(
                    endpointAlignmentFailureReason);
            }
        }

        internal static bool TryValidateOutputTerminalBridgeLayerAlignment(
            LinkageRouteCenterlineConversionResult conversionResult,
            out string failureReason,
            double acceptanceTolerance = GeometryTolerance)
        {
            failureReason = null;
            var bridgeAccessories = (conversionResult?.Accessories ??
                    new List<LinkageRouteAccessory>())
                .Where(routeAccessory => routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                    routeAccessory.Setting?.PointList?.Count >= 2 &&
                    routeAccessory.Setting.StartBusbarVector != null &&
                    routeAccessory.Setting.StartBusbarVector.Norm >
                        GeometryTolerance)
                .ToList();
            if (bridgeAccessories.Count == 0)
            {
                return true;
            }

            var terminalThicknessDirection = bridgeAccessories[0].Setting
                .StartBusbarVector.Normalized;
            var bridgeMemberIndices = bridgeAccessories
                .Where(routeAccessory =>
                    routeAccessory.RelatedGeneratedMemberIndex >= 0)
                .Select(routeAccessory =>
                    routeAccessory.RelatedGeneratedMemberIndex)
                .ToHashSet();
            foreach (var bridgeMemberIndex in bridgeMemberIndices)
            {
                var bridgeGeneratedSetting = conversionResult.StackMembers?
                    .FirstOrDefault(stackMember => stackMember?.MemberIndex ==
                        bridgeMemberIndex)
                    ?.PlanarParts?
                    .FirstOrDefault(planarPart =>
                        planarPart?.PointList?.Count >= 2);
                var relatedBridgeAccessories = bridgeAccessories.Where(
                        routeAccessory =>
                            routeAccessory.RelatedGeneratedMemberIndex ==
                                bridgeMemberIndex)
                    .ToList();
                if (bridgeGeneratedSetting == null ||
                    relatedBridgeAccessories.Count == 0)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0} 层始端搭接排缺少对应的多始端连接排实体。",
                        bridgeMemberIndex + 1);
                    return false;
                }

                var generatedCenterCoordinate = DotPoint(
                    bridgeGeneratedSetting.PointList[0],
                    terminalThicknessDirection);
                var hasExactBridgeContact = relatedBridgeAccessories.Any(
                    routeAccessory =>
                    {
                        var bridgeCenterCoordinate = DotPoint(
                            routeAccessory.Setting.PointList[0],
                            terminalThicknessDirection);
                        var requiredCenterDistance =
                            (bridgeGeneratedSetting.BusBarThickness +
                             routeAccessory.Setting.BusBarThickness) / 2d;
                        return Math.Abs(
                            Math.Abs(generatedCenterCoordinate -
                                bridgeCenterCoordinate) -
                            requiredCenterDistance) <= acceptanceTolerance;
                    });
                var hasBridgeGapSupport = conversionResult.Accessories.Any(
                    routeAccessory =>
                        routeAccessory?.Kind ==
                            LinkageRouteAccessoryKind.SupportBlock &&
                        routeAccessory.TerminalSide ==
                            LinkageRouteTerminalSide.Start &&
                        routeAccessory.RelatedGeneratedMemberIndex ==
                            bridgeMemberIndex);
                if (!hasExactBridgeContact && !hasBridgeGapSupport)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "第 {0} 层始端搭接排与多始端连接排之间既未贴合也无最终垫块。",
                        bridgeMemberIndex + 1);
                    return false;
                }
            }

            // 连接排外侧生成排不再强制按连接排包络逐层紧贴；普通 I 排保持
            // 原层位，最终厚向间隙由上面的垫块门禁和内部搭接闭合校验负责。
            if (UsesFixedConnectionBridgeLayerPolicy())
            {
                return true;
            }

            var bridgeIntervals = bridgeAccessories.Select(routeAccessory =>
                new
                {
                    CenterCoordinate = DotPoint(
                        routeAccessory.Setting.PointList[0],
                        terminalThicknessDirection),
                    HalfThickness = routeAccessory.Setting.BusBarThickness / 2d,
                    routeAccessory.RelatedGeneratedMemberIndex
                })
                .Where(bridgeInterval =>
                    bridgeInterval.HalfThickness > GeometryTolerance)
                .ToList();
            if (bridgeIntervals.Count == 0)
            {
                return true;
            }

            var bridgePackMinimumBoundary = bridgeIntervals.Min(
                bridgeInterval => bridgeInterval.CenterCoordinate -
                    bridgeInterval.HalfThickness);
            var bridgePackMaximumBoundary = bridgeIntervals.Max(
                bridgeInterval => bridgeInterval.CenterCoordinate +
                    bridgeInterval.HalfThickness);
            var bridgePackCenterCoordinate =
                (bridgePackMinimumBoundary + bridgePackMaximumBoundary) / 2d;
            for (var sideSign = -1; sideSign <= 1; sideSign += 2)
            {
                var sideBridgeMemberIndices = bridgeIntervals
                    .Where(bridgeInterval =>
                        Math.Sign(bridgeInterval.CenterCoordinate -
                            bridgePackCenterCoordinate) == sideSign)
                    .Select(bridgeInterval =>
                        bridgeInterval.RelatedGeneratedMemberIndex)
                    .ToHashSet();
                if (sideBridgeMemberIndices.Count == 0)
                {
                    continue;
                }

                var expectedOuterBoundary = sideSign > 0
                    ? bridgePackMaximumBoundary
                    : -bridgePackMinimumBoundary;
                foreach (var sideBridgeMemberIndex in sideBridgeMemberIndices)
                {
                    var sideBridgeSetting = conversionResult.StackMembers?
                        .FirstOrDefault(stackMember => stackMember?.MemberIndex ==
                            sideBridgeMemberIndex)
                        ?.PlanarParts?
                        .FirstOrDefault(planarPart =>
                            planarPart?.PointList?.Count >= 2);
                    if (sideBridgeSetting == null)
                    {
                        continue;
                    }

                    expectedOuterBoundary = Math.Max(
                        expectedOuterBoundary,
                        sideSign * DotPoint(
                            sideBridgeSetting.PointList[0],
                            terminalThicknessDirection) +
                            sideBridgeSetting.BusBarThickness / 2d);
                }

                var followingMembers = conversionResult.StartTerminalMemberIndices
                    .Where(memberIndex =>
                        !bridgeMemberIndices.Contains(memberIndex))
                    .Select(memberIndex => new
                    {
                        MemberIndex = memberIndex,
                        Setting = conversionResult.StackMembers?
                            .FirstOrDefault(stackMember =>
                                stackMember?.MemberIndex == memberIndex)
                            ?.PlanarParts?
                            .FirstOrDefault(planarPart =>
                                planarPart?.PointList?.Count >= 2)
                    })
                    .Where(member => member.Setting != null)
                    .Select(member => new
                    {
                        member.MemberIndex,
                        member.Setting,
                        SideCoordinate = sideSign * DotPoint(
                            member.Setting.PointList[0],
                            terminalThicknessDirection)
                    })
                    .Where(member => member.SideCoordinate >=
                        sideSign * bridgePackCenterCoordinate -
                            GeometryTolerance)
                    .OrderBy(member => member.SideCoordinate)
                    .ToList();
                foreach (var followingMember in followingMembers)
                {
                    var followingHalfThickness =
                        followingMember.Setting.BusBarThickness / 2d;
                    var expectedCenterCoordinate = expectedOuterBoundary +
                        followingHalfThickness;
                    if (Math.Abs(followingMember.SideCoordinate -
                            expectedCenterCoordinate) > acceptanceTolerance)
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "第 {0} 层始端搭接排与连接排侧叠排未逐层贴合，厚向偏差 {1:F3} mm。",
                            followingMember.MemberIndex + 1,
                            followingMember.SideCoordinate -
                                expectedCenterCoordinate);
                        return false;
                    }

                    expectedOuterBoundary = followingMember.SideCoordinate +
                        followingHalfThickness;
                }
            }

            return true;
        }

        private static void AlignFollowingStartMemberLayersOutsideBridgePack(
            LinkageRouteCenterlineConversionResult conversionResult,
            ISet<int> bridgeMemberIndices)
        {
            var bridgeAccessories = (conversionResult?.Accessories ??
                    new List<LinkageRouteAccessory>())
                .Where(routeAccessory => routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                    routeAccessory.Setting?.PointList?.Count >= 2 &&
                    routeAccessory.Setting.StartBusbarVector != null &&
                    routeAccessory.Setting.StartBusbarVector.Norm >
                        GeometryTolerance)
                .ToList();
            if (bridgeAccessories.Count == 0 ||
                conversionResult?.StartTerminalMemberIndices == null)
            {
                return;
            }

            var terminalThicknessDirection = bridgeAccessories[0].Setting
                .StartBusbarVector.Normalized;
            var bridgeIntervals = bridgeAccessories
                .Select(routeAccessory => new
                {
                    CenterCoordinate = DotPoint(
                        routeAccessory.Setting.PointList[0],
                        terminalThicknessDirection),
                    HalfThickness = routeAccessory.Setting.BusBarThickness / 2d,
                    routeAccessory.RelatedGeneratedMemberIndex
                })
                .Where(bridgeInterval =>
                    bridgeInterval.HalfThickness > GeometryTolerance)
                .ToList();
            if (bridgeIntervals.Count == 0)
            {
                return;
            }

            var bridgePackMinimumBoundary = bridgeIntervals.Min(
                bridgeInterval => bridgeInterval.CenterCoordinate -
                    bridgeInterval.HalfThickness);
            var bridgePackMaximumBoundary = bridgeIntervals.Max(
                bridgeInterval => bridgeInterval.CenterCoordinate +
                    bridgeInterval.HalfThickness);
            var bridgePackCenterCoordinate =
                (bridgePackMinimumBoundary + bridgePackMaximumBoundary) / 2d;

            for (var sideSign = -1; sideSign <= 1; sideSign += 2)
            {
                var sideBridgeMemberIndices = bridgeIntervals
                    .Where(bridgeInterval =>
                        Math.Sign(bridgeInterval.CenterCoordinate -
                            bridgePackCenterCoordinate) == sideSign)
                    .Select(bridgeInterval =>
                        bridgeInterval.RelatedGeneratedMemberIndex)
                    .ToHashSet();
                if (sideBridgeMemberIndices.Count == 0)
                {
                    continue;
                }

                var sideOuterBoundary = sideSign > 0
                    ? bridgePackMaximumBoundary
                    : -bridgePackMinimumBoundary;
                foreach (var sideBridgeMemberIndex in sideBridgeMemberIndices)
                {
                    var sideBridgeSetting = conversionResult.StackMembers
                        .FirstOrDefault(stackMember => stackMember?.MemberIndex ==
                            sideBridgeMemberIndex)
                        ?.PlanarParts?
                        .FirstOrDefault(planarPart =>
                            planarPart?.PointList?.Count >= 2);
                    if (sideBridgeSetting == null)
                    {
                        continue;
                    }

                    var sideBridgeCoordinate = sideSign * DotPoint(
                        sideBridgeSetting.PointList[0],
                        terminalThicknessDirection);
                    sideOuterBoundary = Math.Max(
                        sideOuterBoundary,
                        sideBridgeCoordinate +
                            sideBridgeSetting.BusBarThickness / 2d);
                }

                var followingMembers = conversionResult.StartTerminalMemberIndices
                    .Where(memberIndex =>
                        !bridgeMemberIndices.Contains(memberIndex))
                    .Select(memberIndex => new
                    {
                        MemberIndex = memberIndex,
                        StackMember = conversionResult.StackMembers
                            .FirstOrDefault(stackMember =>
                                stackMember?.MemberIndex == memberIndex)
                    })
                    .Where(member => member.StackMember != null)
                    .Select(member => new
                    {
                        member.MemberIndex,
                        member.StackMember,
                        Setting = member.StackMember.PlanarParts?
                            .FirstOrDefault(planarPart =>
                                planarPart?.PointList?.Count >= 2)
                    })
                    .Where(member => member.Setting != null)
                    .Select(member => new
                    {
                        member.MemberIndex,
                        member.StackMember,
                        member.Setting,
                        SideCoordinate = sideSign * DotPoint(
                            member.Setting.PointList[0],
                            terminalThicknessDirection)
                    })
                    .Where(member => member.SideCoordinate >=
                        sideSign * bridgePackCenterCoordinate -
                        GeometryTolerance)
                    .OrderBy(member => member.SideCoordinate)
                    .ToList();
                foreach (var followingMember in followingMembers)
                {
                    var followingHalfThickness =
                        followingMember.Setting.BusBarThickness / 2d;
                    var requiredCenterCoordinate = sideOuterBoundary +
                        followingHalfThickness;
                    var sideCoordinateDisplacement =
                        requiredCenterCoordinate -
                        followingMember.SideCoordinate;
                    if (Math.Abs(sideCoordinateDisplacement) >
                        GeometryTolerance)
                    {
                        var signedDisplacement = sideSign *
                            sideCoordinateDisplacement;
                        if (!TryMoveStartTerminalLapChain(
                                conversionResult,
                                followingMember.StackMember,
                                0,
                                terminalThicknessDirection,
                                signedDisplacement,
                                out var followingMoveFailureReason) &&
                            !string.IsNullOrWhiteSpace(
                                followingMoveFailureReason) &&
                            !conversionResult.Diagnostics.Contains(
                                followingMoveFailureReason))
                        {
                            conversionResult.Diagnostics.Add(
                                followingMoveFailureReason);
                        }
                    }

                    var actualFollowingSideCoordinate = sideSign * DotPoint(
                        followingMember.Setting.PointList[0],
                        terminalThicknessDirection);
                    sideOuterBoundary = Math.Max(
                        sideOuterBoundary,
                        actualFollowingSideCoordinate + followingHalfThickness);
                }
            }
        }

        /// <summary>
        /// 端部输出阶段把连接排关联的实际始端物理段移到连接排的
        /// 正确厚向贴合层。非 I 排只移动始端搭接段；足够长的 I 排
        /// 转换成 N 排后再移动始端段，不重新枚举或淘汰方案。
        /// </summary>
        private static bool TryAlignBridgeGeneratedLayerToAccessory(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageStackedRouteMember bridgeGeneratedMember,
            PlanarBendBusbarSetting bridgeSetting,
            int preferredBridgeSideSign,
            out string failureReason)
        {
            failureReason = null;
            var bridgeGeneratedSetting = bridgeGeneratedMember?.PlanarParts?
                .FirstOrDefault(planarPart =>
                    planarPart?.PointList?.Count >= 2);
            if (bridgeGeneratedSetting?.PointList == null ||
                bridgeGeneratedSetting.PointList.Count < 2 ||
                bridgeSetting?.PointList == null ||
                bridgeSetting.PointList.Count < 2 ||
                bridgeSetting.StartBusbarVector == null ||
                bridgeSetting.StartBusbarVector.Norm <= GeometryTolerance ||
                bridgeGeneratedSetting.WidthVector == null ||
                bridgeGeneratedSetting.WidthVector.Norm <= GeometryTolerance)
            {
                failureReason = "双始端连接排输出层位修正缺少有效始端实体方向。";
                return false;
            }

            var terminalThicknessDirection = bridgeSetting
                .StartBusbarVector.Normalized;
            var bridgeSideSign = preferredBridgeSideSign == 0
                ? Math.Sign(new Vector3d(
                        bridgeSetting.PointList[0],
                        bridgeGeneratedSetting.PointList[0])
                    .Dot(terminalThicknessDirection))
                : preferredBridgeSideSign < 0
                    ? -1
                    : 1;
            if (bridgeSideSign == 0)
            {
                failureReason = "双始端连接排输出层位修正缺少实际搭接侧。";
                return false;
            }

            var bridgeCenterPoint = new Point3d(
                (bridgeSetting.PointList[0].X + bridgeSetting.PointList[1].X) /
                    2d,
                (bridgeSetting.PointList[0].Y + bridgeSetting.PointList[1].Y) /
                    2d,
                (bridgeSetting.PointList[0].Z + bridgeSetting.PointList[1].Z) /
                    2d);
            var generatedCenterCoordinate = new Vector3d(
                    bridgeCenterPoint,
                    bridgeGeneratedSetting.PointList[0])
                .Dot(terminalThicknessDirection);
            var targetCenterCoordinate = bridgeSideSign *
                (bridgeSetting.BusBarThickness / 2d +
                 bridgeGeneratedSetting.BusBarThickness / 2d);
            var layerDisplacement = targetCenterCoordinate -
                generatedCenterCoordinate;
            if (Math.Abs(layerDisplacement) > GeometryTolerance &&
                !TryMoveStartTerminalLapChain(
                    conversionResult,
                    bridgeGeneratedMember,
                    0,
                    terminalThicknessDirection,
                    layerDisplacement,
                    out failureReason))
            {
                return false;
            }

            bridgeGeneratedSetting = bridgeGeneratedMember.PlanarParts
                .FirstOrDefault(planarPart => planarPart?.PointList?.Count >= 2);
            if (bridgeGeneratedSetting == null ||
                !LinkageRouteSectionSpecificationPostProcessor
                    .TryExtendPerpendicularBoundaryEndpoint(
                        bridgeGeneratedSetting,
                        true,
                        bridgeSetting,
                        bridgeSetting.BusBarWidth))
            {
                failureReason =
                    "双始端连接排关联生成排无法延伸到连接排实际外侧边界。";
                return false;
            }

            // 厚面贴合并不代表斜过渡已离开连接排：转折太早时，首段
            // 已接触但相邻斜段仍切入连接排。复用内部接口的局部退让，
            // 只后移近端转折点，固定首末端和远端层位保持不动。
            var bridgeSafeSetting = bridgeGeneratedSetting.Copy();
            var fixedBridgeSettings = conversionResult.Accessories.Where(accessory =>
                    accessory?.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge && accessory.Setting != null)
                .Select(accessory => accessory.Setting).ToList();
            if (!LinkageRouteSectionSpecificationPostProcessor.TryMoveLapTransitionBeyondIntersections(
                    bridgeSafeSetting, true, fixedBridgeSettings, bridgeGeneratedSetting, 0d,
                    out var bridgeTransitionRetreat))
            {
                failureReason = "连接排搭接厚面已贴合，但生成排转折段无法退出连接排实际包络。";
                return false;
            }
            if (bridgeTransitionRetreat > GeometryTolerance)
            {
                bridgeGeneratedSetting.PointList = bridgeSafeSetting.PointList;
                conversionResult.Diagnostics.Add("连接排端部斜过渡已沿搭接直腿后移，消除实际连接排穿透。");
            }

            return true;
        }

        /// <summary>
        /// 只移动始端水平搭接所占的第一条实际直段。已有非共线折弯由
        /// 分段偏移器吸收层位变化；直 I 排只有端部错开量严格大于自身排厚，
        /// 且总长严格大于 2 × 排宽 + 2 × 折弯余量时，才改造成两端保留
        /// 直段的 N 排。
        /// </summary>
        internal static bool TryMoveStartTerminalLapSegment(
            PlanarBendBusbarSetting planarSetting,
            Vector3d terminalThicknessDirection,
            double signedDistance,
            out bool movedEntireStraightIBar,
            out string failureReason)
        {
            movedEntireStraightIBar = false;
            failureReason = null;
            if (planarSetting?.PointList == null ||
                planarSetting.PointList.Count < 2 ||
                planarSetting.WidthVector == null ||
                planarSetting.WidthVector.Norm <= GeometryTolerance ||
                planarSetting.StartBusbarVector == null ||
                planarSetting.StartBusbarVector.Norm <= GeometryTolerance ||
                terminalThicknessDirection == null ||
                terminalThicknessDirection.Norm <= GeometryTolerance)
            {
                failureReason =
                    "多始端水平连接排端段偏移缺少有效铜排中心线或方向。";
                return false;
            }

            if (Math.Abs(signedDistance) <= GeometryTolerance)
            {
                return true;
            }

            var normalizedTerminalThicknessDirection =
                terminalThicknessDirection.Normalized;
            var firstSegmentDirection = new Vector3d(
                planarSetting.PointList[0],
                planarSetting.PointList[1]);
            if (firstSegmentDirection.Norm <= GeometryTolerance)
            {
                failureReason =
                    "多始端水平连接排端段偏移遇到零长度始端线段。";
                return false;
            }

            firstSegmentDirection = firstSegmentDirection.Normalized;
            var normalizedWidthDirection = planarSetting.WidthVector.Normalized;
            var firstSegmentThicknessDirection = firstSegmentDirection
                .Cross(normalizedWidthDirection).Normalized;
            if (firstSegmentThicknessDirection.Dot(
                    planarSetting.StartBusbarVector.Normalized) < 0d)
            {
                firstSegmentThicknessDirection = new Vector3d(
                    -firstSegmentThicknessDirection.X,
                    -firstSegmentThicknessDirection.Y,
                    -firstSegmentThicknessDirection.Z);
            }

            var thicknessDirectionAlignment = firstSegmentThicknessDirection
                .Dot(normalizedTerminalThicknessDirection);
            if (Math.Abs(thicknessDirectionAlignment) <
                1d - GeometryTolerance)
            {
                failureReason =
                    "多始端水平连接排目标偏移方向与始端搭接段排厚方向不平行。";
                return false;
            }

            var localOffsetDistance = signedDistance *
                thicknessDirectionAlignment;
            var straightPrefixSegmentCount = 1;
            while (straightPrefixSegmentCount <
                planarSetting.PointList.Count - 1)
            {
                var followingSegmentDirection = new Vector3d(
                    planarSetting.PointList[straightPrefixSegmentCount],
                    planarSetting.PointList[straightPrefixSegmentCount + 1]);
                if (followingSegmentDirection.Norm <= GeometryTolerance ||
                    Math.Abs(followingSegmentDirection.Normalized.Dot(
                        firstSegmentDirection)) < 1d - GeometryTolerance)
                {
                    break;
                }

                straightPrefixSegmentCount++;
            }

            var isStraightIBar = straightPrefixSegmentCount ==
                planarSetting.PointList.Count - 1;
            if (isStraightIBar)
            {
                var startPoint = planarSetting.PointList[0];
                var endPoint = planarSetting.PointList[
                    planarSetting.PointList.Count - 1];
                var straightLength = startPoint.DistanceTo(endPoint);
                var terminalStraightReserve = planarSetting.BusBarWidth +
                    Math.Max(0d, planarSetting.BendAllowance);
                var requiredIBarLength = 2d * terminalStraightReserve;
                var isOffsetLargeEnoughForN = Math.Abs(localOffsetDistance) >
                    planarSetting.BusBarThickness + GeometryTolerance;
                var isLengthLargeEnoughForN = straightLength >
                    requiredIBarLength + GeometryTolerance;
                if (!isOffsetLargeEnoughForN || !isLengthLargeEnoughForN)
                {
                    // I 形不可折弯不代表不可移动：先贴合固定连接排，
                    // 将另一端的位移传给后续 I/非 I 排，最后补正间隙。
                    for (var terminalIBarPointIndex = 0;
                        terminalIBarPointIndex < planarSetting.PointList.Count;
                        terminalIBarPointIndex++)
                    {
                        planarSetting.PointList[terminalIBarPointIndex] =
                            LinkageRouteSolidGeometry.Translate(
                                planarSetting.PointList[terminalIBarPointIndex],
                                normalizedTerminalThicknessDirection,
                                signedDistance);
                    }
                    movedEntireStraightIBar = true;
                    return true;
                }

                // 只有错开量已经超过一个 I 排排厚，且长度足够保留两端
                // 折弯余量时，才把直 I 排拆成两端直段 + 中间段的 N 排。
                var worldDisplacement = new Vector3d(
                    normalizedTerminalThicknessDirection.X * signedDistance,
                    normalizedTerminalThicknessDirection.Y * signedDistance,
                    normalizedTerminalThicknessDirection.Z * signedDistance);
                var shiftedStartPoint = LinkageRouteSolidGeometry.Translate(
                    startPoint,
                    worldDisplacement.Normalized,
                    worldDisplacement.Norm);
                var shiftedStartReservePoint =
                    LinkageRouteSolidGeometry.Translate(
                        LinkageRouteSolidGeometry.Translate(
                            startPoint,
                            firstSegmentDirection,
                            terminalStraightReserve),
                        worldDisplacement.Normalized,
                        worldDisplacement.Norm);
                var preservedEndReservePoint =
                    LinkageRouteSolidGeometry.Translate(
                        endPoint,
                        firstSegmentDirection,
                        -terminalStraightReserve);
                planarSetting.PointList = new List<Point3d>
                {
                    shiftedStartPoint,
                    shiftedStartReservePoint,
                    preservedEndReservePoint,
                    new Point3d(endPoint.X, endPoint.Y, endPoint.Z)
                };
                return true;
            }

            var segmentOffsetDistances = new double[
                planarSetting.PointList.Count - 1];
            for (var straightPrefixSegmentIndex = 0;
                straightPrefixSegmentIndex < straightPrefixSegmentCount;
                straightPrefixSegmentIndex++)
            {
                segmentOffsetDistances[straightPrefixSegmentIndex] =
                    localOffsetDistance;
            }

            if (LinkageRouteStackedCenterlineOffsetter.TryOffset(
                    planarSetting,
                    segmentOffsetDistances,
                    true,
                    out var movedPointList,
                    out failureReason))
            {
                planarSetting.PointList = movedPointList;
                return true;
            }

            // 端部连接排贴合所需的完整偏移可能把某个折点压成零长度
            // 线段。此时不能直接放弃候选：在 [0, requested] 内回退到
            // 最大可制造偏移，下一轮联动会继续处理剩余厚向差。
            var zeroOffsetDistances = new double[
                segmentOffsetDistances.Length];
            if (!LinkageRouteStackedCenterlineOffsetter.TryOffset(
                    planarSetting,
                    zeroOffsetDistances,
                    true,
                    out var bestOffsetPointList,
                    out var zeroOffsetFailureReason))
            {
                failureReason =
                    "多始端水平连接排始端搭接段局部偏移失败：" +
                    (zeroOffsetFailureReason ?? failureReason);
                return false;
            }

            var lowerSuccessfulFactor = 0d;
            var upperFailedFactor = 1d;
            for (var backoffIteration = 0;
                backoffIteration < 14;
                backoffIteration++)
            {
                var candidateFactor =
                    (lowerSuccessfulFactor + upperFailedFactor) / 2d;
                var candidateOffsetDistances = segmentOffsetDistances
                    .Select(distance => distance * candidateFactor)
                    .ToArray();
                if (LinkageRouteStackedCenterlineOffsetter.TryOffset(
                        planarSetting,
                        candidateOffsetDistances,
                        true,
                        out var candidatePointList,
                        out _))
                {
                    lowerSuccessfulFactor = candidateFactor;
                    bestOffsetPointList = candidatePointList;
                }
                else
                {
                    upperFailedFactor = candidateFactor;
                }
            }

            if (lowerSuccessfulFactor <= GeometryTolerance)
            {
                failureReason =
                    "多始端水平连接排始端搭接段局部偏移失败：完整偏移会产生零长度线段，且没有可用的局部回退位置。";
                return false;
            }

            planarSetting.PointList = bestOffsetPointList;
            failureReason = null;
            return true;
        }

        /// <summary>
        /// 始端搭接段需要偏移时，仅当错开量超过 I 排排厚且长度足够的 I 排
        /// 才在本排内转成 N 排吸收位移；否则保持 I 形整体移动并联动后续排。
        /// 不满足转换条件的 I 排保持刚性 I 形，不在此处插点或转 N；内部
        /// 搭接链产生的厚向余隙只保留给全部联动完成后的最终搭接面垫块生成器，
        /// 本方法不在联动过程中提前创建垫块。
        /// </summary>
        internal static bool TryMoveStartTerminalLapChain(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageStackedRouteMember stackedMember,
            int startPlanarPartIndex,
            Vector3d terminalThicknessDirection,
            double signedDistance,
            out string failureReason)
        {
            failureReason = null;
            if (stackedMember?.PlanarParts == null ||
                startPlanarPartIndex < 0 ||
                startPlanarPartIndex >= stackedMember.PlanarParts.Count)
            {
                failureReason =
                    "多始端水平连接排搭接链缺少有效的逐段铜排。";
                return false;
            }

            var originalPointLists = stackedMember.PlanarParts
                .Select(planarPart => (planarPart?.PointList ??
                        new List<Point3d>())
                    .Select(point => new Point3d(point.X, point.Y, point.Z))
                    .ToList())
                .ToList();
            for (var planarPartIndex = startPlanarPartIndex;
                planarPartIndex < stackedMember.PlanarParts.Count;
                planarPartIndex++)
            {
                var currentPlanarPart = stackedMember.PlanarParts[
                    planarPartIndex];
                if (!TryMoveStartTerminalLapSegment(
                        currentPlanarPart,
                        terminalThicknessDirection,
                        signedDistance,
                        out var movedEntireStraightIBar,
                        out failureReason))
                {
                    // 已经完成的前序 I/N 段位移要保留，后续最终垫块
                    // 需要基于这组稳定位置计算；只回滚当前失败段及其
                    // 后续尚未处理的搭接段，避免一次失败抹掉前面可用的调整。
                    for (var restorePartIndex = planarPartIndex;
                        restorePartIndex < stackedMember.PlanarParts.Count;
                        restorePartIndex++)
                    {
                        stackedMember.PlanarParts[restorePartIndex].PointList =
                            originalPointLists[restorePartIndex];
                    }
                    return false;
                }

                if (!movedEntireStraightIBar)
                {
                    return true;
                }
            }

            var propagatedDiagnostic = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "多始端水平连接排第 {0} 层由短 I 排组成的搭接链已整体联动偏移；末端若形成厚向余隙，必须由实际搭接面垫块补齐。",
                stackedMember.MemberIndex + 1);
            if (conversionResult?.Diagnostics != null &&
                !conversionResult.Diagnostics.Contains(propagatedDiagnostic))
            {
                conversionResult.Diagnostics.Add(propagatedDiagnostic);
            }

            return true;
        }

        private static bool TryAddPropagatedInternalLapSupportBlock(
            LinkageRouteCenterlineConversionResult conversionResult,
            int generatedMemberIndex,
            int boundaryIndex,
            PlanarBendBusbarSetting precedingSetting,
            PlanarBendBusbarSetting followingSetting,
            Vector3d terminalThicknessDirection,
            out string failureReason)
        {
            failureReason = null;
            if (conversionResult?.Accessories == null ||
                precedingSetting?.PointList?.Count < 2 ||
                followingSetting?.PointList?.Count < 2 ||
                terminalThicknessDirection == null ||
                terminalThicknessDirection.Norm <= GeometryTolerance)
            {
                failureReason = "联动偏移后的内部搭接垫块缺少有效实体。";
                return false;
            }

            var normalizedThicknessDirection = terminalThicknessDirection
                .Normalized;
            var precedingEndpoint = precedingSetting.PointList[
                precedingSetting.PointList.Count - 1];
            var followingEndpoint = followingSetting.PointList[0];
            var signedFollowingCoordinate = new Vector3d(
                    precedingEndpoint,
                    followingEndpoint)
                .Dot(normalizedThicknessDirection);
            var requiredContactCenterDistance =
                (precedingSetting.BusBarThickness +
                 followingSetting.BusBarThickness) / 2d;
            if (Math.Abs(signedFollowingCoordinate) -
                    requiredContactCenterDistance <= GeometryTolerance)
            {
                failureReason =
                    "联动偏移停止处不存在可由垫块补偿的正厚向缝隙。";
                return false;
            }

            if (!LinkageRouteTerminalLapRegionValidator
                    .TryCreateProjectedLapFootprint(
                        precedingSetting,
                        false,
                        followingSetting,
                        true,
                        Math.Min(
                            precedingSetting.BusBarWidth,
                            followingSetting.BusBarWidth),
                        out var supportFootprint))
            {
                failureReason =
                    "联动偏移后的内部搭接缝隙无法取得实际投影搭接面。";
                return false;
            }

            var supportMinimumCoordinate = signedFollowingCoordinate > 0d
                ? precedingSetting.BusBarThickness / 2d
                : signedFollowingCoordinate +
                  followingSetting.BusBarThickness / 2d;
            var supportMaximumCoordinate = signedFollowingCoordinate > 0d
                ? signedFollowingCoordinate -
                  followingSetting.BusBarThickness / 2d
                : -precedingSetting.BusBarThickness / 2d;
            var supportBlockKey = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "output-chain-boundary:{0}|member:{1}",
                boundaryIndex,
                generatedMemberIndex);
            for (var accessoryIndex = conversionResult.Accessories.Count - 1;
                accessoryIndex >= 0;
                accessoryIndex--)
            {
                if (string.Equals(
                        conversionResult.Accessories[accessoryIndex]
                            ?.SupportBlockKey,
                        supportBlockKey,
                        StringComparison.Ordinal))
                {
                    conversionResult.Accessories.RemoveAt(accessoryIndex);
                }
            }

            var supportBlockAccessory = CreateSupportBlockAccessoryCore(
                supportFootprint,
                precedingEndpoint,
                normalizedThicknessDirection,
                Math.Min(
                    supportMinimumCoordinate,
                    supportMaximumCoordinate),
                Math.Max(
                    supportMinimumCoordinate,
                    supportMaximumCoordinate),
                precedingSetting.BarBendingRadius,
                precedingSetting.BendAllowance,
                precedingSetting.GroupKey,
                boundaryIndex == 0,
                generatedMemberIndex);
            supportBlockAccessory.SupportBlockKey = supportBlockKey;
            conversionResult.Accessories.Add(supportBlockAccessory);
            return true;
        }

        private static double DotPoint(
            Point3d point,
            Vector3d direction)
        {
            return point.X * direction.X +
                point.Y * direction.Y +
                point.Z * direction.Z;
        }

        private static bool UsesFixedConnectionBridgeLayerPolicy()
        {
            return true;
        }

        /// <summary>
        /// 双始端 I/L 连接排合并到主路径后，不再有独立附件标记。此时
        /// 第一段是连接排本体，第二段才是从连接排继续延伸的始端搭接排。
        /// 两段都必须在最终实体阶段统一首段搭接长度；否则只有与连接排
        /// 直接对应的那一层达到连接排宽度，其他叠排仍停在较短位置，
        /// 进而使始端垫块按错误端面生成。
        /// </summary>
        private static void ApplyPhysicalBridgeStartLapGeometry(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageTerminalAssembly startTerminalAssembly)
        {
            if (conversionResult?.StackMembers == null ||
                conversionResult.StartTerminalMemberIndices == null ||
                conversionResult.StartTerminalMemberIndices.Count == 0)
            {
                return;
            }

            var bridgeSettingsByMemberIndex = new Dictionary<int,
                PlanarBendBusbarSetting>();
            var downstreamStartSettingsByMemberIndex = new Dictionary<int,
                PlanarBendBusbarSetting>();
            foreach (var startMemberIndex in conversionResult
                .StartTerminalMemberIndices
                .OrderBy(memberIndex => memberIndex))
            {
                var stackMember = conversionResult.StackMembers
                    .FirstOrDefault(candidateStackMember =>
                        candidateStackMember != null &&
                        candidateStackMember.MemberIndex == startMemberIndex);
                var planarParts = stackMember?.PlanarParts;
                if (planarParts == null || planarParts.Count == 0)
                {
                    continue;
                }

                var bridgeSetting = planarParts[0];
                if (bridgeSetting?.PointList != null &&
                    bridgeSetting.PointList.Count >= 2)
                {
                    bridgeSettingsByMemberIndex[startMemberIndex] =
                        bridgeSetting;
                }

                if (planarParts.Count > 1)
                {
                    var downstreamStartSetting = planarParts[1];
                    if (downstreamStartSetting?.PointList != null &&
                        downstreamStartSetting.PointList.Count >= 2)
                    {
                        downstreamStartSettingsByMemberIndex[startMemberIndex] =
                            downstreamStartSetting;
                    }
                }
            }

            foreach (var startMemberIndex in downstreamStartSettingsByMemberIndex
                .Keys
                .OrderBy(memberIndex => memberIndex))
            {
                if (!bridgeSettingsByMemberIndex.TryGetValue(
                        startMemberIndex,
                        out var bridgeSetting))
                {
                    continue;
                }

                var downstreamStartSetting = downstreamStartSettingsByMemberIndex[
                    startMemberIndex];
                PlanarBendBusbarSetting collectiveFixedStartBusbar = null;
                var hasCollectiveFixedStartBusbar =
                    startTerminalAssembly != null &&
                    LinkageRouteDoubleStartLapGeometry
                        .TryCreateCollectiveFixedBusbar(
                            startTerminalAssembly,
                            out collectiveFixedStartBusbar);
                if (bridgeSetting.BusBarWidth > GeometryTolerance &&
                    !(hasCollectiveFixedStartBusbar
                        ? TrySetBridgeLengthToRequiredLapCoverage(
                            bridgeSetting,
                            collectiveFixedStartBusbar,
                            downstreamStartSetting)
                        : TryExtendBridgeLengthToGeneratedLap(
                            bridgeSetting,
                            downstreamStartSetting)))
                {
                    conversionResult.Diagnostics.Add(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "双始端主路径第 {0} 根连接排无法沿排长方向覆盖生成排，已保留原几何。",
                            startMemberIndex + 1));
                }
            }

            // 合并连接排已经成为每个始端成员的第一段实体。连接排固定不动，
            // 但其后的实际搭接段仍必须按连接排包络向外逐层排列；否则多根叠排
            // 会出现连接排范围与后续搭接排范围交错。只从第二个物理段开始移动。
            AlignMergedPhysicalBridgeFollowingLayers(
                conversionResult,
                bridgeSettingsByMemberIndex,
                startTerminalAssembly?.ThicknessDirection);
        }

        private static void AlignMergedPhysicalBridgeFollowingLayers(
            LinkageRouteCenterlineConversionResult conversionResult,
            IReadOnlyDictionary<int, PlanarBendBusbarSetting> bridgeSettingsByMemberIndex,
            Vector3d fallbackThicknessDirection)
        {
            if (conversionResult?.StackMembers == null ||
                bridgeSettingsByMemberIndex == null ||
                bridgeSettingsByMemberIndex.Count == 0)
            {
                return;
            }

            var bridgeSettings = bridgeSettingsByMemberIndex.Values
                .Where(setting => setting?.PointList?.Count >= 2)
                .ToList();
            if (bridgeSettings.Count == 0)
            {
                return;
            }

            var referenceBridgeSetting = bridgeSettings.FirstOrDefault(setting =>
                setting.WidthVector != null &&
                setting.WidthVector.Norm > GeometryTolerance);
            var terminalThicknessDirection = referenceBridgeSetting == null
                ? null
                : new Vector3d(
                    referenceBridgeSetting.PointList[
                        referenceBridgeSetting.PointList.Count - 2],
                    referenceBridgeSetting.PointList[
                        referenceBridgeSetting.PointList.Count - 1])
                    .Cross(referenceBridgeSetting.WidthVector.Normalized);
            if (terminalThicknessDirection != null &&
                terminalThicknessDirection.Norm > GeometryTolerance &&
                referenceBridgeSetting.StartBusbarVector != null &&
                referenceBridgeSetting.StartBusbarVector.Norm >
                    GeometryTolerance &&
                terminalThicknessDirection.Dot(
                    referenceBridgeSetting.StartBusbarVector.Normalized) < 0d)
            {
                terminalThicknessDirection = new Vector3d(
                    -terminalThicknessDirection.X,
                    -terminalThicknessDirection.Y,
                    -terminalThicknessDirection.Z);
            }
            terminalThicknessDirection =
                terminalThicknessDirection?.Norm > GeometryTolerance
                    ? terminalThicknessDirection
                    : fallbackThicknessDirection;
            if (terminalThicknessDirection == null ||
                terminalThicknessDirection.Norm <= GeometryTolerance)
            {
                return;
            }
            terminalThicknessDirection = terminalThicknessDirection.Normalized;

            var bridgeIntervals = bridgeSettings
                .Select(setting => new
                {
                    CenterCoordinate = DotPoint(
                        setting.PointList[setting.PointList.Count - 1],
                        terminalThicknessDirection),
                    HalfThickness = setting.BusBarThickness / 2d
                })
                .Where(interval => interval.HalfThickness > GeometryTolerance)
                .ToList();
            if (bridgeIntervals.Count == 0)
            {
                return;
            }

            var bridgePackMinimumBoundary = bridgeIntervals.Min(interval =>
                interval.CenterCoordinate - interval.HalfThickness);
            var bridgePackMaximumBoundary = bridgeIntervals.Max(interval =>
                interval.CenterCoordinate + interval.HalfThickness);
            var followingMembers = bridgeSettingsByMemberIndex
                .Select(pair => new
                {
                    MemberIndex = pair.Key,
                    FollowingSetting = conversionResult.StackMembers
                        .FirstOrDefault(member => member?.MemberIndex == pair.Key)
                        ?.PlanarParts?
                        .Skip(1)
                        .FirstOrDefault(setting => setting?.PointList?.Count >= 2)
                })
                .Where(member => member.FollowingSetting != null)
                .ToList();
            if (followingMembers.Count == 0)
            {
                return;
            }

            var followingMinimumBoundary = followingMembers.Min(member =>
                DotPoint(
                    member.FollowingSetting.PointList[0],
                    terminalThicknessDirection) -
                member.FollowingSetting.BusBarThickness / 2d);
            var followingMaximumBoundary = followingMembers.Max(member =>
                DotPoint(
                    member.FollowingSetting.PointList[0],
                    terminalThicknessDirection) +
                member.FollowingSetting.BusBarThickness / 2d);
            if (bridgePackMaximumBoundary <=
                    followingMinimumBoundary + GeometryTolerance ||
                followingMaximumBoundary <=
                    bridgePackMinimumBoundary + GeometryTolerance)
            {
                return;
            }

            var selectedCommonDisplacement = bridgePackMinimumBoundary -
                followingMaximumBoundary;
            foreach (var followingMember in followingMembers)
            {
                if (!TryMoveStartTerminalLapChain(
                        conversionResult,
                        conversionResult.StackMembers.First(member =>
                            member.MemberIndex == followingMember.MemberIndex),
                        1,
                        terminalThicknessDirection,
                        selectedCommonDisplacement,
                        out var failureReason) &&
                    !string.IsNullOrWhiteSpace(failureReason) &&
                    !conversionResult.Diagnostics.Contains(failureReason))
                {
                    conversionResult.Diagnostics.Add(failureReason);
                }
            }
        }

        private static void AlignStartLapSegmentLengths(
            IEnumerable<PlanarBendBusbarSetting> startLapSettings)
        {
            var validStartLapSettings = (startLapSettings ??
                    Enumerable.Empty<PlanarBendBusbarSetting>())
                .Where(startLapSetting =>
                    startLapSetting?.PointList != null &&
                    startLapSetting.PointList.Count >= 2)
                .Select(startLapSetting => new
                {
                    Setting = startLapSetting,
                    OutwardDirection = new Vector3d(
                        startLapSetting.PointList[1],
                        startLapSetting.PointList[0])
                })
                .Where(startLapGeometry =>
                    startLapGeometry.OutwardDirection.Norm > GeometryTolerance)
                .Select(startLapGeometry => new
                {
                    startLapGeometry.Setting,
                    OutwardDirection = startLapGeometry.OutwardDirection.Normalized,
                    LapLength = startLapGeometry.OutwardDirection.Norm
                })
                .ToList();
            if (validStartLapSettings.Count == 0)
            {
                return;
            }

            var commonLapLength = validStartLapSettings.Max(
                startLapGeometry => startLapGeometry.LapLength);
            foreach (var startLapGeometry in validStartLapSettings)
            {
                if (commonLapLength - startLapGeometry.LapLength <=
                    GeometryTolerance)
                {
                    continue;
                }

                startLapGeometry.Setting.PointList[0] =
                    LinkageRouteSolidGeometry.Translate(
                        startLapGeometry.Setting.PointList[1],
                        startLapGeometry.OutwardDirection,
                        commonLapLength);
            }
        }

        /// <summary>
        /// 连接排所在的始端搭接排首段端点是实际有效垂直搭接端面。
        /// 将同一始端物理段的其他叠排成员端点对齐到同一端面，避免只有
        /// 连接排直接接触的成员长度足够、其余成员仍停留在较短端点。
        /// </summary>
        private static bool TryAlignStartMembersToBridgeEndpoint(
            LinkageRouteCenterlineConversionResult conversionResult,
            PlanarBendBusbarSetting bridgeGeneratedSetting,
            out string failureReason)
        {
            failureReason = null;
            if (conversionResult?.StackMembers == null ||
                conversionResult.StackMembers.Count == 0)
            {
                return true;
            }

            if (bridgeGeneratedSetting?.PointList == null ||
                bridgeGeneratedSetting.PointList.Count < 2)
            {
                failureReason =
                    "始端搭接排端面传播缺少连接排对应的首段几何，已保留原几何。";
                return false;
            }

            var bridgeInteriorPoint = bridgeGeneratedSetting.PointList[1];
            var bridgeOutwardDirection = new Vector3d(
                bridgeInteriorPoint,
                bridgeGeneratedSetting.PointList[0]);
            if (bridgeOutwardDirection.Norm <= GeometryTolerance)
            {
                failureReason =
                    "始端搭接排端面传播无法确定连接排首段方向，已保留原几何。";
                return false;
            }

            var bridgeEndpointCoordinate = DotPoint(
                bridgeGeneratedSetting.PointList[0],
                bridgeOutwardDirection.Normalized);
            var normalizedBridgeOutwardDirection = bridgeOutwardDirection
                .Normalized;
            var alignmentFailureMemberIndex = -1;
            var startMemberGeometries = new List<StartMemberGeometry>();

            var startMemberIndices = conversionResult.StartTerminalMemberIndices != null &&
                conversionResult.StartTerminalMemberIndices.Count > 0
                ? conversionResult.StartTerminalMemberIndices.OrderBy(
                    memberIndex => memberIndex)
                : conversionResult.StackMembers
                    .Where(stackMember => stackMember != null)
                    .Select(stackMember => stackMember.MemberIndex)
                    .OrderBy(memberIndex => memberIndex);
            foreach (var startMemberIndex in startMemberIndices)
            {
                var startStackMember = conversionResult.StackMembers
                    .FirstOrDefault(stackMember => stackMember != null &&
                        stackMember.MemberIndex == startMemberIndex);
                var startPlanarSetting = startStackMember?.PlanarParts
                    ?.FirstOrDefault(planarSetting => planarSetting != null &&
                        planarSetting.PointList != null &&
                        planarSetting.PointList.Count >= 2);
                if (startPlanarSetting == null)
                {
                    alignmentFailureMemberIndex = startMemberIndex;
                    break;
                }

                var currentEndpoint = startPlanarSetting.PointList[0];
                var currentInteriorPoint = startPlanarSetting.PointList[1];
                var currentOutwardDirection = new Vector3d(
                    currentInteriorPoint,
                    currentEndpoint);
                if (currentOutwardDirection.Norm <= GeometryTolerance)
                {
                    alignmentFailureMemberIndex = startMemberIndex;
                    break;
                }

                startMemberGeometries.Add(new StartMemberGeometry
                {
                    Setting = startPlanarSetting,
                    InteriorPoint = currentInteriorPoint,
                    OutwardDirection = currentOutwardDirection.Normalized,
                    LapLength = currentOutwardDirection.Norm
                });
            }

            if (alignmentFailureMemberIndex >= 0)
            {
                failureReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "始端搭接排第 {0} 层无法与连接排端面统一垂直搭接高度，已保留已完成的几何修正。",
                    alignmentFailureMemberIndex + 1);
                return false;
            }

            if (startMemberGeometries.Count == 0)
            {
                return true;
            }

            foreach (var startMemberGeometry in startMemberGeometries)
            {
                var memberOutwardDirection = startMemberGeometry.OutwardDirection;
                var directionAlignment = memberOutwardDirection.Dot(
                    normalizedBridgeOutwardDirection);
                if (directionAlignment < 1d - GeometryTolerance)
                {
                    failureReason =
                        "同组始端搭接排首段方向不一致，无法按连接排主端面齐平。";
                    return false;
                }

                var requiredLapLength =
                    (bridgeEndpointCoordinate -
                     DotPoint(
                         startMemberGeometry.InteriorPoint,
                         normalizedBridgeOutwardDirection)) /
                    directionAlignment;
                if (requiredLapLength <= GeometryTolerance)
                {
                    failureReason =
                        "同组始端搭接排无法在连接排主端面前保留有效直段。";
                    return false;
                }

                if (Math.Abs(requiredLapLength - startMemberGeometry.LapLength) <=
                    GeometryTolerance)
                {
                    continue;
                }

                startMemberGeometry.Setting.PointList[0] =
                    LinkageRouteSolidGeometry.Translate(
                        startMemberGeometry.InteriorPoint,
                        memberOutwardDirection,
                        requiredLapLength);
            }

            return true;
        }

        private sealed class StartMemberGeometry
        {
            public PlanarBendBusbarSetting Setting { get; set; }
            public Point3d InteriorPoint { get; set; }
            public Vector3d OutwardDirection { get; set; }
            public double LapLength { get; set; }
        }

        private static bool TryValidateSpacerlessGeneratedTerminalLayers(
            IList<LinkageStackedRouteMember> generatedStackMembers,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            bool useStartTerminal,
            ISet<int> eligibleMemberIndices,
            string terminalLabel,
            LinkageRouteAccessory doubleStartBridgeAccessory,
            out string failureReason,
            IReadOnlyList<LinkageRouteAccessory> singleStartConnectionAccessories = null)
        {
            failureReason = null;
            if (!TryBuildGeneratedTerminalLayers(
                    generatedStackMembers,
                    terminalAssembly,
                    useStartTerminal,
                    eligibleMemberIndices,
                    out var generatedTerminalLayers,
                    out var terminalReferencePoint) ||
                !TryBuildFixedTerminalLayers(
                    terminalAssembly,
                    terminalReferencePoint,
                    out var fixedTerminalLayers))
            {
                failureReason = terminalLabel +
                    "无垫块搭接无法解析生成叠排或固定排端部层位。";
                return false;
            }

            var terminalThicknessDirection = terminalAssembly
                .ThicknessDirection.Normalized;
            var bridgeThicknessIntervals = new List<KeyValuePair<int, ThicknessInterval>>();
            var actualBridgeAccessories = singleStartConnectionAccessories ??
                (doubleStartBridgeAccessory == null ? new List<LinkageRouteAccessory>() :
                    new List<LinkageRouteAccessory> { doubleStartBridgeAccessory });
            foreach (var actualBridgeAccessory in actualBridgeAccessories.Where(accessory => accessory?.Setting != null))
            {
                var bridgeCenterCoordinate = new Vector3d(
                        terminalReferencePoint,
                        actualBridgeAccessory.Setting.PointList[0])
                    .Dot(terminalThicknessDirection);
                var bridgeInterval = new ThicknessInterval
                {
                    Minimum = bridgeCenterCoordinate -
                        actualBridgeAccessory.Setting.BusBarThickness / 2d,
                    Maximum = bridgeCenterCoordinate +
                        actualBridgeAccessory.Setting.BusBarThickness / 2d
                };
                bridgeThicknessIntervals.Add(new KeyValuePair<int, ThicknessInterval>(
                    singleStartConnectionAccessories != null ? actualBridgeAccessory.RelatedGeneratedMemberIndex :
                        terminalPlacement?.DoubleStartHorizontalBridgeGeneratedMemberIndex ?? -1, bridgeInterval));
            }

            var spacerlessMovedGeneratedMemberIndices = new HashSet<int>(
                terminalPlacement?.SpacerlessMovedGeneratedMemberIndices ??
                new List<int>());
            if (spacerlessMovedGeneratedMemberIndices.Any(
                    generatedMemberIndex => generatedMemberIndex < 0 ||
                        generatedMemberIndex >= generatedStackMembers.Count))
            {
                failureReason = terminalLabel +
                    "无垫块搭接记录了超出叠排范围的移动成员索引。";
                return false;
            }

            foreach (var generatedTerminalLayer in generatedTerminalLayers)
            {
                var generatedLayerInterval = CreateThicknessInterval(
                    generatedTerminalLayer.CenterCoordinate,
                    generatedTerminalLayer.Thickness);
                var generatedLayerHasPhysicalContact = generatedTerminalLayers
                    .Where(otherGeneratedLayer =>
                        otherGeneratedLayer.MemberIndex !=
                            generatedTerminalLayer.MemberIndex)
                    .Any(otherGeneratedLayer => AreThicknessIntervalsTouching(
                        generatedLayerInterval,
                        CreateThicknessInterval(
                            otherGeneratedLayer.CenterCoordinate,
                            otherGeneratedLayer.Thickness))) ||
                    fixedTerminalLayers.Any(fixedTerminalLayer =>
                        AreThicknessIntervalsTouching(
                            generatedLayerInterval,
                            new ThicknessInterval
                            {
                                Minimum = fixedTerminalLayer.MinimumCoordinate,
                                Maximum = fixedTerminalLayer.MaximumCoordinate
                            })) ||
                    bridgeThicknessIntervals.Any(bridgeInterval =>
                        AreThicknessIntervalsTouching(
                            generatedLayerInterval,
                            bridgeInterval.Value));
                // 只要求实际为闭合原普通垫块夹缝而移动过的成员完成贴合。
                // 柜顶未列入闭缝移动的厚差层保持既有无垫块位置时，
                // 可以没有直接厚向接触，不能因此把整个候选误判失败。
                if (spacerlessMovedGeneratedMemberIndices.Contains(
                        generatedTerminalLayer.MemberIndex) &&
                    !generatedLayerHasPhysicalContact)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}无垫块搭接第 {1} 层没有贴合固定端、桥排或相邻生成排。",
                        terminalLabel,
                        generatedTerminalLayer.MemberIndex + 1);
                    return false;
                }

                if (generatedTerminalLayers
                        .Where(otherGeneratedLayer =>
                            otherGeneratedLayer.MemberIndex !=
                                generatedTerminalLayer.MemberIndex)
                        .Any(otherGeneratedLayer =>
                            GetThicknessIntervalOverlap(
                                generatedLayerInterval,
                                CreateThicknessInterval(
                                    otherGeneratedLayer.CenterCoordinate,
                                    otherGeneratedLayer.Thickness)) >
                                GeometryTolerance) ||
                    fixedTerminalLayers.Any(fixedTerminalLayer =>
                        GetThicknessIntervalOverlap(
                            generatedLayerInterval,
                            new ThicknessInterval
                            {
                                Minimum = fixedTerminalLayer.MinimumCoordinate,
                                Maximum = fixedTerminalLayer.MaximumCoordinate
                            }) > GeometryTolerance) ||
                    bridgeThicknessIntervals.Any(bridgeInterval =>
                        GetThicknessIntervalOverlap(
                            generatedLayerInterval,
                            bridgeInterval.Value) > GeometryTolerance))
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}无垫块搭接第 {1} 层与其他端部实体发生排厚正重叠。",
                        terminalLabel,
                        generatedTerminalLayer.MemberIndex + 1);
                    return false;
                }
            }

            foreach (var terminalContact in terminalPlacement?.Contacts ??
                new List<LinkageRouteStackTerminalContact>())
            {
                var contactedGeneratedLayer = generatedTerminalLayers
                    .FirstOrDefault(generatedTerminalLayer =>
                        generatedTerminalLayer.MemberIndex ==
                            terminalContact.GeneratedMemberIndex);
                var contactedFixedLayer = fixedTerminalLayers
                    .FirstOrDefault(fixedTerminalLayer =>
                        fixedTerminalLayer.FixedMemberIndices.Contains(
                            terminalContact.FixedMemberIndex));
                if (contactedGeneratedLayer == null ||
                    contactedFixedLayer == null)
                {
                    failureReason = terminalLabel +
                        "无垫块搭接声明了无法解析的端部接触关系。";
                    return false;
                }

                var generatedContactInterval = CreateThicknessInterval(
                    contactedGeneratedLayer.CenterCoordinate,
                    contactedGeneratedLayer.Thickness);
                var fixedContactInterval = new ThicknessInterval
                {
                    Minimum = contactedFixedLayer.MinimumCoordinate,
                    Maximum = contactedFixedLayer.MaximumCoordinate
                };
                var isDirectFixedContact = AreThicknessIntervalsTouching(
                    generatedContactInterval,
                    fixedContactInterval);
                var isDoubleStartBridgeContact = bridgeThicknessIntervals.Any(bridgeInterval =>
                    bridgeInterval.Key ==
                            terminalContact.GeneratedMemberIndex &&
                    AreThicknessIntervalsTouching(
                        generatedContactInterval,
                        bridgeInterval.Value) &&
                    AreThicknessIntervalsTouching(
                        bridgeInterval.Value,
                        fixedContactInterval));
                if (!isDirectFixedContact && !isDoubleStartBridgeContact)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}无垫块搭接第 {1} 层声明的固定端接触没有落在真实贴合面上。",
                        terminalLabel,
                        terminalContact.GeneratedMemberIndex + 1);
                    return false;
                }
            }

            return true;
        }

        private static ThicknessInterval CreateThicknessInterval(
            double centerCoordinate,
            double thickness)
        {
            return new ThicknessInterval
            {
                Minimum = centerCoordinate - thickness / 2d,
                Maximum = centerCoordinate + thickness / 2d
            };
        }

        private static bool AreThicknessIntervalsTouching(
            ThicknessInterval firstInterval,
            ThicknessInterval secondInterval)
        {
            return Math.Abs(
                    firstInterval.Maximum - secondInterval.Minimum) <=
                    GeometryTolerance ||
                Math.Abs(
                    secondInterval.Maximum - firstInterval.Minimum) <=
                    GeometryTolerance;
        }

        private static double GetThicknessIntervalOverlap(
            ThicknessInterval firstInterval,
            ThicknessInterval secondInterval)
        {
            return Math.Min(firstInterval.Maximum, secondInterval.Maximum) -
                Math.Max(firstInterval.Minimum, secondInterval.Minimum);
        }

        private static bool TryCreateDoubleStartHorizontalBridgeAccessory(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            out LinkageRouteAccessory bridgeAccessory,
            out string failureReason)
        {
            return TryCreateDoubleStartHorizontalBridgeAccessoryForPlacement(
                conversionResult,
                routeRequest,
                routeGroupRequest,
                null,
                out bridgeAccessory,
                out failureReason);
        }

        private static bool TryCreateDoubleStartHorizontalBridgeAccessoryForPlacement(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageRouteTerminalBridgePlacement bridgePlacement,
            out LinkageRouteAccessory bridgeAccessory,
            out string failureReason)
        {
            bridgeAccessory = null;
            failureReason = null;
            var usesSectionSpecificOutput = conversionResult
                    .StartTerminalMemberIndices.Count > 0 ||
                conversionResult.EndTerminalMemberIndices.Count > 0;
            var bridgeGeneratedMemberIndex = bridgePlacement?.GeneratedMemberIndex ??
                conversionResult.StartStackPlacement
                    .DoubleStartHorizontalBridgeGeneratedMemberIndex.Value;
            var bridgeGeneratedMember = conversionResult.StackMembers
                .FirstOrDefault(stackedRouteMember =>
                    stackedRouteMember.MemberIndex == bridgeGeneratedMemberIndex);
            var bridgeGeneratedSetting = bridgeGeneratedMember?.PlanarParts
                .FirstOrDefault();
            if (bridgeGeneratedSetting == null ||
                !TryGetConnectionFixedBusbar(
                    routeGroupRequest.StartTerminal,
                    out var collectiveStartFixedBusbar))
            {
                failureReason = "双始端横向桥排无法解析实际搭接矩形。";
                return false;
            }

            var terminalThicknessDirection = routeGroupRequest.StartTerminal
                .ThicknessDirection.Normalized;
            var bridgeLengthDirection = collectiveStartFixedBusbar.WidthVector
                .Normalized;
            // 双始端桥排是独立附件，不能让主路径首段的宽度截短
            // 桥排与固定始端的垂直搭接区域。这里只替换搭接参考
            // 对象，绝不修改 conversionResult 中的主路径分段。
            var bridgeBusbarWidth = routeGroupRequest.GetDoubleStartBridgeBusbarWidth(routeRequest);
            var bridgeLapReferenceSetting = bridgeGeneratedSetting.Copy();
            bridgeLapReferenceSetting.BusBarWidth = bridgeBusbarWidth;
            var bridgeLapFootprintCreated =
                LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                    bridgeLapReferenceSetting,
                    true,
                    collectiveStartFixedBusbar,
                    true,
                    usesSectionSpecificOutput
                        ? routeGroupRequest.GetStartLapBusbarWidth(routeRequest)
                        : routeGroupRequest.GetBusbarWidth(routeRequest),
                    out var bridgeLapFootprint);
            if (!bridgeLapFootprintCreated)
            {
                // 历史输入可能只有旧的单段接触数据；保留原参考对象，
                // 使本次输出修复不会把原本可用的候选变成无方案。
                bridgeLapFootprintCreated =
                    LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                        bridgeGeneratedSetting,
                        true,
                        collectiveStartFixedBusbar,
                        true,
                        usesSectionSpecificOutput
                            ? routeGroupRequest.GetStartLapBusbarWidth(routeRequest)
                            : routeGroupRequest.GetBusbarWidth(routeRequest),
                        out bridgeLapFootprint);
            }
            // 连接排可以沿自身排长方向延伸到双始端整体范围之外。旧逻辑
            // 在这里要求“当前生成排首段”和固定始端立即存在投影搭接，
            // 会把本应由连接排延伸解决的候选提前淘汰。没有现成投影时
            // 仍以固定始端整体参考点建立桥排，后续由连接排长度覆盖校验
            // 和最终实体门禁确认真实搭接，不能在此处误删候选。
            var bridgeFootprintCenter = bridgeLapFootprintCreated
                ? AveragePoint(bridgeLapFootprint.Corners)
                : ProjectToPlane(
                    bridgeGeneratedSetting.PointList[0],
                    collectiveStartFixedBusbar.PointList[0],
                    terminalThicknessDirection);

            var bridgeWidthDirectionVector = terminalThicknessDirection.Cross(
                bridgeLengthDirection);
            if (bridgeWidthDirectionVector.Norm <= GeometryTolerance)
            {
                failureReason = "双始端横向桥排无法建立排宽方向。";
                return false;
            }

            var bridgeWidthDirection = bridgeWidthDirectionVector.Normalized;
            // 固定端 PointList[0] 只是几何序列的起点，不一定是本次始端
            // 实际选中的最近搭接边。CreateTerminalAssembly 已经把每个
            // 成员选中的边界汇总到 RouteAnchor；输出连接排必须以该真实
            // 搭接边为排长方向基准，否则当某些固定排的有效出口位于
            // PointList 末端时，连接排会在始端路径起点留下小段间隙。
            var collectiveFixedReferencePoint =
                ResolveStartTerminalBridgeReferencePoint(
                    routeGroupRequest.StartTerminal,
                    collectiveStartFixedBusbar);
            var bridgeCenterPoint = ProjectToPlane(
                bridgeFootprintCenter,
                collectiveFixedReferencePoint,
                terminalThicknessDirection);
            var bridgeWidthCenterDisplacement = new Vector3d(
                    collectiveFixedReferencePoint,
                    bridgeCenterPoint)
                .Dot(bridgeLengthDirection);
            bridgeCenterPoint = Translate(
                bridgeCenterPoint,
                bridgeLengthDirection,
                -bridgeWidthCenterDisplacement);
            bridgeCenterPoint = ClampBridgeCenterToFixedStartLapRange(
                bridgeCenterPoint,
                bridgeWidthDirection,
                bridgeBusbarWidth,
                collectiveStartFixedBusbar,
                routeGroupRequest.StartTerminal.RouteAnchor);
            var bridgeSingleBusbarThickness = bridgePlacement?.Thickness > 0d
                ? bridgePlacement.Thickness
                : conversionResult.StartStackPlacement
                    .DoubleStartHorizontalBridgeThickness;
            var bridgeSurfaceSideSign = bridgePlacement?.SideSign != 0
                ? bridgePlacement.SideSign
                : conversionResult.StartStackPlacement
                    .DoubleStartHorizontalBridgeSideSign;
            bridgeCenterPoint = Translate(
                bridgeCenterPoint,
                terminalThicknessDirection,
                bridgeSurfaceSideSign *
                (collectiveStartFixedBusbar.BusBarThickness / 2d +
                 bridgeSingleBusbarThickness / 2d));
            var bridgeHalfLength = collectiveStartFixedBusbar.BusBarWidth / 2d;
            var bridgeMinimumLengthCoordinate = -bridgeHalfLength;
            var bridgeMaximumLengthCoordinate = bridgeHalfLength;
            if (routeGroupRequest.StartTerminal.Members.Count > 1 &&
                bridgeGeneratedSetting.PointList != null &&
                bridgeGeneratedSetting.PointList.Count >= 2)
            {
                // 短搭接会把中间排始端向固定排远侧推进。当推进后的首段已
                // 完全落到双始端连接排原长度区间之外时，只延长连接排到该
                // 中间排的最近边缘，消除两者之间的实体缝隙；仍与普通搜索
                // 使用的满搭接层位和路径无关。
                var generatedStartLengthCoordinate = new Vector3d(
                        bridgeCenterPoint,
                        bridgeGeneratedSetting.PointList[0])
                    .Dot(bridgeLengthDirection);
                var generatedAdjacentLengthCoordinate = new Vector3d(
                        bridgeCenterPoint,
                        bridgeGeneratedSetting.PointList[1])
                    .Dot(bridgeLengthDirection);
                var generatedHalfWidthAlongBridgeLength =
                    bridgeGeneratedSetting.WidthVector == null ||
                    bridgeGeneratedSetting.WidthVector.Norm <= GeometryTolerance
                        ? 0d
                        : Math.Abs(bridgeGeneratedSetting.WidthVector.Normalized
                            .Dot(bridgeLengthDirection)) *
                          bridgeGeneratedSetting.BusBarWidth / 2d;
                var generatedSegmentMinimumLengthCoordinate = Math.Min(
                    generatedStartLengthCoordinate,
                    generatedAdjacentLengthCoordinate) -
                    generatedHalfWidthAlongBridgeLength;
                var generatedSegmentMaximumLengthCoordinate = Math.Max(
                    generatedStartLengthCoordinate,
                    generatedAdjacentLengthCoordinate) +
                    generatedHalfWidthAlongBridgeLength;
                if (generatedSegmentMinimumLengthCoordinate <
                    bridgeMinimumLengthCoordinate - GeometryTolerance)
                {
                    bridgeMinimumLengthCoordinate =
                        generatedSegmentMinimumLengthCoordinate;
                }
                if (generatedSegmentMaximumLengthCoordinate >
                    bridgeMaximumLengthCoordinate + GeometryTolerance)
                {
                    bridgeMaximumLengthCoordinate =
                        generatedSegmentMaximumLengthCoordinate;
                }
            }

            var bridgeSetting = new PlanarBendBusbarSetting
            {
                IsCenter = true,
                PointList = new List<Point3d>
                {
                    Translate(
                        bridgeCenterPoint,
                        bridgeLengthDirection,
                        bridgeMinimumLengthCoordinate),
                    Translate(
                        bridgeCenterPoint,
                        bridgeLengthDirection,
                        bridgeMaximumLengthCoordinate)
                },
                StartBusbarVector = CopyVector(terminalThicknessDirection),
                WidthVector = CopyVector(bridgeWidthDirection),
                BusBarWidth = bridgeBusbarWidth,
                BusBarThickness = bridgeSingleBusbarThickness,
                BarBendingRadius = routeRequest.BendingRadius,
                BendAllowance = routeRequest.GetEffectiveBendAllowance(),
                GroupKey = routeGroupRequest.GroupKey
            };
            bridgeAccessory = new LinkageRouteAccessory
            {
                Kind = LinkageRouteAccessoryKind.DoubleStartHorizontalBridge,
                TerminalSide = LinkageRouteTerminalSide.Start,
                RelatedGeneratedMemberIndex = bridgeGeneratedMemberIndex,
                Setting = bridgeSetting
            };
            return true;
        }

        internal static bool TryRebuildHoleShiftTerminalSupports(
            LinkageRouteCenterlineConversionResult conversion, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, out string failureReason)
        {
            failureReason = null;
            // 预制诊断实体没有层位信息时不猜造垫块；完整接通门禁仍必须通过。
            if (conversion.StartStackPlacement == null || conversion.EndStackPlacement == null) return true;
            foreach (var oldSupport in conversion.Accessories.Where(item => item.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                string.IsNullOrWhiteSpace(item.SupportBlockKey)).ToList()) conversion.Accessories.Remove(oldSupport);
            var bridge = conversion.Accessories.Where(item => item.Kind == LinkageRouteAccessoryKind.DoubleStartHorizontalBridge)
                .OrderByDescending(item => item.Setting.GetDevelopedLength()).FirstOrDefault();
            if (!request.ShouldUseSpacerlessTerminalLap(true) &&
                !TryAddTerminalSupportBlocks(conversion, request, group, group.StartTerminal,
                    conversion.StartStackPlacement, true, true, bridge, out failureReason)) return false;
            return request.ShouldUseSpacerlessTerminalLap(false) ||
                TryAddTerminalSupportBlocks(conversion, request, group, group.EndTerminal,
                    conversion.EndStackPlacement, false, false, null, out failureReason);
        }

        internal static bool TryRebuildFinalStartBridgeSupportBlocks(
            LinkageRouteCenterlineConversionResult conversion, LinkageMultiGroupRouteRequest request,
            LinkageRouteGroupRequest group, out string failureReason)
        {
            failureReason = null;
            if (request == null || group?.StartTerminal == null || request.ShouldUseSpacerlessTerminalLap(true))
                return true;
            var bridge = conversion.Accessories.Where(accessory => accessory?.Kind ==
                    LinkageRouteAccessoryKind.DoubleStartHorizontalBridge && accessory.Setting?.PointList?.Count >= 2)
                .OrderByDescending(accessory => accessory.Setting.GetDevelopedLength()).FirstOrDefault();
            if (bridge == null)
            {
                AddProtrudingStartBridgeSupportBlocks(conversion, request, group);
                return true;
            }
            if (conversion.StartStackPlacement == null) return true;
            var previousStartSupports = conversion.Accessories.Where(accessory => accessory?.Kind ==
                    LinkageRouteAccessoryKind.SupportBlock && accessory.TerminalSide == LinkageRouteTerminalSide.Start &&
                    string.IsNullOrWhiteSpace(accessory.SupportBlockKey)).ToList();
            foreach (var support in previousStartSupports) conversion.Accessories.Remove(support);
            if (TryAddTerminalSupportBlocks(conversion, request, group, group.StartTerminal,
                    conversion.StartStackPlacement, true, true, bridge, out failureReason)) return true;
            for (var index = conversion.Accessories.Count - 1; index >= 0; index--)
                if (conversion.Accessories[index]?.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                    conversion.Accessories[index].TerminalSide == LinkageRouteTerminalSide.Start &&
                    string.IsNullOrWhiteSpace(conversion.Accessories[index].SupportBlockKey))
                    conversion.Accessories.RemoveAt(index);
            foreach (var support in previousStartSupports) conversion.Accessories.Add(support);
            return false;
        }

        private static bool TryAddTerminalSupportBlocks(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            bool useStartTerminal,
            bool fillOccupiedGapResidualSpace,
            LinkageRouteAccessory doubleStartBridgeAccessory,
            out string failureReason)
        {
            failureReason = null;
            var usesSectionSpecificOutput = conversionResult
                    .StartTerminalMemberIndices.Count > 0 ||
                conversionResult.EndTerminalMemberIndices.Count > 0;
            var materializedTerminalMemberCount = useStartTerminal
                ? conversionResult.StartTerminalMemberIndices?.Count ?? 0
                : conversionResult.EndTerminalMemberIndices?.Count ?? 0;
            var terminalGeneratedStackCount = materializedTerminalMemberCount > 0
                ? materializedTerminalMemberCount
                : usesSectionSpecificOutput
                ? useStartTerminal
                    ? routeGroupRequest.GetStartLapStackCount(routeRequest)
                    : routeGroupRequest.GetCabinetTopLapStackCount(routeRequest)
                : routeGroupRequest.GetStackCount(routeRequest);
            if (terminalGeneratedStackCount <= 1 && useStartTerminal)
            {
                return true;
            }

            if (!TryBuildGeneratedTerminalLayers(
                    conversionResult.StackMembers,
                    terminalAssembly,
                    useStartTerminal,
                    useStartTerminal
                        ? conversionResult.StartTerminalMemberIndices
                        : conversionResult.EndTerminalMemberIndices,
                    out var generatedTerminalLayers,
                    out var terminalReferencePoint) ||
                !TryBuildFixedTerminalLayers(
                    terminalAssembly,
                    terminalReferencePoint,
                    out var fixedTerminalLayers))
            {
                failureReason = (useStartTerminal ? "始端" : "柜顶") +
                    "垫块无法解析叠排和固定排厚度层。";
                return false;
            }

            var terminalThicknessDirection = terminalAssembly
                .ThicknessDirection.Normalized;
            var referenceLapFootprints = BuildReferenceLapFootprints(
                generatedTerminalLayers,
                fixedTerminalLayers,
                terminalPlacement,
                terminalAssembly,
                useStartTerminal,
                usesSectionSpecificOutput
                    ? useStartTerminal
                        ? routeGroupRequest.GetStartLapBusbarWidth(routeRequest)
                        : routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest)
                    : routeGroupRequest.GetBusbarWidth(routeRequest),
                doubleStartBridgeAccessory);
            if (fillOccupiedGapResidualSpace)
            {
                if (!TryAddAllStartResidualSupportBlocks(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    terminalAssembly,
                    terminalPlacement,
                    generatedTerminalLayers,
                    fixedTerminalLayers,
                    terminalReferencePoint,
                    terminalThicknessDirection,
                    referenceLapFootprints,
                    doubleStartBridgeAccessory,
                    out failureReason)) return false;
                AddProtrudingStartBridgeSupportBlocks(conversionResult, routeRequest, routeGroupRequest);
                return true;
            }

            for (var generatedGapIndex = 0;
                generatedGapIndex < generatedTerminalLayers.Count - 1;
                generatedGapIndex++)
            {
                var lowerGeneratedLayer = generatedTerminalLayers[
                    generatedGapIndex];
                var upperGeneratedLayer = generatedTerminalLayers[
                    generatedGapIndex + 1];
                var generatedGapMinimum = lowerGeneratedLayer.CenterCoordinate +
                    lowerGeneratedLayer.Thickness / 2d;
                var generatedGapMaximum = upperGeneratedLayer.CenterCoordinate -
                    upperGeneratedLayer.Thickness / 2d;
                if (generatedGapMaximum - generatedGapMinimum <=
                    GeometryTolerance)
                {
                    continue;
                }

                var fixedLayersInsideGap = fixedTerminalLayers
                    .Where(fixedTerminalLayer =>
                        fixedTerminalLayer.MaximumCoordinate >
                            generatedGapMinimum + GeometryTolerance &&
                        fixedTerminalLayer.MinimumCoordinate <
                            generatedGapMaximum - GeometryTolerance)
                    .ToList();
                if (fixedLayersInsideGap.Count == 0)
                {
                    if (!TryResolveEmptyGapFootprint(
                            lowerGeneratedLayer,
                            upperGeneratedLayer,
                            useStartTerminal,
                            usesSectionSpecificOutput
                                ? useStartTerminal
                                    ? routeGroupRequest.GetStartLapBusbarWidth(
                                        routeRequest)
                                    : routeGroupRequest.GetCabinetTopLapBusbarWidth(
                                        routeRequest)
                                : routeGroupRequest.GetBusbarWidth(routeRequest),
                            referenceLapFootprints,
                            out var emptyGapFootprint))
                    {
                        failureReason = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}第 {1} 条未占用叠排夹缝无法取得垫块搭接区域。",
                            useStartTerminal ? "始端" : "柜顶",
                            generatedGapIndex + 1);
                        return false;
                    }

                    conversionResult.Accessories.Add(CreateSupportBlockAccessory(
                        emptyGapFootprint,
                        terminalReferencePoint,
                        terminalThicknessDirection,
                        generatedGapMinimum,
                        generatedGapMaximum,
                        routeRequest,
                        routeGroupRequest,
                        useStartTerminal,
                        lowerGeneratedLayer.MemberIndex));
                    continue;
                }

            }

            if (!useStartTerminal)
            {
                AddCabinetResidualSupportBlocks(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    terminalAssembly,
                    generatedTerminalLayers,
                    fixedTerminalLayers,
                    terminalReferencePoint,
                    terminalThicknessDirection,
                    usesSectionSpecificOutput
                        ? routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest)
                        : routeGroupRequest.GetBusbarWidth(routeRequest));
            }

            var hasCompactFixedGap = !useStartTerminal &&
                fixedTerminalLayers.Count >= 2 &&
                Enumerable.Range(0, fixedTerminalLayers.Count - 1).Any(
                    fixedGapIndex =>
                        fixedTerminalLayers[fixedGapIndex + 1].MinimumCoordinate -
                        fixedTerminalLayers[fixedGapIndex].MaximumCoordinate +
                            GeometryTolerance <
                        generatedTerminalLayers.Min(
                            generatedTerminalLayer => generatedTerminalLayer.Thickness));
            if ((fixedTerminalLayers.Count > generatedTerminalLayers.Count ||
                    hasCompactFixedGap) &&
                !TryAddUnoccupiedFixedHostGapSupportBlocks(
                    conversionResult,
                    routeRequest,
                    routeGroupRequest,
                    terminalAssembly,
                    useStartTerminal,
                    generatedTerminalLayers,
                    fixedTerminalLayers,
                    terminalReferencePoint,
                    terminalThicknessDirection,
                    referenceLapFootprints,
                    usesSectionSpecificOutput
                        ? useStartTerminal
                            ? routeGroupRequest.GetStartLapBusbarWidth(routeRequest)
                            : routeGroupRequest.GetCabinetTopLapBusbarWidth(routeRequest)
                        : routeGroupRequest.GetBusbarWidth(routeRequest),
                    doubleStartBridgeAccessory,
                    out failureReason))
            {
                return false;
            }

            return true;
        }

        // 普通柜顶的夹缝即使已插入生成排，仍须补齐其与固定排之间的
        // 剩余厚差。仅取最终实际相邻层和两者有限搭接投影，不使用其它
        // 接口的参考投影，不改变任何固定端或已经成功的生成排层位。
        private static void AddCabinetResidualSupportBlocks(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageTerminalAssembly terminalAssembly,
            IReadOnlyList<GeneratedTerminalLayer> generatedTerminalLayers,
            IReadOnlyList<FixedTerminalLayer> fixedTerminalLayers,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            double requiredBusbarWidth)
        {
            foreach (var generatedLayer in generatedTerminalLayers)
            {
                if (!LinkageRouteTerminalLapRegionValidator
                    .TryCreateProjectedLapFootprint(
                        generatedLayer.Setting, false,
                        generatedLayer.Setting, false,
                        requiredBusbarWidth, out var generatedFootprint))
                    continue;

                var footprintOrigin = generatedFootprint.Corners[0];
                var footprintLengthDirection = generatedFootprint.LengthDirection.Normalized;
                var footprintWidthDirection = generatedFootprint.WidthDirection.Normalized;
                var occupants = new List<CabinetSupportOccupant>();
                foreach (var fixedMemberIndex in fixedTerminalLayers
                    .SelectMany(layer => layer.FixedMemberIndices).Distinct())
                {
                    AddCabinetSupportOccupant(
                        occupants, generatedLayer.Setting,
                        terminalAssembly.Members[fixedMemberIndex]?.FixedBusbar,
                        true, terminalReferencePoint, terminalThicknessDirection,
                        footprintOrigin, footprintLengthDirection,
                        footprintWidthDirection, requiredBusbarWidth);
                }
                foreach (var occupiedGeneratedLayer in generatedTerminalLayers)
                {
                    AddCabinetSupportOccupant(
                        occupants, generatedLayer.Setting, occupiedGeneratedLayer.Setting,
                        false, terminalReferencePoint, terminalThicknessDirection,
                        footprintOrigin, footprintLengthDirection,
                        footprintWidthDirection, requiredBusbarWidth);
                }
                foreach (var supportAccessory in conversionResult.Accessories.Where(
                    accessory => accessory.Kind == LinkageRouteAccessoryKind.SupportBlock &&
                        accessory.TerminalSide == LinkageRouteTerminalSide.End))
                {
                    AddCabinetSupportOccupant(
                        occupants, generatedLayer.Setting, supportAccessory.Setting,
                        false, terminalReferencePoint, terminalThicknessDirection,
                        footprintOrigin, footprintLengthDirection,
                        footprintWidthDirection, requiredBusbarWidth);
                }

                // 按有限投影的实际边界分区；在其它位置的铜排不能占满本区，
                // 局部占位也不能遮掉整个搭接面。各区只补生成排与最近固定排
                // 之间的正间隙，已有铜排及垫块仍按其真实厚向区间扣除。
                var partitionOccupants = occupants.Select(occupant => new LinkageSupportProjectionOccupant
                {
                    LengthMinimum = occupant.LengthMinimum, LengthMaximum = occupant.LengthMaximum,
                    WidthMinimum = occupant.WidthMinimum, WidthMaximum = occupant.WidthMaximum
                }).ToArray();
                var generatedInterval = CreateThicknessInterval(
                    generatedLayer.CenterCoordinate, generatedLayer.Thickness);
                foreach (var partitionCell in LinkageSupportProjectionPartition.Partition(partitionOccupants))
                {
                    var cellLengthMinimum = partitionCell.LengthMinimum;
                    var cellLengthMaximum = partitionCell.LengthMaximum;
                    var cellWidthMinimum = partitionCell.WidthMinimum;
                    var cellWidthMaximum = partitionCell.WidthMaximum;
                    var cellOccupants = partitionCell.OccupantIndices.Select(index => occupants[index]).ToList();
                    if (!cellOccupants.Any(occupant => occupant.IsFixedTerminal))
                        continue;
                    var residualIntervals = GetResidualIntervals(
                        cellOccupants.Min(occupant => occupant.Interval.Minimum),
                        cellOccupants.Max(occupant => occupant.Interval.Maximum),
                        cellOccupants.Select(occupant => occupant.Interval));
                    foreach (var residualInterval in residualIntervals)
                    {
                        var generatedIsLowerBoundary = Math.Abs(
                            generatedInterval.Maximum - residualInterval.Minimum) <= GeometryTolerance;
                        var generatedIsUpperBoundary = Math.Abs(
                            generatedInterval.Minimum - residualInterval.Maximum) <= GeometryTolerance;
                        if (!cellOccupants.Any(occupant => occupant.IsFixedTerminal &&
                            (generatedIsLowerBoundary && Math.Abs(occupant.Interval.Minimum -
                                residualInterval.Maximum) <= GeometryTolerance ||
                             generatedIsUpperBoundary && Math.Abs(occupant.Interval.Maximum -
                                residualInterval.Minimum) <= GeometryTolerance)))
                            continue;
                        var residualFootprint = new LinkageRouteLapFootprint
                        {
                            Corners = new[]
                            {
                                Translate(Translate(footprintOrigin, footprintLengthDirection,
                                    cellLengthMinimum), footprintWidthDirection, cellWidthMinimum),
                                Translate(Translate(footprintOrigin, footprintLengthDirection,
                                    cellLengthMinimum), footprintWidthDirection, cellWidthMaximum),
                                Translate(Translate(footprintOrigin, footprintLengthDirection,
                                    cellLengthMaximum), footprintWidthDirection, cellWidthMinimum),
                                Translate(Translate(footprintOrigin, footprintLengthDirection,
                                    cellLengthMaximum), footprintWidthDirection, cellWidthMaximum)
                            },
                            LengthDirection = footprintLengthDirection,
                            WidthDirection = footprintWidthDirection,
                            PlaneNormal = generatedFootprint.PlaneNormal
                        };
                        conversionResult.Accessories.Add(CreateSupportBlockAccessory(
                            residualFootprint, terminalReferencePoint,
                            terminalThicknessDirection, residualInterval.Minimum,
                            residualInterval.Maximum, routeRequest, routeGroupRequest,
                            false, generatedLayer.MemberIndex));
                    }
                }
            }
        }

        private static void AddCabinetSupportOccupant(
            ICollection<CabinetSupportOccupant> occupants,
            PlanarBendBusbarSetting generatedSetting,
            PlanarBendBusbarSetting occupiedSetting,
            bool isFixedTerminal,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            Point3d footprintOrigin,
            Vector3d footprintLengthDirection,
            Vector3d footprintWidthDirection,
            double requiredBusbarWidth)
        {
            if (!LinkageRouteTerminalLapRegionValidator.TryCreateProjectedLapFootprint(
                generatedSetting, false, occupiedSetting, false,
                requiredBusbarWidth, out var occupiedFootprint))
                return;
            var lengthCoordinates = occupiedFootprint.Corners.Select(corner =>
                new Vector3d(footprintOrigin, corner).Dot(footprintLengthDirection)).ToArray();
            var widthCoordinates = occupiedFootprint.Corners.Select(corner =>
                new Vector3d(footprintOrigin, corner).Dot(footprintWidthDirection)).ToArray();
            var centerCoordinate = new Vector3d(
                terminalReferencePoint, occupiedSetting.PointList.Last())
                .Dot(terminalThicknessDirection);
            occupants.Add(new CabinetSupportOccupant
            {
                Interval = CreateThicknessInterval(centerCoordinate, occupiedSetting.BusBarThickness),
                IsFixedTerminal = isFixedTerminal,
                LengthMinimum = lengthCoordinates.Min(),
                LengthMaximum = lengthCoordinates.Max(),
                WidthMinimum = widthCoordinates.Min(),
                WidthMaximum = widthCoordinates.Max()
            });
        }

        private static bool TryAddAllStartResidualSupportBlocks(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            IReadOnlyList<GeneratedTerminalLayer> generatedTerminalLayers,
            IReadOnlyList<FixedTerminalLayer> fixedTerminalLayers,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            IReadOnlyList<LinkageRouteLapFootprint> referenceLapFootprints,
            LinkageRouteAccessory doubleStartBridgeAccessory,
            out string failureReason)
        {
            failureReason = null;
            var usesSectionSpecificOutput = conversionResult
                    .StartTerminalMemberIndices.Count > 0 ||
                conversionResult.EndTerminalMemberIndices.Count > 0;
            var occupiedIntervals = generatedTerminalLayers
                .Select(generatedTerminalLayer => new ThicknessInterval
                {
                    Minimum = generatedTerminalLayer.CenterCoordinate -
                        generatedTerminalLayer.Thickness / 2d,
                    Maximum = generatedTerminalLayer.CenterCoordinate +
                        generatedTerminalLayer.Thickness / 2d
                })
                .Concat(fixedTerminalLayers.Select(fixedTerminalLayer =>
                    new ThicknessInterval
                    {
                        Minimum = fixedTerminalLayer.MinimumCoordinate,
                        Maximum = fixedTerminalLayer.MaximumCoordinate
                    }))
                .ToList();
            foreach (var bridgeAccessory in conversionResult.Accessories.Where(
                routeAccessory => routeAccessory?.Kind ==
                        LinkageRouteAccessoryKind.DoubleStartHorizontalBridge &&
                    routeAccessory.Setting?.PointList?.Count >= 2))
            {
                var bridgeCenterCoordinate = new Vector3d(
                        terminalReferencePoint,
                        bridgeAccessory.Setting.PointList[0])
                    .Dot(terminalThicknessDirection);
                occupiedIntervals.Add(new ThicknessInterval
                {
                    Minimum = bridgeCenterCoordinate -
                        bridgeAccessory.Setting.BusBarThickness / 2d,
                    Maximum = bridgeCenterCoordinate +
                        bridgeAccessory.Setting.BusBarThickness / 2d
                });
            }

            var occupiedMinimum = occupiedIntervals.Min(
                thicknessInterval => thicknessInterval.Minimum);
            var occupiedMaximum = occupiedIntervals.Max(
                thicknessInterval => thicknessInterval.Maximum);
            var residualIntervals = GetResidualIntervals(
                occupiedMinimum,
                occupiedMaximum,
                occupiedIntervals);
            foreach (var residualInterval in residualIntervals)
            {
                IReadOnlyList<LinkageRouteLapFootprint>
                    doubleStartResidualSupportFootprints = null;
                if (doubleStartBridgeAccessory?.Setting != null ||
                    terminalAssembly?.Members?.Count >= 2)
                {
                    doubleStartResidualSupportFootprints =
                        BuildActualDoubleStartResidualSupportFootprints(
                            residualInterval,
                            generatedTerminalLayers,
                            fixedTerminalLayers,
                            terminalAssembly,
                            terminalPlacement,
                            terminalReferencePoint,
                            terminalThicknessDirection,
                            doubleStartBridgeAccessory,
                            usesSectionSpecificOutput
                                ? routeGroupRequest.GetStartLapBusbarWidth(
                                    routeRequest)
                                : routeGroupRequest.GetBusbarWidth(routeRequest));
                    if (doubleStartResidualSupportFootprints.Count == 0)
                    {
                        // 双始端横向桥排只允许在真实搭接区域内补垫块。
                        // Contacts 仅表示层位求解时的直接接触，不能代表
                        // 中间仍有待填夹缝的生成排是否具有平面搭接区域。
                        continue;
                    }
                }

                var nearestGeneratedLayer = generatedTerminalLayers
                    .OrderBy(generatedTerminalLayer => Math.Min(
                        Math.Abs(residualInterval.Minimum -
                            (generatedTerminalLayer.CenterCoordinate +
                             generatedTerminalLayer.Thickness / 2d)),
                        Math.Abs(residualInterval.Maximum -
                            (generatedTerminalLayer.CenterCoordinate -
                             generatedTerminalLayer.Thickness / 2d))))
                    .First();
                var nearestFixedLayer = fixedTerminalLayers
                    .OrderBy(fixedTerminalLayer => Math.Min(
                        Math.Abs(residualInterval.Minimum -
                            fixedTerminalLayer.MaximumCoordinate),
                        Math.Abs(residualInterval.Maximum -
                            fixedTerminalLayer.MinimumCoordinate)))
                    .FirstOrDefault();
                var supportFootprints = doubleStartResidualSupportFootprints !=
                        null
                    ? doubleStartResidualSupportFootprints.ToList()
                    : nearestFixedLayer == null
                        ? new List<LinkageRouteLapFootprint>()
                        : BuildFixedLayerSupportFootprints(
                            nearestGeneratedLayer,
                            nearestFixedLayer,
                            terminalAssembly,
                            true,
                            usesSectionSpecificOutput
                                ? routeGroupRequest.GetStartLapBusbarWidth(
                                    routeRequest)
                                : routeGroupRequest.GetBusbarWidth(routeRequest),
                            doubleStartBridgeAccessory,
                            referenceLapFootprints).ToList();
                if (supportFootprints.Count == 0 &&
                    referenceLapFootprints.Count > 0)
                {
                    supportFootprints.Add(referenceLapFootprints[0]);
                }

                if (supportFootprints.Count == 0)
                {
                    failureReason = "始端厚差夹缝无法取得垫块搭接区域。";
                    return false;
                }

                foreach (var supportFootprint in supportFootprints)
                {
                    conversionResult.Accessories.Add(
                        CreateSupportBlockAccessory(
                            supportFootprint,
                            terminalReferencePoint,
                            terminalThicknessDirection,
                            residualInterval.Minimum,
                            residualInterval.Maximum,
                            routeRequest,
                            routeGroupRequest,
                            true,
                            nearestGeneratedLayer.MemberIndex));
                }
            }

            return true;
        }

        private static IReadOnlyList<LinkageRouteLapFootprint>
            BuildActualDoubleStartResidualSupportFootprints(
            ThicknessInterval residualInterval,
            IReadOnlyList<GeneratedTerminalLayer> generatedTerminalLayers,
            IReadOnlyList<FixedTerminalLayer> fixedTerminalLayers,
            LinkageTerminalAssembly terminalAssembly,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            LinkageRouteAccessory doubleStartBridgeAccessory,
            double requiredBusbarWidth)
        {
            var boundaryGeneratedLayers = generatedTerminalLayers
                .Where(generatedTerminalLayer =>
                    Math.Abs(
                        residualInterval.Minimum -
                        (generatedTerminalLayer.CenterCoordinate +
                         generatedTerminalLayer.Thickness / 2d)) <=
                        GeometryTolerance ||
                    Math.Abs(
                        residualInterval.Maximum -
                        (generatedTerminalLayer.CenterCoordinate -
                         generatedTerminalLayer.Thickness / 2d)) <=
                        GeometryTolerance)
                .ToList();
            if (boundaryGeneratedLayers.Count == 0)
            {
                return Array.Empty<LinkageRouteLapFootprint>();
            }

            // 多始端连接排不再直接复制为等长等宽垫块。这里仅为真实
            // 局部厚向夹缝计算实际投影交集；水平非 I 排已经在上层完成
            // 端点贴合，因此不会进入此分支。
            var residualTouchesDoubleStartBridge =
                IsResidualAdjacentToDoubleStartBridge(
                    residualInterval,
                    terminalReferencePoint,
                    terminalThicknessDirection,
                    doubleStartBridgeAccessory);
            if (terminalAssembly?.Members?.Count >= 2 &&
                !residualTouchesDoubleStartBridge &&
                LinkageRouteDoubleStartLapGeometry.TryCreateCollectiveFixedBusbar(
                    terminalAssembly,
                    out var collectiveFixedBusbar))
            {
                var collectiveFootprints = new List<LinkageRouteLapFootprint>();
                foreach (var boundaryGeneratedLayer in boundaryGeneratedLayers)
                {
                    if (!LinkageRouteTerminalLapRegionValidator
                            .TryCreateProjectedLapFootprint(
                                boundaryGeneratedLayer.Setting,
                                true,
                                collectiveFixedBusbar,
                                true,
                                requiredBusbarWidth,
                                out var collectiveFootprint))
                    {
                        collectiveFootprints.Clear();
                        break;
                    }

                    collectiveFootprints.Add(collectiveFootprint);
                }

                if (collectiveFootprints.Count == boundaryGeneratedLayers.Count &&
                    TryIntersectLapFootprints(
                        collectiveFootprints,
                        out var collectiveSupportFootprint))
                {
                    return new[] { collectiveSupportFootprint };
                }
            }

            var nearestFixedLayer = fixedTerminalLayers
                .OrderBy(fixedTerminalLayer => Math.Min(
                    Math.Abs(residualInterval.Minimum -
                        fixedTerminalLayer.MaximumCoordinate),
                    Math.Abs(residualInterval.Maximum -
                        fixedTerminalLayer.MinimumCoordinate)))
                .FirstOrDefault();
            var sharedFixedMemberFootprints = new List<
                LinkageRouteLapFootprint>();
            foreach (var fixedMemberIndex in
                residualTouchesDoubleStartBridge
                    ? Array.Empty<int>()
                    : nearestFixedLayer?.FixedMemberIndices ?? Array.Empty<int>())
            {
                if (fixedMemberIndex < 0 ||
                    fixedMemberIndex >= terminalAssembly.Members.Count)
                {
                    continue;
                }

                var fixedMemberSetting = terminalAssembly.Members[
                    fixedMemberIndex]?.FixedBusbar;
                var generatedFixedFootprints = new List<
                    LinkageRouteLapFootprint>();
                foreach (var boundaryGeneratedLayer in boundaryGeneratedLayers)
                {
                    if (!LinkageRouteTerminalLapRegionValidator
                            .TryCreateProjectedLapFootprint(
                                boundaryGeneratedLayer.Setting,
                                true,
                                fixedMemberSetting,
                                true,
                                requiredBusbarWidth,
                                out var generatedFixedFootprint))
                    {
                        generatedFixedFootprints.Clear();
                        break;
                    }

                    generatedFixedFootprints.Add(generatedFixedFootprint);
                }

                if (generatedFixedFootprints.Count ==
                        boundaryGeneratedLayers.Count &&
                    TryIntersectLapFootprints(
                        generatedFixedFootprints,
                        out var sharedFixedMemberFootprint))
                {
                    // 两根始端必须分别保留自己的真实搭接矩形，不能把
                    // 中间无实体区域合并成一整块垫块。
                    sharedFixedMemberFootprints.Add(
                        sharedFixedMemberFootprint);
                }
            }

            if (sharedFixedMemberFootprints.Count > 0)
            {
                return sharedFixedMemberFootprints;
            }

            if (!IsResidualOnDoubleStartBridgeLapSide(
                    residualInterval,
                    terminalPlacement,
                    terminalReferencePoint,
                    terminalThicknessDirection,
                    doubleStartBridgeAccessory))
            {
                return Array.Empty<LinkageRouteLapFootprint>();
            }

            var generatedBridgeFootprints = new List<
                LinkageRouteLapFootprint>();
            foreach (var boundaryGeneratedLayer in boundaryGeneratedLayers)
            {
                if (!LinkageRouteTerminalLapRegionValidator
                        .TryCreateProjectedLapFootprint(
                            boundaryGeneratedLayer.Setting,
                            true,
                            doubleStartBridgeAccessory.Setting,
                            true,
                            requiredBusbarWidth,
                            out var generatedBridgeFootprint))
                {
                    return Array.Empty<LinkageRouteLapFootprint>();
                }

                generatedBridgeFootprints.Add(generatedBridgeFootprint);
            }

            return TryIntersectLapFootprints(
                    generatedBridgeFootprints,
                    out var sharedGeneratedBridgeFootprint)
                ? new[] { sharedGeneratedBridgeFootprint }
                : Array.Empty<LinkageRouteLapFootprint>();
        }

        private static bool IsResidualOnOppositeDoubleStartBridgeSide(
            ThicknessInterval residualInterval,
            IReadOnlyList<FixedTerminalLayer> fixedTerminalLayers,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            LinkageRouteAccessory doubleStartBridgeAccessory)
        {
            if (residualInterval == null ||
                fixedTerminalLayers == null ||
                fixedTerminalLayers.Count == 0 ||
                doubleStartBridgeAccessory?.Setting == null ||
                terminalPlacement == null ||
                terminalPlacement.DoubleStartHorizontalBridgeSideSign == 0)
            {
                return false;
            }

            var fixedTerminalMinimumCoordinate = fixedTerminalLayers.Min(
                fixedTerminalLayer => fixedTerminalLayer.MinimumCoordinate);
            var fixedTerminalMaximumCoordinate = fixedTerminalLayers.Max(
                fixedTerminalLayer => fixedTerminalLayer.MaximumCoordinate);
            return terminalPlacement.DoubleStartHorizontalBridgeSideSign > 0
                ? residualInterval.Maximum <=
                    fixedTerminalMinimumCoordinate + GeometryTolerance
                : residualInterval.Minimum >=
                    fixedTerminalMaximumCoordinate - GeometryTolerance;
        }

        private static Point3d ResolveStartTerminalBridgeReferencePoint(
            LinkageTerminalAssembly startTerminalAssembly,
            PlanarBendBusbarSetting collectiveFixedBusbar)
        {
            var fallbackReferencePoint = collectiveFixedBusbar?.PointList
                ?.FirstOrDefault();
            if (fallbackReferencePoint == null ||
                startTerminalAssembly?.RouteAnchor == null ||
                collectiveFixedBusbar.WidthVector == null ||
                collectiveFixedBusbar.WidthVector.Norm <= GeometryTolerance)
            {
                return fallbackReferencePoint;
            }

            var bridgeLengthDirection = collectiveFixedBusbar.WidthVector
                .Normalized;
            var anchorLengthDisplacement = new Vector3d(
                    fallbackReferencePoint,
                    startTerminalAssembly.RouteAnchor)
                .Dot(bridgeLengthDirection);
            return Translate(
                fallbackReferencePoint,
                bridgeLengthDirection,
                anchorLengthDisplacement);
        }

        /// <summary>
        /// 连接排在创建时直接由固定始端的实际搭接段确定排宽位置。
        /// 后续实体化只允许移动始端搭接排，不能再拿生成排反向移动连接排。
        /// </summary>
        private static Point3d ClampBridgeCenterToFixedStartLapRange(
            Point3d bridgeCenterPoint,
            Vector3d bridgeWidthDirection,
            double bridgeBusbarWidth,
            PlanarBendBusbarSetting collectiveFixedBusbar,
            Point3d routeAnchor)
        {
            if (bridgeCenterPoint == null ||
                bridgeWidthDirection == null ||
                bridgeWidthDirection.Norm <= GeometryTolerance ||
                bridgeBusbarWidth <= GeometryTolerance ||
                collectiveFixedBusbar?.PointList == null ||
                collectiveFixedBusbar.PointList.Count < 2)
            {
                return bridgeCenterPoint;
            }

            var normalizedBridgeWidthDirection = bridgeWidthDirection.Normalized;
            var selectedSegmentStartPoint = default(Point3d);
            var selectedSegmentEndPoint = default(Point3d);
            var selectedSegmentDistance = double.PositiveInfinity;
            for (var pointIndex = 0;
                pointIndex + 1 < collectiveFixedBusbar.PointList.Count;
                pointIndex++)
            {
                var segmentStartPoint = collectiveFixedBusbar.PointList[pointIndex];
                var segmentEndPoint = collectiveFixedBusbar.PointList[pointIndex + 1];
                var segmentVector = new Vector3d(
                    segmentStartPoint,
                    segmentEndPoint);
                var segmentLength = segmentVector.Norm;
                if (segmentLength + GeometryTolerance < bridgeBusbarWidth ||
                    segmentLength <= GeometryTolerance ||
                    Math.Abs(segmentVector.Normalized.Dot(
                        normalizedBridgeWidthDirection)) <
                            1d - GeometryTolerance)
                {
                    continue;
                }

                var anchorReferencePoint = routeAnchor ?? bridgeCenterPoint;
                var anchorProjectionLength = Math.Max(
                    0d,
                    Math.Min(
                        segmentLength,
                        new Vector3d(
                                segmentStartPoint,
                                anchorReferencePoint)
                            .Dot(segmentVector.Normalized)));
                var anchorProjectionPoint = Translate(
                    segmentStartPoint,
                    segmentVector.Normalized,
                    anchorProjectionLength);
                var segmentDistance = anchorProjectionPoint.DistanceTo(
                    anchorReferencePoint);
                if (segmentDistance >=
                    selectedSegmentDistance - GeometryTolerance)
                {
                    continue;
                }

                selectedSegmentStartPoint = segmentStartPoint;
                selectedSegmentEndPoint = segmentEndPoint;
                selectedSegmentDistance = segmentDistance;
            }

            if (selectedSegmentStartPoint == null ||
                selectedSegmentEndPoint == null)
            {
                return bridgeCenterPoint;
            }

            var firstEndpointCoordinate = new Vector3d(
                    bridgeCenterPoint,
                    selectedSegmentStartPoint)
                .Dot(normalizedBridgeWidthDirection);
            var secondEndpointCoordinate = new Vector3d(
                    bridgeCenterPoint,
                    selectedSegmentEndPoint)
                .Dot(normalizedBridgeWidthDirection);
            var bridgeHalfWidth = bridgeBusbarWidth / 2d;
            var minimumCenterTranslation = Math.Min(
                    firstEndpointCoordinate,
                    secondEndpointCoordinate) +
                bridgeHalfWidth;
            var maximumCenterTranslation = Math.Max(
                    firstEndpointCoordinate,
                    secondEndpointCoordinate) -
                bridgeHalfWidth;
            if (minimumCenterTranslation >
                maximumCenterTranslation + GeometryTolerance)
            {
                return bridgeCenterPoint;
            }

            var selectedTranslation = Math.Max(
                minimumCenterTranslation,
                Math.Min(maximumCenterTranslation, 0d));
            return Math.Abs(selectedTranslation) <= GeometryTolerance
                ? bridgeCenterPoint
                : Translate(
                    bridgeCenterPoint,
                    normalizedBridgeWidthDirection,
                    selectedTranslation);
        }

        private static bool IsResidualAdjacentToDoubleStartBridge(
            ThicknessInterval residualInterval,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            LinkageRouteAccessory doubleStartBridgeAccessory)
        {
            if (!TryGetDoubleStartBridgeThicknessInterval(
                    terminalReferencePoint,
                    terminalThicknessDirection,
                    doubleStartBridgeAccessory,
                    out var doubleStartBridgeMinimumCoordinate,
                    out var doubleStartBridgeMaximumCoordinate))
            {
                return false;
            }

            return Math.Abs(
                    residualInterval.Minimum -
                    doubleStartBridgeMaximumCoordinate) <= GeometryTolerance ||
                Math.Abs(
                    residualInterval.Maximum -
                    doubleStartBridgeMinimumCoordinate) <= GeometryTolerance;
        }

        private static bool IsResidualOnDoubleStartBridgeLapSide(
            ThicknessInterval residualInterval,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            LinkageRouteAccessory doubleStartBridgeAccessory)
        {
            if (terminalPlacement == null ||
                terminalPlacement.DoubleStartHorizontalBridgeSideSign == 0)
            {
                return false;
            }

            if (!TryGetDoubleStartBridgeThicknessInterval(
                    terminalReferencePoint,
                    terminalThicknessDirection,
                    doubleStartBridgeAccessory,
                    out var doubleStartBridgeMinimumCoordinate,
                    out var doubleStartBridgeMaximumCoordinate))
            {
                return false;
            }

            return terminalPlacement.DoubleStartHorizontalBridgeSideSign < 0
                ? residualInterval.Maximum <=
                    doubleStartBridgeMinimumCoordinate + GeometryTolerance
                : residualInterval.Minimum >=
                    doubleStartBridgeMaximumCoordinate - GeometryTolerance;
        }

        private static bool TryGetDoubleStartBridgeThicknessInterval(
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            LinkageRouteAccessory doubleStartBridgeAccessory,
            out double doubleStartBridgeMinimumCoordinate,
            out double doubleStartBridgeMaximumCoordinate)
        {
            doubleStartBridgeMinimumCoordinate = 0d;
            doubleStartBridgeMaximumCoordinate = 0d;
            var doubleStartBridgeSetting = doubleStartBridgeAccessory?.Setting;
            if (doubleStartBridgeSetting?.PointList == null ||
                doubleStartBridgeSetting.PointList.Count == 0 ||
                doubleStartBridgeSetting.BusBarThickness <= GeometryTolerance)
            {
                return false;
            }

            var doubleStartBridgeCenterCoordinate = new Vector3d(
                    terminalReferencePoint,
                    doubleStartBridgeSetting.PointList[0])
                .Dot(terminalThicknessDirection.Normalized);
            doubleStartBridgeMinimumCoordinate =
                doubleStartBridgeCenterCoordinate -
                doubleStartBridgeSetting.BusBarThickness / 2d;
            doubleStartBridgeMaximumCoordinate =
                doubleStartBridgeCenterCoordinate +
                doubleStartBridgeSetting.BusBarThickness / 2d;
            return true;
        }

        internal static bool TryIntersectLapFootprints(
            IReadOnlyList<LinkageRouteLapFootprint> sourceLapFootprints,
            out LinkageRouteLapFootprint intersectedLapFootprint)
        {
            intersectedLapFootprint = null;
            var firstLapFootprint = sourceLapFootprints?.FirstOrDefault();
            if (firstLapFootprint?.Corners == null ||
                firstLapFootprint.Corners.Count < 4)
            {
                return false;
            }

            var intersectionOrigin = firstLapFootprint.Corners[0];
            var intersectionLengthDirection = firstLapFootprint
                .LengthDirection.Normalized;
            var intersectionWidthDirection = firstLapFootprint
                .WidthDirection.Normalized;
            var intersectionPlaneNormalVector = intersectionLengthDirection
                .Cross(intersectionWidthDirection);
            if (intersectionPlaneNormalVector.Norm <= GeometryTolerance)
            {
                return false;
            }

            var intersectionPlaneNormal = intersectionPlaneNormalVector
                .Normalized;
            var intersectionLengthMinimum = double.NegativeInfinity;
            var intersectionLengthMaximum = double.PositiveInfinity;
            var intersectionWidthMinimum = double.NegativeInfinity;
            var intersectionWidthMaximum = double.PositiveInfinity;
            foreach (var sourceLapFootprint in sourceLapFootprints)
            {
                if (sourceLapFootprint?.Corners == null ||
                    sourceLapFootprint.Corners.Count < 4)
                {
                    return false;
                }

                var sourcePlaneNormalVector = sourceLapFootprint
                    .LengthDirection.Normalized.Cross(
                        sourceLapFootprint.WidthDirection.Normalized);
                if (sourcePlaneNormalVector.Norm <= GeometryTolerance ||
                    Math.Abs(sourcePlaneNormalVector.Normalized.Dot(
                        intersectionPlaneNormal)) < 1d - GeometryTolerance)
                {
                    return false;
                }

                var sourceLengthCoordinates = sourceLapFootprint.Corners
                    .Select(sourceCorner => new Vector3d(
                            intersectionOrigin,
                            sourceCorner)
                        .Dot(intersectionLengthDirection))
                    .ToList();
                var sourceWidthCoordinates = sourceLapFootprint.Corners
                    .Select(sourceCorner => new Vector3d(
                            intersectionOrigin,
                            sourceCorner)
                        .Dot(intersectionWidthDirection))
                    .ToList();
                intersectionLengthMinimum = Math.Max(
                    intersectionLengthMinimum,
                    sourceLengthCoordinates.Min());
                intersectionLengthMaximum = Math.Min(
                    intersectionLengthMaximum,
                    sourceLengthCoordinates.Max());
                intersectionWidthMinimum = Math.Max(
                    intersectionWidthMinimum,
                    sourceWidthCoordinates.Min());
                intersectionWidthMaximum = Math.Min(
                    intersectionWidthMaximum,
                    sourceWidthCoordinates.Max());
            }

            if (intersectionLengthMaximum - intersectionLengthMinimum <=
                    GeometryTolerance ||
                intersectionWidthMaximum - intersectionWidthMinimum <=
                    GeometryTolerance)
            {
                return false;
            }

            var minimumLengthMinimumWidthCorner = Translate(
                Translate(
                    intersectionOrigin,
                    intersectionLengthDirection,
                    intersectionLengthMinimum),
                intersectionWidthDirection,
                intersectionWidthMinimum);
            var maximumLengthMinimumWidthCorner = Translate(
                Translate(
                    intersectionOrigin,
                    intersectionLengthDirection,
                    intersectionLengthMaximum),
                intersectionWidthDirection,
                intersectionWidthMinimum);
            var maximumLengthMaximumWidthCorner = Translate(
                Translate(
                    intersectionOrigin,
                    intersectionLengthDirection,
                    intersectionLengthMaximum),
                intersectionWidthDirection,
                intersectionWidthMaximum);
            var minimumLengthMaximumWidthCorner = Translate(
                Translate(
                    intersectionOrigin,
                    intersectionLengthDirection,
                    intersectionLengthMinimum),
                intersectionWidthDirection,
                intersectionWidthMaximum);
            intersectedLapFootprint = new LinkageRouteLapFootprint
            {
                Corners = new List<Point3d>
                {
                    minimumLengthMinimumWidthCorner,
                    maximumLengthMinimumWidthCorner,
                    maximumLengthMaximumWidthCorner,
                    minimumLengthMaximumWidthCorner
                },
                LengthDirection = CopyVector(intersectionLengthDirection),
                WidthDirection = CopyVector(intersectionWidthDirection)
            };
            return true;
        }

        private static bool TryAddUnoccupiedFixedHostGapSupportBlocks(
            LinkageRouteCenterlineConversionResult conversionResult,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            LinkageTerminalAssembly terminalAssembly,
            bool useStartTerminal,
            IReadOnlyList<GeneratedTerminalLayer> generatedTerminalLayers,
            IReadOnlyList<FixedTerminalLayer> fixedTerminalLayers,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            IReadOnlyList<LinkageRouteLapFootprint> referenceLapFootprints,
            double requiredBusbarWidth,
            LinkageRouteAccessory doubleStartBridgeAccessory,
            out string failureReason)
        {
            failureReason = null;
            for (var fixedGapIndex = 0;
                fixedGapIndex < fixedTerminalLayers.Count - 1;
                fixedGapIndex++)
            {
                var lowerFixedLayer = fixedTerminalLayers[fixedGapIndex];
                var upperFixedLayer = fixedTerminalLayers[fixedGapIndex + 1];
                var fixedGapMinimum = lowerFixedLayer.MaximumCoordinate;
                var fixedGapMaximum = upperFixedLayer.MinimumCoordinate;
                if (fixedGapMaximum - fixedGapMinimum <= GeometryTolerance ||
                    (!useStartTerminal && fixedGapMaximum - fixedGapMinimum <
                        MinimumCabinetFixedGapSupportThickness - GeometryTolerance) ||
                    generatedTerminalLayers.Any(generatedTerminalLayer =>
                        generatedTerminalLayer.CenterCoordinate +
                            generatedTerminalLayer.Thickness / 2d >
                                fixedGapMinimum + GeometryTolerance &&
                        generatedTerminalLayer.CenterCoordinate -
                            generatedTerminalLayer.Thickness / 2d <
                                fixedGapMaximum - GeometryTolerance))
                {
                    continue;
                }

                var nearestGeneratedLayer = generatedTerminalLayers
                    .OrderBy(generatedTerminalLayer => Math.Abs(
                        generatedTerminalLayer.CenterCoordinate -
                        (fixedGapMinimum + fixedGapMaximum) / 2d))
                    .First();
                var supportFootprint =
                    referenceLapFootprints.FirstOrDefault() ??
                    BuildFixedLayerSupportFootprints(
                            nearestGeneratedLayer,
                            lowerFixedLayer,
                            terminalAssembly,
                            useStartTerminal,
                            requiredBusbarWidth,
                            doubleStartBridgeAccessory,
                            referenceLapFootprints)
                        .FirstOrDefault() ??
                    BuildFixedLayerSupportFootprints(
                            nearestGeneratedLayer,
                            upperFixedLayer,
                            terminalAssembly,
                            useStartTerminal,
                            requiredBusbarWidth,
                            doubleStartBridgeAccessory,
                            referenceLapFootprints)
                        .FirstOrDefault() ??
                    TryCreateFixedGapSupportFootprint(
                        lowerFixedLayer,
                        upperFixedLayer,
                        terminalAssembly,
                        useStartTerminal,
                        requiredBusbarWidth);
                if (supportFootprint == null)
                {
                    failureReason = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}第 {1} 条未占用固定排夹缝无法取得垫块搭接区域。",
                        useStartTerminal ? "始端" : "柜顶",
                        fixedGapIndex + 1);
                    return false;
                }

                conversionResult.Accessories.Add(CreateSupportBlockAccessory(
                    supportFootprint,
                    terminalReferencePoint,
                    terminalThicknessDirection,
                    fixedGapMinimum,
                    fixedGapMaximum,
                    routeRequest,
                    routeGroupRequest,
                    false,
                    nearestGeneratedLayer.MemberIndex));
            }

            return true;
        }

        private static LinkageRouteLapFootprint
            TryCreateFixedGapSupportFootprint(
            FixedTerminalLayer lowerFixedLayer,
            FixedTerminalLayer upperFixedLayer,
            LinkageTerminalAssembly terminalAssembly,
            bool useStartTerminal,
            double requiredBusbarWidth)
        {
            // 2 搭 3 等层数差一时，空层位本身没有“生成排-固定排”
            // 接触对，不能把缺少参考搭接矩形误判为方案失败。垫块位于
            // 两个相邻固定层之间，因此使用两层共同的末端投影作为范围。
            foreach (var lowerFixedMemberIndex in
                lowerFixedLayer.FixedMemberIndices)
            {
                foreach (var upperFixedMemberIndex in
                    upperFixedLayer.FixedMemberIndices)
                {
                    var lowerFixedSetting = terminalAssembly.Members[
                        lowerFixedMemberIndex]?.FixedBusbar;
                    var upperFixedSetting = terminalAssembly.Members[
                        upperFixedMemberIndex]?.FixedBusbar;
                    if (LinkageRouteTerminalLapRegionValidator
                        .TryCreateProjectedLapFootprint(
                            lowerFixedSetting,
                            useStartTerminal,
                            upperFixedSetting,
                            useStartTerminal,
                            requiredBusbarWidth,
                            out var fixedGapSupportFootprint))
                    {
                        return fixedGapSupportFootprint;
                    }
                }
            }

            return null;
        }

        private static IReadOnlyList<LinkageRouteLapFootprint>
            BuildReferenceLapFootprints(
            IReadOnlyList<GeneratedTerminalLayer> generatedTerminalLayers,
            IReadOnlyList<FixedTerminalLayer> fixedTerminalLayers,
            LinkageRouteStackTerminalPlacement terminalPlacement,
            LinkageTerminalAssembly terminalAssembly,
            bool useStartTerminal,
            double requiredBusbarWidth,
            LinkageRouteAccessory doubleStartBridgeAccessory)
        {
            var referenceFootprints = new List<LinkageRouteLapFootprint>();
            foreach (var terminalContact in terminalPlacement?.Contacts ??
                new List<LinkageRouteStackTerminalContact>())
            {
                var contactedGeneratedLayer = generatedTerminalLayers
                    .FirstOrDefault(generatedTerminalLayer =>
                        generatedTerminalLayer.MemberIndex ==
                            terminalContact.GeneratedMemberIndex);
                if (contactedGeneratedLayer == null ||
                    terminalContact.FixedMemberIndex < 0 ||
                    terminalContact.FixedMemberIndex >=
                        terminalAssembly.Members.Count)
                {
                    continue;
                }

                var contactedFixedSetting = terminalAssembly.Members[
                    terminalContact.FixedMemberIndex]?.FixedBusbar;
                if (LinkageRouteTerminalLapRegionValidator
                    .TryCreateProjectedLapFootprint(
                        contactedGeneratedLayer.Setting,
                        useStartTerminal,
                        contactedFixedSetting,
                        useStartTerminal,
                        requiredBusbarWidth,
                        out var contactFootprint))
                {
                    referenceFootprints.Add(contactFootprint);
                }
            }

            return referenceFootprints
                .OrderByDescending(GetFootprintArea)
                .ToList();
        }

        private static IReadOnlyList<LinkageRouteLapFootprint>
            BuildFixedLayerSupportFootprints(
            GeneratedTerminalLayer adjacentGeneratedLayer,
            FixedTerminalLayer fixedTerminalLayer,
            LinkageTerminalAssembly terminalAssembly,
            bool useStartTerminal,
            double requiredBusbarWidth,
            LinkageRouteAccessory doubleStartBridgeAccessory,
            IReadOnlyList<LinkageRouteLapFootprint> referenceLapFootprints)
        {
            var fixedLayerFootprints = new List<LinkageRouteLapFootprint>();
            foreach (var fixedMemberIndex in fixedTerminalLayer.FixedMemberIndices)
            {
                var fixedMemberSetting = terminalAssembly.Members[
                    fixedMemberIndex]?.FixedBusbar;
                if (LinkageRouteTerminalLapRegionValidator
                    .TryCreateProjectedLapFootprint(
                        adjacentGeneratedLayer.Setting,
                        useStartTerminal,
                        fixedMemberSetting,
                        useStartTerminal,
                        requiredBusbarWidth,
                        out var generatedFixedFootprint))
                {
                    fixedLayerFootprints.Add(generatedFixedFootprint);
                    continue;
                }

                if (doubleStartBridgeAccessory?.Setting != null &&
                    LinkageRouteTerminalLapRegionValidator
                        .TryCreateProjectedLapFootprint(
                            doubleStartBridgeAccessory.Setting,
                            true,
                            fixedMemberSetting,
                            useStartTerminal,
                            requiredBusbarWidth,
                            out var bridgeFixedFootprint))
                {
                    fixedLayerFootprints.Add(bridgeFixedFootprint);
                }
            }

            if (fixedLayerFootprints.Count == 0 &&
                referenceLapFootprints.Count > 0)
            {
                fixedLayerFootprints.Add(referenceLapFootprints[0]);
            }

            return fixedLayerFootprints;
        }

        private static bool TryResolveEmptyGapFootprint(
            GeneratedTerminalLayer lowerGeneratedLayer,
            GeneratedTerminalLayer upperGeneratedLayer,
            bool useStartTerminal,
            double requiredBusbarWidth,
            IReadOnlyList<LinkageRouteLapFootprint> referenceLapFootprints,
            out LinkageRouteLapFootprint emptyGapFootprint)
        {
            emptyGapFootprint = referenceLapFootprints.FirstOrDefault();
            return emptyGapFootprint != null ||
                LinkageRouteTerminalLapRegionValidator
                    .TryCreateProjectedLapFootprint(
                        lowerGeneratedLayer.Setting,
                        useStartTerminal,
                        upperGeneratedLayer.Setting,
                        useStartTerminal,
                        requiredBusbarWidth,
                        out emptyGapFootprint);
        }

        internal static LinkageRouteAccessory CreateSupportBlockAccessory(
            LinkageRouteLapFootprint supportFootprint,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            double supportMinimumCoordinate,
            double supportMaximumCoordinate,
            LinkageMultiGroupRouteRequest routeRequest,
            LinkageRouteGroupRequest routeGroupRequest,
            bool useStartTerminal,
            int relatedGeneratedMemberIndex)
        {
            return CreateSupportBlockAccessoryCore(
                supportFootprint,
                terminalReferencePoint,
                terminalThicknessDirection,
                supportMinimumCoordinate,
                supportMaximumCoordinate,
                routeRequest.BendingRadius,
                routeRequest.GetEffectiveBendAllowance(),
                routeGroupRequest.GroupKey,
                useStartTerminal,
                relatedGeneratedMemberIndex);
        }

        // 独立纯几何门面只传实际矩形及厚向区间；旧多组请求和原调用者不变。
        internal static PlanarBendBusbarSetting CreateSharedSupportBlock(
            LinkageRouteLapFootprint footprint, Point3d reference, Vector3d normal,
            double minimum, double maximum)
            => CreateSupportBlockAccessoryCore(footprint, reference, normal,
                minimum, maximum, 0d, 0d, "shared", false, 0).Setting;

        internal static IReadOnlyList<Tuple<double, double>> GetSharedResidualIntervals(
            double minimum, double maximum, IEnumerable<Tuple<double, double>> occupied)
            => GetResidualIntervals(minimum, maximum, occupied.Select(interval => new ThicknessInterval
            {
                Minimum = Math.Max(minimum, interval.Item1),
                Maximum = Math.Min(maximum, interval.Item2)
            })).Select(interval => Tuple.Create(interval.Minimum, interval.Maximum)).ToArray();

        private static LinkageRouteAccessory CreateSupportBlockAccessoryCore(
            LinkageRouteLapFootprint supportFootprint,
            Point3d terminalReferencePoint,
            Vector3d terminalThicknessDirection,
            double supportMinimumCoordinate,
            double supportMaximumCoordinate,
            double bendingRadius,
            double bendAllowance,
            string groupKey,
            bool useStartTerminal,
            int relatedGeneratedMemberIndex)
        {
            var supportLengthDirection = supportFootprint.LengthDirection.Normalized;
            var supportWidthDirection = supportFootprint.WidthDirection.Normalized;
            if (supportLengthDirection.Cross(supportWidthDirection).Dot(
                    terminalThicknessDirection.Normalized) < 0d)
            {
                supportWidthDirection = Scale(supportWidthDirection, -1d);
            }

            var sourceFootprintCenter = AveragePoint(supportFootprint.Corners);
            var desiredSupportCenterCoordinate =
                (supportMinimumCoordinate + supportMaximumCoordinate) / 2d;
            var sourceSupportCenterCoordinate = new Vector3d(
                    terminalReferencePoint,
                    sourceFootprintCenter)
                .Dot(terminalThicknessDirection.Normalized);
            var supportCenterPoint = Translate(
                sourceFootprintCenter,
                terminalThicknessDirection,
                desiredSupportCenterCoordinate - sourceSupportCenterCoordinate);
            var supportLengthCoordinates = supportFootprint.Corners
                .Select(footprintCorner => new Vector3d(
                        sourceFootprintCenter,
                        footprintCorner)
                    .Dot(supportLengthDirection))
                .ToList();
            var supportWidthCoordinates = supportFootprint.Corners
                .Select(footprintCorner => new Vector3d(
                        sourceFootprintCenter,
                        footprintCorner)
                    .Dot(supportWidthDirection))
                .ToList();
            var supportSetting = new PlanarBendBusbarSetting
            {
                IsCenter = true,
                PointList = new List<Point3d>
                {
                    Translate(
                        supportCenterPoint,
                        supportLengthDirection,
                        supportLengthCoordinates.Min()),
                    Translate(
                        supportCenterPoint,
                        supportLengthDirection,
                        supportLengthCoordinates.Max())
                },
                StartBusbarVector = CopyVector(
                    terminalThicknessDirection.Normalized),
                WidthVector = CopyVector(supportWidthDirection),
                BusBarWidth = supportWidthCoordinates.Max() -
                    supportWidthCoordinates.Min(),
                BusBarThickness = supportMaximumCoordinate -
                    supportMinimumCoordinate,
                BarBendingRadius = bendingRadius,
                BendAllowance = bendAllowance,
                GroupKey = groupKey
            };
            return new LinkageRouteAccessory
            {
                Kind = LinkageRouteAccessoryKind.SupportBlock,
                TerminalSide = useStartTerminal
                    ? LinkageRouteTerminalSide.Start
                    : LinkageRouteTerminalSide.End,
                RelatedGeneratedMemberIndex = relatedGeneratedMemberIndex,
                Setting = supportSetting
            };
        }

        private static bool TryBuildGeneratedTerminalLayers(
            IEnumerable<LinkageStackedRouteMember> stackedRouteMembers,
            LinkageTerminalAssembly terminalAssembly,
            bool useStartTerminal,
            ISet<int> eligibleMemberIndices,
            out List<GeneratedTerminalLayer> generatedTerminalLayers,
            out Point3d terminalReferencePoint)
        {
            generatedTerminalLayers = new List<GeneratedTerminalLayer>();
            terminalReferencePoint = terminalAssembly?.Members?
                .FirstOrDefault(terminalMember =>
                    terminalMember?.FixedBusbar?.PointList?.Count >= 2)
                ?.FixedBusbar.PointList[0];
            if (terminalReferencePoint == null ||
                terminalAssembly?.ThicknessDirection == null ||
                terminalAssembly.ThicknessDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            var terminalThicknessDirection = terminalAssembly
                .ThicknessDirection.Normalized;
            foreach (var stackedRouteMember in stackedRouteMembers ??
                Enumerable.Empty<LinkageStackedRouteMember>())
            {
                if (eligibleMemberIndices != null &&
                    eligibleMemberIndices.Count > 0 &&
                    !eligibleMemberIndices.Contains(
                        stackedRouteMember.MemberIndex))
                {
                    continue;
                }

                var terminalSetting = useStartTerminal
                    ? stackedRouteMember?.PlanarParts?.FirstOrDefault()
                    : stackedRouteMember?.PlanarParts?.LastOrDefault();
                if (terminalSetting?.PointList == null ||
                    terminalSetting.PointList.Count < 2 ||
                    terminalSetting.BusBarThickness <= GeometryTolerance)
                {
                    continue;
                }

                var terminalCenterPoint = useStartTerminal
                    ? terminalSetting.PointList[0]
                    : terminalSetting.PointList[
                        terminalSetting.PointList.Count - 1];
                generatedTerminalLayers.Add(new GeneratedTerminalLayer
                {
                    MemberIndex = stackedRouteMember.MemberIndex,
                    CenterCoordinate = new Vector3d(
                            terminalReferencePoint,
                            terminalCenterPoint)
                        .Dot(terminalThicknessDirection),
                    Thickness = terminalSetting.BusBarThickness,
                    Setting = terminalSetting
                });
            }

            generatedTerminalLayers = generatedTerminalLayers
                .OrderBy(generatedTerminalLayer =>
                    generatedTerminalLayer.CenterCoordinate)
                .ToList();
            return generatedTerminalLayers.Count > 0;
        }

        private static bool TryBuildFixedTerminalLayers(
            LinkageTerminalAssembly terminalAssembly,
            Point3d terminalReferencePoint,
            out List<FixedTerminalLayer> fixedTerminalLayers)
        {
            fixedTerminalLayers = new List<FixedTerminalLayer>();
            if (terminalAssembly?.Members == null ||
                terminalAssembly.ThicknessDirection == null ||
                terminalAssembly.ThicknessDirection.Norm <= GeometryTolerance)
            {
                return false;
            }

            var terminalThicknessDirection = terminalAssembly
                .ThicknessDirection.Normalized;
            for (var fixedMemberIndex = 0;
                fixedMemberIndex < terminalAssembly.Members.Count;
                fixedMemberIndex++)
            {
                var fixedMemberSetting = terminalAssembly.Members[
                    fixedMemberIndex]?.FixedBusbar;
                if (fixedMemberSetting?.PointList == null ||
                    fixedMemberSetting.PointList.Count < 2 ||
                    fixedMemberSetting.BusBarThickness <= GeometryTolerance)
                {
                    continue;
                }

                var fixedMemberCenterCoordinate = new Vector3d(
                        terminalReferencePoint,
                        fixedMemberSetting.PointList[0])
                    .Dot(terminalThicknessDirection);
                var matchingFixedLayer = fixedTerminalLayers.FirstOrDefault(
                    fixedTerminalLayer => Math.Abs(
                        fixedTerminalLayer.CenterCoordinate -
                        fixedMemberCenterCoordinate) <= GeometryTolerance);
                if (matchingFixedLayer == null)
                {
                    matchingFixedLayer = new FixedTerminalLayer
                    {
                        CenterCoordinate = fixedMemberCenterCoordinate,
                        Thickness = fixedMemberSetting.BusBarThickness
                    };
                    fixedTerminalLayers.Add(matchingFixedLayer);
                }
                else
                {
                    matchingFixedLayer.Thickness = Math.Max(
                        matchingFixedLayer.Thickness,
                        fixedMemberSetting.BusBarThickness);
                }

                matchingFixedLayer.FixedMemberIndices.Add(fixedMemberIndex);
            }

            fixedTerminalLayers = fixedTerminalLayers
                .OrderBy(fixedTerminalLayer => fixedTerminalLayer.CenterCoordinate)
                .ToList();
            return fixedTerminalLayers.Count > 0;
        }

        private static IReadOnlyList<ThicknessInterval> GetResidualIntervals(
            double gapMinimum,
            double gapMaximum,
            IEnumerable<ThicknessInterval> occupiedIntervals)
            => LinkageSupportProjectionPartition.ResidualIntervals(gapMinimum, gapMaximum,
                occupiedIntervals?.Select(interval => Tuple.Create(interval.Minimum, interval.Maximum)))
                .Select(interval => new ThicknessInterval { Minimum = interval.Item1, Maximum = interval.Item2 }).ToArray();

        private static LinkageRouteStackTerminalPlacement CopyPlacement(
            LinkageRouteStackTerminalPlacement sourcePlacement)
        {
            if (sourcePlacement == null)
            {
                return null;
            }

            return new LinkageRouteStackTerminalPlacement
            {
                Signature = sourcePlacement.Signature,
                BaseCenterOffset = sourcePlacement.BaseCenterOffset,
                GeneratedMemberOffsets = sourcePlacement.GeneratedMemberOffsets
                    .ToList(),
                SpacerlessMovedGeneratedMemberIndices = (sourcePlacement
                        .SpacerlessMovedGeneratedMemberIndices ??
                        new List<int>())
                    .ToList(),
                Contacts = sourcePlacement.Contacts.Select(terminalContact =>
                    new LinkageRouteStackTerminalContact
                    {
                        GeneratedMemberIndex = terminalContact.GeneratedMemberIndex,
                        FixedMemberIndex = terminalContact.FixedMemberIndex
                    }).ToList(),
                DoubleStartHorizontalBridgeGeneratedMemberIndex =
                    sourcePlacement.DoubleStartHorizontalBridgeGeneratedMemberIndex,
                DoubleStartHorizontalBridgeSideSign =
                    sourcePlacement.DoubleStartHorizontalBridgeSideSign,
                    DoubleStartHorizontalBridgeThickness =
                        sourcePlacement.DoubleStartHorizontalBridgeThickness,
                    DoubleStartHorizontalBridgePlacements =
                        (sourcePlacement.DoubleStartHorizontalBridgePlacements ??
                            new List<LinkageRouteTerminalBridgePlacement>())
                        .Select(bridgePlacement => new LinkageRouteTerminalBridgePlacement
                        {
                            GeneratedMemberIndex = bridgePlacement.GeneratedMemberIndex,
                            SideSign = bridgePlacement.SideSign,
                            Thickness = bridgePlacement.Thickness
                        }).ToList()
                };
        }

        private static bool TryGetBoundaryDirection(
            PlanarBendBusbarSetting busbarSetting,
            bool useStart,
            out Vector3d boundaryDirection)
        {
            boundaryDirection = null;
            if (busbarSetting?.PointList == null ||
                busbarSetting.PointList.Count < 2)
            {
                return false;
            }

            var segmentIndex = useStart
                ? 0
                : busbarSetting.PointList.Count - 2;
            var boundaryDirectionVector = new Vector3d(
                busbarSetting.PointList[segmentIndex],
                busbarSetting.PointList[segmentIndex + 1]);
            if (boundaryDirectionVector.Norm <= GeometryTolerance)
            {
                return false;
            }

            boundaryDirection = boundaryDirectionVector.Normalized;
            return true;
        }

        private static string CreateAccessorySignature(
            LinkageRouteAccessory routeAccessory)
        {
            var accessorySetting = routeAccessory.Setting;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3:F6}|{4:F6}",
                routeAccessory.Kind,
                routeAccessory.TerminalSide,
                string.Join(",", accessorySetting.PointList.Select(settingPoint =>
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:F6}:{1:F6}:{2:F6}",
                        settingPoint.X,
                        settingPoint.Y,
                        settingPoint.Z))),
                accessorySetting.BusBarWidth,
                accessorySetting.BusBarThickness);
        }

        private static double GetFootprintArea(
            LinkageRouteLapFootprint lapFootprint)
        {
            if (lapFootprint?.Corners == null || lapFootprint.Corners.Count < 4)
            {
                return 0d;
            }

            var footprintOrigin = lapFootprint.Corners[0];
            var lengthCoordinates = lapFootprint.Corners.Select(footprintCorner =>
                new Vector3d(footprintOrigin, footprintCorner).Dot(
                    lapFootprint.LengthDirection.Normalized)).ToList();
            var widthCoordinates = lapFootprint.Corners.Select(footprintCorner =>
                new Vector3d(footprintOrigin, footprintCorner).Dot(
                    lapFootprint.WidthDirection.Normalized)).ToList();
            return Math.Max(0d, lengthCoordinates.Max() - lengthCoordinates.Min()) *
                Math.Max(0d, widthCoordinates.Max() - widthCoordinates.Min());
        }

        private static Point3d AveragePoint(IEnumerable<Point3d> sourcePoints)
        {
            var pointList = sourcePoints.ToList();
            return new Point3d(
                pointList.Average(sourcePoint => sourcePoint.X),
                pointList.Average(sourcePoint => sourcePoint.Y),
                pointList.Average(sourcePoint => sourcePoint.Z));
        }

        private static Point3d ProjectToPlane(
            Point3d sourcePoint,
            Point3d planePoint,
            Vector3d planeNormal)
        {
            var normalizedPlaneNormal = planeNormal.Normalized;
            var planeDistance = new Vector3d(planePoint, sourcePoint).Dot(
                normalizedPlaneNormal);
            return Translate(
                sourcePoint,
                normalizedPlaneNormal,
                -planeDistance);
        }

        private static Point3d Translate(
            Point3d sourcePoint,
            Vector3d direction,
            double distance)
        {
            var normalizedDirection = direction.Normalized;
            return new Point3d(
                sourcePoint.X + normalizedDirection.X * distance,
                sourcePoint.Y + normalizedDirection.Y * distance,
                sourcePoint.Z + normalizedDirection.Z * distance);
        }

        private static Vector3d Scale(Vector3d sourceVector, double scale)
        {
            return new Vector3d(
                sourceVector.X * scale,
                sourceVector.Y * scale,
                sourceVector.Z * scale);
        }

        private static Vector3d CopyVector(Vector3d sourceVector)
        {
            return new Vector3d(
                sourceVector.X,
                sourceVector.Y,
                sourceVector.Z);
        }

        private sealed class CabinetSupportOccupant
        {
            public ThicknessInterval Interval { get; set; }
            public bool IsFixedTerminal { get; set; }
            public double LengthMinimum { get; set; }
            public double LengthMaximum { get; set; }
            public double WidthMinimum { get; set; }
            public double WidthMaximum { get; set; }
        }

        private sealed class GeneratedTerminalLayer
        {
            public int MemberIndex { get; set; }
            public double CenterCoordinate { get; set; }
            public double Thickness { get; set; }
            public PlanarBendBusbarSetting Setting { get; set; }
        }

        private sealed class FixedTerminalLayer
        {
            public double CenterCoordinate { get; set; }
            public double Thickness { get; set; }
            public double MinimumCoordinate => CenterCoordinate - Thickness / 2d;
            public double MaximumCoordinate => CenterCoordinate + Thickness / 2d;
            public IList<int> FixedMemberIndices { get; } = new List<int>();
        }

        private sealed class ThicknessInterval
        {
            public double Minimum { get; set; }
            public double Maximum { get; set; }
        }
    }
}
